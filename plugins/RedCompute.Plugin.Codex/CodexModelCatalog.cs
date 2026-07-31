using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public sealed record CodexReasoningEffort(string Id, string? Description);

public sealed record CodexServiceTier(string Id, string? Name, string? Description);

public sealed record CodexModel(
    string Id,
    string DisplayName,
    string? Description,
    bool IsDefault,
    bool Hidden,
    string? DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffort> SupportedReasoningEfforts,
    IReadOnlyList<CodexServiceTier> ServiceTiers,
    IReadOnlyList<string> InputModalities)
{
    /// <summary>
    /// Always false. <c>Fast</c> means *speed*, not a smaller model — and speed is billed: Codex
    /// exposes it as the <c>priority</c> service tier, "1.5x speed, increased usage", which costs
    /// roughly double. Every model offers it, so it is a runtime choice, not a property of any one
    /// model, and it is opted into per-thread/per-turn via the <c>serviceTier</c> param on
    /// <c>thread/start</c> and <c>turn/start</c>.
    ///
    /// Flagging a model here would silently run it hot. Do not infer this from
    /// <c>defaultReasoningEffort</c> either: gpt-5.6-sol defaults to low effort because it is strong
    /// enough not to need more ("highly capable at lower reasoning efforts") — that is a capability
    /// signal, not a speed or cost one.
    /// </summary>
    public bool Fast => false;

    public bool SupportsImages => InputModalities.Contains("image");
}

/// <summary>
/// Live model catalog, read from the app-server's <c>model/list</c>.
///
/// This replaces a hardcoded array that had rotted badly: by 2026-07-31 it listed six models of
/// which none still existed, and it was used to *validate* the model parameter — so it rejected
/// every model the account could actually run. The real catalog is account-scoped and resolved
/// server-side, which is exactly why it must not be duplicated here.
/// </summary>
public sealed class CodexModelCatalog(CodexConfig config, Action<string, Guid?> log)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CodexModel>? _cached;
    private DateTimeOffset _fetchedAt;
    private string? _lastError;

    /// <summary>Last fetch failure, if the catalog is currently serving nothing.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Cache-only view, for the synchronous parts of ISessionProvider. Empty until
    /// <see cref="PrimeAsync"/> or a request has populated it — callers must tolerate that rather
    /// than blocking a request thread on a process spawn.
    /// </summary>
    public IReadOnlyList<CodexModel> Cached => _cached ?? [];

    /// <summary>Fire-and-forget warm-up, called at provider start so the model list is ready.</summary>
    public async Task PrimeAsync(CancellationToken ct = default)
    {
        try { await GetAsync(forceRefresh: true, ct); }
        catch (Exception ex) { log($"[Codex] Model catalog prime failed: {ex.Message}", null); }
    }

    public async Task<IReadOnlyList<CodexModel>> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is { Count: > 0 } && DateTimeOffset.UtcNow - _fetchedAt < CacheTtl)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is { Count: > 0 } && DateTimeOffset.UtcNow - _fetchedAt < CacheTtl)
                return _cached;

            var models = await FetchAsync(ct);
            _cached = models;
            _fetchedAt = DateTimeOffset.UtcNow;
            _lastError = null;
            return models;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            log($"[Codex] model/list failed: {ex.Message}", null);
            // Prefer a stale catalog over none — a rate limit shouldn't empty the model dropdown.
            return _cached ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The catalog's own default model id, or null when the catalog is unavailable.</summary>
    public async Task<string?> GetDefaultModelIdAsync(CancellationToken ct = default)
    {
        var models = await GetAsync(ct: ct);
        return models.FirstOrDefault(m => m.IsDefault)?.Id ?? models.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// True when the model id is one the account can actually run. Unknown ids are rejected only
    /// when we have a catalog to reject them against — if model/list is unreachable we let the
    /// request through and allow the CLI to be the judge, rather than failing closed on stale data.
    /// </summary>
    public async Task<bool> IsValidModelAsync(string modelId, CancellationToken ct = default)
    {
        var models = await GetAsync(ct: ct);
        return models.Count == 0 || models.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<string>> GetSupportedEffortsAsync(string modelId, CancellationToken ct = default)
    {
        var models = await GetAsync(ct: ct);
        var model = models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model?.SupportedReasoningEfforts.Select(e => e.Id).ToList() ?? [];
    }

    public async Task<List<ModelInfo>> ToModelInfoAsync(CancellationToken ct = default)
    {
        var models = await GetAsync(ct: ct);
        return models
            .Where(m => !m.Hidden)
            .Select(m => new ModelInfo { Id = m.Id, Name = m.DisplayName, Fast = m.Fast })
            .ToList();
    }

    private async Task<IReadOnlyList<CodexModel>> FetchAsync(CancellationToken ct)
    {
        await using var conn = await CodexAppServerConnection.StartAsync(
            config.CodexPath, workingDirectory: null, log, env: null, ct);

        var result = await conn.SendRequestAsync("model/list", new { }, timeoutSeconds: 30, ct);
        return Parse(result);
    }

    internal static IReadOnlyList<CodexModel> Parse(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<CodexModel>();
        foreach (var m in data.EnumerateArray())
        {
            var id = Str(m, "id");
            if (string.IsNullOrEmpty(id)) continue;

            models.Add(new CodexModel(
                Id: id,
                DisplayName: Str(m, "displayName") ?? id,
                Description: Str(m, "description"),
                IsDefault: Bool(m, "isDefault"),
                Hidden: Bool(m, "hidden"),
                DefaultReasoningEffort: Str(m, "defaultReasoningEffort"),
                SupportedReasoningEfforts: ParseEfforts(m),
                ServiceTiers: ParseTiers(m),
                InputModalities: ParseModalities(m)));
        }
        return models;
    }

    private static IReadOnlyList<CodexReasoningEffort> ParseEfforts(JsonElement m)
    {
        if (!m.TryGetProperty("supportedReasoningEfforts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var efforts = new List<CodexReasoningEffort>();
        foreach (var e in arr.EnumerateArray())
        {
            // Tolerate both the object form and a bare string, in case the shape is simplified later.
            if (e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s)) efforts.Add(new CodexReasoningEffort(s, null));
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                var id = Str(e, "reasoningEffort");
                if (!string.IsNullOrEmpty(id)) efforts.Add(new CodexReasoningEffort(id, Str(e, "description")));
            }
        }
        return efforts;
    }

    private static IReadOnlyList<CodexServiceTier> ParseTiers(JsonElement m)
    {
        if (!m.TryGetProperty("serviceTiers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object)
            .Select(t => new CodexServiceTier(Str(t, "id") ?? "", Str(t, "name"), Str(t, "description")))
            .Where(t => t.Id.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<string> ParseModalities(JsonElement m)
    {
        if (!m.TryGetProperty("inputModalities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ["text"];

        var list = arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToList();
        return list.Count > 0 ? list : ["text"];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
