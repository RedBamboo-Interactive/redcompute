using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using RedCompute.Node.Credentials;
using RedCompute.Node.Pairing;
using RedCompute.Node.Reports;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Node.Tests;

internal static class TestAssembly
{
    /// <summary>
    /// Before anything reads ComputeStorage: the execution host's data root (its containment ledgers) is
    /// a temp folder, never RedCompute's real one on this machine.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "leaf-node-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("REDCOMPUTE_DATA_ROOT", root);
    }
}

/// <summary>A node home in a temp folder: its own paths, a file key store, a probe runner that runs nothing.</summary>
internal sealed class NodeHome : IDisposable
{
    public NodeHome(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "leaf-node-" + (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Paths = NodePaths.Resolve(variable => variable == NodePaths.HomeVariable ? Root : null,
            folder => throw new InvalidOperationException("an isolated home asks for no OS folder"), OperatingSystem.IsWindows());
        Host = new LocalExecutionHost("node_test");
    }

    public string Root { get; }
    public NodePaths Paths { get; }
    public LocalExecutionHost Host { get; }
    public StringWriter Log { get; } = new();

    public NodeEnvironment Environment(Func<NodeServerClient>? server = null, IProbeRunner? probes = null,
        Func<NodePairing, Func<ECDsa>, Func<CancellationToken, Task<ILinkTransport>>>? dial = null, LinkOptions? link = null,
        TimeSpan? hardwareReport = null, Func<RuntimeRegistration, HostSpawnSpec>? runtimeSpec = null,
        Func<RuntimeRegistration, CancellationToken, Task<bool>>? runtimeProbe = null, TimeSpan? runtimeProbeInterval = null)
        => new()
        {
            Paths = Paths,
            Host = Host,
            KeyStores = new FileKeyStores(Paths),
            Log = new NodeLog(Paths.LogFile, Log),
            ServerClient = server ?? (() => new NodeServerClient(new StubHttp((_, _) => throw new HttpRequestException("no server")))),
            ProbeRunner = probes ?? new ScriptedProbes(),
            Dial = dial,
            Link = link ?? LinkOptions.Default,
            Backoff = new ReconnectBackoff(new Random(7), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)),
            HardwareReportInterval = hardwareReport ?? TimeSpan.FromHours(1),
            SweepContainmentAtStart = false,
            RuntimeSpec = runtimeSpec,
            RuntimeProbe = runtimeProbe,
            RuntimeProbeInterval = runtimeProbeInterval ?? TimeSpan.FromMilliseconds(100),
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The file store for every key: what the tests use instead of the OS keyring.</summary>
internal sealed class FileKeyStores(NodePaths paths) : INodeKeyStores
{
    public INodeKeyStore ForNewKey(bool allowFileKey) => new FileKeyStore(paths);
    public INodeKeyStore ForKeyRef(string keyRef) => new FileKeyStore(paths);
}

/// <summary>A probe runner with canned answers per (CLI file name, first argument), recording every argv.</summary>
internal sealed class ScriptedProbes : IProbeRunner
{
    public Dictionary<string, ProbeOutput> Answers { get; } = new(StringComparer.Ordinal);
    public List<(string File, string[] Arguments)> Calls { get; } = [];

    public Task<ProbeOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls) Calls.Add((fileName, arguments.ToArray()));
        var key = Path.GetFileNameWithoutExtension(fileName) + " " + string.Join(' ', arguments);
        return Task.FromResult(Answers.TryGetValue(key, out var answer) ? answer : new ProbeOutput(false, -1, "", "not scripted"));
    }
}

/// <summary>An HttpMessageHandler answering from a function (the stand-in kernel for pair and unpair).</summary>
internal sealed class StubHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return answer(request, body);
    }
}

internal static class Wait
{
    /// <summary>
    /// The audit once <paramref name="ready"/> holds for it. The agent records some exchanges after their effect is visible
    /// (a forward once its close is queued, a stop once the process is gone), so a test that asserts on a line waits for it
    /// instead of reading once; the line's presence is what is asserted, the wait is only for it to be written.
    /// </summary>
    public static async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> AuditAsync(AuditLog audit,
        Func<IReadOnlyList<System.Text.Json.Nodes.JsonObject>, bool> ready, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var records = audit.Read();
            if (ready(records) || DateTime.UtcNow > deadline) return records; // on timeout the caller's own assertion reports it
            await Task.Delay(10);
        }
    }

    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }
}
