using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedCompute.PluginSdk;

/// <summary>
/// Opt-in contract for CLI-backed providers whose npm package can be managed through RedCompute.
/// </summary>
public interface ICliVersionManagedProvider
{
    string ProviderId { get; }
    string ProviderDisplayName { get; }
    NpmCliVersionManager CliVersionManager { get; }
}

public sealed record CliProviderSessionState(
    int BlockingSessionCount,
    IReadOnlyList<string> IdleSessionIds,
    IReadOnlyCollection<int> OwnedProcessIds)
{
    public static readonly CliProviderSessionState Empty = new(0, [], []);
}

public sealed record CliProcessInfo(int ProcessId, string ExecutablePath);

public interface ICliProcessInspector
{
    IReadOnlyList<CliProcessInfo> FindPackageProcesses(
        string packageDirectory,
        IReadOnlyCollection<int> ownedRootProcessIds);
}

public sealed record CliCommand(string FileName, IReadOnlyList<string> Arguments);

public sealed record CliCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(value => value.Length > 0));
}

public interface ICliCommandRunner
{
    Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default);
}

public sealed class ProcessCliCommandRunner : ICliCommandRunner
{
    public async Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(command) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new CliCommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static ProcessStartInfo CreateStartInfo(CliCommand command)
    {
        ProcessStartInfo startInfo;
        var extension = Path.GetExtension(command.FileName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(FormatWindowsBatchCommand(command));
        }
        else
        {
            startInfo = new ProcessStartInfo(command.FileName);
            foreach (var argument in command.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    private static string FormatWindowsBatchCommand(CliCommand command)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        return string.Join(" ", new[] { Quote(command.FileName) }.Concat(command.Arguments.Select(Quote)));
    }
}

public sealed class SystemCliProcessInspector : ICliProcessInspector
{
    public IReadOnlyList<CliProcessInfo> FindPackageProcesses(
        string packageDirectory,
        IReadOnlyCollection<int> ownedRootProcessIds)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            return [];

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory))
            + Path.DirectorySeparatorChar;
        var parentProcesses = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ReadWindowsParentProcesses()
            : new Dictionary<int, int>();
        var ownedRoots = ownedRootProcessIds.Where(pid => pid > 0).ToHashSet();
        var result = new List<CliProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(executable)) continue;

                string fullPath;
                try { fullPath = Path.GetFullPath(executable); }
                catch { continue; }
                if (!fullPath.StartsWith(root, comparison)) continue;
                if (IsOwnedProcessTree(process.Id, ownedRoots, parentProcesses)) continue;
                result.Add(new CliProcessInfo(process.Id, fullPath));
            }
        }

        return result;
    }

    private static bool IsOwnedProcessTree(
        int processId,
        IReadOnlySet<int> ownedRoots,
        IReadOnlyDictionary<int, int> parentProcesses)
    {
        var current = processId;
        var visited = new HashSet<int>();
        while (current > 0 && visited.Add(current))
        {
            if (ownedRoots.Contains(current)) return true;
            if (!parentProcesses.TryGetValue(current, out current)) break;
        }
        return false;
    }

    private static Dictionary<int, int> ReadWindowsParentProcesses()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed record CliInstalledVersionResult(string? Version, string? Error);

public sealed record CliVersionStatus(
    string PackageName,
    string? InstalledVersion,
    string? DetectionError,
    int ActiveSessionCount,
    int IdleSessionCount,
    int ExternalProcessCount,
    bool InstallAllowed,
    string? BlockedReason,
    CliInstallOperation? Operation);

public sealed record CliVersionCatalog(
    IReadOnlyList<string> Versions,
    string? LatestVersion);

public sealed record CliInstallOperation(
    Guid Id,
    string Status,
    string RequestedVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? InstalledVersion = null,
    string? ErrorCode = null,
    string? Error = null);

public sealed class CliInstallRejectedException : Exception
{
    public string Code { get; }
    public int? ActiveSessionCount { get; }

    public CliInstallRejectedException(string code, string message, int? activeSessionCount = null)
        : base(message)
    {
        Code = code;
        ActiveSessionCount = activeSessionCount;
    }
}

/// <summary>
/// Shared npm-backed version manager. Starting or Active sessions and non-Leaf package processes
/// block installs. Idle provider sessions are parked through the provider's existing resumable
/// maintenance-stop path immediately before npm is launched.
/// </summary>
public sealed class NpmCliVersionManager
{
    private static readonly Regex VersionPattern = new(
        @"(?<![0-9A-Za-z])v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StableVersionPattern = new(
        @"^\d+\.\d+\.\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _processDisplayName;
    private readonly Func<string?> _resolveExecutable;
    private readonly Func<string?> _resolveNpmExecutable;
    private readonly Func<string?> _resolvePackageDirectory;
    private readonly Func<CliProviderSessionState> _sessionState;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _parkIdleSessions;
    private readonly ICliCommandRunner _runner;
    private readonly ICliProcessInspector _processInspector;
    private readonly TimeSpan _installTimeout;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly Dictionary<Guid, CliInstallOperation> _operations = [];
    private Guid? _currentOperationId;
    private TaskCompletionSource? _installFinished;

    public string PackageName { get; }

    public NpmCliVersionManager(
        string packageName,
        string processDisplayName,
        Func<string?> resolveExecutable,
        Func<CliProviderSessionState> sessionState,
        Func<IReadOnlyList<string>, CancellationToken, Task> parkIdleSessions,
        ICliCommandRunner? runner = null,
        Func<string?>? resolveNpmExecutable = null,
        Func<string?>? resolvePackageDirectory = null,
        ICliProcessInspector? processInspector = null,
        TimeSpan? installTimeout = null)
    {
        PackageName = packageName;
        _processDisplayName = processDisplayName;
        _resolveExecutable = resolveExecutable;
        _sessionState = sessionState;
        _parkIdleSessions = parkIdleSessions;
        _runner = runner ?? new ProcessCliCommandRunner();
        _resolveNpmExecutable = resolveNpmExecutable ?? ResolveNpmExecutable;
        _resolvePackageDirectory = resolvePackageDirectory
            ?? (() => ResolveGlobalNpmPackageDirectory(packageName));
        _processInspector = processInspector ?? new SystemCliProcessInspector();
        _installTimeout = installTimeout ?? TimeSpan.FromMinutes(10);
    }

    public async Task<CliVersionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var installed = await GetInstalledVersionAsync(ct);
        var sessions = GetSessionState();
        var activeCount = sessions.BlockingSessionCount;
        var externalProcesses = FindExternalProcesses(sessions);
        var operation = CurrentOperation();
        var installing = operation?.Status is "queued" or "running";
        var blockedReason = activeCount > 0 ? FormatSessionCount(activeCount)
            : externalProcesses.Count > 0 ? FormatExternalProcessCount(externalProcesses.Count)
            : installing ? "Install already in progress"
            : null;
        return new CliVersionStatus(
            PackageName,
            installed.Version,
            installed.Error,
            activeCount,
            sessions.IdleSessionIds.Count,
            externalProcesses.Count,
            blockedReason is null,
            blockedReason,
            operation);
    }

    public async Task<CliInstalledVersionResult> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        var executable = _resolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
            return new CliInstalledVersionResult(null, "CLI executable not found");

        try
        {
            var result = await _runner.RunAsync(new CliCommand(executable, ["--version"]), ct);
            if (result.ExitCode != 0)
                return new CliInstalledVersionResult(null,
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"Version command exited with code {result.ExitCode}"
                        : result.CombinedOutput);

            var version = ParseInstalledVersion(result.CombinedOutput);
            return version is null
                ? new CliInstalledVersionResult(null, $"Could not parse CLI version from: {result.CombinedOutput.Trim()}")
                : new CliInstalledVersionResult(version, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CliInstalledVersionResult(null, ex.Message);
        }
    }

    public async Task<CliVersionCatalog> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var npm = _resolveNpmExecutable()
            ?? throw new InvalidOperationException("npm executable not found");
        var versionsResult = await _runner.RunAsync(
            new CliCommand(npm, ["view", PackageName, "versions", "--json"]), ct);
        if (versionsResult.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(versionsResult.CombinedOutput)
                ? $"npm view exited with code {versionsResult.ExitCode}"
                : versionsResult.CombinedOutput);

        var latestResult = await _runner.RunAsync(
            new CliCommand(npm, ["view", PackageName, "dist-tags.latest", "--json"]), ct);
        if (latestResult.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(latestResult.CombinedOutput)
                ? $"npm view latest exited with code {latestResult.ExitCode}"
                : latestResult.CombinedOutput);

        return new CliVersionCatalog(
            ParseAvailableVersions(versionsResult.StandardOutput),
            ParseLatestVersion(latestResult.StandardOutput));
    }

    public CliInstallOperation StartInstall(string requestedVersion)
    {
        requestedVersion = requestedVersion.Trim();
        if (!requestedVersion.Equals("latest", StringComparison.OrdinalIgnoreCase)
            && !StableVersionPattern.IsMatch(requestedVersion))
            throw new CliInstallRejectedException("invalid_version", "Version must be 'latest' or a stable x.y.z version");

        CliInstallOperation operation;
        TaskCompletionSource installFinished;
        lock (_gate)
        {
            var current = CurrentOperationUnsafe();
            if (current?.Status is "queued" or "running")
                throw new CliInstallRejectedException("install_in_progress", "Install already in progress");

            var sessions = GetSessionState();
            var activeCount = sessions.BlockingSessionCount;
            if (activeCount > 0)
                throw new CliInstallRejectedException("sessions_running", FormatSessionCount(activeCount), activeCount);
            var externalProcesses = FindExternalProcesses(sessions);
            if (externalProcesses.Count > 0)
                throw new CliInstallRejectedException(
                    "external_processes", FormatExternalProcessCount(externalProcesses.Count));

            operation = new CliInstallOperation(
                Guid.NewGuid(), "queued",
                requestedVersion.Equals("latest", StringComparison.OrdinalIgnoreCase) ? "latest" : requestedVersion,
                DateTimeOffset.UtcNow);
            _operations[operation.Id] = operation;
            _currentOperationId = operation.Id;
            installFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _installFinished = installFinished;
        }

        _ = Task.Run(() => RunInstallAsync(operation.Id, installFinished));
        return operation;
    }

    public CliInstallOperation? GetOperation(Guid id)
    {
        lock (_gate)
            return _operations.GetValueOrDefault(id);
    }

    public static string? ParseInstalledVersion(string output)
        => VersionPattern.Match(output) is { Success: true } match
            ? match.Groups["version"].Value
            : null;

    public static IReadOnlyList<string> ParseAvailableVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var versions = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(document.RootElement.GetString())
                => [document.RootElement.GetString()!],
            _ => [],
        };
        versions = versions.Where(version => StableVersionPattern.IsMatch(version)).ToList();
        versions.Sort(SemanticVersionDescendingComparer.Instance);
        return versions;
    }

    public static string? ParseLatestVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String
            && document.RootElement.GetString() is { } version
            && StableVersionPattern.IsMatch(version)
                ? version
                : null;
    }

    public static CliCommand BuildInstallCommand(
        string npmExecutable, string packageName, string requestedVersion)
        => new(npmExecutable,
            ["install", "--global", $"{packageName}@{requestedVersion}"]);

    /// <summary>
    /// Serializes every provider process launch against an install. A launch already in progress
    /// finishes first; launches admitted after an install is queued wait for its bounded operation.
    /// </summary>
    public async Task<T> RunWhenInstallIdleAsync<T>(
        Func<Task<T>> launch,
        CancellationToken ct = default)
    {
        while (true)
        {
            Task? installFinished;
            lock (_gate)
                installFinished = CurrentOperationUnsafe()?.Status is "queued" or "running"
                    ? _installFinished?.Task
                    : null;
            if (installFinished is not null)
            {
                await installFinished.WaitAsync(ct);
                continue;
            }

            await _launchGate.WaitAsync(ct);
            try
            {
                lock (_gate)
                    installFinished = CurrentOperationUnsafe()?.Status is "queued" or "running"
                        ? _installFinished?.Task
                        : null;
                if (installFinished is null)
                    return await launch();
            }
            finally
            {
                _launchGate.Release();
            }

            await installFinished.WaitAsync(ct);
        }
    }

    private async Task RunInstallAsync(Guid operationId, TaskCompletionSource installFinished)
    {
        var launchGateAcquired = false;
        using var timeout = new CancellationTokenSource(_installTimeout);
        try
        {
            var ct = timeout.Token;
            await _launchGate.WaitAsync(ct);
            launchGateAcquired = true;
            var sessions = GetSessionState();
            var activeCount = sessions.BlockingSessionCount;
            if (activeCount > 0)
            {
                Complete(operationId, "refused", "sessions_running", FormatSessionCount(activeCount));
                return;
            }

            if (sessions.IdleSessionIds.Count > 0)
                await _parkIdleSessions(sessions.IdleSessionIds, ct);

            sessions = GetSessionState();
            activeCount = sessions.BlockingSessionCount;
            if (activeCount > 0)
            {
                Complete(operationId, "refused", "sessions_running", FormatSessionCount(activeCount));
                return;
            }

            var remainingProcesses = FindPackageProcesses([]);
            if (remainingProcesses.Count > 0)
            {
                Complete(operationId, "refused", "external_processes",
                    FormatExternalProcessCount(remainingProcesses.Count));
                return;
            }

            Update(operationId, operation => operation with
            {
                Status = "running",
                StartedAt = DateTimeOffset.UtcNow,
            });

            var operation = GetOperation(operationId)!;
            var npm = _resolveNpmExecutable()
                ?? throw new InvalidOperationException("npm executable not found");
            var command = BuildInstallCommand(npm, PackageName, operation.RequestedVersion);
            var result = await _runner.RunAsync(command, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? $"npm install exited with code {result.ExitCode}"
                    : result.CombinedOutput);

            var installed = await GetInstalledVersionAsync(ct);
            Update(operationId, current => current with
            {
                Status = "completed",
                CompletedAt = DateTimeOffset.UtcNow,
                InstalledVersion = installed.Version,
                Error = installed.Error,
            });
        }
        catch (OperationCanceledException)
        {
            Complete(operationId, "failed", "install_timeout",
                $"CLI install exceeded the {_installTimeout.TotalMinutes:g}-minute time limit");
        }
        catch (Exception ex)
        {
            Complete(operationId, "failed", "npm_install_failed", ex.Message);
        }
        finally
        {
            if (launchGateAcquired) _launchGate.Release();
            installFinished.TrySetResult();
        }
    }

    private void Complete(Guid id, string status, string errorCode, string error)
        => Update(id, operation => operation with
        {
            Status = status,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorCode = errorCode,
            Error = error,
        });

    private void Update(Guid id, Func<CliInstallOperation, CliInstallOperation> update)
    {
        lock (_gate)
        {
            if (_operations.TryGetValue(id, out var operation))
                _operations[id] = update(operation);
        }
    }

    private CliInstallOperation? CurrentOperation()
    {
        lock (_gate)
            return CurrentOperationUnsafe();
    }

    private CliInstallOperation? CurrentOperationUnsafe()
        => _currentOperationId is { } id ? _operations.GetValueOrDefault(id) : null;

    private CliProviderSessionState GetSessionState()
    {
        var state = _sessionState() ?? CliProviderSessionState.Empty;
        return state with
        {
            BlockingSessionCount = Math.Max(0, state.BlockingSessionCount),
            IdleSessionIds = state.IdleSessionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OwnedProcessIds = state.OwnedProcessIds.Where(pid => pid > 0).Distinct().ToArray(),
        };
    }

    private IReadOnlyList<CliProcessInfo> FindExternalProcesses(CliProviderSessionState sessions)
        => FindPackageProcesses(sessions.OwnedProcessIds);

    private IReadOnlyList<CliProcessInfo> FindPackageProcesses(IReadOnlyCollection<int> ownedRootProcessIds)
    {
        var packageDirectory = _resolvePackageDirectory();
        if (string.IsNullOrWhiteSpace(packageDirectory)) return [];
        return _processInspector.FindPackageProcesses(packageDirectory, ownedRootProcessIds);
    }

    private static string FormatSessionCount(int count)
        => $"{count} {(count == 1 ? "session" : "sessions")} running";

    private string FormatExternalProcessCount(int count)
        => $"{count} {_processDisplayName} {(count == 1 ? "process" : "processes")} still running outside Leaf";

    public static string? ResolveGlobalNpmPackageDirectory(string packageName)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData)) return null;
        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? null
            : Path.Combine([applicationData, "npm", "node_modules", .. segments]);
    }

    private static string? ResolveNpmExecutable()
    {
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "npm.cmd", "npm.exe", "npm" }
            : new[] { "npm" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    private sealed class SemanticVersionDescendingComparer : IComparer<string>
    {
        public static readonly SemanticVersionDescendingComparer Instance = new();

        public int Compare(string? left, string? right) => -CompareAscending(left, right);

        private static int CompareAscending(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var l = ParsedVersion.Parse(left);
            var r = ParsedVersion.Parse(right);
            for (var i = 0; i < 3; i++)
            {
                var numeric = l.Core[i].CompareTo(r.Core[i]);
                if (numeric != 0) return numeric;
            }

            if (l.Prerelease.Count == 0 && r.Prerelease.Count > 0) return 1;
            if (l.Prerelease.Count > 0 && r.Prerelease.Count == 0) return -1;
            for (var i = 0; i < Math.Max(l.Prerelease.Count, r.Prerelease.Count); i++)
            {
                if (i >= l.Prerelease.Count) return -1;
                if (i >= r.Prerelease.Count) return 1;
                var lToken = l.Prerelease[i];
                var rToken = r.Prerelease[i];
                var lNumeric = long.TryParse(lToken, out var ln);
                var rNumeric = long.TryParse(rToken, out var rn);
                if (lNumeric && rNumeric)
                {
                    var comparison = ln.CompareTo(rn);
                    if (comparison != 0) return comparison;
                }
                else if (lNumeric != rNumeric)
                {
                    return lNumeric ? -1 : 1;
                }
                else
                {
                    var comparison = string.CompareOrdinal(lToken, rToken);
                    if (comparison != 0) return comparison;
                }
            }
            return 0;
        }

        private sealed record ParsedVersion(long[] Core, IReadOnlyList<string> Prerelease)
        {
            public static ParsedVersion Parse(string value)
            {
                var withoutBuild = value.Split('+', 2)[0];
                var pieces = withoutBuild.Split('-', 2);
                var core = pieces[0].Split('.').Select(part => long.TryParse(part, out var n) ? n : 0L)
                    .Concat([0L, 0L, 0L]).Take(3).ToArray();
                var prerelease = pieces.Length > 1 ? pieces[1].Split('.') : [];
                return new ParsedVersion(core, prerelease);
            }
        }
    }
}
