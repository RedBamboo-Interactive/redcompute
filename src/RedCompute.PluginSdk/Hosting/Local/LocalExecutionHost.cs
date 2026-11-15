using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Hardware;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// Node zero: the machine RedCompute runs on. Every member forwards to the code that did the work
/// before the host existed (<see cref="SessionProcessContainment"/>, <see cref="OwnedProcessKiller"/>,
/// <see cref="PlatformSupport"/>, System.IO), so a launch through it hands the OS the same
/// <see cref="ProcessStartInfo"/> through the same entry point as the site did.
/// </summary>
public sealed class LocalExecutionHost : IExecutionHost
{
    /// <summary>
    /// The host a provider uses when RedCompute hands it none (tests, tools). Its node id is the
    /// registry's alias for the local node.
    /// </summary>
    public static LocalExecutionHost Shared { get; } = new(INodeRegistry.LocalAlias);

    public LocalExecutionHost(string nodeId)
    {
        NodeId = nodeId;
        PathRules = new LocalPathRules();
        Processes = new LocalProcesses(this);
        Containment = new LocalContainment();
        Files = new LocalFileSystem(this);
        Network = new LocalNetwork();
        Platform = new LocalPlatform();
        Hardware = new LocalHardware();
        Queries = new LocalQueries();
    }

    public string NodeId { get; }
    public HostPath Path(string value) => new(NodeId, value);
    public IHostPathRules PathRules { get; }
    public IHostProcesses Processes { get; }
    public IHostContainment Containment { get; }
    public IHostFileSystem Files { get; }
    public IHostNetwork Network { get; }
    public IHostPlatform Platform { get; }
    public IHostHardware Hardware { get; }
    public IHostQueries Queries { get; }

    /// <summary>
    /// The single point where this host hands a start info to the OS: (start info, containment,
    /// owner) to the process. Tests replace it to capture launches; null is the real entry point.
    /// </summary>
    internal Func<ProcessStartInfo, ContainmentMode, string, Process?>? StartProcess { get; set; }

    /// <summary>
    /// The start info a site built before the host existed, from its spec: the same fields, set in
    /// the same way. <c>Environment</c> is touched only for an overlay or a non-inherited base,
    /// because touching it makes .NET pass an explicit environment block.
    /// </summary>
    internal static ProcessStartInfo ToStartInfo(HostSpawnSpec spec)
    {
        if (spec.ArgumentList is not null && spec.RawArguments is not null)
            throw new ArgumentException("A spawn spec has an argument list or raw arguments, not both", nameof(spec));

        var start = new ProcessStartInfo
        {
            FileName = spec.FileName,
            CreateNoWindow = spec.CreateNoWindow,
            UseShellExecute = spec.UseShellExecute,
            RedirectStandardInput = spec.StandardInput == StdioMode.Pipe,
            RedirectStandardOutput = spec.StandardOutput == StdioMode.Pipe,
            RedirectStandardError = spec.StandardError == StdioMode.Pipe,
        };
        if (spec.RawArguments is not null) start.Arguments = spec.RawArguments;
        if (spec.ArgumentList is not null)
            foreach (var argument in spec.ArgumentList) start.ArgumentList.Add(argument);
        if (spec.WorkingDirectory is { } directory) start.WorkingDirectory = directory.Value;
        if (spec.StandardInputEncoding is not null) start.StandardInputEncoding = spec.StandardInputEncoding;
        if (spec.StandardOutputEncoding is not null) start.StandardOutputEncoding = spec.StandardOutputEncoding;
        if (spec.StandardErrorEncoding is not null) start.StandardErrorEncoding = spec.StandardErrorEncoding;

        switch (spec.EnvironmentBase)
        {
            case EnvironmentBase.Inherit:
                break;
            case EnvironmentBase.Empty:
                start.Environment.Clear();
                break;
            default:
                throw new NotSupportedException(
                    $"Environment base {spec.EnvironmentBase} is only available to desktop-parented launches");
        }
        if (spec.EnvironmentOverlay is not null)
            foreach (var (key, value) in spec.EnvironmentOverlay) start.Environment[key] = value;
        return start;
    }

    /// <summary>"none" or "session:&lt;owner&gt;", the containment notation of the launch goldens.</summary>
    internal static string DescribeContainment(ContainmentMode mode, string owner) => mode switch
    {
        ContainmentMode.None => "none",
        ContainmentMode.Session => $"session:{owner}",
        ContainmentMode.Kept => $"kept:{owner}",
        _ => $"{mode.ToString().ToLowerInvariant()}:{owner}",
    };

    private Process? Launch(HostSpawnSpec spec)
    {
        if (spec.Containment is not (ContainmentMode.None or ContainmentMode.Session))
            throw new ArgumentException(
                $"Containment {spec.Containment} is not a plain launch; use {nameof(IHostContainment)}", nameof(spec));
        var start = ToStartInfo(spec);
        if (StartProcess is { } hook) return hook(start, spec.Containment, spec.OwnerLabel);
        return spec.Containment == ContainmentMode.Session
            ? SessionProcessContainment.Start(start, spec.OwnerLabel)
            : Process.Start(start);
    }

    private sealed class LocalPathRules : IHostPathRules
    {
        public char DirectorySeparator => System.IO.Path.DirectorySeparatorChar;
        public StringComparison Comparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        public bool IsPathFullyQualified(string path) => System.IO.Path.IsPathFullyQualified(path);
        public string GetFullPath(string path) => System.IO.Path.GetFullPath(path);
        public string Combine(params string[] parts) => System.IO.Path.Combine(parts);
    }

    private sealed class LocalProcesses(LocalExecutionHost host) : IHostProcesses
    {
        public IHostProcess? Start(HostSpawnSpec spec)
            => host.Launch(spec) is { } process ? new LocalHostProcess(host.NodeId, process) : null;

        public bool TryKillRecorded(int? pid, string cliName, Action<string>? log = null)
            => OwnedProcessKiller.TryKill(pid, cliName, log);

        public bool KillIdentity(int pid, long createdUtcTicks, string owner)
            => SessionProcessContainment.KillIdentity(pid, createdUtcTicks, owner);

        public IReadOnlyList<HostProcessInfo> List()
        {
            var parents = ParentMap();
            var result = new List<HostProcessInfo>();
            foreach (var process in Process.GetProcesses())
                using (process)
                    if (Describe(process, parents, executablePath: false) is { } info) result.Add(info);
            return result;
        }

        public HostProcessInfo? Find(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return Describe(process, ParentMap(), executablePath: true);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Windows only: the Toolhelp parent map containment already reads. Elsewhere parents read 0.</summary>
        private static Dictionary<int, int>? ParentMap()
        {
            if (!OperatingSystem.IsWindows()) return null;
            var map = new Dictionary<int, int>();
            foreach (var (pid, parent) in SessionProcessContainment.Native.Snapshot()) map[pid] = parent;
            return map;
        }

        private static HostProcessInfo? Describe(Process process, Dictionary<int, int>? parents, bool executablePath)
        {
            try
            {
                var pid = process.Id;
                string? path = null;
                if (executablePath)
                    try { path = process.MainModule?.FileName; } catch { }
                return new HostProcessInfo(pid, parents?.GetValueOrDefault(pid) ?? 0, process.ProcessName, path,
                    SessionProcessContainment.CreationUtcTicks(process));
            }
            catch (InvalidOperationException)
            {
                return null; // exited while being read
            }
        }
    }

    private sealed class LocalContainment : IHostContainment
    {
        public Action<string>? Log
        {
            get => SessionProcessContainment.Log;
            set => SessionProcessContainment.Log = value;
        }

        public Func<int, long, bool>? IsProtected
        {
            get => SessionProcessContainment.IsProtected;
            set => SessionProcessContainment.IsProtected = value;
        }

        public string LedgerDirectory => SessionProcessContainment.LedgerDirectory;
        public int SweepLedgers() => SessionProcessContainment.SweepLedgers();
        public bool IsSessionMember(int pid) => SessionProcessContainment.IsSessionMember(pid);
        public IContainedTree? FindByRoot(int rootPid) => SessionProcessContainment.FindByRoot(rootPid);

        public IContainedTree? Keep(int rootPid, string owner, string ledgerPath)
        {
            Process root;
            try { root = Process.GetProcessById(rootPid); }
            catch (ArgumentException) { return null; }
            using (root) return SessionProcessContainment.Keep(root, owner, ledgerPath);
        }

        public IContainedTree? Keep(int rootPid, long createdUtcTicks, string owner, string ledgerPath)
        {
            Process root;
            try { root = Process.GetProcessById(rootPid); }
            catch (ArgumentException) { return null; }
            using (root)
            {
                // The handle that proves the identity is the one the job is given.
                if (SessionProcessContainment.CreationUtcTicks(root) != createdUtcTicks) return null;
                return SessionProcessContainment.Keep(root, owner, ledgerPath);
            }
        }

        public IContainedTree? Resume(string owner, string ledgerPath)
            => SessionProcessContainment.Resume(owner, ledgerPath);

        public IContainedTree? Resume(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities)
            => SessionProcessContainment.Resume(owner, ledgerPath, identities);

        /// <summary>
        /// Create the command outside RedCompute's jobs, suspended; put it in its own kept job; only
        /// then let it run, so nothing it starts is ever outside the kept tree.
        /// </summary>
        public DetachedLaunch StartDetachedKept(DetachedSpawnSpec spec)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("desktop-parented launches are supported on Windows only");
            var started = DetachedProcessLauncher.StartSuspended(spec.CommandLine, spec.WorkingDirectory.Value, spec.Environment);

            using (started)
            {
                ContainedProcessTree? tree = null;
                try
                {
                    using var process = Process.GetProcessById(started.Pid);
                    var created = SessionProcessContainment.CreationUtcTicks(process);
                    tree = SessionProcessContainment.Keep(process, spec.Owner, spec.LedgerPath);
                    if (tree is null) throw new InvalidOperationException("could not contain the launched process");
                    started.Resume();
                    return new DetachedLaunch(started.Pid, created, tree, started.Via);
                }
                catch
                {
                    started.Abort();
                    tree?.Reap();
                    throw;
                }
            }
        }
    }

    private sealed class LocalFileSystem(LocalExecutionHost host) : IHostFileSystem
    {
        public bool FileExists(HostPath path) => File.Exists(path.Value);
        public bool DirectoryExists(HostPath path) => Directory.Exists(path.Value);

        public HostFileStat Stat(HostPath path)
        {
            var file = new FileInfo(path.Value);
            if (file.Exists)
                return new HostFileStat(true, false, file.Length, file.Attributes, file.LastWriteTimeUtc);
            var directory = new DirectoryInfo(path.Value);
            return directory.Exists
                ? new HostFileStat(true, true, 0, directory.Attributes, directory.LastWriteTimeUtc)
                : new HostFileStat(false, false, 0, default, default);
        }

        public string ReadAllText(HostPath path) => File.ReadAllText(path.Value);
        public Task<string> ReadAllTextAsync(HostPath path, CancellationToken ct = default) => File.ReadAllTextAsync(path.Value, ct);
        public IEnumerable<string> ReadLines(HostPath path) => File.ReadLines(path.Value);
        public byte[] ReadAllBytes(HostPath path) => File.ReadAllBytes(path.Value);
        public Stream OpenRead(HostPath path) => File.OpenRead(path.Value);
        public Stream OpenRead(HostPath path, FileShare share) => new FileStream(path.Value, FileMode.Open, FileAccess.Read, share);

        public string? ResolveLinkTarget(HostPath path, bool returnFinalTarget)
            => new FileInfo(path.Value).ResolveLinkTarget(returnFinalTarget)?.FullName;

        public FileAttributes GetAttributes(HostPath path) => File.GetAttributes(path.Value);

        public void WriteAllText(HostPath path, string contents, Encoding? encoding = null)
        {
            if (encoding is null) File.WriteAllText(path.Value, contents);
            else File.WriteAllText(path.Value, contents, encoding);
        }

        public Task WriteAllTextAsync(HostPath path, string contents, Encoding? encoding = null, CancellationToken ct = default)
            => encoding is null
                ? File.WriteAllTextAsync(path.Value, contents, ct)
                : File.WriteAllTextAsync(path.Value, contents, encoding, ct);

        public void WriteAllBytes(HostPath path, byte[] bytes) => File.WriteAllBytes(path.Value, bytes);

        public void ReplaceAtomically(HostPath path, string contents)
        {
            var temp = path.Value + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path.Value, overwrite: true);
        }

        public void CreateDirectory(HostPath path) => Directory.CreateDirectory(path.Value);

        public IEnumerable<HostPath> EnumerateFiles(HostPath directory, string pattern, SearchOption option)
            => Directory.EnumerateFiles(directory.Value, pattern, option).Select(host.Path);

        public IEnumerable<HostPath> EnumerateDirectories(HostPath directory, string pattern, SearchOption option)
            => Directory.EnumerateDirectories(directory.Value, pattern, option).Select(host.Path);

        public void DeleteFile(HostPath path) => File.Delete(path.Value);
        public void DeleteDirectory(HostPath path, bool recursive) => Directory.Delete(path.Value, recursive);

        public HostPath CreateTempDirectory(string prefix)
        {
            var path = IsolatedWorkingDirectory.NewPath(prefix);
            Directory.CreateDirectory(path);
            return host.Path(path);
        }

        public HostPath TempPath => host.Path(System.IO.Path.GetTempPath());
    }

    private sealed class LocalNetwork : IHostNetwork
    {
        public bool IsListening(int port)
            => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == port);

        public Uri ResolveServiceUri(Uri hostLocalUri) => hostLocalUri;

        public Task<IHostPortForward> ReverseForwardAsync(Uri serverLocalUri, CancellationToken ct = default)
            => Task.FromResult<IHostPortForward>(new IdentityForward(serverLocalUri));

        private sealed class IdentityForward(Uri uri) : IHostPortForward
        {
            public Uri ServerSideUri { get; } = uri;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class LocalPlatform : IHostPlatform
    {
        private readonly Lazy<HostPlatformInfo> _info = new(() => new HostPlatformInfo(
            OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "macos"
                : "unknown",
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.MachineName,
            Environment.UserName));

        public HostPlatformInfo Info => _info.Value;
        public bool IsWindows => OperatingSystem.IsWindows();
        public bool IsLinux => OperatingSystem.IsLinux();
        public bool CanLaunchLocalBackends => PlatformSupport.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    }

    /// <summary>
    /// The SQLite read OpenCode's native reader and title lookup made themselves: the same connection
    /// string (built the same way), the same busy timeout, parameters bound with <c>AddWithValue</c>.
    /// Microsoft.Data.Sqlite is RedCompute's own copy (the SDK compiles against it privately).
    /// </summary>
    private sealed class LocalQueries : IHostQueries
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QuerySqliteReadOnly(HostPath database, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, SqliteReadOptions? options = null)
        {
            options ??= SqliteReadOptions.Default;
            using var connection = new SqliteConnection(options.Pooling
                ? $"Data Source={database.Value};Mode=ReadOnly"
                : $"Data Source={database.Value};Mode=ReadOnly;Pooling=False");
            connection.Open();
            if (options.BusyTimeoutMilliseconds is { } busyTimeout)
            {
                using var timeout = connection.CreateCommand();
                timeout.CommandText = $"PRAGMA busy_timeout={busyTimeout}";
                timeout.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameters is not null)
                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }
}
