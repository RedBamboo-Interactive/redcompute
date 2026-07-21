using System.IO;
using System.Net.Http;
using System.Text.Json;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public record ProviderEntityConfig(
    string Id, string Slug, string Name, string Backend,
    string? Icon, string? EndpointUrl, string? ApiKey, string? DefaultModel,
    string Status, string? Description)
{
    public string? ProviderType { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public JsonElement? Settings { get; init; }
}

/// <summary>
/// Authoritative source for provider configuration. Fetches provider entities from RedLeaf
/// (type=provider) at startup and builds ProviderConfig objects from entity data.settings.
/// Falls back to config.json values for any field not present in the entity, and to a
/// read-only entity-cache.json when RedLeaf is unreachable at boot.
/// </summary>
public class ProviderConfigService
{
    private readonly RedComputeConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute");
    private static readonly string CachePath = Path.Combine(CacheDir, "entity-cache.json");

    private readonly object _lock = new();
    private Dictionary<string, ProviderEntityConfig> _providers;
    private string? _defaultProviderSlug;

    // Kernel capability slug -> Compute capability slug (capability entity's
    // compute_slug). A null value means the capability is known but has no compute
    // backing (e.g. "entities", "auth") and must never key into config.Capabilities.
    // Provider entities list KERNEL slugs (ai-inference, image-generation) while
    // config.Capabilities is keyed by COMPUTE slugs (ai-session, image-gen).
    private Dictionary<string, string?> _capabilitySlugMap = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _loadedFromRedLeaf;
    private volatile bool _loadedFromCache;

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
        string? providersJson = null;
        string? suiteConfigJson = null;
        string? capabilitiesJson = null;
        try
        {
            var baseUrl = _config.RedLeafUrl.TrimEnd('/');

            providersJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=provider&limit=100", ct);
            var parsed = ParseProviders(providersJson);
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

            try
            {
                suiteConfigJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=suite-config&limit=1", ct);
                var defaultSlug = ParseDefaultProviderFromSuiteConfig(suiteConfigJson, parsed);
                if (defaultSlug != null)
                {
                    lock (_lock) { _defaultProviderSlug = defaultSlug; }
                    _log($"[ProviderConfig] Default provider from suite-config: {defaultSlug}", null);
                }
            }
            catch { /* suite-config is optional */ }

            try
            {
                capabilitiesJson = await _http.GetStringAsync($"{baseUrl}/api/entities?type=capability&limit=100", ct);
                var map = ParseCapabilitySlugMap(capabilitiesJson);
                if (map.Count > 0)
                    lock (_lock) { _capabilitySlugMap = map; }
            }
            catch { /* mapping is best-effort; ApplyToConfig only trusts explicit entries */ }

            if (_loadedFromRedLeaf)
                WriteCache(providersJson!, suiteConfigJson, capabilitiesJson);
        }
        catch (Exception ex)
        {
            _log($"[ProviderConfig] Failed to fetch providers from RedLeaf, using fallbacks: {ex.Message}", null);
        }
    }

    public bool LoadedFromRedLeaf => _loadedFromRedLeaf;
    public bool LoadedFromCache => _loadedFromCache;

    /// <summary>
    /// Single blocking attempt at entity sync for startup. Tries RedLeaf first, then
    /// falls back to the entity cache. Returns true if either source provided data.
    /// </summary>
    public async Task<bool> InitialSyncAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        if (_loadedFromRedLeaf) return true;

        if (TryLoadFromCache())
        {
            _loadedFromCache = true;
            return true;
        }

        _log("[ProviderConfig] No entity data available (RedLeaf offline, no cache); using config.json fallbacks", null);
        return false;
    }

    /// <summary>
    /// Background retry loop — keeps trying until RedLeaf is reached at least once.
    /// Safe to fire-and-forget after InitialSyncAsync.
    ///
    /// Deliberate limitation (audit W10): a late first success refreshes the provider
    /// entity snapshot but does NOT re-run ApplyToConfig — capability provider configs
    /// keep their boot values (cache or config.json) until the next restart. Mutating
    /// ProviderConfig objects that running backends already hold (serverPath, ports)
    /// mid-flight is riskier than one boot of possibly-stale settings; kernel-relayed
    /// settings PUTs still apply live edits in the meantime.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loadedFromRedLeaf) return;

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

            var source = _loadedFromCache ? "cache" : "fallback";
            _log($"[ProviderConfig] Serving {_providers.Count} {source} provider(s); " +
                 $"RedLeaf fetch not yet succeeded (attempt {attempt}), retrying in {delay.TotalSeconds:0}s", null);

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
    }

    /// <summary>
    /// Overlay entity-derived provider settings onto the in-memory config. Entity values
    /// win; config.json values fill any gaps. Call after InitialSyncAsync and before
    /// InitializeCapabilities.
    /// </summary>
    public void ApplyToConfig(RedComputeConfig config)
    {
        Dictionary<string, ProviderEntityConfig> snapshot;
        Dictionary<string, string?> slugMap;
        lock (_lock) { snapshot = _providers; slugMap = _capabilitySlugMap; }

        foreach (var entity in snapshot.Values)
        {
            if (entity.Capabilities.Count == 0 || entity.Settings == null) continue;

            foreach (var kernelSlug in entity.Capabilities)
            {
                // Entities carry kernel capability slugs; config.Capabilities is keyed
                // by compute slugs. Translate via the capability entities' compute_slug.
                var mapped = slugMap.TryGetValue(kernelSlug, out var mappedSlug);
                if (mapped && mappedSlug == null) continue; // known non-compute capability
                var capSlug = mapped ? mappedSlug! : kernelSlug;

                if (!config.Capabilities.TryGetValue(capSlug, out var cap))
                {
                    // Without an explicit mapping, an unknown slug must not fabricate a
                    // capability -- an untranslated kernel slug (ai-inference) would
                    // otherwise become a phantom entry shadowing the real one (ai-session).
                    if (!mapped) continue;
                    cap = new CapabilityConfig();
                    config.Capabilities[capSlug] = cap;
                }

                var entityType = entity.ProviderType ?? entity.Backend;
                var providerName = FindMatchingProviderName(cap, entityType, entity);
                if (providerName == null)
                {
                    providerName = entity.Slug;
                    cap.Providers[providerName] = new ProviderConfig { Type = entityType };
                }

                var provider = cap.Providers[providerName];
                ApplyEntityToProvider(entity, provider);
            }
        }
    }

    /// <summary>
    /// Resolve a provider reference for a capability to the config provider name.
    /// Accepts either the config name itself (local-wsl) or a provider entity slug
    /// (tts-local): the kernel keys its settings relays by entity slug, which need
    /// not match Compute's provider names. Returns null when neither resolves.
    /// </summary>
    public string? ResolveProviderName(CapabilityConfig cap, string reference)
    {
        if (cap.Providers.ContainsKey(reference)) return reference;

        Dictionary<string, ProviderEntityConfig> snapshot;
        lock (_lock) { snapshot = _providers; }
        if (!snapshot.TryGetValue(reference, out var entity)) return null;

        var entityType = entity.ProviderType ?? entity.Backend;
        return FindMatchingProviderName(cap, entityType, entity);
    }

    public ProviderEntityConfig Resolve(string slug)
    {
        Dictionary<string, ProviderEntityConfig> snapshot;
        lock (_lock) { snapshot = _providers; }

        if (snapshot.TryGetValue(slug, out var exact)) return exact;

        if (AliasMap.TryGetValue(slug, out var aliasSlug) && snapshot.TryGetValue(aliasSlug, out var aliased))
            return aliased;

        return new ProviderEntityConfig(slug, slug, slug, slug, "ph-fill ph-plug", null, null, null, "active", null);
    }

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
            "ph-fill ph-sparkle", null, null, null, "active", "Default Anthropic provider via Claude Code CLI.");
    }

    public string DefaultProviderSlug
    {
        get { lock (_lock) { return _defaultProviderSlug ?? "anthropic-direct"; } }
    }

    public IReadOnlyList<ProviderEntityConfig> GetAll()
    {
        lock (_lock) { return _providers.Values.Where(p => p.Status == "active").ToList(); }
    }

    // ---- entity → ProviderConfig mapping -------------------------------------------------------

    private static string? FindMatchingProviderName(CapabilityConfig cap, string entityType, ProviderEntityConfig entity)
    {
        var matches = cap.Providers
            .Where(p => string.Equals(p.Value.Type, entityType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0].Key;
        if (matches.Count == 0) return null;

        // Multiple providers of the same type — disambiguate by backendPort
        if (entity.Settings is { } settings)
        {
            var entityPort = GetSettingInt(settings, "backendPort");
            if (entityPort.HasValue)
            {
                var portMatch = matches.FirstOrDefault(m => m.Value.BackendPort == entityPort.Value);
                if (portMatch.Key != null) return portMatch.Key;
            }
        }

        return matches[0].Key;
    }

    private static void ApplyEntityToProvider(ProviderEntityConfig entity, ProviderConfig provider)
    {
        if (entity.ProviderType != null) provider.Type = entity.ProviderType;
        if (entity.ApiKey != null) provider.ApiKey = entity.ApiKey;

        if (entity.Settings is not { } settings) return;

        if (GetSettingString(settings, "serverPath") is { } sp) provider.ServerPath = sp;
        if (GetSettingString(settings, "venvPath") is { } vp) provider.VenvPath = vp;
        if (GetSettingInt(settings, "backendPort") is { } bp) provider.BackendPort = bp;
        if (GetSettingString(settings, "wslDistro") is { } wd) provider.WslDistro = wd;
        if (GetSettingString(settings, "model") is { } m) provider.Model = m;
        else if (entity.DefaultModel != null) provider.Model = entity.DefaultModel;
        if (GetSettingString(settings, "voicesBasePath") is { } vbp) provider.VoicesBasePath = vbp;
        if (GetSettingString(settings, "healthEndpoint") is { } he) provider.HealthEndpoint = he;
        if (GetSettingInt(settings, "startupTimeoutSeconds") is { } sts) provider.StartupTimeoutSeconds = sts;
        if (GetSettingString(settings, "apiKey") is { } ak) provider.ApiKey = ak;
        if (GetSettingString(settings, "podId") is { } pi) provider.PodId = pi;
        if (GetSettingInt(settings, "gpuCount") is { } gc) provider.GpuCount = gc;
        if (GetSettingBool(settings, "autoStopOnExit") is { } ase) provider.AutoStopOnExit = ase;

        if (settings.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            provider.Extra ??= new Dictionary<string, object?>();
            foreach (var prop in extra.EnumerateObject())
                provider.Extra[prop.Name] = ExtractJsonValue(prop.Value);
        }

        // Any settings keys not in the known set go to Extra
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serverPath", "venvPath", "backendPort", "wslDistro", "model",
            "voicesBasePath", "healthEndpoint", "startupTimeoutSeconds",
            "apiKey", "podId", "gpuCount", "autoStopOnExit", "extra"
        };
        foreach (var prop in settings.EnumerateObject())
        {
            if (knownKeys.Contains(prop.Name)) continue;
            provider.Extra ??= new Dictionary<string, object?>();
            provider.Extra[prop.Name] = ExtractJsonValue(prop.Value);
        }
    }

    private static object? ExtractJsonValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt32(out var n) => n,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    private static string? GetSettingString(JsonElement settings, string key)
        => settings.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetSettingInt(JsonElement settings, string key)
    {
        if (!settings.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static bool? GetSettingBool(JsonElement settings, string key)
    {
        if (!settings.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    // ---- entity cache --------------------------------------------------------------------------

    private void WriteCache(string providersJson, string? suiteConfigJson, string? capabilitiesJson)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var envelope = JsonSerializer.Serialize(new { providers = providersJson, suiteConfig = suiteConfigJson, capabilities = capabilitiesJson });
            File.WriteAllText(CachePath, envelope);
            _log($"[ProviderConfig] Entity cache written to {CachePath}", null);
        }
        catch (Exception ex)
        {
            _log($"[ProviderConfig] Failed to write entity cache: {ex.Message}", null);
        }
    }

    private bool TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;

            var cacheJson = File.ReadAllText(CachePath);
            using var doc = JsonDocument.Parse(cacheJson);

            var providersRaw = doc.RootElement.TryGetProperty("providers", out var pv) && pv.ValueKind == JsonValueKind.String
                ? pv.GetString() : null;
            if (string.IsNullOrWhiteSpace(providersRaw)) return false;

            var parsed = ParseProviders(providersRaw);
            if (parsed.Count == 0) return false;

            var dict = parsed.ToDictionary(p => p.Slug, StringComparer.OrdinalIgnoreCase);
            lock (_lock) { _providers = dict; }

            var suiteConfigRaw = doc.RootElement.TryGetProperty("suiteConfig", out var sv) && sv.ValueKind == JsonValueKind.String
                ? sv.GetString() : null;
            if (suiteConfigRaw != null)
            {
                var slug = ParseDefaultProviderFromSuiteConfig(suiteConfigRaw, parsed);
                if (slug != null)
                    lock (_lock) { _defaultProviderSlug = slug; }
            }

            var capabilitiesRaw = doc.RootElement.TryGetProperty("capabilities", out var cv) && cv.ValueKind == JsonValueKind.String
                ? cv.GetString() : null;
            if (capabilitiesRaw != null)
            {
                var map = ParseCapabilitySlugMap(capabilitiesRaw);
                if (map.Count > 0)
                    lock (_lock) { _capabilitySlugMap = map; }
            }

            _log($"[ProviderConfig] Loaded {parsed.Count} provider(s) from entity cache", null);
            return true;
        }
        catch (Exception ex)
        {
            _log($"[ProviderConfig] Failed to read entity cache: {ex.Message}", null);
            return false;
        }
    }

    // ---- fallbacks -----------------------------------------------------------------------------

    private static Dictionary<string, ProviderEntityConfig> BuildFallbacks() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-direct"] = new("anthropic-direct", "anthropic-direct", "Anthropic (Direct)", "claude-code",
                "ph-fill ph-sparkle", null, null, null, "active", "Default Anthropic provider via Claude Code CLI."),
            ["opencode-default"] = new("opencode-default", "opencode-default", "OpenCode (Default)", "opencode",
                "ph-fill ph-open-ai-logo", null, null, "gpt-4o", "active", null),
        };

    // ---- suite-config parsing ------------------------------------------------------------------

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

                var match = providers.FirstOrDefault(p =>
                    string.Equals(p.Id, refValue, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Slug;

                match = providers.FirstOrDefault(p =>
                    string.Equals(p.Slug, refValue, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Slug;
            }
        }
        catch { }
        return null;
    }

    // ---- RedLeaf response parsing --------------------------------------------------------------

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

            JsonElement? settings = null;
            if (data.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                settings = s.Clone();

            return new ProviderEntityConfig(
                Id:           id,
                Slug:         slug!,
                Name:         name!,
                Backend:      backend!,
                Icon:         GetString(data, "icon") ?? "ph-fill ph-plug",
                EndpointUrl:  GetString(data, "endpoint_url"),
                ApiKey:       GetString(data, "api_key"),
                DefaultModel: GetString(data, "default_model"),
                Status:       GetString(data, "status") ?? "active",
                Description:  GetString(data, "description"))
            {
                ProviderType = GetString(data, "provider_type") ?? GetString(data, "kind"),
                Capabilities = ParseCapabilities(data),
                Settings = settings,
            };
        }
        catch (JsonException) { return null; }
        finally { dataDoc?.Dispose(); }
    }

    private static IReadOnlyList<string> ParseCapabilities(JsonElement data)
    {
        if (!data.TryGetProperty("capabilities", out var caps))
            return Array.Empty<string>();

        if (caps.ValueKind == JsonValueKind.String)
        {
            var s = caps.GetString();
            return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : [s];
        }

        if (caps.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var el in caps.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                var s = GetString(el, "slug") ?? GetString(el, "name");
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
        }
        return result;
    }

    /// <summary>
    /// Builds kernel-slug -> compute-slug from capability entities. Entries whose
    /// entity has no compute_slug map to null: the capability exists in the kernel
    /// but has no Compute backing, so it must never be created in config.Capabilities.
    /// </summary>
    private static Dictionary<string, string?> ParseCapabilitySlugMap(string json)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array) array = root;
            else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array)) return map;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var slug = GetString(item, "slug");
                if (string.IsNullOrWhiteSpace(slug)) continue;

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

                var computeSlug = GetString(data, "compute_slug");
                map[slug] = string.IsNullOrWhiteSpace(computeSlug) ? null : computeSlug;
            }
        }
        catch (JsonException) { }
        return map;
    }

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
