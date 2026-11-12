using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Servers an agent deliberately keeps beyond its session (a playtest preview, a portal dev server).
/// Everything a session starts dies with it (<see cref="SessionProcessContainment"/>); this registry
/// is the one explicit way out. A launched server runs under RedCompute, never inside a session job,
/// with its output in a log file so it does not depend on RedCompute's pipes and keeps running
/// across a RedCompute restart. Each entry is listed and stoppable, and is known only by its exact
/// identity (PID and creation time), never by name.
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
    private readonly string _logDirectory;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LongLivedProcessRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SafeFileHandle> _jobs = new(StringComparer.Ordinal);

    internal Func<ProcessStartInfo, Process?> LaunchProcess { get; set; } = Process.Start;

    public LongLivedProcessRegistry(string directory, Action<string> log)
    {
        _statePath = Path.Combine(directory, "long-lived-processes.json");
        _logDirectory = Path.Combine(directory, "long-lived-processes");
        _log = log;
        Load();
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute");

    /// <summary>The reaper's guard: a registered, still-identical process is never reaped.</summary>
    public bool IsProtected(int pid, long createdUtcTicks)
    {
        lock (_gate)
            return _records.Values.Any(record => record.Pid == pid && record.ProcessCreatedUtcTicks == createdUtcTicks);
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

        var id = NewId();
        Directory.CreateDirectory(_logDirectory);
        var logPath = Path.Combine(_logDirectory, $"{id}.log");
        // cmd owns the redirection: the server writes to its log file, not to a RedCompute pipe
        // that would break when RedCompute restarts, and it reads NUL, not RedCompute's stdin.
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{request.Command} < NUL >> \"{logPath}\" 2>&1\"",
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in request.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[key] = value;

        Process process;
        try
        {
            process = LaunchProcess(startInfo) ?? throw new InvalidOperationException("Process.Start returned no process");
        }
        catch (Exception ex)
        {
            return LongLivedProcessResult.Invalid($"could not start: {ex.Message}");
        }

        using (process)
        {
            var created = CreationTicks(process);
            var record = new LongLivedProcessRecord
            {
                Id = id,
                Name = request.Name.Trim(),
                Description = request.Description,
                Command = request.Command,
                WorkingDirectory = request.WorkingDirectory,
                Port = request.Port,
                Pid = process.Id,
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
                if (OperatingSystem.IsWindows() && OwnJob(process) is { } job) _jobs[id] = job;
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
                var id = NewId();
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
                _records[id] = record;
                Save();
                _log($"[LongLived] Adopted '{record.Name}' ({id}) PID {record.Pid} for {owner.ActorName ?? "unknown"}");
                return LongLivedProcessResult.Ok(View(record));
            }
        }
    }

    /// <summary>Stop the process tree (only if it is still the same process) and forget it.</summary>
    public LongLivedProcessResult Stop(string id)
    {
        LongLivedProcessRecord record;
        SafeFileHandle? job;
        lock (_gate)
        {
            if (!_records.Remove(id, out record!)) return LongLivedProcessResult.NotFound($"no long-lived process {id}");
            _jobs.Remove(id, out job);
            Save();
        }

        var stopped = false;
        if (job is not null && OperatingSystem.IsWindows())
        {
            stopped = IsAlive(record) && SessionProcessContainment.TerminateJob(job);
            job.Dispose();
        }
        if (!stopped && IsAlive(record))
        {
            try
            {
                using var process = Process.GetProcessById(record.Pid);
                if (CreationTicks(process) == record.ProcessCreatedUtcTicks)
                {
                    process.Kill(entireProcessTree: true);
                    stopped = true;
                }
            }
            catch { }
        }
        _log($"[LongLived] Removed '{record.Name}' ({id}) PID {record.Pid}{(stopped ? ", stopped" : ", was not running")}");
        return LongLivedProcessResult.Ok(View(record) with { Running = false });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var records = JsonSerializer.Deserialize<List<LongLivedProcessRecord>>(File.ReadAllText(_statePath), JsonOptions) ?? [];
            lock (_gate)
                foreach (var record in records)
                    _records[record.Id] = record;
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

    private static LongLivedProcessView View(LongLivedProcessRecord record) => new(
        record.Id, record.Name, record.Description, record.Command, record.WorkingDirectory, record.Port,
        record.Pid, new DateTimeOffset(new DateTime(record.ProcessCreatedUtcTicks, DateTimeKind.Utc)),
        IsAlive(record), record.Source, record.StartedAt, record.StartedBy, record.SessionId, record.LogPath);

    private static bool IsAlive(LongLivedProcessRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return !process.HasExited && CreationTicks(process) == record.ProcessCreatedUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static long CreationTicks(Process process) => SessionProcessContainment.CreationUtcTicks(process);

    /// <summary>Own job (no kill-on-close) so Stop ends a server that detached children of its own.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private SafeFileHandle? OwnJob(Process process)
    {
        try
        {
            return SessionProcessContainment.CreateStopJob(process);
        }
        catch (Exception ex)
        {
            _log($"[LongLived] No job for PID {process.Id}: {ex.Message}");
        }
        return null;
    }

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
    int Pid, DateTimeOffset ProcessStartedAt, bool Running, string Source, DateTimeOffset RegisteredAt,
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
