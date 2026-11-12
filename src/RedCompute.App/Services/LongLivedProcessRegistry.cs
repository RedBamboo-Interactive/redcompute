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
/// restarted RedCompute takes it back. A launched server starts through a launcher that exits at
/// once, so it is never in RedCompute's own process tree and a tree kill of RedCompute cannot reach
/// it, and writes to a log file rather than a RedCompute pipe. Entries are listed and stoppable,
/// and known only by exact identity, never by name.
/// </summary>
public sealed class LongLivedProcessRegistry
{
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

    internal Func<ProcessStartInfo, Process?> LaunchProcess { get; set; } = Process.Start;

    public LongLivedProcessRegistry(string directory, Action<string> log)
    {
        _statePath = Path.Combine(directory, "long-lived-processes.json");
        _entryDirectory = Path.Combine(directory, "long-lived-processes");
        _log = log;
        Load();
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
        if (request.Port is { } listening && IsListening(listening))
            return LongLivedProcessResult.Conflict($"port {listening} is already in use");
        if (!OperatingSystem.IsWindows())
            return LongLivedProcessResult.Invalid("long-lived processes are supported on Windows only");

        var id = NewId();
        Directory.CreateDirectory(_entryDirectory);
        var logPath = Path.Combine(_entryDirectory, $"{id}.log");
        var scriptPath = Path.Combine(_entryDirectory, $"{id}.cmd");
        // The command runs from a script, so no quoting of it crosses a command line: the server
        // reads NUL and writes its log, never a RedCompute pipe that a restart would break.
        File.WriteAllText(scriptPath, string.Join("\r\n",
            "@echo off",
            $"cd /d \"{request.WorkingDirectory}\"",
            $"call :run < NUL >> \"{logPath}\" 2>&1",
            "exit /b %errorlevel%",
            ":run",
            request.Command,
            "exit /b %errorlevel%",
            ""), new UTF8Encoding(false));
        // `start /b` from a cmd that exits at once: the server's parent is gone, so it is outside
        // RedCompute's process tree; the kept tree's job and parentage hold it instead.
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c start \"\" /b \"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"{scriptPath}\"",
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in request.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[key] = value;

        Process launcher;
        try
        {
            launcher = LaunchProcess(startInfo) ?? throw new InvalidOperationException("Process.Start returned no process");
        }
        catch (Exception ex)
        {
            return LongLivedProcessResult.Invalid($"could not start: {ex.Message}");
        }

        using (launcher)
        {
            var tree = SessionProcessContainment.Keep(launcher, $"long-lived {id}", LedgerPath(id));
            if (tree is null)
                return LongLivedProcessResult.Invalid("could not contain the launched process");
            var (pid, created) = WaitForServerRoot(tree, launcher.Id);
            var record = new LongLivedProcessRecord
            {
                Id = id,
                Name = request.Name.Trim(),
                Description = request.Description,
                Command = request.Command,
                WorkingDirectory = request.WorkingDirectory,
                Port = request.Port,
                Pid = pid,
                ProcessCreatedUtcTicks = created,
                LogPath = logPath,
                Source = "launched",
                StartedAt = DateTimeOffset.UtcNow,
                OwnerUserId = owner.UserId,
                StartedBy = owner.ActorName,
                SessionId = owner.SessionId,
            };
            lock (_gate)
            {
                _records[id] = record;
                _trees[id] = tree;
                Save();
            }
            _log($"[LongLived] Started '{record.Name}' ({id}) PID {record.Pid} in {record.WorkingDirectory}"
                 + (record.Port is { } p ? $" on port {p}" : "") + $" for {owner.ActorName ?? "unknown"}");
            return LongLivedProcessResult.Ok(View(record));
        }
    }

    /// <summary>
    /// Register a process that is already running outside every session job, such as a server an
    /// earlier agent left behind. A session member cannot leave its job, so it would die with its
    /// session whatever the registry said; it has to be relaunched through <see cref="Launch"/>.
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
                StartedAt = DateTimeOffset.UtcNow,
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
            return LongLivedProcessResult.Ok(View(record));
        }
    }

    /// <summary>Stop the whole tree (only processes that are provably still its own) and forget it.</summary>
    public LongLivedProcessResult Stop(string id)
    {
        LongLivedProcessRecord record;
        ContainedProcessTree? tree;
        lock (_gate)
        {
            if (!_records.Remove(id, out record!)) return LongLivedProcessResult.NotFound($"no long-lived process {id}");
            _trees.Remove(id, out tree);
            Save();
        }

        var wasRunning = tree is not null && tree.LiveProcesses.Count > 0;
        if (tree is not null && OperatingSystem.IsWindows()) tree.Reap();
        foreach (var path in new[] { LedgerPath(id), Path.Combine(_entryDirectory, $"{id}.cmd") })
            try { File.Delete(path); } catch { }
        _log($"[LongLived] Removed '{record.Name}' ({id}) PID {record.Pid}{(wasRunning ? ", stopped" : ", was not running")}");
        return LongLivedProcessResult.Ok(View(record) with { Running = false, ProcessIds = [] });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var records = JsonSerializer.Deserialize<List<LongLivedProcessRecord>>(File.ReadAllText(_statePath), JsonOptions) ?? [];
            foreach (var record in records)
            {
                // Its ledger lists every live process of the tree; a record written before ledgers
                // existed knows only its root.
                var tree = File.Exists(LedgerPath(record.Id))
                    ? SessionProcessContainment.Resume($"long-lived {record.Id}", LedgerPath(record.Id))
                    : SessionProcessContainment.Resume($"long-lived {record.Id}", LedgerPath(record.Id),
                        [(record.Pid, record.ProcessCreatedUtcTicks)]);
                lock (_gate)
                {
                    _records[record.Id] = record;
                    if (tree is not null) _trees[record.Id] = tree;
                }
            }
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

    private LongLivedProcessView View(LongLivedProcessRecord record)
    {
        var live = _trees.TryGetValue(record.Id, out var tree) ? tree.LiveProcesses : [];
        return new LongLivedProcessView(
            record.Id, record.Name, record.Description, record.Command, record.WorkingDirectory, record.Port,
            record.Pid, new DateTimeOffset(new DateTime(record.ProcessCreatedUtcTicks, DateTimeKind.Utc)),
            live.Count > 0, live.Select(entry => entry.Pid).Order().ToArray(), record.Source, record.StartedAt,
            record.StartedBy, record.SessionId, record.LogPath);
    }

    /// <summary>The server's own root: the script's cmd, once the launcher has started it.</summary>
    private static (int Pid, long Created) WaitForServerRoot(ContainedProcessTree tree, int launcherPid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var root = tree.LiveProcesses.Where(entry => entry.Pid != launcherPid).OrderBy(entry => entry.CreatedUtcTicks)
                .FirstOrDefault();
            if (root.Pid != 0) return root;
            Thread.Sleep(50);
        }
        return tree.LiveProcesses.OrderBy(entry => entry.CreatedUtcTicks).FirstOrDefault() is { Pid: not 0 } any
            ? any
            : (launcherPid, tree.RootCreatedUtcTicks);
    }

    private string LedgerPath(string id) => Path.Combine(_entryDirectory, $"{id}.processes.json");

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

public sealed record LongLivedLaunchRequest(
    string Name, string Command, string WorkingDirectory, int? Port = null, string? Description = null,
    Dictionary<string, string>? Environment = null);

public sealed record LongLivedAdoptRequest(
    int Pid, string Name, int? Port = null, string? Description = null, DateTimeOffset? ProcessStartedAt = null);

public sealed record LongLivedProcessView(
    string Id, string Name, string? Description, string Command, string? WorkingDirectory, int? Port,
    int Pid, DateTimeOffset ProcessStartedAt, bool Running, int[] ProcessIds, string Source, DateTimeOffset RegisteredAt,
    string? StartedBy, string? SessionId, string? LogPath);

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
    public int? Port { get; set; }
    public int Pid { get; set; }
    public long ProcessCreatedUtcTicks { get; set; }
    public string? LogPath { get; set; }
    public string Source { get; set; } = "launched";
    public DateTimeOffset StartedAt { get; set; }
    public string? OwnerUserId { get; set; }
    public string? StartedBy { get; set; }
    public string? SessionId { get; set; }
}
