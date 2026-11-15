using System.Text;
using RedCompute.Core.Hardware;

namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// A path that belongs to one node; its rules are that node's (separator, case, qualification).
/// Crossing the host boundary as a <see cref="HostPath"/> says which machine a path is on.
/// </summary>
public readonly record struct HostPath(string NodeId, string Value)
{
    /// <summary>The bare path: display and logging are unchanged.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// The machine a provider's work runs on: its processes, files, ports, platform and hardware.
/// Node zero's <see cref="Local.LocalExecutionHost"/> is today's code, moved behind this surface; a
/// remote node will implement the same surface over a link (later phases).
/// </summary>
public interface IExecutionHost
{
    /// <summary>The node this host executes on.</summary>
    string NodeId { get; }

    /// <summary>Binds a string to this host. No normalisation.</summary>
    HostPath Path(string value);

    IHostPathRules PathRules { get; }
    IHostProcesses Processes { get; }
    IHostContainment Containment { get; }
    IHostFileSystem Files { get; }
    IHostNetwork Network { get; }
    IHostPlatform Platform { get; }
    IHostHardware Hardware { get; }
    IHostQueries Queries { get; }
}

public interface IHostPathRules
{
    char DirectorySeparator { get; }

    /// <summary>The host's usual path comparison. Locally each site keeps the comparison it uses today.</summary>
    StringComparison Comparison { get; }

    bool IsPathFullyQualified(string path);
    string GetFullPath(string path);
    string Combine(params string[] parts);
}

// ---- processes ----------------------------------------------------------------------------------

/// <summary>How a launched process tree is tied to RedCompute.</summary>
public enum ContainmentMode
{
    /// <summary>Uncontained, as today's backend launches (ComfyUI, LocalWsl, Kev, git, docker, npm, the Claude fork probe).</summary>
    None,

    /// <summary><see cref="SessionProcessContainment.Start"/>: kill-on-close job, parentage escapee tracking, ledger.</summary>
    Session,

    /// <summary>A job without kill-on-close, ledgered (long-lived). Reached through <see cref="IHostContainment.Keep"/>.</summary>
    Kept,

    /// <summary>Suspended, desktop-shell parent, user environment block, then kept. Reached through <see cref="IHostContainment.StartDetachedKept"/>.</summary>
    DetachedDesktop,
}

/// <summary>The environment a launch starts from, before its overlay.</summary>
public enum EnvironmentBase
{
    /// <summary>RedCompute's own environment (every Phase 1 <see cref="IHostProcesses.Start"/> site).</summary>
    Inherit,

    /// <summary>The interactive user's environment block (desktop-parented launches only).</summary>
    UserProfile,

    /// <summary>Nothing but the overlay.</summary>
    Empty,
}

/// <summary>Exactly the two states every launch site uses today.</summary>
public enum StdioMode { NotRedirected, Pipe }

/// <summary>
/// What to launch, independent of the machine. The local host turns it into the same
/// <see cref="System.Diagnostics.ProcessStartInfo"/> the site built before, field for field.
/// </summary>
public sealed record HostSpawnSpec
{
    public required string FileName { get; init; }

    /// <summary>Arguments quoted by the OS rules. At most one of this and <see cref="RawArguments"/>.</summary>
    public IReadOnlyList<string>? ArgumentList { get; init; }

    /// <summary>A command tail passed verbatim, for sites that build their own quoting.</summary>
    public string? RawArguments { get; init; }

    /// <summary>Null inherits RedCompute's working directory, as an unset start info does.</summary>
    public HostPath? WorkingDirectory { get; init; }

    /// <summary>
    /// Variables set over the base, in order. Null or empty leaves the environment untouched; any
    /// entry makes the launch pass an explicit environment block, as setting
    /// <c>ProcessStartInfo.Environment[key]</c> does. A site that sets variables in a loop over a
    /// possibly empty dictionary is therefore reproduced exactly.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOverlay { get; init; }

    public EnvironmentBase EnvironmentBase { get; init; } = EnvironmentBase.Inherit;
    public StdioMode StandardInput { get; init; }
    public StdioMode StandardOutput { get; init; }
    public StdioMode StandardError { get; init; }

    /// <summary>Copied verbatim (for example <c>new UTF8Encoding(false)</c>); null keeps the default.</summary>
    public Encoding? StandardInputEncoding { get; init; }
    public Encoding? StandardOutputEncoding { get; init; }
    public Encoding? StandardErrorEncoding { get; init; }
    public bool CreateNoWindow { get; init; }

    /// <summary>False at every provider site.</summary>
    public bool UseShellExecute { get; init; }

    public required ContainmentMode Containment { get; init; }

    /// <summary>Who owns the tree: "claude-code", "codex", "comfyui", ... Names a session tree and its ledger.</summary>
    public required string OwnerLabel { get; init; }
}

/// <summary>
/// A launched process, shaped like the <see cref="System.Diagnostics.Process"/> members providers
/// use. The local implementation forwards each member and returns the process's own stream objects.
/// </summary>
public interface IHostProcess : IDisposable
{
    string NodeId { get; }
    int Id { get; }

    /// <summary><c>Process.StartTime</c> in UTC.</summary>
    DateTime StartTimeUtc { get; }

    /// <summary>Creation time as UTC ticks, the identity containment and the long-lived registry share.</summary>
    long CreatedUtcTicks { get; }

    /// <summary>Throws as <c>Process.StandardInput</c> does when stdin is not piped.</summary>
    StreamWriter StandardInput { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }

    void BeginOutputReadLine();
    void BeginErrorReadLine();

    /// <summary>One line of stdout; null at end of stream (as <c>DataReceivedEventArgs.Data</c>).</summary>
    event Action<string?>? OutputLine;

    /// <summary>One line of stderr; null at end of stream.</summary>
    event Action<string?>? ErrorLine;

    bool EnableRaisingEvents { get; set; }
    event EventHandler? Exited;
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill(bool entireProcessTree);
    bool WaitForExit(int milliseconds);
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>A process seen on the host. <see cref="CreatedUtcTicks"/> is 0 when the OS would not say.</summary>
public sealed record HostProcessInfo(int Pid, int ParentPid, string Name, string? ExecutablePath, long CreatedUtcTicks);

public interface IHostProcesses
{
    /// <summary>
    /// Synchronous like <c>Process.Start</c>; null when the OS returns no process. Containment failing
    /// never fails the launch. Never starts readers and never awaits.
    /// </summary>
    IHostProcess? Start(HostSpawnSpec spec);

    /// <summary>
    /// Kill a PID recorded by an earlier run, only while it still looks like the CLI
    /// (<see cref="OwnedProcessKiller.TryKill"/>): the CLI's own name, or node, cmd or docker.
    /// </summary>
    bool TryKillRecorded(int? pid, string cliName, Action<string>? log = null);

    /// <summary>Kill a tree only while PID and creation time both match, sparing registered long-lived processes.</summary>
    bool KillIdentity(int pid, long createdUtcTicks, string owner);

    IReadOnlyList<HostProcessInfo> List();
    HostProcessInfo? Find(int pid);
}

// ---- containment ---------------------------------------------------------------------------------

/// <summary>A contained process tree (<see cref="ContainedProcessTree"/> locally).</summary>
public interface IContainedTree
{
    string Owner { get; }
    int RootPid { get; }
    long RootCreatedUtcTicks { get; }
    bool IsReaped { get; }
    bool IsKept { get; }
    DateTimeOffset? LastExitAt { get; }
    IReadOnlyList<(int Pid, long CreatedUtcTicks)> LiveProcesses { get; }
    IReadOnlyList<(int Pid, long CreatedUtcTicks)> Escapees { get; }
    IReadOnlyList<(int Pid, long CreatedUtcTicks)> Refresh();
    bool Tracks(int pid, long createdUtcTicks);
    void Reap();
    void Release(bool deleteLedger);
    event Action<IContainedTree>? Reaped;
}

/// <summary>A desktop-parented launch: one cmd.exe line, the user's environment plus an overlay.</summary>
public sealed record DetachedSpawnSpec(string CommandLine, HostPath WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment, string Owner, string LedgerPath);

public interface IHostContainment
{
    Action<string>? Log { get; set; }

    /// <summary>The long-lived registry's guard: true for (pid, creation ticks) a reap must leave alone.</summary>
    Func<int, long, bool>? IsProtected { get; set; }

    string LedgerDirectory { get; }

    /// <summary>Boot: reap what an earlier run's sessions left behind, never this run's ledgers.</summary>
    int SweepLedgers();

    bool IsSessionMember(int pid);
    IContainedTree? FindByRoot(int rootPid);
    IContainedTree? Keep(int rootPid, string owner, string ledgerPath);
    IContainedTree? Resume(string owner, string ledgerPath);
    IContainedTree? Resume(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities);

    /// <summary>Start suspended under the desktop shell, keep, then resume: the 4f3ce47 sequence as one primitive.</summary>
    (int Pid, long CreatedUtcTicks, IContainedTree? Tree) StartDetachedKept(DetachedSpawnSpec spec);
}

// ---- files ---------------------------------------------------------------------------------------

/// <summary><see cref="Attributes"/> carries <see cref="FileAttributes.ReparsePoint"/>.</summary>
public sealed record HostFileStat(bool Exists, bool IsDirectory, long Length, FileAttributes Attributes,
    DateTime LastWriteTimeUtc);

/// <summary>
/// The System.IO calls sites make today, with the same encodings, exceptions and enumeration
/// options. Synchronous members exist because several sites run in constructors at boot.
/// </summary>
public interface IHostFileSystem
{
    bool FileExists(HostPath path);
    bool DirectoryExists(HostPath path);
    HostFileStat Stat(HostPath path);
    string ReadAllText(HostPath path);
    Task<string> ReadAllTextAsync(HostPath path, CancellationToken ct = default);
    IEnumerable<string> ReadLines(HostPath path);
    byte[] ReadAllBytes(HostPath path);
    Stream OpenRead(HostPath path);

    /// <summary>
    /// Opens for reading while others keep the file open under <paramref name="share"/> (for example a
    /// CLI still appending to its transcript: <c>FileShare.ReadWrite | FileShare.Delete</c>).
    /// <see cref="OpenRead(HostPath)"/> is this with <see cref="FileShare.Read"/>.
    /// </summary>
    Stream OpenRead(HostPath path, FileShare share);

    /// <summary>
    /// The full path a link at <paramref name="path"/> points to (its final target when
    /// <paramref name="returnFinalTarget"/>), or null when it is not a link: <c>FileInfo.ResolveLinkTarget</c>,
    /// with its exceptions.
    /// </summary>
    string? ResolveLinkTarget(HostPath path, bool returnFinalTarget);

    /// <summary><c>File.GetAttributes</c>: a file's or a directory's attributes, throwing when neither exists.</summary>
    FileAttributes GetAttributes(HostPath path);
    void WriteAllText(HostPath path, string contents, Encoding? encoding = null);
    Task WriteAllTextAsync(HostPath path, string contents, Encoding? encoding = null, CancellationToken ct = default);
    void WriteAllBytes(HostPath path, byte[] bytes);

    /// <summary>Write a temporary sibling, then move it over <paramref name="path"/>.</summary>
    void ReplaceAtomically(HostPath path, string contents);

    void CreateDirectory(HostPath path);
    IEnumerable<HostPath> EnumerateFiles(HostPath directory, string pattern, SearchOption option);
    IEnumerable<HostPath> EnumerateDirectories(HostPath directory, string pattern, SearchOption option);
    void DeleteFile(HostPath path);
    void DeleteDirectory(HostPath path, bool recursive);

    /// <summary>A new empty directory under <see cref="TempPath"/>, named as <see cref="IsolatedWorkingDirectory"/> names them.</summary>
    HostPath CreateTempDirectory(string prefix);

    HostPath TempPath { get; }
}

// ---- network -------------------------------------------------------------------------------------

/// <summary>A server-side endpoint reachable from the host. Disposing it ends the forward.</summary>
public interface IHostPortForward : IAsyncDisposable
{
    Uri ServerSideUri { get; }
}

public interface IHostNetwork
{
    /// <summary>True when a TCP listener on the host holds <paramref name="port"/>.</summary>
    bool IsListening(int port);

    /// <summary>How the server reaches a service the host serves at <paramref name="hostLocalUri"/>. Locally the same Uri.</summary>
    Uri ResolveServiceUri(Uri hostLocalUri);

    /// <summary>Makes a server-local endpoint reachable from the host. Locally the same Uri.</summary>
    Task<IHostPortForward> ReverseForwardAsync(Uri serverLocalUri, CancellationToken ct = default);
}

// ---- platform ------------------------------------------------------------------------------------

public sealed record HostPlatformInfo(string Os, string OsDescription, string Architecture, string MachineName,
    string UserName);

public interface IHostPlatform
{
    HostPlatformInfo Info { get; }
    bool IsWindows { get; }
    bool IsLinux { get; }

    /// <summary><see cref="PlatformSupport.CanLaunchLocalBackends"/> for this host.</summary>
    bool CanLaunchLocalBackends { get; }

    string? GetEnvironmentVariable(string name);
    string GetFolderPath(Environment.SpecialFolder folder);
}

// ---- hardware ------------------------------------------------------------------------------------

public interface IHostHardware
{
    /// <summary>
    /// One sample of CPU, RAM, GPUs and per-process VRAM. Capability attribution
    /// (<see cref="GpuProcessInfo.CapabilitySlug"/>, <see cref="GpuInfo.CapabilityVram"/>) stays empty:
    /// RedCompute attributes it.
    /// </summary>
    HardwareSnapshot Sample();
}

// ---- host-side query -----------------------------------------------------------------------------

/// <summary>
/// How <see cref="IHostQueries.QuerySqliteReadOnly"/> opens the database, so each caller keeps the
/// connection it opened before the host: always <c>Mode=ReadOnly</c>, pooled or not, with or without
/// a busy timeout.
/// </summary>
public sealed record SqliteReadOptions
{
    /// <summary>Pooled, no busy timeout: <c>Data Source=...;Mode=ReadOnly</c>.</summary>
    public static SqliteReadOptions Default { get; } = new();

    /// <summary>False appends <c>;Pooling=False</c>: each query opens and closes the file.</summary>
    public bool Pooling { get; init; } = true;

    /// <summary><c>PRAGMA busy_timeout</c> run on the connection before the query; null runs none.</summary>
    public int? BusyTimeoutMilliseconds { get; init; }
}

public interface IHostQueries
{
    /// <summary>
    /// A read-only query against a live SQLite file on the host. Rows come back as data, so a remote
    /// host can serialise them; mapping stays with the caller. Each row maps column name to value as
    /// SQLite stored it (<see cref="long"/>, <see cref="double"/>, <see cref="string"/> or byte[]), NULL
    /// as null. Opening or running the query throws as the SQLite provider does.
    /// </summary>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> QuerySqliteReadOnly(HostPath database, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, SqliteReadOptions? options = null);
}
