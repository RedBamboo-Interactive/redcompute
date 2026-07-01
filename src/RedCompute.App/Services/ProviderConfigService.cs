using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public record ProviderEntityConfig(
    string Id, string Slug, string Name, string Backend,
    string? Icon, string? EndpointUrl, string? ApiKey, string? DefaultModel,
    string Status, string? Description);

/// <summary>
/// Resolves RedLeaf provider entities (type=provider) to concrete backend configs.
/// Fetched at startup from RedLeaf; hardcoded fallbacks keep resolution working offline.
/// Default provider is read from the suite-config entity, not from a boolean on providers.
/// </summary>
public class ProviderConfigService
{
    private readonly RedComputeConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private Dictionary<string, ProviderEntityConfig> _providers;
    private string? _defaultProviderSlug;

    // True once a non-empty provider list has been fetched from RedLeaf at least once.
    // Until then we are serving the hardcoded fallbacks and any provider outside that
    // set (e.g. a custom Meta/OpenCode endpoint) will 404 at resolution time.
    private volatile bool _loadedFromRedLeaf;

    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-code"] = "anthropic-direct",
        ["opencode"]    = "opencode-default",
    };

    public ProviderConfigService(RedComputeConfig config, Action<string, Guid?> log)
    {
        _config = config;
        _log = log;
        _providers = BuildFallbacks();
        _defaultProviderSlug = "anthropic-direct";
    }

    /// <summary>Re-fetch provider entities and suite-config from RedLeaf.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var baseUrl = _config.RedLeafUrl.TrimEnd('/');

            var json = await _http.GetStringAsync($"{baseUrl}/api/entities?type=provider&limit=100", ct);
            var parsed = ParseProviders(json);
            if (parsed.Count == 0)
            {
                _log("[ProviderConfig] RedLeaf returned no provider entities; keeping current providers", null);
            }
            else
            {
                var dict = parsed.ToDictionary(p => p.Slug, StringComparer.OrdinalIgnoreCase);
                lock (_lock) { _providers = dict; }
                _loadedFromRedLeaf = true;
                _log($"[ProviderConfig] Loaded {parsed.Count} provider(s) from RedLeaf", null);
            }

            // Read suite-config to find the default provider
            try
            {
                var configJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=suite-config&limit=1", ct);
                var defaultSlug = ParseDefaultProviderFromSuiteConfig(configJson, parsed);
                if (defaultSlug != null)
                {
                    lock (_lock) { _defaultProviderSlug = defaultSlug; }
                    _log($"[ProviderConfig] Default provider from suite-config: {defaultSlug}", null);
                }
            }
            catch { /* suite-config is optional */ }
        }
        catch (Exception ex)
        {
            _log($"[ProviderConfig] Failed to fetch providers from RedLeaf, using fallbacks: {ex.Message}", null);
        }
    }

    /// <summary>
    /// True once the real provider list has been loaded from RedLeaf. While false, the
    /// service is serving the hardcoded fallbacks only — surface this in health/status so
    /// a degraded provider list can't silently pass for the real thing.
    /// </summary>
    public bool LoadedFromRedLeaf => _loadedFromRedLeaf;

    /// <summary>
    /// Load providers from RedLeaf at startup, retrying with capped backoff until the first
    /// successful non-empty load. This is what keeps a cold-start race (RedCompute up before
    /// RedLeaf is ready) from latching onto the two-item fallback list for the rest of the
    /// process lifetime. Safe to fire-and-forget; it self-heals whenever RedLeaf comes online.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(30);
        var attempt = 0;

        while (!ct.IsCancellationRequested && !_loadedFromRedLeaf)
        {
            attempt++;
            await RefreshAsync(ct);
            if (_loadedFromRedLeaf)
            {
                if (attempt > 1)
                    _log($"[ProviderConfig] Provider list recovered from RedLeaf after {attempt} attempt(s)", null);
                return;
            }

            _log($"[ProviderConfig] DEGRADED — serving {_providers.Count} fallback provider(s) only; " +
                 $"RedLeaf fetch not yet succeeded (attempt {attempt}), retrying in {delay.TotalSeconds:0}s", null);

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
    }

    public ProviderEntityConfig Resolve(string slug)
    {
        Dictionary<string, ProviderEntityConfig> snapshot;
        lock (_lock) { snapshot = _providers; }

        if (snapshot.TryGetValue(slug, out var exact)) return exact;

        if (AliasMap.TryGetValue(slug, out var aliasSlug) && snapshot.TryGetValue(aliasSlug, out var aliased))
            return aliased;

        return new ProviderEntityConfig(slug, slug, slug, slug, "fa-solid fa-plug", null, null, null, "active", null);
    }

    /// <summary>Returns the default provider from suite-config, falling back to first active, then hardcoded.</summary>
    public ProviderEntityConfig GetDefault()
    {
        Dictionary<string, ProviderEntityConfig> snapshot;
        string? defaultSlug;
        lock (_lock) { snapshot = _providers; defaultSlug = _defaultProviderSlug; }

        if (defaultSlug != null && snapshot.TryGetValue(defaultSlug, out var configured))
            return configured;

        var first = snapshot.Values.FirstOrDefault(p => p.Status == "active");
        if (first != null) return first;

        return new ProviderEntityConfig("anthropic-direct", "anthropic-direct", "Anthropic (Direct)", "claude-code",
            "fa-solid fa-a", null, null, null, "active", "Default Anthropic provider via Claude Code CLI.");
    }

    /// <summary>The slug of the current default provider (from suite-config).</summary>
    public string DefaultProviderSlug
    {
        get { lock (_lock) { return _defaultProviderSlug ?? "anthropic-direct"; } }
    }

    public IReadOnlyList<ProviderEntityConfig> GetAll()
    {
        lock (_lock) { return _providers.Values.Where(p => p.Status == "active").ToList(); }
    }

    private static Dictionary<string, ProviderEntityConfig> BuildFallbacks() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-direct"] = new("anthropic-direct", "anthropic-direct", "Anthropic (Direct)", "claude-code",
                "fa-solid fa-a", null, null, null, "active", "Default Anthropic provider via Claude Code CLI."),
            ["opencode-default"] = new("opencode-default", "opencode-default", "OpenCode (Default)", "opencode",
                "fa-brands fa-openai", null, null, "gpt-4o", "active", null),
        };

    // ---- suite-config parsing ---------------------------------------------------------------

    /// <summary>
    /// Reads the default_provider entity_ref from suite-config. The field stores a provider
    /// entity ID (GUID), so we resolve it back to a slug via the providers list.
    /// </summary>
    private static string? ParseDefaultProviderFromSuiteConfig(string json, List<ProviderEntityConfig> providers)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array)) return null;

            foreach (var item in array.EnumerateArray())
            {
                var data = item;
                if (item.TryGetProperty("data", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Object) data = d;
                    else if (d.ValueKind == JsonValueKind.String)
                    {
                        var raw = d.GetString();
                        if (!string.IsNullOrWhiteSpace(raw))
                            using (var dd = JsonDocument.Parse(raw)) { data = dd.RootElement.Clone(); }
                    }
                }

                var refValue = GetString(data, "default_provider");
                if (string.IsNullOrWhiteSpace(refValue)) continue;

                // entity_ref stores the entity ID — find the matching provider by ID
                var match = providers.FirstOrDefault(p =>
                    string.Equals(p.Id, refValue, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Slug;

                // Maybe it's stored as a slug directly
                match = providers.FirstOrDefault(p =>
                    string.Equals(p.Slug, refValue, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Slug;
            }
        }
        catch { }
        return null;
    }

    // ---- RedLeaf response parsing -----------------------------------------------------------

    private static List<ProviderEntityConfig> ParseProviders(string json)
    {
        var result = new List<ProviderEntityConfig>();
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
                var p = ParseOne(item);
                if (p != null) result.Add(p);
            }
        }
        catch (JsonException) { }
        return result;
    }

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

    private static ProviderEntityConfig? ParseOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id   = GetString(item, "id")   ?? GetString(item, "slug") ?? "";
        var slug = GetString(item, "slug") ?? id;
        var name = GetString(item, "name") ?? slug;

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

            var backend = GetString(data, "backend");
            if (string.IsNullOrWhiteSpace(backend)) return null;

            return new ProviderEntityConfig(
                Id:           id,
                Slug:         slug!,
                Name:         name!,
                Backend:      backend!,
                Icon:         GetString(data, "icon") ?? "fa-solid fa-plug",
                EndpointUrl:  GetString(data, "endpoint_url"),
                ApiKey:       GetString(data, "api_key"),
                DefaultModel: GetString(data, "default_model"),
                Status:       GetString(data, "status") ?? "active",
                Description:  GetString(data, "description"));
        }
        catch (JsonException) { return null; }
        finally { dataDoc?.Dispose(); }
    }

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
