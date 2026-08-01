using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

/// <summary>An abstract quality tier with display properties.</summary>
public record QualityTier(string Id, string Slug, string Label, string? Color, string? Icon, int SortOrder);

/// <summary>An abstract quality tier resolved to a concrete provider + model + params.</summary>
public record QualityMode(
    string Id, string Slug, string QualityTier, string Provider,
    string Model, string? Effort, int? ThinkingBudget,
    int? Timeout, int? MaxTurns, bool IsDefault, string? Description,
    int? ContextWindow = null);

/// <summary>
/// The concrete settings a quality tier resolves to, handed to the inference backend.
/// A null model means the tier has no mode for the requested provider — the provider's
/// own default model applies.
/// </summary>
public record ResolvedMode(string Provider, string? Model, string? Effort,
    string? Backend = null, string? EndpointUrl = null, string? ApiKey = null,
    int? ThinkingBudget = null, string? QualityTier = null);

/// <summary>
/// Resolves entity-defined quality tiers to provider-specific
/// model + params for the whole Red Suite. Modes are defined as RedLeaf entities
/// (type=quality-mode) and fetched from RedLeaf. Display and model choices are not duplicated
/// in code; when RedLeaf is offline the current entity-derived snapshot is retained.
/// </summary>
public class QualityModeService
{
    private readonly RedComputeConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly ProviderConfigService _providerConfig;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private Dictionary<string, List<QualityMode>> _modes;
    private Dictionary<string, QualityTier> _tiers;
    private Dictionary<string, ModelTokenPricing> _modelPricing;
    private string? _defaultTierSlug;

    public QualityModeService(RedComputeConfig config, Action<string, Guid?> log, ProviderConfigService providerConfig)
    {
        _config = config;
        _log = log;
        _providerConfig = providerConfig;
        _modes = new Dictionary<string, List<QualityMode>>(StringComparer.OrdinalIgnoreCase);
        _tiers = new Dictionary<string, QualityTier>(StringComparer.OrdinalIgnoreCase);
        _modelPricing = new Dictionary<string, ModelTokenPricing>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-fetch quality modes from RedLeaf and replace the in-memory cache. On any failure the
    /// existing cache (fallbacks or a previous successful fetch) is left untouched.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = _config.RedLeafUrl.TrimEnd('/');

            var tiersJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=quality-tier&limit=100", ct);
            var parsedTiers = ParseTiers(tiersJson);
            if (parsedTiers.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no quality-tier entities; keeping current tiers", null);
            }
            else
            {
                var tiersDict = parsedTiers.ToDictionary(t => t.Slug, StringComparer.OrdinalIgnoreCase);
                lock (_lock) { _tiers = tiersDict; }
                _log($"[QualityModes] Loaded {parsedTiers.Count} quality tier(s) from RedLeaf", null);
            }

            var modesJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=quality-mode&limit=100", ct);
            var parsed = ParseModes(modesJson, parsedTiers);
            if (parsed.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no usable quality-mode entities; keeping current modes", null);
            }
            else
            {
                var grouped = parsed
                    .GroupBy(m => m.QualityTier, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                lock (_lock) { _modes = grouped; }
                _log($"[QualityModes] Loaded {parsed.Count} quality mode(s) across {grouped.Count} tier(s) from RedLeaf", null);
            }

            var pricingJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=inference-model&limit=200", ct);
            var parsedPricing = ParseModelPricing(pricingJson);
            if (parsedPricing.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no complete model pricing; keeping current rates", null);
            }
            else
            {
                lock (_lock) { _modelPricing = parsedPricing; }
                _log($"[QualityModes] Loaded API-equivalent pricing for {parsedPricing.Count} model id(s)", null);
            }

            try
            {
                var suiteConfigJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=suite-config&limit=1", ct);
                var defaultTierRef = ParseDefaultTierReference(suiteConfigJson);
                if (!string.IsNullOrWhiteSpace(defaultTierRef))
                {
                    var defaultTier = parsedTiers.FirstOrDefault(t =>
                        string.Equals(t.Id, defaultTierRef, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.Slug, defaultTierRef, StringComparison.OrdinalIgnoreCase));
                    if (defaultTier != null)
                        lock (_lock) { _defaultTierSlug = defaultTier.Slug; }
                }
            }
            catch { /* suite-config is optional; tier entities remain authoritative */ }
        }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to fetch quality mode entities; keeping the current entity snapshot: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Resolve a quality tier to concrete settings. A null/blank tier uses the suite-config entity default.
    /// When <paramref name="preferredProvider"/> is given, only that provider's modes are
    /// considered — if the tier has none for it, the model is left unset so the provider's own
    /// default applies (never another provider's model). An unknown tier resolves as the entity default.
    /// </summary>
    public ResolvedMode Resolve(string? qualityTier = null, string? preferredProvider = null)
    {
        Dictionary<string, List<QualityMode>> snapshot;
        string? defaultTier;
        lock (_lock) { snapshot = _modes; defaultTier = _defaultTierSlug; }

        var tier = string.IsNullOrWhiteSpace(qualityTier) ? defaultTier : qualityTier.Trim();
        List<QualityMode>? candidates = null;

        if (string.IsNullOrWhiteSpace(tier)
            || !snapshot.TryGetValue(tier, out candidates)
            || candidates.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(tier))
                _log($"[QualityModes] Unknown quality tier '{tier}' requested; resolving as the suite default", null);

            if (string.IsNullOrWhiteSpace(defaultTier)
                || !snapshot.TryGetValue(defaultTier, out candidates)
                || candidates.Count == 0)
            {
                var pc = string.IsNullOrWhiteSpace(preferredProvider)
                    ? _providerConfig.GetDefault()
                    : _providerConfig.Resolve(preferredProvider);
                return new ResolvedMode(pc.Slug, pc.DefaultModel, null, pc.Backend, pc.EndpointUrl, pc.ApiKey, QualityTier: tier);
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var match = candidates.FirstOrDefault(m =>
                string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase));
            if (match != null) return ToResolved(match);
            var ppc = _providerConfig.Resolve(preferredProvider);
            return new ResolvedMode(preferredProvider, ppc.DefaultModel, null, ppc.Backend, ppc.EndpointUrl, ppc.ApiKey, QualityTier: tier);
        }

        var chosen = candidates.FirstOrDefault(m => m.IsDefault) ?? candidates[0];
        return ToResolved(chosen);
    }

    /// <summary>
    /// The context window a quality mode declares for <paramref name="model"/>, or null when no
    /// mode declares one. Modes only set this where the inference runtime under-reports the real
    /// window: Claude Code's `modelUsage.contextWindow` reports a flat 200k for every 1M-context
    /// model, so a declared value is treated as more trustworthy than what the runtime says.
    /// </summary>
    public int? GetContextWindow(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        lock (_lock)
        {
            return _modes.Values
                .SelectMany(v => v)
                .FirstOrDefault(m => m.ContextWindow.HasValue
                    && string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase))
                ?.ContextWindow;
        }
    }

    /// <summary>All known modes across every tier.</summary>
    public IReadOnlyList<QualityMode> GetAll()
    {
        lock (_lock) { return _modes.Values.SelectMany(v => v).ToList(); }
    }

    /// <summary>The set of quality tiers currently known, ordered by SortOrder.</summary>
    public IReadOnlyList<QualityTier> GetTiers()
    {
        lock (_lock) { return _tiers.Values.OrderBy(t => t.SortOrder).ToList(); }
    }

    /// <summary>
    /// Estimate API-equivalent cost from cumulative tokens when the provider did not report an
    /// actual monetary charge. Cached input is removed from regular input before applying rates.
    /// </summary>
    public double? EstimateCostUsd(string? model, int? inputTokens, int? cachedInputTokens, int? outputTokens)
    {
        if (string.IsNullOrWhiteSpace(model) || !inputTokens.HasValue || !outputTokens.HasValue)
            return null;

        ModelTokenPricing? pricing;
        lock (_lock) { _modelPricing.TryGetValue(model, out pricing); }
        return pricing?.EstimateUsd(inputTokens.Value, cachedInputTokens ?? 0, outputTokens.Value);
    }

    public string? DefaultTierSlug
    {
        get { lock (_lock) { return _defaultTierSlug; } }
    }

    private ResolvedMode ToResolved(QualityMode m)
    {
        var pc = _providerConfig.Resolve(m.Provider);
        return new(m.Provider, m.Model, m.Effort, pc.Backend, pc.EndpointUrl, pc.ApiKey, m.ThinkingBudget, m.QualityTier);
    }

    // ---- RedLeaf response parsing --------------------------------------------------------

    private static List<QualityTier> ParseTiers(string json)
    {
        var result = new List<QualityTier>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
                array = root;
            else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array))
                return result;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(item, "id") ?? GetString(item, "slug") ?? "";
                var slug = GetString(item, "slug") ?? GetString(item, "id");
                if (string.IsNullOrWhiteSpace(slug)) continue;

                JsonDocument? dataDoc = null;
                try
                {
                    var data = item;
                    if (item.TryGetProperty("data", out var d))
                    {
                        if (d.ValueKind == JsonValueKind.Object) data = d;
                        else if (d.ValueKind == JsonValueKind.String)
                        {
                            var raw = d.GetString();
                            if (!string.IsNullOrWhiteSpace(raw)) { dataDoc = JsonDocument.Parse(raw); data = dataDoc.RootElement; }
                        }
                    }

                    var label    = GetString(data, "label") ?? GetString(item, "name") ?? slug!;
                    var color    = GetString(data, "color");
                    var icon     = GetString(data, "icon");
                    var sortOrder = GetInt(data, "sort_order") ?? GetInt(data, "sortOrder") ?? 99;

                    result.Add(new QualityTier(id, slug!, label, color, icon, sortOrder));
                }
                catch (JsonException) { }
                finally { dataDoc?.Dispose(); }
            }
        }
        catch (JsonException) { }
        return result;
    }

    private List<QualityMode> ParseModes(string json, IReadOnlyList<QualityTier> tiers)
    {
        var result = new List<QualityMode>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array))
            return result;

        foreach (var item in array.EnumerateArray())
        {
            var mode = ParseOne(item);
            if (mode == null) continue;

            var tier = tiers.FirstOrDefault(t =>
                string.Equals(t.Id, mode.QualityTier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Slug, mode.QualityTier, StringComparison.OrdinalIgnoreCase));

            result.Add(mode with
            {
                QualityTier = tier?.Slug ?? mode.QualityTier,
                Provider = _providerConfig.ResolveReference(mode.Provider),
            });
        }
        return result;
    }

    private static string? ParseDefaultTierReference(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array)) return null;

            var item = array.EnumerateArray().FirstOrDefault();
            if (item.ValueKind != JsonValueKind.Object) return null;

            if (!item.TryGetProperty("data", out var data))
                data = item;
            else if (data.ValueKind == JsonValueKind.String)
            {
                var raw = data.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return null;
                using var dataDoc = JsonDocument.Parse(raw);
                return GetString(dataDoc.RootElement, "default_quality_tier");
            }

            return GetString(data, "default_quality_tier");
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, ModelTokenPricing> ParseModelPricing(string json)
    {
        var result = new Dictionary<string, ModelTokenPricing>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array)) return result;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var slug = GetString(item, "slug");
                JsonDocument? dataDoc = null;
                try
                {
                    var data = item;
                    if (item.TryGetProperty("data", out var d))
                    {
                        if (d.ValueKind == JsonValueKind.Object) data = d;
                        else if (d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } raw)
                        {
                            dataDoc = JsonDocument.Parse(raw);
                            data = dataDoc.RootElement;
                        }
                    }

                    var input = GetDouble(data, "cost_input");
                    var cached = GetDouble(data, "cost_cached_input");
                    var output = GetDouble(data, "cost_output");
                    if (!input.HasValue || !cached.HasValue || !output.HasValue) continue;

                    var pricing = new ModelTokenPricing(input.Value, cached.Value, output.Value);
                    var modelId = GetString(data, "model_id");
                    if (!string.IsNullOrWhiteSpace(slug)) result[slug] = pricing;
                    if (!string.IsNullOrWhiteSpace(modelId)) result[modelId] = pricing;
                }
                catch (JsonException) { }
                finally { dataDoc?.Dispose(); }
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>RedLeaf may wrap the list in a paging envelope — find the entity array.</summary>
    private static bool TryFindArray(JsonElement obj, out JsonElement array)
    {
        foreach (var key in new[] { "items", "entities", "results", "data" })
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                array = v;
                return true;
            }
        }
        array = default;
        return false;
    }

    private static QualityMode? ParseOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = GetString(item, "id") ?? GetString(item, "slug") ?? "";
        var slug = GetString(item, "slug") ?? id;

        // The mode's settings live in the entity's `data` payload. RedLeaf returns `data` as a
        // stringified JSON blob (e.g. "{\"provider\": ...}"), but tolerate a nested object too.
        JsonDocument? dataDoc = null;
        try
        {
            var data = item;
            if (item.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == JsonValueKind.Object)
                {
                    data = d;
                }
                else if (d.ValueKind == JsonValueKind.String)
                {
                    var raw = d.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        dataDoc = JsonDocument.Parse(raw);
                        data = dataDoc.RootElement;
                    }
                }
            }

            var tier = GetString(data, "quality_tier") ?? GetString(data, "qualityTier");
            var provider = GetString(data, "provider");
            var model = GetString(data, "model");

            // A mode without tier/provider/model can't be resolved — skip it.
            if (string.IsNullOrWhiteSpace(tier) || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                return null;

            // GetString returns independent copies, so the record stays valid after dataDoc is disposed.
            return new QualityMode(
                Id: id,
                Slug: slug,
                QualityTier: tier!,
                Provider: provider!,
                Model: model!,
                Effort: GetString(data, "effort"),
                ThinkingBudget: GetInt(data, "thinking_budget") ?? GetInt(data, "thinkingBudget"),
                Timeout: GetInt(data, "timeout"),
                MaxTurns: GetInt(data, "max_turns") ?? GetInt(data, "maxTurns"),
                IsDefault: GetBool(data, "is_default") ?? GetBool(data, "isDefault") ?? false,
                Description: GetString(data, "description"),
                ContextWindow: GetInt(data, "context_window") ?? GetInt(data, "contextWindow"));
        }
        catch (JsonException)
        {
            // Malformed `data` blob — skip this entity rather than failing the whole refresh.
            return null;
        }
        finally
        {
            dataDoc?.Dispose();
        }
    }

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var n) => n,
            JsonValueKind.String when double.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }
}
