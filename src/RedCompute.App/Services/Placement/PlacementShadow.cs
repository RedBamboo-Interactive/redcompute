using System.Collections.Concurrent;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// What a start actually used, from its legacy fields, as the endpoint handed it to the provider: the
/// working directory (<c>projectPath</c> / <c>workingDir</c>), the scratch directory (sessions), the
/// repository id, <c>addDirs</c> and the scratch keys of <c>env</c> (execute).
/// </summary>
public sealed record LegacyPlacement(
    string? WorkingDirectory,
    string? ScratchDirectory,
    string? RepositoryId,
    IReadOnlyList<string>? AddDirs,
    IReadOnlyDictionary<string, string>? Environment);

/// <summary>How one field of a resolved placement compares with the legacy field that was used.</summary>
public static class ShadowOutcome
{
    /// <summary>The same bytes.</summary>
    public const string Exact = "exact";

    /// <summary>The same place written differently: separators, case where the node ignores it, a trailing separator, an empty list for none.</summary>
    public const string Equivalent = "equivalent";

    /// <summary>A different place, or one side names a place and the other none.</summary>
    public const string Mismatch = "mismatch";

    internal static int Rank(string outcome) => outcome switch { Exact => 0, Equivalent => 1, _ => 2 };
}

/// <summary>
/// Stage A of the placement rollout (design 8.1, Layer 3): a start that carries both its legacy fields and
/// its <c>placement</c> is resolved by reference, off the request path, and compared field by field with
/// what the legacy fields made the provider use. The legacy fields keep deciding the launch; this only
/// counts, per caller app, route and placement shape, so the flip to stage B rests on live evidence. The
/// counts survive restarts (<see cref="PlacementShadowStore"/>) and carry no paths.
/// </summary>
public sealed class PlacementShadow
{
    /// <summary>The scratch variables a caller sets from its kernel scratch (the kernel's PrepareExecution environment).</summary>
    internal static readonly string[] ScratchVariables = ["REDLEAF_SCRATCH_ROOT", "REDLEAF_SCRATCH_DIR", "TEMP", "TMP", "TMPDIR"];

    /// <summary>Beyond this many distinct (app, route, shape) paths, new ones are counted under the app <c>other</c>.</summary>
    internal const int MaxPaths = 200;

    private readonly PlacementResolver _resolver;
    private readonly PlacementShadowStore? _store;
    private readonly Action<string, Guid?> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly PlacementShadowState _state;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public PlacementShadow(PlacementResolver resolver, PlacementShadowStore? store, Action<string, Guid?> log,
        Func<DateTimeOffset>? clock = null)
    {
        _resolver = resolver;
        _store = store;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _state = store?.Load(log) ?? new PlacementShadowState();
    }

    /// <summary>
    /// Starts the comparison in the background and returns at once: resolving may wait on RedLeaf, and a start
    /// never waits on, or fails because of, its shadow. Every fault is counted, none escapes.
    /// </summary>
    public void Observe(PlacementRequest placement, PlacementRoute route, string? app, LegacyPlacement legacy,
        IHostPathRules rules)
    {
        var task = Task.Run(() => ObserveAsync(placement, route, app, legacy, rules));
        _inFlight[task] = 0;
        _ = task.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when every comparison started so far has been counted (tests, shutdown).</summary>
    internal Task IdleAsync() => Task.WhenAll(_inFlight.Keys);

    internal async Task ObserveAsync(PlacementRequest placement, PlacementRoute route, string? app, LegacyPlacement legacy,
        IHostPathRules rules)
    {
        Resolution<ResolvedPlacement> resolution;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            resolution = await _resolver.ResolveAsync(placement, route, timeout.Token);
        }
        catch (Exception ex)
        {
            resolution = Resolution<ResolvedPlacement>.Fail(503, "placement_resolution_unavailable", ex.GetType().Name);
        }

        var appName = AppName(app);
        var routeName = RouteName(route);
        var shape = Shape(placement);
        IReadOnlyDictionary<string, string>? fields = resolution.Ok ? Compare(resolution.Value!, legacy, route, rules) : null;
        lock (_gate)
        {
            var now = _clock();
            _state.Since ??= now;
            _state.UpdatedAt = now;
            var path = PathFor(appName, routeName, shape, now);
            path.Observed++;
            path.LastSeenAt = now;
            if (resolution.Error is { } error)
            {
                if (error.Status == 503) path.Unavailable++; else path.Refused++;
                path.Codes[error.Code] = path.Codes.GetValueOrDefault(error.Code) + 1;
            }
            else
            {
                path.Compared++;
                foreach (var (field, outcome) in fields!)
                {
                    if (!path.Fields.TryGetValue(field, out var counts)) path.Fields[field] = counts = new ShadowFieldCounts();
                    counts.Add(outcome);
                }
                if (fields.Values.Contains(ShadowOutcome.Mismatch)) path.LastMismatchAt = now;
            }
            try { _store?.Save(_state); }
            catch (Exception ex) { _log($"[Placement] Shadow counts not saved: {ex.Message}", null); }
        }

        // Names, codes and outcomes only: the log is no place for the paths either.
        if (resolution.Error is { } refused)
            _log($"[Placement] Shadow {appName}/{routeName} {shape}: not resolved ({refused.Code})", null);
        else if (fields!.Values.Any(outcome => outcome != ShadowOutcome.Exact))
            _log($"[Placement] Shadow {appName}/{routeName} {shape}: "
                + string.Join(", ", fields.Select(pair => $"{pair.Key} {pair.Value}")), null);
    }

    /// <summary>The counts so far, a copy (never paths).</summary>
    public PlacementShadowState Status()
    {
        lock (_gate) return _state.Copy();
    }

    // ── the comparison ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Field by field, the fields this route uses: <c>cwd</c> on both routes; <c>repositoryId</c> and
    /// <c>scratch</c> on sessions (their <c>scratchDir</c>; a session's scratch environment is built from it);
    /// <c>addDirs</c> and <c>env</c> (its scratch keys) on execute, which has no repository field. A field
    /// neither side names is left out.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Compare(ResolvedPlacement resolved, LegacyPlacement legacy,
        PlacementRoute route, IHostPathRules rules)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Path(string field, string? used, string? placed)
        {
            if (used is null && placed is null) return;
            fields[field] = ComparePaths(used, placed, rules);
        }

        Path("cwd", legacy.WorkingDirectory, resolved.WorkingDirectory);
        if (route == PlacementRoute.Session)
        {
            var legacyRepository = Guid.TryParse(legacy.RepositoryId, out var parsed) ? parsed : (Guid?)null;
            if (legacy.RepositoryId is not null || resolved.RepositoryId is not null)
                fields["repositoryId"] = legacyRepository is not null && legacyRepository == resolved.RepositoryId
                    ? string.Equals(legacy.RepositoryId, resolved.RepositoryId!.Value.ToString("D"), StringComparison.Ordinal)
                        ? ShadowOutcome.Exact
                        : ShadowOutcome.Equivalent
                    : ShadowOutcome.Mismatch;
            Path("scratch", legacy.ScratchDirectory, resolved.ScratchDirectory);
        }
        else
        {
            var addDirs = CompareLists(legacy.AddDirs, resolved.AddDirs, rules);
            if (addDirs is not null) fields["addDirs"] = addDirs;
            var env = CompareEnvironment(legacy.Environment, resolved.ScratchEnvironment, rules);
            if (env is not null) fields["env"] = env;
        }
        return fields;
    }

    internal static string ComparePaths(string? used, string? placed, IHostPathRules rules)
    {
        if (string.Equals(used, placed, StringComparison.Ordinal)) return ShadowOutcome.Exact;
        if (used is null || placed is null) return ShadowOutcome.Mismatch;
        return string.Equals(Normalize(used, rules), Normalize(placed, rules), rules.Comparison)
            ? ShadowOutcome.Equivalent
            : ShadowOutcome.Mismatch;
    }

    /// <summary>Null when neither side names a directory; an empty list and none are equivalent.</summary>
    private static string? CompareLists(IReadOnlyList<string>? used, IReadOnlyList<string>? placed, IHostPathRules rules)
    {
        if (used is null && placed is null) return null;
        if ((used?.Count ?? 0) == 0 && (placed?.Count ?? 0) == 0)
            return used is not null && placed is not null ? ShadowOutcome.Exact : ShadowOutcome.Equivalent;
        if (used is null || placed is null || used.Count != placed.Count) return ShadowOutcome.Mismatch;
        return Worst(used.Select((value, index) => ComparePaths(value, placed[index], rules)));
    }

    /// <summary>The scratch variables only (a caller's other variables are not placement); null when neither side sets one.</summary>
    private static string? CompareEnvironment(IReadOnlyDictionary<string, string>? used,
        IReadOnlyDictionary<string, string>? placed, IHostPathRules rules)
    {
        string? Value(IReadOnlyDictionary<string, string>? env, string name)
        {
            if (env is null) return null;
            foreach (var (key, value) in env)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }

        var outcomes = ScratchVariables
            .Select(name => (Used: Value(used, name), Placed: Value(placed, name)))
            .Where(pair => pair.Used is not null || pair.Placed is not null)
            .Select(pair => ComparePaths(pair.Used, pair.Placed, rules))
            .ToList();
        return outcomes.Count == 0 ? null : Worst(outcomes);
    }

    private static string Worst(IEnumerable<string> outcomes)
        => outcomes.DefaultIfEmpty(ShadowOutcome.Exact).MaxBy(ShadowOutcome.Rank)!;

    /// <summary>The node's own form of a path: its separator, its full form, no trailing separator.</summary>
    private static string Normalize(string path, IHostPathRules rules)
    {
        var separator = rules.DirectorySeparator;
        var value = separator == '\\' ? path.Replace('/', '\\') : path;
        try
        {
            if (rules.IsPathFullyQualified(value)) value = rules.GetFullPath(value);
        }
        catch
        {
            // Not a path the node accepts: compared as written.
        }
        var trimmed = value.TrimEnd(separator);
        // A root keeps its separator (C:\, /).
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? value : trimmed;
    }

    // ── the keys ───────────────────────────────────────────────────────────────────────────────────

    internal static string RouteName(PlacementRoute route) => route switch
    {
        PlacementRoute.Session => "sessions",
        PlacementRoute.Execute => "execute",
        _ => "generate",
    };

    /// <summary>The placement's shape, which with the app and route names a creation path: references only.</summary>
    internal static string Shape(PlacementRequest placement)
    {
        var parts = new List<string>();
        if (placement.Workspace is not null) parts.Add("workspace");
        if (placement.Repository is { } repository)
            parts.Add("repository"
                + (repository.CheckoutId is not null ? "+checkout" : "")
                + (repository.Branch is not null ? "+branch" : "")
                + (repository.SubPath is not null ? "+subPath" : ""));
        if (placement.Scratch is not null) parts.Add($"scratch({placement.ScratchAs})");
        return parts.Count == 0 ? "node" : string.Join("+", parts);
    }

    /// <summary>The caller app's id from the job's provenance, kept to a short identifier.</summary>
    internal static string AppName(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return "unknown";
        var trimmed = app.Trim();
        if (trimmed.Length > 64
            || trimmed.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            return "unknown";
        return trimmed;
    }

    private ShadowPathCounts PathFor(string app, string route, string shape, DateTimeOffset now)
    {
        var path = _state.Paths.FirstOrDefault(p => p.App == app && p.Route == route && p.Shape == shape);
        if (path is not null) return path;
        if (_state.Paths.Count >= MaxPaths)
        {
            app = "other";
            path = _state.Paths.FirstOrDefault(p => p.App == app && p.Route == route && p.Shape == shape);
            if (path is not null) return path;
        }
        path = new ShadowPathCounts { App = app, Route = route, Shape = shape, FirstSeenAt = now };
        _state.Paths.Add(path);
        return path;
    }
}

/// <summary>
/// <c>GET /placement/shadow-status</c>: since when the shadow has counted, and per creation path (caller app,
/// route, placement shape) how often it was seen, resolved, unavailable or refused, and how each field compared.
/// </summary>
public sealed class PlacementShadowState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<ShadowPathCounts> Paths { get; set; } = [];

    /// <summary>Sums over every path.</summary>
    public ShadowTotals Totals => new(
        Paths.Sum(p => p.Observed), Paths.Sum(p => p.Compared), Paths.Sum(p => p.Unavailable), Paths.Sum(p => p.Refused),
        Paths.Count(p => p.Fields.Values.Any(f => f.Mismatch > 0) || p.Refused > 0),
        Paths.Sum(p => p.Fields.Values.Sum(f => f.Exact)),
        Paths.Sum(p => p.Fields.Values.Sum(f => f.Equivalent)),
        Paths.Sum(p => p.Fields.Values.Sum(f => f.Mismatch)));

    internal PlacementShadowState Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        Since = Since,
        UpdatedAt = UpdatedAt,
        Paths = Paths
            .OrderBy(p => p.App, StringComparer.Ordinal).ThenBy(p => p.Route, StringComparer.Ordinal)
            .ThenBy(p => p.Shape, StringComparer.Ordinal)
            .Select(p => p.Copy()).ToList(),
    };
}

/// <param name="PathsWithDisagreement">Paths with at least one field mismatch or one refused resolution.</param>
public sealed record ShadowTotals(long Observed, long Compared, long Unavailable, long Refused, int PathsWithDisagreement,
    long Exact, long Equivalent, long Mismatch);

/// <summary>One creation path's counts.</summary>
public sealed class ShadowPathCounts
{
    public string App { get; set; } = "";
    public string Route { get; set; } = "";
    public string Shape { get; set; } = "";
    /// <summary>Starts that carried this placement.</summary>
    public long Observed { get; set; }
    /// <summary>Resolved and compared.</summary>
    public long Compared { get; set; }
    /// <summary>Not resolved because a resolver could not answer (503): says nothing about the placement.</summary>
    public long Unavailable { get; set; }
    /// <summary>Not resolved because the placement was refused (404, 409, 422): legacy worked, placement would not have.</summary>
    public long Refused { get; set; }
    /// <summary>Resolution error codes and how often each was seen.</summary>
    public SortedDictionary<string, long> Codes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, ShadowFieldCounts> Fields { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastMismatchAt { get; set; }

    internal ShadowPathCounts Copy() => new()
    {
        App = App, Route = Route, Shape = Shape, Observed = Observed, Compared = Compared, Unavailable = Unavailable,
        Refused = Refused, Codes = new(Codes, StringComparer.Ordinal),
        Fields = new(Fields.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()), StringComparer.Ordinal),
        FirstSeenAt = FirstSeenAt, LastSeenAt = LastSeenAt, LastMismatchAt = LastMismatchAt,
    };
}

public sealed class ShadowFieldCounts
{
    public long Exact { get; set; }
    public long Equivalent { get; set; }
    public long Mismatch { get; set; }

    internal void Add(string outcome)
    {
        switch (outcome)
        {
            case ShadowOutcome.Exact: Exact++; break;
            case ShadowOutcome.Equivalent: Equivalent++; break;
            default: Mismatch++; break;
        }
    }

    internal ShadowFieldCounts Copy() => new() { Exact = Exact, Equivalent = Equivalent, Mismatch = Mismatch };
}
