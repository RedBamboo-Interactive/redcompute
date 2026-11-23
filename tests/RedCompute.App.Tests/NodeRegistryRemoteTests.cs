using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// The registry's remote nodes (design 7.1): identity from the kernel, liveness from the link, the last
/// report cached across restarts, and <see cref="NodeUnavailableException"/> for every remote host in Phase 3.
/// </summary>
public sealed class NodeRegistryRemoteTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "rc-node-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private static NodeLinkState Link(string session = "s1") => new(session, Now, Now, 12.5, -3, 1,
        ["describe.v1", "hardware.v1", "cli.v1", "diag.echo"], "0.1.0", false, "cng");

    [Fact]
    public void Kernel_nodes_join_after_node_zero_and_node_zero_is_unchanged()
    {
        var registry = Registry();
        var before = registry.Local;

        registry.ApplyKernelNodes([Kernel(RemoteId(2), "Zeta"), Kernel(RemoteId(1), "Alpha"),
            new KernelNode(LocalId, "This computer", KernelNode.LocalKind, "online", null, Owner, true, null, null, null, null, null, null, null, null)]);

        Assert.Equal([LocalId, RemoteId(1), RemoteId(2)], registry.Nodes.Select(node => node.Id));
        Assert.Equal(before, registry.Local);
        var alpha = registry.Find(RemoteId(1))!;
        Assert.Equal((NodeKind.Remote, NodeStatus.Offline, "Alpha", Owner, true), (alpha.Kind, alpha.Status, alpha.Name, alpha.OwnerUserId, alpha.Confirmed));
        Assert.Equal(("linux", "x64", "leaflinux", "0.1.0"), (alpha.Os, alpha.Architecture, alpha.MachineName, alpha.AgentVersion));
        Assert.Equal(Now.AddDays(-1), alpha.PairedAt);
        Assert.Null(alpha.Link);
        Assert.Equal(new NodePlacement(true, null), NodeRegistry.PlacementOf(registry.Local));
    }

    [Fact]
    public void Every_remote_host_is_unavailable_with_its_reason_and_unknown_ids_stay_unknown()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2), confirmed: false), Kernel(RemoteId(3)), Kernel(RemoteId(4), status: "needs-update"),
            Kernel(RemoteId(5))]);
        registry.LinkUp(RemoteId(1), Link());
        registry.LinkUp(RemoteId(2), Link("s2"));
        registry.LinkUp(RemoteId(5), Link("s5") with { Features = ["describe.v1", "runtime.v1", "forward.http.v1", "forward.ws.v1"] });
        INodeRegistry nodes = registry;

        // A Phase 3 agent needs an update to run work; a Phase 4 one is eligible, but this registry has no NodeHub.
        Assert.Equal(NodeUnavailableReasons.NeedsUpdate, Reason(() => nodes.HostFor(RemoteId(1))));
        Assert.Equal(NodeUnavailableReasons.RemoteExecutionNotAvailable, Reason(() => nodes.HostFor(RemoteId(5))));
        Assert.Equal(NodeUnavailableReasons.NotConfirmed, Reason(() => nodes.HostFor(RemoteId(2))));
        Assert.Equal(NodeUnavailableReasons.Offline, Reason(() => nodes.HostFor(RemoteId(3))));
        Assert.Equal(NodeUnavailableReasons.NeedsUpdate, Reason(() => nodes.HostFor(RemoteId(4))));
        registry.MarkRevoked(RemoteId(3), "revoked_by_owner");
        Assert.Equal(NodeUnavailableReasons.Revoked, Reason(() => nodes.HostFor(RemoteId(3))));
        Assert.True(new NodeUnavailableException(RemoteId(3), NodeUnavailableReasons.Offline).Retryable);

        Assert.Throws<KeyNotFoundException>(() => nodes.HostFor(RemoteId(9)));
        Assert.Same(registry.LocalHost, nodes.HostFor(null));
        Assert.Same(registry.LocalHost, nodes.HostFor("LOCAL"));
        Assert.Same(registry.LocalHost, nodes.HostFor(LocalId));

        static string Reason(Action call) => Assert.Throws<NodeUnavailableException>(call).Reason;
    }

    [Fact]
    public void Revocation_is_terminal_whatever_the_link_or_kernel_says_next()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        registry.ApplyKernelNodes([Kernel(RemoteId(1), status: "revoked", reason: "revoked_by_owner")]);

        registry.LinkUp(RemoteId(1), Link());
        registry.ApplyKernelNodes([Kernel(RemoteId(1), status: "online")]);

        var node = registry.Find(RemoteId(1))!;
        Assert.Equal((NodeStatus.Revoked, "revoked_by_owner"), (node.Status, node.StatusReason));
        Assert.Null(node.Link);
        Assert.Equal("node_revoked", registry.LinkRefusal(RemoteId(1)));
    }

    [Fact]
    public void A_node_missing_from_the_kernels_list_is_forgotten_locally_but_never_revoked()
    {
        // Review finding: absence used to raise Revoked, and NodeHub closed the link with 4403 (the agent
        // deletes its key): one bad 200 would have unpaired every node. Design 3.5: absence forgets locally.
        var registry = Registry(_cache);
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2))]);
        registry.LinkUp(RemoteId(2), Link());
        Assert.True(File.Exists(Path.Combine(_cache, RemoteId(2) + ".json")));
        var changes = new List<NodeStatusChange>();
        var updates = new List<string>();
        registry.StatusChanged += changes.Add;
        registry.Updated += updates.Add;

        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);

        Assert.Null(registry.Find(RemoteId(2)));
        Assert.False(File.Exists(Path.Combine(_cache, RemoteId(2) + ".json")));
        Assert.Equal("node_unknown", registry.LinkRefusal(RemoteId(2)));
        Assert.Empty(changes);
        Assert.Equal([RemoteId(2)], updates);

        // An empty answer forgets everything and revokes nothing; the next good answer brings the nodes back.
        registry.ApplyKernelNodes([]);
        Assert.Empty(changes);
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2))]);
        Assert.Equal([NodeStatus.Offline, NodeStatus.Offline], registry.Nodes.Skip(1).Select(node => node.Status));
        Assert.All(changes, change => Assert.Null(change.Previous));
        Assert.DoesNotContain(changes, change => change.Node.Status == NodeStatus.Revoked);
    }

    [Fact]
    public void Only_the_current_link_going_down_takes_the_node_offline()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        registry.LinkUp(RemoteId(1), Link("old"));
        registry.LinkUp(RemoteId(1), Link("new"));

        registry.LinkDown(RemoteId(1), "old", "superseded");
        Assert.Equal(NodeStatus.Online, registry.Find(RemoteId(1))!.Status);

        registry.LinkDown(RemoteId(1), "new", "heartbeat_timeout");
        var node = registry.Find(RemoteId(1))!;
        Assert.Equal((NodeStatus.Offline, "heartbeat_timeout"), (node.Status, node.StatusReason));
    }

    [Fact]
    public void Status_changes_are_raised_once_per_transition_and_hardware_alone_is_not_an_update()
    {
        var registry = Registry();
        var changes = new List<NodeStatusChange>();
        var updates = new List<string>();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        registry.StatusChanged += changes.Add;
        registry.Updated += updates.Add;

        registry.LinkUp(RemoteId(1), Link());
        registry.ApplyReport(RemoteId(1), null, Hardware(10));
        registry.ApplyReport(RemoteId(1), null, Hardware(20));
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);

        Assert.Equal([(NodeStatus.Online, (NodeStatus?)NodeStatus.Offline)], changes.Select(change => (change.Node.Status, change.Previous)));
        Assert.Equal([RemoteId(1)], updates);
        Assert.Equal(20, registry.Find(RemoteId(1))!.Report!.Hardware!.Cpu.UsagePercent);

        registry.ApplyReport(RemoteId(1), [new NodeCliReport("claude", true, "2.1.0", true, "subscription", null)], null);
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public void A_restarted_registry_shows_each_node_offline_with_its_last_report()
    {
        var first = Registry(_cache);
        first.ApplyKernelNodes([Kernel(RemoteId(1), "Lab VM"), Kernel(RemoteId(2), "Gone soon")]);
        first.LinkUp(RemoteId(1), Link());
        first.ApplyDescribe(RemoteId(1), new NodeDescribe("linux", "x64", "Fedora Linux 41", "leaflinux", "Virtual CPU", "0.1.0", "secret-service"));
        first.ApplyReport(RemoteId(1), [new NodeCliReport("claude", true, "2.1.0", true, "subscription", "/home/lab/.local/bin/claude")], Hardware());
        first.MarkRevoked(RemoteId(2), "revoked_by_owner");
        File.WriteAllText(Path.Combine(_cache, RemoteId(7) + ".json"), "{ not json");
        File.WriteAllText(Path.Combine(_cache, "notes.txt"), "ignored");

        var second = Registry(_cache);

        var lab = second.Find(RemoteId(1))!;
        Assert.Equal((NodeStatus.Offline, "Lab VM", "Virtual CPU"), (lab.Status, lab.Name, lab.ProcessorName));
        Assert.Equal("claude", Assert.Single(lab.Report!.Clis).Name);
        Assert.Equal(8, lab.Report.Hardware!.Cpu.CoreCount);
        Assert.Null(lab.Link);
        Assert.Equal(NodeStatus.Revoked, second.Find(RemoteId(2))!.Status);
        Assert.Null(second.Find(RemoteId(7)));
    }

    [Fact]
    public void A_cache_file_that_cannot_be_loaded_is_set_aside_and_startup_goes_on()
    {
        // Review finding: the load runs during startup and caught only Json, IO and access exceptions.
        var first = Registry(_cache);
        first.ApplyKernelNodes([Kernel(RemoteId(1), "Lab VM"), Kernel(RemoteId(2), "Breaks the parser")]);
        File.WriteAllText(Path.Combine(_cache, RemoteId(3) + ".json"), "null");
        File.WriteAllText(Path.Combine(_cache, RemoteId(4) + ".json"), """{ "id": "node_../../etc", "name": "x" }""");
        File.WriteAllText(Path.Combine(_cache, RemoteId(5) + ".json"), $$"""{ "id": "{{RemoteId(5)}}", "name": null, "report": { "clis": null } }""");
        var log = new List<string>();

        var registry = new NodeRegistry(Identity, NodeRegistryTests.Platform(), "0.2.0", Now, () => Now, null, _cache, log.Add,
            json => json.Contains("Breaks the parser") ? throw new InvalidOperationException("a parser bug") : NodeRegistry.ParseCache(json));

        Assert.Equal([LocalId, RemoteId(1), RemoteId(5)], registry.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal));
        var sanitized = registry.Find(RemoteId(5))!;
        Assert.Equal(RemoteId(5), sanitized.Name);
        Assert.Empty(sanitized.Report!.Clis);
        foreach (var bad in new[] { 2, 3, 4 })
        {
            Assert.False(File.Exists(Path.Combine(_cache, RemoteId(bad) + ".json")));
            Assert.Single(Directory.GetFiles(_cache, RemoteId(bad) + ".json.*.bad"));
        }
        Assert.Contains(log, line => line.Contains("InvalidOperationException") && line.Contains(RemoteId(2)));
        Assert.Equal(3, log.Count(line => line.Contains("Set aside")));

        // The next start does not trip over what was set aside.
        var again = new List<string>();
        _ = new NodeRegistry(Identity, NodeRegistryTests.Platform(), "0.2.0", Now, () => Now, remoteCacheDirectory: _cache, log: again.Add);
        Assert.DoesNotContain(again, line => line.Contains("Set aside") || line.Contains("Could not"));
    }

    [Fact]
    public void Without_a_cache_directory_nothing_is_written()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        registry.MarkRevoked(RemoteId(1), "x");

        Assert.False(Directory.Exists(_cache));
    }

    // ================================================================= the kernel sync

    [Fact]
    public async Task The_kernel_client_reads_the_node_list_with_redcomputes_service_identity()
    {
        var jwt = new JwtService(JwtOptions());
        string? bearer = null;
        var handler = new StubHandler(request =>
        {
            bearer = request.Headers.Authorization?.Parameter;
            return Json(HttpStatusCode.OK, $$"""
                { "nodes": [
                  { "id": "{{LocalId}}", "name": "PC", "kind": "local", "status": "online", "confirmed": true },
                  { "id": "{{RemoteId(1)}}", "name": "Lab VM", "kind": "remote", "status": "needs-update", "statusReason": "agent_too_old",
                    "ownerId": "user-owner", "confirmed": true, "platform": { "os": "linux", "arch": "x64", "osDescription": "Fedora", "machineName": "leaflinux" },
                    "agentVersion": "0.1.0", "protocolVersion": 1, "lastSeen": "2026-11-16T11:00:00Z", "pairedAt": "2026-11-15T09:30:00+00:00",
                    "keyFingerprint": "3FA2-91C0-77DE-0B14", "unknownMember": { "x": 1 } },
                  { "id": "node_../../etc", "name": "bad", "kind": "remote" },
                  { "id": "{{RemoteId(2)}}", "kind": "satellite" }
                ] }
                """);
        });
        var client = new KernelNodeClient(new HttpClient(new BadgeBearerHandler(
            () => jwt.GenerateServiceAccessToken("redcompute", roles: ["service"]), handler)) { BaseAddress = new Uri("http://kernel/") });

        var nodes = await client.ListAsync(CancellationToken.None);

        Assert.Equal([LocalId, RemoteId(1)], nodes!.Select(node => node.Id));
        var lab = nodes[1];
        Assert.Equal(("needs-update", "agent_too_old", "leaflinux", 1), (lab.Status, lab.StatusReason, lab.MachineName, lab.ProtocolVersion));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero), lab.PairedAt);
        Assert.Equal("service:redcompute", jwt.ValidateToken(bearer!)!.FindFirst("sub")!.Value);
        Assert.Equal("http://kernel/api/nodes", handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_kernel_that_cannot_list_nodes_leaves_the_registry_as_it_is(HttpStatusCode status)
    {
        var client = new KernelNodeClient(new HttpClient(new StubHandler(_ => Json(status, "{}"))) { BaseAddress = new Uri("http://kernel/") });
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        await using var sync = new KernelNodeSync(registry, client, _ => { });

        Assert.False(await sync.RefreshAsync(CancellationToken.None));
        Assert.NotNull(registry.Find(RemoteId(1)));
        Assert.Contains(((int)status).ToString(), client.LastProblem);
    }

    [Fact]
    public async Task An_unreachable_kernel_is_not_an_exception()
    {
        var client = new KernelNodeClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused"))) { BaseAddress = new Uri("http://kernel/") });

        Assert.Null(await client.ListAsync(CancellationToken.None));
        Assert.False(await client.PutPresenceAsync(RemoteId(1), new JsonObject(), CancellationToken.None));
    }

    [Fact]
    public async Task A_refresh_never_answers_with_a_read_that_started_before_it()
    {
        // Review finding: a resync within 2 s of the last one returned false at once, and one asked for while a
        // read was under way did not wait for it. A node that dials straight after pairing must be found.
        var kernel = new GatedKernel();
        var registry = Registry();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { }, minimumResyncInterval: TimeSpan.Zero);

        var early = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 1, "the first read");
        // The node pairs while that read is under way; it dials and NodeHub asks for a refresh.
        var afterPairing = sync.RefreshAsync(CancellationToken.None);
        kernel.Answer(0, []);
        Assert.True(await early);
        Assert.False(afterPairing.IsCompleted);

        await WaitAsync(() => kernel.Lists == 2, "a second read");
        kernel.Answer(1, [Kernel(RemoteId(1))]);
        Assert.True(await afterPairing);
        Assert.NotNull(registry.Find(RemoteId(1)));
    }

    [Fact]
    public async Task Refreshes_asked_for_together_share_one_read_and_wait_out_the_spacing()
    {
        var kernel = new GatedKernel();
        var registry = Registry();
        // The spacing runs on a clock the test moves, so the boundary is exact whatever the machine is doing.
        var time = new ManualTime();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { }, time: time, minimumResyncInterval: TimeSpan.FromMilliseconds(300));

        var prior = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 1, "the first read");
        var burst = Enumerable.Range(0, 5).Select(_ => sync.RefreshAsync(CancellationToken.None)).ToList();
        kernel.Answer(0, [Kernel(RemoteId(1))]);
        Assert.True(await prior);

        // The burst arrived while the first read was under way: it waits for the spacing (it is not refused)
        // and shares one new read. Its one flight is parked on the spacing timer.
        await WaitAsync(() => time.PendingTimers == 1, "the spacing timer");
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(1, kernel.Lists);
        Assert.All(burst, task => Assert.False(task.IsCompleted));

        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitAsync(() => kernel.Lists == 2, "the second read");
        kernel.Answer(1, [Kernel(RemoteId(1)), Kernel(RemoteId(2))]);

        Assert.All(await Task.WhenAll(burst), Assert.True);
        // Every caller has its answer from that one read; none is left to start a third.
        Assert.Equal(2, kernel.Lists);
        Assert.NotNull(registry.Find(RemoteId(2)));
    }

    /// <summary>A clock and timers that move only when the test says so.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks = TimeSpan.FromDays(1).Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }

        public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = _timers.Where(t => t.Due <= _ticks).ToList();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long? Due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate) { Due = null; owner._timers.Remove(this); }
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task A_refresh_the_kernel_cannot_answer_is_false_and_the_next_one_reads_again()
    {
        var kernel = new GatedKernel();
        var registry = Registry();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { }, minimumResyncInterval: TimeSpan.Zero);

        var failed = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 1, "the read");
        kernel.Answer(0, null);
        Assert.False(await failed);

        var thrown = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 2, "the second read");
        kernel.Fail(1, new HttpRequestException("refused"));
        Assert.False(await thrown);

        var answered = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 3, "the third read");
        kernel.Answer(2, [Kernel(RemoteId(1))]);
        Assert.True(await answered);
    }

    [Fact]
    public async Task With_no_remote_node_the_presence_loop_sleeps()
    {
        // Review finding: the presence loop woke every 2 s for ever, with no remote node at all.
        var kernel = new FakeKernel();
        var registry = Registry();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { }, presenceDelay: TimeSpan.FromMilliseconds(20));
        sync.Start();
        await WaitAsync(() => kernel.Lists >= 1, "the start-up sync");

        await Task.Delay(300);
        Assert.Equal(0, sync.PresenceWakeups);
        Assert.Empty(kernel.Presence);

        kernel.Nodes = [Kernel(RemoteId(1))];
        sync.RequestSync();
        await WaitAsync(() => registry.Find(RemoteId(1)) is not null, "the node");
        registry.LinkUp(RemoteId(1), Link());
        await WaitAsync(() =>
        {
            lock (kernel.Presence) return kernel.Presence.Any(entry => entry.Body["status"]?.GetValue<string>() == "online");
        }, "the presence push");
        Assert.InRange(sync.PresenceWakeups, 1, 3);
    }

    [Fact]
    public async Task Node_zero_is_reported_from_node_json_and_the_detected_platform()
    {
        var kernel = new FakeKernel();
        var registry = Registry();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { });

        Assert.True(await sync.PutLocalAsync(CancellationToken.None));

        var body = Assert.Single(kernel.Local);
        Assert.Equal(LocalId, body["id"]!.GetValue<string>());
        Assert.Equal("0.2.0", body["agentVersion"]!.GetValue<string>());
        Assert.Equal(registry.Local.MachineName, body["platform"]!["machineName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Presence_carries_transitions_and_summaries_never_usage_paths_or_revocations()
    {
        var kernel = new FakeKernel();
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2))]);
        await using var sync = new KernelNodeSync(registry, kernel, _ => { });

        registry.LinkUp(RemoteId(1), Link());
        registry.ApplyReport(RemoteId(1), [new NodeCliReport("claude", true, "2.1.0", true, "subscription", "/home/lab/.local/bin/claude")], Hardware(55));
        registry.MarkRevoked(RemoteId(2), "revoked_by_owner");
        await sync.FlushPresenceAsync(CancellationToken.None);

        var (id, body) = Assert.Single(kernel.Presence);
        Assert.Equal(RemoteId(1), id);
        Assert.Equal("online", body["status"]!.GetValue<string>());
        Assert.Equal(1, body["protocolVersion"]!.GetValue<int>());
        var text = body.ToJsonString();
        Assert.Contains("\"cpuCores\":8", text);
        Assert.DoesNotContain("usage", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".local/bin", text);

        registry.LinkDown(RemoteId(1), "s1", "heartbeat_timeout");
        await sync.FlushPresenceAsync(CancellationToken.None);
        var offline = kernel.Presence[^1].Body;
        Assert.Equal(("offline", "heartbeat_timeout"), (offline["status"]!.GetValue<string>(), offline["statusReason"]!.GetValue<string>()));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>A kernel whose every list read waits until the test answers it (by index), or fails it.</summary>
    internal sealed class GatedKernel : IKernelNodes
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<KernelNode>?>> _reads = [];

        public int Lists
        {
            get { lock (_reads) return _reads.Count; }
        }

        public void Answer(int read, IReadOnlyList<KernelNode>? nodes)
        {
            lock (_reads) _reads[read].SetResult(nodes);
        }

        public void Fail(int read, Exception error)
        {
            lock (_reads) _reads[read].SetException(error);
        }

        public Task<IReadOnlyList<KernelNode>?> ListAsync(CancellationToken ct)
        {
            var read = new TaskCompletionSource<IReadOnlyList<KernelNode>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_reads) _reads.Add(read);
            return read.Task;
        }

        public Task<bool> PutLocalAsync(JsonObject body, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> PutPresenceAsync(string nodeId, JsonObject body, CancellationToken ct) => Task.FromResult(true);
    }

    internal sealed class FakeKernel : IKernelNodes
    {
        public IReadOnlyList<KernelNode>? Nodes { get; set; } = [];
        private int _lists;
        public int Lists => Volatile.Read(ref _lists);
        public List<JsonObject> Local { get; } = [];
        public List<(string Id, JsonObject Body)> Presence { get; } = [];

        public Task<IReadOnlyList<KernelNode>?> ListAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _lists);
            return Task.FromResult(Nodes);
        }

        public Task<bool> PutLocalAsync(JsonObject body, CancellationToken ct)
        {
            Local.Add(body);
            return Task.FromResult(true);
        }

        public Task<bool> PutPresenceAsync(string nodeId, JsonObject body, CancellationToken ct)
        {
            lock (Presence) Presence.Add((nodeId, body));
            return Task.FromResult(true);
        }
    }
}
