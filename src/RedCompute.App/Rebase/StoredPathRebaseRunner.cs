using System.IO;
using System.Text.Json;
using RedCompute.Core.Storage;

namespace RedCompute.App.Rebase;

/// <summary>States of <see cref="RebaseStatus.State"/>.</summary>
public static class RebaseStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>One owner's line in <see cref="RebaseStatus"/>.</summary>
public sealed record RebaseOwnerStatus(string Owner, string State, int Examined, int Changed, int Skipped, string? Error);

/// <summary>
/// What <c>GET /storage/rebase-status</c> answers. <see cref="Generation"/> is the generation of the map this
/// process was given, or the last one it completed when it was given none.
/// </summary>
public sealed record RebaseStatus(long Generation, string State, IReadOnlyList<RebaseOwnerStatus> Owners);

/// <summary>
/// Runs every owner's rebaser once per generation, before any service opens the stores. The completed
/// generation is recorded in the data root (<c>rebase-state.json</c>), so a second start with the same map
/// does nothing. A failed owner leaves the generation incomplete: the next start runs everything again,
/// which is safe because every rebaser is idempotent.
/// </summary>
public sealed class StoredPathRebaseRunner
{
    public const string StateFileName = "rebase-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _statePath;
    private readonly IReadOnlyList<IStoredPathRebaser> _rebasers;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private RebaseStatus _status;

    public StoredPathRebaseRunner(ComputeStorage storage, IEnumerable<IStoredPathRebaser> rebasers, Action<string> log)
    {
        _statePath = Path.Combine(storage.DataRoot, StateFileName);
        _rebasers = rebasers.ToArray();
        _log = log;
        _status = ReadRecorded() ?? new RebaseStatus(0, RebaseStates.Completed, []);
    }

    /// <summary>The status now; safe to read from request threads.</summary>
    public RebaseStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>
    /// Applies <paramref name="map"/> (null: nothing to do). Returns the final status. Never throws for an
    /// owner's failure: that owner is reported failed and the others still run.
    /// </summary>
    public RebaseStatus Run(RebaseMap? map)
    {
        if (map is null) return Status;

        var recorded = ReadRecorded();
        if (recorded is { State: RebaseStates.Completed } && recorded.Generation >= map.Generation)
        {
            _log($"[Rebase] Generation {map.Generation} already completed; nothing to do");
            Set(recorded with { Generation = Math.Max(recorded.Generation, map.Generation) });
            return Status;
        }

        var owners = _rebasers.Select(r => new RebaseOwnerStatus(r.Owner, RebaseStates.Pending, 0, 0, 0, null)).ToList();
        Set(new RebaseStatus(map.Generation, RebaseStates.Pending, owners));
        Set(new RebaseStatus(map.Generation, RebaseStates.Running, owners));

        for (var i = 0; i < _rebasers.Count; i++)
        {
            var rebaser = _rebasers[i];
            owners[i] = owners[i] with { State = RebaseStates.Running };
            Set(new RebaseStatus(map.Generation, RebaseStates.Running, owners));
            try
            {
                var result = rebaser.Rebase(map);
                owners[i] = new RebaseOwnerStatus(rebaser.Owner, RebaseStates.Completed,
                    result.Examined, result.Changed, result.Skipped, null);
                _log($"[Rebase] {rebaser.Owner}: {result.Changed} of {result.Examined} value(s) rewritten"
                     + (result.Skipped > 0 ? $", {result.Skipped} left as they were" : ""));
            }
            catch (Exception ex)
            {
                owners[i] = new RebaseOwnerStatus(rebaser.Owner, RebaseStates.Failed, 0, 0, 0, ex.Message);
                _log($"[Rebase] {rebaser.Owner} failed: {ex.Message}");
            }
        }

        var failed = owners.Any(o => o.State == RebaseStates.Failed);
        var final = new RebaseStatus(map.Generation, failed ? RebaseStates.Failed : RebaseStates.Completed, owners);
        Set(final);
        // Only a clean run is recorded as the completed generation; a failed one is retried at the next start.
        if (!failed) Persist(final); else PersistFailure(final);
        return final;
    }

    private void Set(RebaseStatus status)
    {
        lock (_gate) _status = status;
    }

    private RebaseStatus? ReadRecorded()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            return JsonSerializer.Deserialize<RebaseStatus>(File.ReadAllText(_statePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            // Torn or damaged: kept for inspection rather than replaced by the next save. Every rebaser is
            // idempotent, so running the generation again is safe.
            _log($"[Rebase] Could not read {_statePath} ({ex.Message}); "
                 + DurableFile.SetAsideAndDescribe(_statePath, DateTimeOffset.UtcNow));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"[Rebase] Could not read {_statePath}: {ex.Message}");
            return null;
        }
    }

    private void Persist(RebaseStatus status)
        => DurableFile.WriteAllText(_statePath, JsonSerializer.Serialize(status, JsonOptions));

    /// <summary>
    /// A failed run keeps the last completed generation on disk (so completed work is not forgotten) and adds
    /// nothing that could read as completion for the failed one.
    /// </summary>
    private void PersistFailure(RebaseStatus status)
    {
        var previous = ReadRecorded();
        if (previous is { State: RebaseStates.Completed }) return;
        Persist(status);
    }
}

/// <summary>The status of this process's rebase, for the endpoint and for the host that ran it.</summary>
public static class StoredPathRebase
{
    private static StoredPathRebaseRunner? _current;

    public static StoredPathRebaseRunner? Current => _current;

    /// <summary>
    /// Reads <c>REDCOMPUTE_REBASE_MAP</c> and runs every rebaser for it. Call once at startup, after
    /// <see cref="ComputeStorage"/> resolved and before any store is opened. A malformed map throws
    /// <see cref="RebaseMapException"/> (the caller exits like a bad data root).
    /// </summary>
    public static RebaseStatus Run(ComputeStorage storage, RebaseMap? map, Action<string> log)
    {
        var runner = new StoredPathRebaseRunner(storage, StoredPathRebasers.For(storage), log);
        _current = runner;
        return runner.Run(map);
    }

    internal static void Reset() => _current = null;
}
