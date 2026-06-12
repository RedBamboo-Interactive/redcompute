using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

/// <summary>An abstract quality tier resolved to a concrete provider + model + params.</summary>
public record QualityMode(
    string Id, string Slug, string QualityTier, string Provider,
    string Model, string? Effort, int? ThinkingBudget,
    int? Timeout, int? MaxTurns, bool IsDefault, string? Description);

/// <summary>The concrete settings a quality tier resolves to, handed to the inference backend.</summary>
public record ResolvedMode(
    string Provider, string Model, string? Effort,
    int? ThinkingBudget, int? Timeout, int? MaxTurns);

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

    public QualityModeService(RedComputeConfig config, Action<string, Guid?> log)
    {
        _config = config;
        _log = log;
        // Seed with fallbacks so Resolve() works before (and if) RedLeaf is ever reached.
        _modes = BuildFallbacks();
    }

    /// <summary>
    /// Re-fetch quality modes from RedLeaf and replace the in-memory cache. On any failure the
    /// existing cache (fallbacks or a previous successful fetch) is left untouched.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_config.RedLeafUrl.TrimEnd('/')}/api/entities?type=quality-mode&limit=100";
            var json = await _http.GetStringAsync(url, ct);

            var parsed = ParseModes(json);
            if (parsed.Count == 0)
            {
                _log("[QualityModes] RedLeaf returned no usable quality-mode entities; keeping current modes", null);
                return;
            }

            var grouped = parsed
                .GroupBy(m => m.QualityTier, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            lock (_lock) { _modes = grouped; }
            _log($"[QualityModes] Loaded {parsed.Count} quality mode(s) across {grouped.Count} tier(s) from RedLeaf", null);
        }
        catch (Exception ex)
        {
            _log($"[QualityModes] Failed to fetch quality modes from RedLeaf, using fallbacks: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Resolve a quality tier to concrete settings. A null/blank tier defaults to "standard".
    /// Prefers a mode matching <paramref name="preferredProvider"/>, then the tier's default
    /// mode, then the first mode. Falls back to standard/claude-code when the tier is unknown.
    /// </summary>
    public ResolvedMode Resolve(string? qualityTier = null, string? preferredProvider = null)
    {
        var tier = string.IsNullOrWhiteSpace(qualityTier) ? "standard" : qualityTier.Trim();

        Dictionary<string, List<QualityMode>> snapshot;
        lock (_lock) { snapshot = _modes; }

        if (snapshot.TryGetValue(tier, out var candidates) && candidates.Count > 0)
        {
            QualityMode? chosen = null;
            if (!string.IsNullOrWhiteSpace(preferredProvider))
                chosen = candidates.FirstOrDefault(m =>
                    string.Equals(m.Provider, preferredProvider, StringComparison.OrdinalIgnoreCase));
            chosen ??= candidates.FirstOrDefault(m => m.IsDefault);
            chosen ??= candidates[0];
            return ToResolved(chosen);
        }

        // Unknown tier — final safety net.
        return new ResolvedMode("claude-code", "sonnet", null, null, 60, null);
    }

    /// <summary>All known modes across every tier.</summary>
    public IReadOnlyList<QualityMode> GetAll()
    {
        lock (_lock) { return _modes.Values.SelectMany(v => v).ToList(); }
    }

    /// <summary>The set of quality tiers currently known.</summary>
    public IReadOnlyList<string> GetTiers()
    {
        lock (_lock) { return _modes.Keys.ToList(); }
    }

    private static ResolvedMode ToResolved(QualityMode m)
        => new(m.Provider, m.Model, m.Effort, m.ThinkingBudget, m.Timeout, m.MaxTurns);

    private static Dictionary<string, List<QualityMode>> BuildFallbacks()
    {
        static QualityMode M(string tier, string model, string? effort, int timeout, bool isDefault)
            => new(
                Id: $"fallback-{tier}", Slug: tier, QualityTier: tier, Provider: "claude-code",
                Model: model, Effort: effort, ThinkingBudget: null, Timeout: timeout, MaxTurns: null,
                IsDefault: isDefault, Description: $"Built-in {tier} fallback");

        return new Dictionary<string, List<QualityMode>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = [M("fast", "haiku", "low", 30, false)],
            ["standard"] = [M("standard", "sonnet", null, 60, true)],
            ["deep"] = [M("deep", "opus", "high", 120, false)],
            ["research"] = [M("research", "fable", "high", 180, false)],
        };
    }

    // ---- RedLeaf response parsing --------------------------------------------------------

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
