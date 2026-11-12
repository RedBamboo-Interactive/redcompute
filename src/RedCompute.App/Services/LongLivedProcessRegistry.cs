using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Servers an agent deliberately keeps beyond its session (a playtest preview, a portal dev server).
/// Everything a session starts dies with it (<see cref="SessionProcessContainment"/>); this registry
/// is the one explicit way out. Each entry is a kept process tree: its own job without kill-on-close,
/// tracked by parentage like a session, every live identity (PID and creation time) on disk so a
/// restarted RedCompute takes it back. A launched server is created outside every job RedCompute
/// belongs to (<see cref="DetachedProcessLauncher"/>): RedLeaf's kill-on-close job would otherwise
/// take it down with RedLeaf, as it did on 12 Nov. It is not RedCompute's child either, so a tree
/// kill of RedCompute cannot reach it, and it writes to a log file rather than a RedCompute pipe.
/// <para>
/// An entry whose processes have all exited is never silently kept as if it ran: it shows
/// <c>state: exited</c> with the exit time the OS recorded (null when it ended while no RedCompute
/// was running, with when it was last seen alive). It stays listed until its owner removes it, or,
/// when it was launched with <c>restart: on-exit</c>, it is relaunched with backoff; five exits in a
/// row within 30 s of starting stop the relaunching. Entries are known only by exact identity, never
/// by name.
/// </para>
/// </summary>
public sealed class LongLivedProcessRegistry : IDisposable
{
    public const string RestartNever = "never";
    public const string RestartOnExit = "on-exit";
    internal const int QuickExitLimit = 5;
    private static readonly TimeSpan QuickExitWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRelaunchDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlivePersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly string _entryDirectory;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LongLivedProcessRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContainedProcessTree> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _relaunchDue = new(StringComparer.Ordinal);
    private readonly Timer? _monitor;
    private DateTimeOffset _alivePersistedAt = DateTimeOffset.MinValue;
    private int _ticking;
    private bool _disposed;

    /// <summary>The first relaunch delay, doubled for each quick exit in a row. Tests shorten it.</summary>
    internal TimeSpan RelaunchDelay { get; set; } = TimeSpan.FromSeconds(2);

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <param name="monitor">Watch entries for exit and relaunch on a timer; tests drive <see cref="Tick"/> instead.</param>
    public LongLivedProcessRegistry(string directory, Action<string> log, bool monitor = true)
    {
        _statePath = Path.Combine(directory, "long-lived-processes.json");
        _entryDirectory = Path.Combine(directory, "long-lived-processes");
        _log = log;
        Load();
        if (monitor) _monitor = new Timer(_ => Tick(), null, TimeSpan.Zero, MonitorInterval);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute");

    /// <summary>The reaper's guard: a process of a registered tree is never reaped by a session.</summary>
    public bool IsProtected(int pid, long createdUtcTicks)
    {
        lock (_gate)
            return _trees.Values.Any(tree => tree.Tracks(pid, createdUtcTicks))
                   || _records.Values.Any(record => record.Pid == pid && record.ProcessCreatedUtcTicks == createdUtcTicks);
    }

    public IReadOnlyList<LongLivedProcessView> List()
    {
        lock (_gate)
            return _records.Values.OrderBy(record => record.StartedAt).Select(View).ToArray();
    }

    public LongLivedProcessView? Get(string id)
    {
        lock (_gate)
            return _records.TryGetValue(id, out var record) ? View(record) : null;
    }

    public LongLivedProcessResult Launch(LongLivedLaunchRequest request, LongLivedOwner owner)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return LongLivedProcessResult.Invalid("name is required");
        if (string.IsNullOrWhiteSpace(request.Command))
            return LongLivedProcessResult.Invalid("command is required");
        if (request.Command.IndexOfAny(['\r', '\n']) >= 0)
            return LongLivedProcessResult.Invalid("command must be one line");
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathFullyQualified(request.WorkingDirectory)
            || !Directory.Exists(request.WorkingDirectory))
            return LongLivedProcessResult.Invalid("workingDirectory must be an existing absolute directory");
        if (request.Port is { } port && (port is < 1 or > 65535))
            return LongLivedProcessResult.Invalid("port must be 1-65535");
        var restart = request.Restart?.Trim().ToLowerInvariant() ?? RestartNever;
        if (restart is not (RestartNever or RestartOnExit))
            return LongLivedProcessResult.Invalid($"restart must be \"{RestartNever}\" or \"{RestartOnExit}\"");
        if (request.Port is { } listening && IsListening(listening))
            return LongLivedProcessResult.Conflict($"port {listening} is already in use");
        if (!OperatingSystem.IsWindows())
            return LongLivedProcessResult.Invalid("long-lived processes are supported on Windows only");

        var id = NewId();
        Directory.CreateDirectory(_entryDirectory);
        var record = new LongLivedProcessRecord
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description,
            Command = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            // Kept on disk only when a relaunch will need it.
            Environment = restart == RestartOnExit && request.Environment is { Count: > 0 } kept ? new(kept) : null,
            Port = request.Port,
            LogPath = Path.Combine(_entryDirectory, $"{id}.log"),
            Source = "launched",
            Restart = restart,
            StartedAt = Clock(),
            OwnerUserId = owner.UserId,
            StartedBy = owner.ActorName,
            SessionId = owner.SessionId,
        };
        WriteScripts(record);

        if (StartTree(record, request.Environment) is not { } started)
        {
            DeleteEntryFiles(id);
            return LongLivedProcessResult.Invalid($"could not start: {record.Note}");
        }
        lock (_gate)
        {
            Apply(record, started);
            _records[id] = record;
            _trees[id] = started.Tree;
            Save();
        }
        _log($"[LongLived] Started '{record.Name}' ({id}) PID {record.Pid} via {record.LaunchedVia} in {record.WorkingDirectory}"
             + (record.Port is { } p ? $" on port {p}" : "") + $" for {owner.ActorName ?? "unknown"}"
             + (restart == RestartOnExit ? ", relaunched on exit" : ""));
        lock (_gate) return LongLivedProcessResult.Ok(View(record));
    }

    /// <summary>
    /// Register a process that is already running outside every session job, such as a server an
    /// earlier agent left behind. A session member cannot leave its job, so it would die with its
    /// session whatever the registry said; it has to be relaunched through <see cref="Launch"/>.
    /// An adopted process has no command of its own to relaunch.
    /// </summary>
    public LongLivedProcessResult Adopt(LongLivedAdoptRequest request, LongLivedOwner owner)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return LongLivedProcessResult.Invalid("name is required");
        if (SessionProcessContainment.IsSessionMember(request.Pid))
            return LongLivedProcessResult.Conflict(
                "that process runs inside a live session and will end with it; start it with POST /processes/long-lived instead");

        Process process;
        try { process = Process.GetProcessById(request.Pid); }
        catch { return LongLivedProcessResult.NotFound($"no process {request.Pid}"); }
        using (process)
        {
            var created = CreationTicks(process);
            if (request.ProcessStartedAt is { } expected
                && Math.Abs((new DateTime(created, DateTimeKind.Utc) - expected.UtcDateTime).TotalSeconds) > 2)
                return LongLivedProcessResult.Conflict("PID now belongs to a process with a different start time");
            string? executable = null;
            try { executable = process.MainModule?.FileName; } catch { }

            lock (_gate)
            {
                if (_records.Values.FirstOrDefault(r => r.Pid == request.Pid && r.ProcessCreatedUtcTicks == created) is { } existing)
                    return LongLivedProcessResult.Ok(View(existing));
            }
            var id = NewId();
            Directory.CreateDirectory(_entryDirectory);
            var tree = SessionProcessContainment.Keep(process, $"long-lived {id}", LedgerPath(id));
            var now = Clock();
            var record = new LongLivedProcessRecord
            {
                Id = id,
                Name = request.Name.Trim(),
                Description = request.Description,
                Command = executable ?? process.ProcessName,
                Port = request.Port,
                Pid = request.Pid,
                ProcessCreatedUtcTicks = created,
                Source = "adopted",
                Restart = RestartNever,
                StartedAt = now,
                LastSeenAliveAt = now,
                OwnerUserId = owner.UserId,
                StartedBy = owner.ActorName,
                SessionId = owner.SessionId,
            };
            lock (_gate)
            {
                _records[id] = record;
                if (tree is not null) _trees[id] = tree;
                Save();
            }
            _log($"[LongLived] Adopted '{record.Name}' ({id}) PID {record.Pid} for {owner.ActorName ?? "unknown"}");
            lock (_gate) return LongLivedProcessResult.Ok(View(record));
        }
    }

    /// <summary>
    /// Stop the whole tree (only processes that are provably still its own) and forget it. This is
    /// also how the owner clears an entry that has exited.
    /// </summary>
    public LongLivedProcessResult Stop(string id)
    {
        LongLivedProcessRecord record;
        ContainedProcessTree? tree;
        LongLivedProcessView view;
        lock (_gate)
        {
            if (!_records.Remove(id, out record!)) return LongLivedProcessResult.NotFound($"no long-lived process {id}");
            _trees.Remove(id, out tree);
            _relaunchDue.Remove(id);
            view = View(record);
            Save();
        }

        var wasRunning = tree is not null && tree.LiveProcesses.Count > 0;
        if (tree is not null && OperatingSystem.IsWindows()) tree.Reap();
        DeleteEntryFiles(id);
        _log($"[LongLived] Removed '{record.Name}' ({id}) PID {record.Pid}{(wasRunning ? ", stopped" : ", was not running")}");
        return LongLivedProcessResult.Ok(view with { Running = false, ProcessIds = [] });
    }

    /// <summary>
    /// One monitor pass: record exits truthfully, relaunch the entries that ask for it once their
    /// backoff has passed, and persist when each entry was last seen alive.
    /// </summary>
    internal void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            KeyValuePair<string, ContainedProcessTree>[] trees;
            lock (_gate)
            {
                if (_disposed) return;
                trees = _trees.ToArray();
            }

            var now = Clock();
            var changed = false;
            foreach (var (id, tree) in trees)
            {
                var alive = tree.Refresh().Count > 0;
                lock (_gate)
                {
                    if (!_records.TryGetValue(id, out var record) || !_trees.TryGetValue(id, out var current) || current != tree)
                        continue;
                    if (alive)
                    {
                        record.LastSeenAliveAt = now;
                        continue;
                    }
                    _trees.Remove(id);
                    tree.Release(deleteLedger: true);
                    RecordExit(record, tree.LastExitAt ?? now, observed: true);
                    changed = true;
                }
            }

            string[] due;
            lock (_gate)
            {
                due = _relaunchDue.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray();
                if (changed || now - _alivePersistedAt >= AlivePersistInterval)
                {
                    _alivePersistedAt = now;
                    Save();
                }
            }
            foreach (var id in due) Relaunch(id);
        }
        catch (Exception ex)
        {
            _log($"[LongLived] Monitor pass failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>
    /// RedCompute is stopping: let go of every tree without killing it, so the next RedCompute
    /// resumes it from its ledger.
    /// </summary>
    public void Dispose()
    {
        _monitor?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Save();
            foreach (var tree in _trees.Values)
                if (tree.IsKept) tree.Release(deleteLedger: false);
            _trees.Clear();
        }
    }

    /// <summary>Under the gate: the entry's processes are all gone.</summary>
    private void RecordExit(LongLivedProcessRecord record, DateTimeOffset? exitedAt, bool observed)
    {
        record.ExitedAt = exitedAt;
        var ranFor = exitedAt is { } end && record.LastLaunchedAt is { } start ? end - start : (TimeSpan?)null;
        record.QuickExits = ranFor is { } span && span < QuickExitWindow ? record.QuickExits + 1 : 0;
        record.Note = observed
            ? null
            : "exited while no RedCompute was watching it" + (record.LastSeenAliveAt is { } seen ? $"; last seen alive {seen:O}" : "");
        if (record.Restart == RestartOnExit && record.Source == "launched")
            ScheduleRelaunch(record, "exited");
        else
            _log($"[LongLived] '{record.Name}' ({record.Id}) PID {record.Pid} exited{(exitedAt is { } at ? $" at {at:O}" : " while no RedCompute was watching it")}; listed until removed");
    }

    /// <summary>Under the gate: relaunch after a backoff that doubles with each quick exit in a row, or give up.</summary>
    private void ScheduleRelaunch(LongLivedProcessRecord record, string what)
    {
        if (record.QuickExits >= QuickExitLimit)
        {
            record.Note = $"not relaunched: {record.QuickExits} launches in a row failed or exited within {QuickExitWindow.TotalSeconds:0} s"
                          + (record.Note is { } last ? $" (last: {last})" : "") + "; see its log, then remove and launch it again";
            _log($"[LongLived] '{record.Name}' ({record.Id}) {what}; {record.QuickExits} quick exits in a row, relaunching stopped");
            return;
        }
        var delay = TimeSpan.FromTicks(Math.Min(MaxRelaunchDelay.Ticks, RelaunchDelay.Ticks << Math.Min(record.QuickExits, 16)));
        _relaunchDue[record.Id] = Clock() + delay;
        _log($"[LongLived] '{record.Name}' ({record.Id}) {what}; relaunching in {delay.TotalSeconds:0.#} s");
    }

    private void Relaunch(string id)
    {
        LongLivedProcessRecord record;
        lock (_gate)
        {
            _relaunchDue.Remove(id);
            if (_disposed || !_records.TryGetValue(id, out record!) || _trees.ContainsKey(id)) return;
        }
        if (!File.Exists(Path.Combine(_entryDirectory, $"{id}.cmd")) || !File.Exists(Path.Combine(_entryDirectory, $"{id}.run.cmd")))
            WriteScripts(record);
        var started = StartTree(record, record.Environment);
        lock (_gate)
        {
            // Removed (or the registry stopped) while it was starting: it is nobody's now.
            if (_disposed || !_records.TryGetValue(id, out var current) || current != record)
            {
                if (started is not null && OperatingSystem.IsWindows()) started.Tree.Reap();
                return;
            }
            if (started is null)
            {
                // A launch that cannot start counts as a quick exit, so a lasting cause gives up.
                record.QuickExits++;
                record.Note = $"relaunch failed: {record.Note}";
                ScheduleRelaunch(record, "could not be relaunched");
                Save();
                return;
            }
            Apply(record, started);
            record.RestartCount++;
            _trees[id] = started.Tree;
            Save();
        }
        _log($"[LongLived] Relaunched '{record.Name}' ({id}) PID {record.Pid} via {record.LaunchedVia} (restart {record.RestartCount})");
    }

    private sealed record StartedTree(ContainedProcessTree Tree, int Pid, long Created, string Via, DateTimeOffset At);

    /// <summary>
    /// Create the entry's script outside RedCompute's jobs, suspended; put it in its own kept job;
    /// only then let it run, so nothing it starts is ever outside the kept tree. Null on failure,
    /// with the reason in <see cref="LongLivedProcessRecord.Note"/>.
    /// </summary>
    private StartedTree? StartTree(LongLivedProcessRecord record, IReadOnlyDictionary<string, string>? environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            record.Note = "long-lived processes are supported on Windows only";
            return null;
        }
        var commandLine = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"{Path.Combine(_entryDirectory, $"{record.Id}.cmd")}\"";
        DetachedProcessLauncher.Started started;
        try
        {
            started = DetachedProcessLauncher.StartSuspended(commandLine, record.WorkingDirectory!, environment);
        }
        catch (Exception ex)
        {
            record.Note = ex.Message;
            return null;
        }

        using (started)
        {
            ContainedProcessTree? tree = null;
            try
            {
                using var process = Process.GetProcessById(started.Pid);
                var created = CreationTicks(process);
                tree = SessionProcessContainment.Keep(process, $"long-lived {record.Id}", LedgerPath(record.Id));
                if (tree is null) throw new InvalidOperationException("could not contain the launched process");
                started.Resume();
                return new StartedTree(tree, started.Pid, created, started.Via, Clock());
            }
            catch (Exception ex)
            {
                started.Abort();
                tree?.Reap();
                record.Note = ex.Message;
                return null;
            }
        }
    }

    private static void Apply(LongLivedProcessRecord record, StartedTree started)
    {
        record.Pid = started.Pid;
        record.ProcessCreatedUtcTicks = started.Created;
        record.LaunchedVia = started.Via;
        record.LastLaunchedAt = started.At;
        record.LastSeenAliveAt = started.At;
        record.ExitedAt = null;
        record.Note = null;
    }

    /// <summary>
    /// The command runs from a script of its own, so no quoting of it crosses a command line, and it
    /// is that script's top level: a command that chains to a batch file without `call` (pnpm.cmd,
    /// npm.cmd) never returns, which inside a `call :label` loses the caller's label. The outer
    /// script gives it NUL and the log, never a RedCompute pipe a restart would break.
    /// </summary>
    private void WriteScripts(LongLivedProcessRecord record)
    {
        var scriptPath = Path.Combine(_entryDirectory, $"{record.Id}.cmd");
        var commandPath = Path.Combine(_entryDirectory, $"{record.Id}.run.cmd");
        File.WriteAllText(commandPath, string.Join("\r\n", "@echo off", record.Command, ""), new UTF8Encoding(false));
        File.WriteAllText(scriptPath, string.Join("\r\n",
            "@echo off",
            $"cd /d \"{record.WorkingDirectory}\"",
            $"\"%ComSpec%\" /d /c \"\"{commandPath}\"\" < NUL >> \"{record.LogPath}\" 2>&1",
            ""), new UTF8Encoding(false));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var records = JsonSerializer.Deserialize<List<LongLivedProcessRecord>>(File.ReadAllText(_statePath), JsonOptions) ?? [];
            var now = Clock();
            foreach (var record in records)
            {
                record.Restart ??= RestartNever;
                // Its ledger lists every live process of the tree; a record written before ledgers
                // existed knows only its root.
                var tree = File.Exists(LedgerPath(record.Id))
                    ? SessionProcessContainment.Resume($"long-lived {record.Id}", LedgerPath(record.Id))
                    : SessionProcessContainment.Resume($"long-lived {record.Id}", LedgerPath(record.Id),
                        [(record.Pid, record.ProcessCreatedUtcTicks)]);
                lock (_gate)
                {
                    _records[record.Id] = record;
                    if (tree is not null)
                    {
                        _trees[record.Id] = tree;
                        record.LastSeenAliveAt = now;
                    }
                    else if (record.ExitedAt is null && record.Note is null)
                    {
                        // Gone while no RedCompute watched it: when it ended is not known.
                        RecordExit(record, null, observed: false);
                    }
                    else if (record.Restart == RestartOnExit && record.Source == "launched" && record.QuickExits < QuickExitLimit)
                    {
                        _relaunchDue[record.Id] = now;
                    }
                }
            }
            lock (_gate) Save();
        }
        catch (Exception ex)
        {
            _log($"[LongLived] Could not read {_statePath}: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"[LongLived] Could not write {_statePath}: {ex.Message}");
        }
    }

    /// <summary>Under the gate.</summary>
    private LongLivedProcessView View(LongLivedProcessRecord record)
    {
        // A started tree tracks its root from the moment it is kept, so an empty one has ended,
        // even if the monitor has not recorded it yet.
        var live = _trees.TryGetValue(record.Id, out var tree) ? tree.LiveProcesses : [];
        var state = live.Count > 0 ? "running" : _relaunchDue.ContainsKey(record.Id) ? "restarting" : "exited";
        var exitedAt = state == "running" ? null : tree?.LastExitAt ?? record.ExitedAt;
        return new LongLivedProcessView(
            record.Id, record.Name, record.Description, record.Command, record.WorkingDirectory, record.Port,
            record.Pid, new DateTimeOffset(new DateTime(record.ProcessCreatedUtcTicks, DateTimeKind.Utc)),
            live.Count > 0, live.Select(entry => entry.Pid).Order().ToArray(), record.Source, record.StartedAt,
            record.StartedBy, record.SessionId, record.LogPath)
        {
            State = state,
            ExitedAt = exitedAt,
            LastSeenAliveAt = record.LastSeenAliveAt,
            Restart = record.Restart ?? RestartNever,
            RestartCount = record.RestartCount,
            LaunchedVia = record.LaunchedVia,
            Note = state == "running" ? null : record.Note,
        };
    }

    private string LedgerPath(string id) => Path.Combine(_entryDirectory, $"{id}.processes.json");

    /// <summary>Its ledger and scripts; the log stays for whoever wants to read it.</summary>
    private void DeleteEntryFiles(string id)
    {
        foreach (var path in new[] { LedgerPath(id), Path.Combine(_entryDirectory, $"{id}.cmd"), Path.Combine(_entryDirectory, $"{id}.run.cmd") })
            try { File.Delete(path); } catch { }
    }

    private static long CreationTicks(Process process) => SessionProcessContainment.CreationUtcTicks(process);

    private static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed record LongLivedOwner(string? UserId, string? ActorName, string? SessionId);

/// <param name="Environment">Added to the user's own environment (never RedCompute's).</param>
/// <param name="Restart">"never" (default) or "on-exit": relaunch it whenever it exits, with backoff.</param>
public sealed record LongLivedLaunchRequest(
    string Name, string Command, string WorkingDirectory, int? Port = null, string? Description = null,
    Dictionary<string, string>? Environment = null, string? Restart = null);

public sealed record LongLivedAdoptRequest(
    int Pid, string Name, int? Port = null, string? Description = null, DateTimeOffset? ProcessStartedAt = null);

/// <summary>
/// <see cref="State"/> is running, restarting (exited, relaunch pending) or exited. An exited entry
/// carries <see cref="ExitedAt"/>, the exit time the OS recorded, or null when it ended while no
/// RedCompute was running, in which case <see cref="LastSeenAliveAt"/> bounds it.
/// </summary>
public sealed record LongLivedProcessView(
    string Id, string Name, string? Description, string Command, string? WorkingDirectory, int? Port,
    int Pid, DateTimeOffset ProcessStartedAt, bool Running, int[] ProcessIds, string Source, DateTimeOffset RegisteredAt,
    string? StartedBy, string? SessionId, string? LogPath)
{
    public string State { get; init; } = "running";
    public DateTimeOffset? ExitedAt { get; init; }
    public DateTimeOffset? LastSeenAliveAt { get; init; }
    public string Restart { get; init; } = LongLivedProcessRegistry.RestartNever;
    public int RestartCount { get; init; }

    /// <summary>How a launched entry left RedCompute's jobs: "desktop" or "breakaway". Null when adopted or older.</summary>
    public string? LaunchedVia { get; init; }

    public string? Note { get; init; }
}

public sealed record LongLivedProcessResult(int Status, LongLivedProcessView? Process, string? Error)
{
    public static LongLivedProcessResult Ok(LongLivedProcessView view) => new(200, view, null);
    public static LongLivedProcessResult Invalid(string error) => new(400, null, error);
    public static LongLivedProcessResult Conflict(string error) => new(409, null, error);
    public static LongLivedProcessResult NotFound(string error) => new(404, null, error);
}

internal sealed class LongLivedProcessRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public int? Port { get; set; }
    public int Pid { get; set; }
    public long ProcessCreatedUtcTicks { get; set; }
    public string? LogPath { get; set; }
    public string Source { get; set; } = "launched";
    public string? Restart { get; set; }
    public string? LaunchedVia { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public DateTimeOffset? LastSeenAliveAt { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
    public int RestartCount { get; set; }
    public int QuickExits { get; set; }
    public string? Note { get; set; }
    public string? OwnerUserId { get; set; }
    public string? StartedBy { get; set; }
    public string? SessionId { get; set; }
}
