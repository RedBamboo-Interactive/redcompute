using System.IO;
using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

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
    int? ThinkingBudget = null, string? QualityTier = null,
    int? TimeoutSeconds = null, int? MaxTurns = null);

public enum QualityResolutionFailure
{
    None,
    CatalogUnavailable,
    UnknownTier,
    ModelUnavailable,
}

/// <summary>
/// Resolves entity-defined quality tiers to provider-specific
/// model + params for the whole Red Suite. Modes are defined as RedLeaf entities
/// (type=quality-mode) and fetched from RedLeaf. Display and model choices are not duplicated
/// in code. A last-known-good disk snapshot covers cold-start outages and RedLeaf is
/// retried in the background until the authoritative entity catalog is available.
/// </summary>
public class QualityModeService : IProviderQualityModeResolver
{
    private readonly RedComputeConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly ProviderConfigService _providerConfig;
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;

    private readonly object _lock = new();
    private Dictionary<string, List<QualityMode>> _modes;
    private Dictionary<string, QualityTier> _tiers;
    private Dictionary<string, ModelTokenPricing> _modelPricing;
    private string? _defaultTierSlug;
    private volatile bool _loadedFromRedLeaf;
    private volatile bool _loadedFromCache;

    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "quality-mode-cache.json");

    public QualityModeService(RedComputeConfig config, Action<string, Guid?> log, ProviderConfigService providerConfig)
        : this(config, log, providerConfig,
            new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, DefaultCachePath,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30))
    {
    }

    internal QualityModeService(
        RedComputeConfig config,
        Action<string, Guid?> log,
        ProviderConfigService providerConfig,
        HttpClient http,
        string cachePath,
        TimeSpan initialRetryDelay,
        TimeSpan maxRetryDelay)
    {
        _config = config;
        _log = log;
        _providerConfig = providerConfig;
        _http = http;
        _cachePath = cachePath;
        _initialRetryDelay = initialRetryDelay;
        _maxRetryDelay = maxRetryDelay;
        _modes = new Dictionary<string, List<QualityMode>>(StringComparer.OrdinalIgnoreCase);
        _tiers = new Dictionary<string, QualityTier>(StringComparer.OrdinalIgnoreCase);
        _modelPricing = new Dictionary<string, ModelTokenPricing>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-fetch quality modes from RedLeaf and atomically replace the in-memory snapshot.
    /// Tiers and modes are one contract: a partial response never replaces either half.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        string? tiersJson = null;
        string? modesJson = null;
        string? pricingJson = null;
        string? suiteConfigJson = null;
        try
        {
            var baseUrl = _config.RedLeafUrl.TrimEnd('/');

            tiersJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=quality-tier&limit=100", ct);
            var parsedTiers = ParseTiers(tiersJson);
            if (parsedTiers.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no quality-tier entities; keeping current snapshot", null);
                return;
            }

            modesJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=quality-mode&limit=100", ct);
            var parsed = ParseModes(modesJson, parsedTiers);
            if (parsed.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no usable quality-mode entities; keeping current snapshot", null);
                return;
            }

            try
            {
                pricingJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=inference-model&limit=200", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log($"[QualityModes] Model pricing unavailable; keeping current rates: {ex.Message}", null);
            }

            try
            {
                suiteConfigJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=suite-config&limit=1", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log($"[QualityModes] Suite default unavailable; keeping current default: {ex.Message}", null);
            }

            ApplySnapshot(parsedTiers, parsed, pricingJson, suiteConfigJson);
            _loadedFromRedLeaf = true;
            WriteCache(tiersJson, modesJson, pricingJson, suiteConfigJson);

            _log($"[QualityModes] Loaded {parsedTiers.Count} tier(s) and {parsed.Count} mode(s) from RedLeaf", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to fetch quality mode entities; keeping the current entity snapshot: {ex.Message}", null);
        }
    }

    public bool LoadedFromRedLeaf => _loadedFromRedLeaf;
    public bool LoadedFromCache => _loadedFromCache;
    public bool HasSnapshot
    {
        get { lock (_lock) { return _tiers.Count > 0 && _modes.Count > 0; } }
    }

    /// <summary>One bounded startup attempt, followed by last-known-good disk recovery.</summary>
    public async Task<bool> InitialSyncAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        if (_loadedFromRedLeaf) return true;

        if (TryLoadFromCache())
        {
            _loadedFromCache = true;
            return true;
        }

        _log("[QualityModes] No RedLeaf or cached quality catalog is available; tier-based requests will fail explicitly", null);
        return false;
    }

    /// <summary>Retry RedLeaf until the authoritative catalog has been loaded at least once.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loadedFromRedLeaf) return;

        var delay = _initialRetryDelay;
        var attempt = 0;
        while (!ct.IsCancellationRequested && !_loadedFromRedLeaf)
        {
            attempt++;
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            if (_loadedFromRedLeaf)
            {
                _log($"[QualityModes] Quality catalog recovered from RedLeaf after {attempt} attempt(s)", null);
                return;
            }

            var snapshotState = HasSnapshot ? "Serving the cached quality catalog" : "Quality catalog is empty";
            _log($"[QualityModes] {snapshotState}; RedLeaf fetch attempt {attempt} failed, retrying in {delay.TotalSeconds:0.###}s", null);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxRetryDelay.Ticks));
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
                return new ResolvedMode(pc.Slug, null, null, pc.Backend, pc.EndpointUrl, pc.ApiKey, QualityTier: tier);
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var match = candidates.FirstOrDefault(m =>
                string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase));
            if (match != null) return ToResolved(match);
            var ppc = _providerConfig.Resolve(preferredProvider);
            return new ResolvedMode(preferredProvider, null, null, ppc.Backend, ppc.EndpointUrl, ppc.ApiKey, QualityTier: tier);
        }

        var chosen = ChooseDefault(candidates);
        return ToResolved(chosen);
    }

    /// <summary>
    /// Resolve a caller-requested quality tier without silently substituting a suite default.
    /// This is the fail-closed path used by session and execution endpoints.
    /// </summary>
    public bool TryResolveRequested(
        string qualityTier,
        string? preferredProvider,
        out ResolvedMode? resolved,
        out QualityResolutionFailure failure)
    {
        resolved = null;
        failure = QualityResolutionFailure.None;

        Dictionary<string, List<QualityMode>> modes;
        Dictionary<string, QualityTier> tiers;
        lock (_lock) { modes = _modes; tiers = _tiers; }

        if (tiers.Count == 0 || modes.Count == 0)
        {
            failure = QualityResolutionFailure.CatalogUnavailable;
            return false;
        }

        var requested = qualityTier.Trim();
        if (!tiers.ContainsKey(requested)
            || !modes.TryGetValue(requested, out var candidates)
            || candidates.Count == 0)
        {
            failure = QualityResolutionFailure.UnknownTier;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var normalizedProvider = _providerConfig.ResolveReference(preferredProvider);
            var match = candidates.FirstOrDefault(m =>
                string.Equals(m.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase));
            resolved = match != null
                ? ToResolved(match)
                : Resolve(requested, normalizedProvider);
        }
        else
        {
            var chosen = ChooseDefault(candidates);
            resolved = ToResolved(chosen);
        }

        if (string.IsNullOrWhiteSpace(resolved.Model))
        {
            resolved = null;
            failure = QualityResolutionFailure.ModelUnavailable;
            return false;
        }

        return true;
    }

    bool IProviderQualityModeResolver.TryResolveRequested(
        string qualityTier,
        string? preferredProvider,
        out ProviderQualityMode? resolved)
    {
        resolved = null;
        if (!TryResolveRequested(qualityTier, preferredProvider, out var mode, out _)
            || mode?.Model is not { Length: > 0 } model)
            return false;

        resolved = new ProviderQualityMode(
            mode.Provider,
            model,
            mode.Effort,
            mode.TimeoutSeconds,
            mode.QualityTier ?? qualityTier);
        return true;
    }

    private QualityMode ChooseDefault(IReadOnlyList<QualityMode> candidates)
    {
        var suiteDefaultProvider = _providerConfig.DefaultProviderSlug;
        if (!string.IsNullOrWhiteSpace(suiteDefaultProvider))
        {
            var providerMatch = candidates.FirstOrDefault(mode =>
                string.Equals(mode.Provider, suiteDefaultProvider, StringComparison.OrdinalIgnoreCase));
            if (providerMatch != null) return providerMatch;
        }

        return candidates.FirstOrDefault(mode => mode.IsDefault) ?? candidates[0];
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
        return new(m.Provider, m.Model, m.Effort, pc.Backend, pc.EndpointUrl, pc.ApiKey,
            m.ThinkingBudget, m.QualityTier, m.Timeout, m.MaxTurns);
    }

    private void ApplySnapshot(
        IReadOnlyList<QualityTier> tiers,
        IReadOnlyList<QualityMode> modes,
        string? pricingJson,
        string? suiteConfigJson)
    {
        var tiersDict = tiers.ToDictionary(t => t.Slug, StringComparer.OrdinalIgnoreCase);
        var grouped = modes
            .GroupBy(m => m.QualityTier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var pricing = string.IsNullOrWhiteSpace(pricingJson)
            ? null
            : ParseModelPricing(pricingJson);

        string? defaultTierSlug = null;
        if (!string.IsNullOrWhiteSpace(suiteConfigJson))
        {
            var defaultTierRef = ParseDefaultTierReference(suiteConfigJson);
            defaultTierSlug = tiers.FirstOrDefault(t =>
                string.Equals(t.Id, defaultTierRef, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Slug, defaultTierRef, StringComparison.OrdinalIgnoreCase))?.Slug;
        }

        lock (_lock)
        {
            _tiers = tiersDict;
            _modes = grouped;
            if (pricing is { Count: > 0 }) _modelPricing = pricing;
            if (!string.IsNullOrWhiteSpace(defaultTierSlug)) _defaultTierSlug = defaultTierSlug;
        }
    }

    private void WriteCache(string tiersJson, string modesJson, string? pricingJson, string? suiteConfigJson)
    {
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            tempPath = _cachePath + ".tmp";
            var envelope = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                capturedAt = DateTimeOffset.UtcNow,
                tiers = tiersJson,
                modes = modesJson,
                pricing = pricingJson,
                suiteConfig = suiteConfigJson,
            });
            File.WriteAllText(tempPath, envelope);
            File.Move(tempPath, _cachePath, overwrite: true);
            _log($"[QualityModes] Last-known-good catalog written to {_cachePath}", null);
        }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to write catalog cache: {ex.Message}", null);
            try { if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private bool TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = doc.RootElement;
            var tiersJson = root.TryGetProperty("tiers", out var tiersValue) && tiersValue.ValueKind == JsonValueKind.String
                ? tiersValue.GetString() : null;
            var modesJson = root.TryGetProperty("modes", out var modesValue) && modesValue.ValueKind == JsonValueKind.String
                ? modesValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(tiersJson) || string.IsNullOrWhiteSpace(modesJson)) return false;

            var tiers = ParseTiers(tiersJson);
            var modes = ParseModes(modesJson, tiers);
            if (tiers.Count == 0 || modes.Count == 0) return false;

            var pricingJson = root.TryGetProperty("pricing", out var pricingValue) && pricingValue.ValueKind == JsonValueKind.String
                ? pricingValue.GetString() : null;
            var suiteConfigJson = root.TryGetProperty("suiteConfig", out var suiteValue) && suiteValue.ValueKind == JsonValueKind.String
                ? suiteValue.GetString() : null;
            ApplySnapshot(tiers, modes, pricingJson, suiteConfigJson);
            _log($"[QualityModes] Loaded {tiers.Count} tier(s) and {modes.Count} mode(s) from last-known-good cache", null);
            return true;
        }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to read catalog cache: {ex.Message}", null);
            return false;
        }
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

                    var modelId = GetString(data, "model_id");
                    var input = GetDouble(data, "cost_input");
                    var output = GetDouble(data, "cost_output");
                    if (!input.HasValue || !output.HasValue) continue;

                    // GPT-5.6 cache reads have a documented 90% discount. Early installed
                    // provider-codex catalogs predate cost_cached_input even though their input
                    // and output rates are valid; accepting that legacy shape keeps API-equivalent
                    // subscription estimates alive until the plugin seed is refreshed. An explicit
                    // entity value always wins.
                    var cached = GetDouble(data, "cost_cached_input")
                        ?? InferCachedInputRate(modelId ?? slug, input.Value);
                    if (!cached.HasValue) continue;

                    var pricing = new ModelTokenPricing(input.Value, cached.Value, output.Value);
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

    private static double? InferCachedInputRate(string? model, double inputRate)
        => model?.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase) == true
            ? inputRate * 0.10
            : null;

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
