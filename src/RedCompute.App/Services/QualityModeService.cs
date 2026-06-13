using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

/// <summary>An abstract quality tier with display properties.</summary>
public record QualityTier(string Slug, string Label, string Color, string Icon, int SortOrder);

/// <summary>An abstract quality tier resolved to a concrete provider + model + params.</summary>
public record QualityMode(
    string Id, string Slug, string QualityTier, string Provider,
    string Model, string? Effort, int? ThinkingBudget,
    int? Timeout, int? MaxTurns, bool IsDefault, string? Description);

/// <summary>
/// The concrete settings a quality tier resolves to, handed to the inference backend.
/// A null model means the tier has no mode for the requested provider — the provider's
/// own default model applies.
/// </summary>
public record ResolvedMode(string Provider, string? Model, string? Effort);

/// <summary>
/// Resolves abstract quality tiers (fast, standard, deep, research) to provider-specific
/// model + params for the whole Red Suite. Modes are defined as RedLeaf entities
/// (type=quality-mode) and fetched at startup; hardcoded fallbacks keep resolution working
/// when RedLeaf is offline.
/// </summary>
public class QualityModeService
{
    private readonly RedComputeConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private Dictionary<string, List<QualityMode>> _modes;
    private Dictionary<string, QualityTier> _tiers;

    public QualityModeService(RedComputeConfig config, Action<string, Guid?> log)
    {
        _config = config;
        _log = log;
        // Seed with fallbacks so Resolve() works before (and if) RedLeaf is ever reached.
        _modes = BuildFallbacks();
        _tiers = BuildTierFallbacks();
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

            var modesJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=quality-mode&limit=100", ct);
            var parsed = ParseModes(modesJson);
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
        }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to fetch quality modes from RedLeaf, using fallbacks: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Resolve a quality tier to concrete settings. A null/blank tier defaults to "standard".
    /// When <paramref name="preferredProvider"/> is given, only that provider's modes are
    /// considered — if the tier has none for it, the model is left unset so the provider's own
    /// default applies (never another provider's model). An unknown tier resolves as "standard".
    /// </summary>
    public ResolvedMode Resolve(string? qualityTier = null, string? preferredProvider = null)
    {
        var tier = string.IsNullOrWhiteSpace(qualityTier) ? "standard" : qualityTier.Trim();

        Dictionary<string, List<QualityMode>> snapshot;
        lock (_lock) { snapshot = _modes; }

        if (!snapshot.TryGetValue(tier, out var candidates) || candidates.Count == 0)
        {
            _log($"[QualityModes] Unknown quality tier '{tier}' requested; resolving as standard", null);
            if (!snapshot.TryGetValue("standard", out candidates) || candidates.Count == 0)
                return string.IsNullOrWhiteSpace(preferredProvider)
                    ? new ResolvedMode("claude-code", "sonnet", null)
                    : new ResolvedMode(preferredProvider, null, null);
        }

        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var match = candidates.FirstOrDefault(m =>
                string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase));
            return match != null ? ToResolved(match) : new ResolvedMode(preferredProvider, null, null);
        }

        var chosen = candidates.FirstOrDefault(m => m.IsDefault) ?? candidates[0];
        return ToResolved(chosen);
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

    private static ResolvedMode ToResolved(QualityMode m)
        => new(m.Provider, m.Model, m.Effort);

    private static Dictionary<string, List<QualityMode>> BuildFallbacks()
    {
        static QualityMode M(string tier, string model, string? effort, bool isDefault)
            => new(
                Id: $"fallback-{tier}", Slug: tier, QualityTier: tier, Provider: "claude-code",
                Model: model, Effort: effort, ThinkingBudget: null, Timeout: null, MaxTurns: null,
                IsDefault: isDefault, Description: $"Built-in {tier} fallback");

        return new Dictionary<string, List<QualityMode>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = [M("fast", "haiku", "low", false)],
            ["standard"] = [M("standard", "sonnet", null, true)],
            ["deep"] = [M("deep", "opus", "high", false)],
            ["research"] = [M("research", "fable", "high", false)],
        };
    }

    private static Dictionary<string, QualityTier> BuildTierFallbacks() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"]     = new("fast",     "Fast",     "#22d3ee", "fa-solid fa-rabbit",     0),
            ["standard"] = new("standard", "Standard", "#a78bfa", "fa-solid fa-bolt",       1),
            ["deep"]     = new("deep",     "Deep",     "#fb923c", "fa-solid fa-brain",      2),
            ["research"] = new("research", "Research", "#f43f5e", "fa-solid fa-microscope", 3),
        };

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
                    var color    = GetString(data, "color") ?? "#a78bfa";
                    var icon     = GetString(data, "icon")  ?? "fa-solid fa-bolt";
                    var sortOrder = GetInt(data, "sort_order") ?? GetInt(data, "sortOrder") ?? 99;

                    result.Add(new QualityTier(slug!, label, color, icon, sortOrder));
                }
                catch (JsonException) { }
                finally { dataDoc?.Dispose(); }
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static List<QualityMode> ParseModes(string json)
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
            if (mode != null) result.Add(mode);
        }
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
                Description: GetString(data, "description"));
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
}
