using System.Collections.Concurrent;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.Node;
using RedCompute.Node.Credentials;
using RedCompute.Node.Reports;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Tests;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;
using Xunit.Abstractions;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// Phase 3's end-to-end proof in one process (design 9, R6): the real node agent (its link client,
/// handler, reporter, key and node.json) against the real NodeHub (admission with the kernel's
/// single-use identity on every connection, the link server, the registry), through the seeded fault
/// proxy of the link harness. Every seed must: link and describe itself, echo 256 KiB with no word
/// duplicated or out of order, push a periodic report, then be revoked: the link closes, the hub
/// refuses it, and the agent stops and deletes its key. 20 seeds per fault schedule (200 in all), or
/// NODELINK_FAULT_SEEDS in all; a failure names its seed.
/// </summary>
public sealed class NodeLinkEndToEndTests(ITestOutputHelper output)
{
    private static int SeedsPerSchedule
        => Math.Max(2, (int.TryParse(Environment.GetEnvironmentVariable("NODELINK_FAULT_SEEDS"), out var n) && n > 0 ? n : 200)
                       / FaultSchedule.All.Count);

    public static TheoryData<string> Schedules => new(FaultSchedule.All.Select(schedule => schedule.Name));

    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task The_real_agent_links_echoes_reports_and_is_revoked_through_nodehub(string schedule)
    {
        var failures = new ConcurrentBag<string>();
        var retries = 0;
        int connections = 0, dropped = 0, corrupted = 0;
        var started = DateTime.UtcNow;
        await Parallel.ForEachAsync(Enumerable.Range(0, SeedsPerSchedule),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2) },
            async (seed, _) =>
            {
                await using var run = new EndToEnd(FaultSchedule.Named(schedule), seed);
                string? failure;
                try
                {
                    failure = await run.RunAsync();
                }
                catch (Exception error)
                {
                    failure = error.ToString();
                }
                Interlocked.Add(ref retries, run.EchoRetries);
                Interlocked.Add(ref connections, run.Proxy.Connections);
                Interlocked.Add(ref dropped, Volatile.Read(ref run.Proxy.Dropped));
                Interlocked.Add(ref corrupted, Volatile.Read(ref run.Proxy.Corrupted));
                if (failure is not null) failures.Add($"seed {seed}: {failure}\n{run.Trace}");
            });
        output.WriteLine($"{schedule}: {SeedsPerSchedule} seeds in {(DateTime.UtcNow - started).TotalSeconds:F1} s; "
                         + $"{connections} connections, {dropped} frames dropped, {corrupted} corrupted; {retries} echo retries after a lost link session");
        Assert.True(failures.IsEmpty, string.Join("\n\n", failures.OrderBy(f => f, StringComparer.Ordinal).Take(5)));

        // The faults really happened: a pass is not a clean run in disguise.
        var faults = FaultSchedule.Named(schedule);
        if (faults.Drop > 0) Assert.True(dropped > 0, "no frame was dropped");
        // The proxy gives each of the first connections one event, cut before half-open before corrupt.
        if (faults.CorruptConnections > Math.Max(faults.CutConnections, faults.HalfOpenConnections))
            Assert.True(corrupted > 0, "no frame was corrupted");
        if (faults.CutConnections + faults.HalfOpenConnections + faults.CorruptConnections > 0)
            Assert.True(connections > SeedsPerSchedule, $"no seed had to reconnect ({connections} connections)");
    }

    /// <summary>One seeded run: a node home, an agent, a hub, a proxy between them.</summary>
    private sealed class EndToEnd : IAsyncDisposable
    {
        private const string Origin = "https://leaf.example.test";

        private static readonly LinkOptions Fast = new()
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            MissedHeartbeats = 3,
            AckDelay = TimeSpan.FromMilliseconds(5),
            RtoMin = TimeSpan.FromMilliseconds(20),
            RtoMax = TimeSpan.FromMilliseconds(300),
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            Tick = TimeSpan.FromMilliseconds(5),
            SessionRetention = TimeSpan.FromMinutes(1),
        };

        private readonly string _nodeId;
        private readonly string _home = Path.Combine(Path.GetTempPath(), "rc-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
        private readonly JwtService _jwt = new(JwtOptions());
        // A real clock: "a periodic report arrived" is a newer ReportedAt.
        private readonly NodeRegistry _registry = Registry(clock: () => DateTimeOffset.UtcNow);
        private readonly NodeHub _hub;
        private readonly LinkFaultProxy _proxy;
        private readonly NodePaths _paths;
        private readonly NodeEnvironment _env;
        private readonly ConcurrentQueue<string> _trace = new();
        private NodeAgent? _agent;
        private Task? _agentRun;

        public EndToEnd(FaultSchedule schedule, int seed)
        {
            _nodeId = RemoteId(1000 + seed);
            _registry.ApplyKernelNodes([Kernel(_nodeId)]);
            _hub = new NodeHub(_registry, line => _trace.Enqueue(line), new NodeHubOptions { Link = Fast });
            _proxy = new LinkFaultProxy(schedule, seed, AcceptAsync);
            _paths = NodePaths.Resolve(name => name == NodePaths.HomeVariable ? _home : null,
                _ => throw new InvalidOperationException("an isolated home asks for no OS folder"), OperatingSystem.IsWindows());
            _env = new NodeEnvironment
            {
                Paths = _paths,
                Host = new LocalExecutionHost("node_e2e"),
                KeyStores = new FileKeys(_paths),
                Log = new NodeLog(_paths.LogFile),
                ProbeRunner = new NoClis(),
                Dial = (_, _) => _proxy.DialAsync,
                Link = Fast,
                Backoff = new ReconnectBackoff(new Random(seed), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)),
                HardwareReportInterval = TimeSpan.FromMilliseconds(400),
                SweepContainmentAtStart = false,
            };
        }

        public int EchoRetries { get; private set; }

        public LinkFaultProxy Proxy => _proxy;

        public string Trace => string.Join("\n", _trace.TakeLast(30));

        /// <summary>
        /// What the kernel's ingress does for each connection: mint a single-use node-link identity, let
        /// NodeHub admit it (a revoked node is refused here), then pump. A refusal closes like the kernel's.
        /// </summary>
        private async Task AcceptAsync(ILinkTransport server)
        {
            var context = Context(_jwt, IngressToken(_jwt, _nodeId));
            var (admission, refusal) = await _hub.AdmitAsync(context, _cts.Token);
            if (refusal is not null)
            {
                _trace.Enqueue($"[ingress] refused {refusal.Code}");
                await server.CloseAsync(refusal.Code == "node_revoked" ? LinkCloseCodes.Revoked : LinkCloseCodes.ProtocolError, refusal.Code,
                    CancellationToken.None);
                return;
            }
            try
            {
                await _hub.Server.RunAsync(server, admission!.NodeId, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task<string?> RunAsync()
        {
            // As `leaf-node pair` leaves it: a committed key and node.json.
            var store = new FileKeyStore(_paths);
            string keyRef;
            using (var key = store.Create(Origin))
            {
                store.Commit(key, _nodeId);
                keyRef = key.KeyRef;
                _env.StateStore.Save(new NodeState
                {
                    Pairing = new NodePairing
                    {
                        NodeId = _nodeId,
                        ServerOrigin = Origin,
                        LinkPath = "/api/nodes/link",
                        KeyRef = key.KeyRef,
                        CredentialStore = store.Kind,
                        Fingerprint = RedCompute.Node.Pairing.NodeCrypto.Fingerprint(key.SubjectPublicKeyInfo),
                        PairedAt = DateTimeOffset.UtcNow,
                    },
                });
            }
            _agent = new NodeAgent(_env);
            _agentRun = _agent.RunAsync(_cts.Token);

            // 1. Linked, described, reported: the registry holds what the agent said about itself.
            await UntilAsync(() => _registry.Find(_nodeId) is { Status: NodeStatus.Online, Link.CredentialStore: "file", Report.Hardware: not null },
                "the node to link and describe itself");
            var linked = _registry.Find(_nodeId)!;
            if (linked.Link!.AgentVersion != AgentIdentity.Version) return $"agent version {linked.Link.AgentVersion}";
            if (linked.Report!.Hardware!.NodeId != _nodeId) return "the hardware sample names another node";
            if (linked.Report.Clis.Count == 0) return "no CLI report";

            // 2. Exactly once, in order, through whatever the proxy does.
            NodeEchoResult? echo = null;
            for (var attempt = 0; echo is null; attempt++)
            {
                try
                {
                    echo = await _hub.EchoAsync(_nodeId, 256 * 1024, TimeSpan.FromSeconds(20), _cts.Token);
                }
                catch (NodeHubException error) when (attempt < 3 && error.Code is "echo_failed" or "node_offline")
                {
                    // The link session was lost (a reset, not a duplicate or a gap): the channel is gone with it.
                    EchoRetries++;
                    _trace.Enqueue($"[test] echo attempt {attempt} failed: {error.Code} {error.Message}");
                    await UntilAsync(() => _registry.Find(_nodeId)?.Status == NodeStatus.Online, "the link to come back");
                }
            }
            if (echo.Bytes != 256 * 1024 || echo.Duplicates != 0 || echo.OutOfOrder != 0)
                return $"echo: {echo}";

            // 3. The agent's periodic report arrives over the same link.
            var reportedAt = _registry.Find(_nodeId)!.Report!.ReportedAt;
            await UntilAsync(() => _registry.Find(_nodeId)!.Report!.ReportedAt > reportedAt, "a periodic report");

            // 4. Revoked: the link closes, the ingress refuses the node, the agent stops and wipes its key.
            await _hub.RevokeAsync(_nodeId, "revoked_by_owner");
            await UntilAsync(() => _env.StateStore.Load() is { Pairing: null, Ended.Reason: "revoked" }, "the agent to forget its pairing");
            if (_registry.Find(_nodeId)!.Status != NodeStatus.Revoked) return "the registry did not keep the revocation";
            if (_hub.Server.FindSession(_nodeId) is not null) return "a session survived the revocation";
            if (store.Open(keyRef, Origin) is { } survivor)
            {
                survivor.Dispose();
                return "the node key survived the revocation";
            }
            if (Directory.Exists(_paths.Spool) && Directory.EnumerateFileSystemEntries(_paths.Spool).Any()) return "the spool survived";
            var refused = await _hub.AdmitAsync(Context(_jwt, IngressToken(_jwt, _nodeId)), CancellationToken.None);
            return refused.Refusal?.Code == "node_revoked" ? null : "the hub admitted a revoked node";
        }

        private async Task UntilAsync(Func<bool> condition, string what)
        {
            while (!condition())
            {
                if (_cts.IsCancellationRequested) throw new TimeoutException($"timed out waiting for {what}");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_agent is not null) await _agent.DisposeAsync();
            // An unpaired agent waits for a new pairing until it is stopped.
            _cts.Cancel();
            if (_agentRun is not null)
                try { await _agentRun.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException or NodeCommandException) { }
            await _hub.DisposeAsync();
            _cts.Dispose();
            _env.Log.Dispose();
            try
            {
                if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Every key in a 0600-style file under the isolated home (no OS keyring in a test).</summary>
    private sealed class FileKeys(NodePaths paths) : INodeKeyStores
    {
        public INodeKeyStore ForNewKey(bool allowFileKey) => new FileKeyStore(paths);
        public INodeKeyStore ForKeyRef(string keyRef) => new FileKeyStore(paths);
    }

    /// <summary>A probe runner that finds no CLI and starts nothing.</summary>
    private sealed class NoClis : IProbeRunner
    {
        public Task<ProbeOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new ProbeOutput(false, -1, "", "not installed"));
    }
}
