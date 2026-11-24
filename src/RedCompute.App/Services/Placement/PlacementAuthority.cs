using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// Stage B of the placement rollout (design 8.1, M1): a start that carries <c>placement</c> is resolved by
/// reference and the resolved values decide where it runs; legacy path fields sent beside it are only compared
/// (409 <c>placement_conflict</c>). The authority is on only while the kernel's resolve route answers (design
/// 8.3 item 4): a RedCompute with an older kernel, or one that cannot reach it, keeps stage A (the legacy fields
/// decide) and does not advertise <see cref="Feature"/>, so no start that worked before fails because of it.
/// </summary>
public sealed class PlacementAuthority
{
    /// <summary>The <c>GET /ping</c> feature naming this contract.</summary>
    public const string Feature = "placement.v1";

    /// <summary>
    /// The whole resolution: up to three RedLeaf or kernel calls (15 s each) and a branch's git (30 s with its
    /// retry) fit with room to spare, so a loaded machine slows a start down instead of failing it. A start that
    /// used to work on its legacy fields must not fail because resolving it took longer than the legacy
    /// validation did.
    /// </summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(90);

    private readonly PlacementResolver _resolver;
    private readonly Func<CancellationToken, Task<bool>>? _probe;
    private readonly Action<string, Guid?> _log;
    private readonly TimeSpan _recheckAfterAnswer;
    private readonly TimeSpan _recheckAfterFailure;
    private volatile bool _active;

    /// <param name="probe">Whether the kernel resolve route answers; null means it always does (hosts and tests with no kernel to ask).</param>
    public PlacementAuthority(PlacementResolver resolver, Func<CancellationToken, Task<bool>>? probe, Action<string, Guid?> log,
        TimeSpan? recheckAfterAnswer = null, TimeSpan? recheckAfterFailure = null)
    {
        _resolver = resolver;
        _probe = probe;
        _log = log;
        _recheckAfterAnswer = recheckAfterAnswer ?? TimeSpan.FromMinutes(5);
        _recheckAfterFailure = recheckAfterFailure ?? TimeSpan.FromSeconds(30);
        _active = probe is null;
    }

    /// <summary>True while placement decides where a start runs.</summary>
    public bool Active => _active;

    /// <summary>The features <c>GET /ping</c> lists for this authority.</summary>
    public IReadOnlyList<string> Features => _active ? [Feature] : [];

    /// <summary>Asks the kernel once (at boot, then on <see cref="RunAsync"/>'s schedule) and records the answer.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_probe is null) return;
        bool answers;
        try { answers = await _probe(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            answers = false;
        }
        if (answers != _active)
            _log(answers
                ? $"[Placement] The kernel resolves placements: {Feature} is advertised and placement is authoritative"
                : $"[Placement] The kernel does not resolve placements: {Feature} is withdrawn and the legacy fields decide", null);
        _active = answers;
    }

    /// <summary>Keeps <see cref="Active"/> current: sooner while the kernel does not answer.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct);
                await Task.Delay(_active ? _recheckAfterAnswer : _recheckAfterFailure, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The placement resolved on its node. A fault is 503 <c>placement_resolution_unavailable</c>, never a
    /// guess: the caller asked for placement, so it is told when it cannot be had.
    /// </summary>
    public async Task<Resolution<ResolvedPlacement>> ResolveAsync(PlacementRequest placement, PlacementRoute route,
        ClaimsPrincipal? caller, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResolveTimeout);
            return await _resolver.ResolveAsync(placement, route, caller, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Resolution<ResolvedPlacement>.Fail(StatusCodes.Status503ServiceUnavailable, "placement_resolution_unavailable",
                $"Placement resolution is unavailable: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 409 <c>placement_conflict</c> when a legacy path field a start sent names a different place than the
    /// placement that decides it. Only fields both sides name are compared: a placement-only start has no
    /// legacy fields, and a legacy field the placement does not decide (a placement naming only a node) is the
    /// legacy field's to decide. The answer names the fields, never the paths.
    /// </summary>
    public static PlacementError? Conflicts(ResolvedPlacement resolved, LegacyPlacement legacy, PlacementRoute route,
        IHostPathRules rules)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Check(string field, string? outcome)
        {
            if (outcome == ShadowOutcome.Mismatch) fields[field] = "differs from placement";
        }

        if (!string.IsNullOrWhiteSpace(legacy.WorkingDirectory) && resolved.WorkingDirectory is { } cwd)
            Check("cwd", PlacementShadow.ComparePaths(legacy.WorkingDirectory, cwd, rules));
        if (route == PlacementRoute.Session)
        {
            if (!string.IsNullOrWhiteSpace(legacy.RepositoryId) && resolved.RepositoryId is { } repositoryId)
                Check("repositoryId", Guid.TryParse(legacy.RepositoryId, out var sent) && sent == repositoryId
                    ? ShadowOutcome.Exact
                    : ShadowOutcome.Mismatch);
            if (!string.IsNullOrWhiteSpace(legacy.ScratchDirectory) && resolved.ScratchDirectory is { } scratch)
                Check("scratch", PlacementShadow.ComparePaths(legacy.ScratchDirectory, scratch, rules));
        }
        else
        {
            // The caller may name more folders than the scratch one: each folder the placement adds must be among them.
            if (legacy.AddDirs is { Count: > 0 } && resolved.AddDirs is not null
                && resolved.AddDirs.Any(placed => legacy.AddDirs.All(used => PlacementShadow.ComparePaths(used, placed, rules) == ShadowOutcome.Mismatch)))
                Check("addDirs", ShadowOutcome.Mismatch);
            if (resolved.ScratchEnvironment is not null && HasScratchVariable(legacy.Environment))
                Check("env", PlacementShadow.CompareEnvironment(legacy.Environment, resolved.ScratchEnvironment, rules));
        }
        return fields.Count == 0
            ? null
            : new PlacementError(StatusCodes.Status409Conflict, "placement_conflict",
                "The legacy path fields name a different place than placement does; send placement alone or make them agree",
                Fields: new Dictionary<string, string>(fields));
    }

    /// <summary>
    /// The one-shot's <c>addDirs</c>: the caller's own list with each folder the placement adds in place of its
    /// equivalent spelling, or appended when the caller did not name it.
    /// </summary>
    public static string[] MergeAddDirs(IReadOnlyList<string>? legacy, IReadOnlyList<string> placed, IHostPathRules rules)
    {
        var merged = legacy?.ToList() ?? [];
        foreach (var folder in placed)
        {
            var index = merged.FindIndex(used => PlacementShadow.ComparePaths(used, folder, rules) != ShadowOutcome.Mismatch);
            if (index >= 0) merged[index] = folder; else merged.Add(folder);
        }
        return [.. merged];
    }

    private static bool HasScratchVariable(IReadOnlyDictionary<string, string>? environment)
        => environment is not null
           && PlacementShadow.ScratchVariables.Any(name => environment.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The one-shot's environment with the resolved scratch variables laid over the caller's other variables
    /// (an existing scratch key keeps the caller's casing). Null when the placement has no scratch.
    /// </summary>
    public static Dictionary<string, string>? WithScratch(Dictionary<string, string>? environment,
        IReadOnlyDictionary<string, string>? scratch)
    {
        if (scratch is null) return environment;
        var merged = environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
        foreach (var (name, value) in scratch)
        {
            var existing = merged.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
            merged[existing ?? name] = value;
        }
        return merged;
    }
}
