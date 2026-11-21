using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// A remote node's execution host (Phase 4, design 2.4, 2.2), handed out by <c>INodeRegistry.HostFor</c> only for a node
/// that is eligible. What a remote node offers is exactly two things:
/// <list type="bullet">
/// <item><see cref="Runtimes"/>: the runtimes its owner registered there, over <c>runtime.v1</c> (a start takes an id
/// and nothing else);</item>
/// <item><see cref="Network"/>'s <see cref="IHostNetwork.CreateServiceHandler"/> and
/// <see cref="IHostNetwork.ConnectWebSocketAsync"/>: HTTP and WebSocket to one of those runtimes, over the node link's
/// forward (no listener anywhere). <see cref="IHostNetwork.ResolveServiceUri"/> returns a <c>.leaf-node.invalid</c>
/// sentinel that never resolves, so code that still builds its own client fails closed and visibly.</item>
/// </list>
/// Every other member (processes, containment, files, host queries, the hardware sampler, the environment and folders,
/// a reverse forward, a full path) throws <see cref="RemoteOperationNotAllowedException"/>: fail closed, by construction.
/// </summary>
public sealed class RemoteExecutionHost : IExecutionHost
{
    /// <summary>Every sentinel host ends with this; <c>.invalid</c> never resolves (RFC 6761).</summary>
    public const string SentinelSuffix = ".leaf-node.invalid";

    private readonly Func<NodeDescriptor?> _node;
    private readonly ForwardClient _forward;
    private readonly RemoteHostRuntimes _runtimes;

    internal RemoteExecutionHost(string nodeId, Func<NodeDescriptor?> node, ForwardClient forward, RemoteHostRuntimes runtimes,
        string? runtimeId = null)
    {
        NodeId = nodeId;
        _node = node;
        _forward = forward;
        _runtimes = runtimes;
        RuntimeId = runtimeId;
        Network = new RemoteNetwork(this);
        Platform = new RemotePlatform(this);
        Hardware = new RemoteHardware(this);
        Processes = new RemoteProcesses(nodeId);
        Containment = new RemoteContainment(nodeId);
        Files = new RemoteFiles(nodeId);
        Queries = new RemoteQueries(nodeId);
    }

    public string NodeId { get; }

    /// <summary>The runtime this host's network reaches whatever port a URI names; null: the runtime serving that port.</summary>
    public string? RuntimeId { get; }

    /// <summary>The same node, its network bound to one runtime (a per-node provider's host, design 3.1).</summary>
    public RemoteExecutionHost ForRuntime(string runtimeId)
        => ForwardOpen.IsRuntimeId(runtimeId)
            ? new RemoteExecutionHost(NodeId, _node, _forward, _runtimes, runtimeId)
            : throw new ArgumentException($"'{runtimeId}' is not a runtime id.", nameof(runtimeId));

    public HostPath Path(string value) => new(NodeId, value);

    public IHostPathRules PathRules => IsWindows ? RemotePathRules.Windows(NodeId) : RemotePathRules.Posix(NodeId);
    public IHostProcesses Processes { get; }
    public IHostContainment Containment { get; }
    public IHostFileSystem Files { get; }
    public IHostNetwork Network { get; }
    public IHostPlatform Platform { get; }
    public IHostHardware Hardware { get; }
    public IHostQueries Queries { get; }
    public IHostRuntimes Runtimes => _runtimes;

    private NodeDescriptor? Node => _node();

    private bool IsWindows => string.Equals(Node?.Os, "windows", StringComparison.OrdinalIgnoreCase);

    private static RemoteOperationNotAllowedException Refused(string nodeId, string operation) => new(nodeId, operation);

    /// <summary>The runtime a host-local URI names: the bound one, else the only runtime the node reported on that port.</summary>
    internal string? RuntimeFor(Uri hostLocalUri)
    {
        if (RuntimeId is not null) return RuntimeId;
        var serving = _runtimes.List().Where(runtime => runtime.Ports.Contains(hostLocalUri.Port)).Take(2).ToList();
        return serving.Count == 1 ? serving[0].Id : null;
    }

    /// <summary><c>http://comfyui-1.node-01jx….leaf-node.invalid:8188</c>: says what was meant, and never resolves.</summary>
    internal string SentinelHost(string? runtimeId) => $"{Label(runtimeId ?? "service")}.{Label(NodeId)}{SentinelSuffix}";

    private static string Label(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var label = builder.ToString().Trim('-');
        return label.Length == 0 ? "x" : label.Length <= 63 ? label : label[..63].TrimEnd('-');
    }

    // ================================================================= network: the forward, nothing else

    private sealed class RemoteNetwork(RemoteExecutionHost host) : IHostNetwork
    {
        public bool IsListening(int port)
            => host._runtimes.List().Any(runtime => runtime.Status == BackendStatus.Running && runtime.Ports.Contains(port)
                                                   && (host.RuntimeId is null || runtime.Id == host.RuntimeId));

        public Uri ResolveServiceUri(Uri hostLocalUri)
            => new UriBuilder(hostLocalUri) { Host = host.SentinelHost(host.RuntimeFor(hostLocalUri)) }.Uri;

        public Task<IHostPortForward> ReverseForwardAsync(Uri serverLocalUri, CancellationToken ct = default)
            => throw Refused(host.NodeId, "IHostNetwork.ReverseForwardAsync");

        public HttpMessageHandler CreateServiceHandler(Uri hostLocalUri)
            => host.RuntimeFor(hostLocalUri) is { } runtime
                ? host._forward.CreateHandler(runtime)
                : new NoRuntimeHandler(host.NodeId, hostLocalUri.Port);

        public Task<WebSocket> ConnectWebSocketAsync(Uri hostLocalUri, CancellationToken ct = default)
            => host.RuntimeFor(hostLocalUri) is { } runtime
                ? host._forward.ConnectWebSocketAsync(runtime, hostLocalUri.AbsolutePath, hostLocalUri.Query, ct)
                : Task.FromException<WebSocket>(NoRuntimeHandler.Error(host.NodeId, hostLocalUri.Port));
    }

    /// <summary>A handler for a port no runtime of the node serves: every request fails, and nothing reaches the node.</summary>
    private sealed class NoRuntimeHandler(string nodeId, int port) : HttpMessageHandler
    {
        public static ForwardException Error(string nodeId, int port) => new(ForwardErrorCodes.RuntimeUnknown,
            $"Node '{nodeId}' reported no single runtime on port {port}.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(Error(nodeId, port));
    }

    // ================================================================= what the node reported about itself

    private sealed class RemotePlatform(RemoteExecutionHost host) : IHostPlatform
    {
        public HostPlatformInfo Info => host.Node is { } node
            ? new HostPlatformInfo(node.Os, node.OsDescription, node.Architecture, node.MachineName, "") { Chassis = node.Chassis }
            : new HostPlatformInfo("", "", "", "", "");

        public bool IsWindows => host.IsWindows;
        public bool IsLinux => string.Equals(host.Node?.Os, "linux", StringComparison.OrdinalIgnoreCase);

        /// <summary>RedCompute launches nothing on a remote node; its runtimes start through <see cref="IExecutionHost.Runtimes"/>.</summary>
        public bool CanLaunchLocalBackends => false;

        public string? GetEnvironmentVariable(string name) => throw Refused(host.NodeId, "IHostPlatform.GetEnvironmentVariable");
        public string GetFolderPath(Environment.SpecialFolder folder) => throw Refused(host.NodeId, "IHostPlatform.GetFolderPath");
        public string ComputeDataRoot => throw Refused(host.NodeId, "IHostPlatform.ComputeDataRoot");
    }

    private sealed class RemoteHardware(RemoteExecutionHost host) : IHostHardware
    {
        public IHostHardwareSampler OpenSampler() => throw Refused(host.NodeId, "IHostHardware.OpenSampler");
        public HostProcessParentage ProcessParentage() => throw Refused(host.NodeId, "IHostHardware.ProcessParentage");
        public string? ProcessorName() => host.Node?.ProcessorName;
    }

    // ================================================================= fail closed

    private sealed class RemoteProcesses(string nodeId) : IHostProcesses
    {
        public IHostProcess? Start(HostSpawnSpec spec) => throw Refused(nodeId, "IHostProcesses.Start");
        public bool TryKillRecorded(int? pid, string cliName, Action<string>? log = null) => throw Refused(nodeId, "IHostProcesses.TryKillRecorded");
        public bool KillIdentity(int pid, long createdUtcTicks, string owner) => throw Refused(nodeId, "IHostProcesses.KillIdentity");
        public IReadOnlyList<HostProcessInfo> List() => throw Refused(nodeId, "IHostProcesses.List");
        public IReadOnlyList<HostProcessInfo> List(bool executablePaths) => throw Refused(nodeId, "IHostProcesses.List");
        public HostProcessInfo? Find(int pid) => throw Refused(nodeId, "IHostProcesses.Find");
    }

    private sealed class RemoteContainment(string nodeId) : IHostContainment
    {
        public Action<string>? Log
        {
            get => throw Refused(nodeId, "IHostContainment.Log");
            set => throw Refused(nodeId, "IHostContainment.Log");
        }

        public Func<int, long, bool>? IsProtected
        {
            get => throw Refused(nodeId, "IHostContainment.IsProtected");
            set => throw Refused(nodeId, "IHostContainment.IsProtected");
        }

        public string LedgerDirectory => throw Refused(nodeId, "IHostContainment.LedgerDirectory");
        public int SweepLedgers() => throw Refused(nodeId, "IHostContainment.SweepLedgers");
        public bool IsSessionMember(int pid) => throw Refused(nodeId, "IHostContainment.IsSessionMember");
        public IContainedTree? FindByRoot(int rootPid) => throw Refused(nodeId, "IHostContainment.FindByRoot");
        public IContainedTree? Keep(int rootPid, string owner, string ledgerPath) => throw Refused(nodeId, "IHostContainment.Keep");
        public IContainedTree? Keep(int rootPid, long createdUtcTicks, string owner, string ledgerPath) => throw Refused(nodeId, "IHostContainment.Keep");
        public IContainedTree? Resume(string owner, string ledgerPath) => throw Refused(nodeId, "IHostContainment.Resume");
        public IContainedTree? Resume(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities)
            => throw Refused(nodeId, "IHostContainment.Resume");
        public DetachedLaunch StartDetachedKept(DetachedSpawnSpec spec) => throw Refused(nodeId, "IHostContainment.StartDetachedKept");
    }

    private sealed class RemoteFiles(string nodeId) : IHostFileSystem
    {
        private RemoteOperationNotAllowedException No(string member) => Refused(nodeId, "IHostFileSystem." + member);

        public bool FileExists(HostPath path) => throw No(nameof(FileExists));
        public bool DirectoryExists(HostPath path) => throw No(nameof(DirectoryExists));
        public HostFileStat Stat(HostPath path) => throw No(nameof(Stat));
        public string? GetFinalDirectoryPath(HostPath path) => throw No(nameof(GetFinalDirectoryPath));
        public string ReadAllText(HostPath path) => throw No(nameof(ReadAllText));
        public Task<string> ReadAllTextAsync(HostPath path, CancellationToken ct = default) => throw No(nameof(ReadAllTextAsync));
        public IEnumerable<string> ReadLines(HostPath path) => throw No(nameof(ReadLines));
        public byte[] ReadAllBytes(HostPath path) => throw No(nameof(ReadAllBytes));
        public Stream OpenRead(HostPath path) => throw No(nameof(OpenRead));
        public Stream OpenRead(HostPath path, FileShare share) => throw No(nameof(OpenRead));
        public string? ResolveLinkTarget(HostPath path, bool returnFinalTarget) => throw No(nameof(ResolveLinkTarget));
        public FileAttributes GetAttributes(HostPath path) => throw No(nameof(GetAttributes));
        public void WriteAllText(HostPath path, string contents, Encoding? encoding = null) => throw No(nameof(WriteAllText));
        public Task WriteAllTextAsync(HostPath path, string contents, Encoding? encoding = null, CancellationToken ct = default) => throw No(nameof(WriteAllTextAsync));
        public void WriteAllBytes(HostPath path, byte[] bytes) => throw No(nameof(WriteAllBytes));
        public void ReplaceAtomically(HostPath path, string contents) => throw No(nameof(ReplaceAtomically));
        public void CreateDirectory(HostPath path) => throw No(nameof(CreateDirectory));
        public IEnumerable<HostPath> EnumerateFiles(HostPath directory, string pattern, SearchOption option) => throw No(nameof(EnumerateFiles));
        public IEnumerable<HostPath> EnumerateDirectories(HostPath directory, string pattern, SearchOption option) => throw No(nameof(EnumerateDirectories));
        public void DeleteFile(HostPath path) => throw No(nameof(DeleteFile));
        public void DeleteDirectory(HostPath path, bool recursive) => throw No(nameof(DeleteDirectory));
        public HostPath CreateTempDirectory(string prefix) => throw No(nameof(CreateTempDirectory));
        public HostPath TempPath => throw No(nameof(TempPath));
    }

    private sealed class RemoteQueries(string nodeId) : IHostQueries
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QuerySqliteReadOnly(HostPath database, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, SqliteReadOptions? options = null)
            => throw Refused(nodeId, "IHostQueries.QuerySqliteReadOnly");
    }

    /// <summary>
    /// The node's path rules, lexical only: separator, comparison, qualification and joining. A full path needs the
    /// node's working directory and its file system, so <see cref="GetFullPath"/> fails closed.
    /// </summary>
    private sealed class RemotePathRules(string nodeId, bool windows) : IHostPathRules
    {
        public static RemotePathRules Windows(string nodeId) => new(nodeId, true);
        public static RemotePathRules Posix(string nodeId) => new(nodeId, false);

        public char DirectorySeparator => windows ? '\\' : '/';
        public StringComparison Comparison => windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public bool IsPathFullyQualified(string path)
        {
            if (!windows) return path.StartsWith('/');
            if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') return true;
            return path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
        }

        public string GetFullPath(string path) => throw Refused(nodeId, "IHostPathRules.GetFullPath");

        public string Combine(params string[] parts)
        {
            var separator = DirectorySeparator;
            var result = "";
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (IsRooted(part)) result = part;
                else if (result.Length == 0 || IsSeparator(result[^1])) result += part;
                else result += separator + part;
            }
            return result;
        }

        private bool IsSeparator(char c) => c == '/' || (windows && c == '\\');

        private bool IsRooted(string part) => IsSeparator(part[0]) || (windows && part.Length >= 2 && char.IsAsciiLetter(part[0]) && part[1] == ':');
    }
}
