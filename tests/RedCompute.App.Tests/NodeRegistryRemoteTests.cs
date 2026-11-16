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
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2), confirmed: false), Kernel(RemoteId(3)), Kernel(RemoteId(4), status: "needs-update")]);
        registry.LinkUp(RemoteId(1), Link());
        registry.LinkUp(RemoteId(2), Link("s2"));
        INodeRegistry nodes = registry;

        Assert.Equal(NodeUnavailableReasons.RemoteExecutionNotAvailable, Reason(() => nodes.HostFor(RemoteId(1))));
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
    public void A_node_the_kernel_removed_is_forgotten_with_its_cache()
    {
        var registry = Registry(_cache);
        registry.ApplyKernelNodes([Kernel(RemoteId(1)), Kernel(RemoteId(2))]);
        Assert.True(File.Exists(Path.Combine(_cache, RemoteId(2) + ".json")));
        var changes = new List<NodeStatusChange>();
        registry.StatusChanged += changes.Add;

        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);

        Assert.Null(registry.Find(RemoteId(2)));
        Assert.False(File.Exists(Path.Combine(_cache, RemoteId(2) + ".json")));
        Assert.Equal("node_unknown", registry.LinkRefusal(RemoteId(2)));
        var removed = Assert.Single(changes);
        Assert.Equal((RemoteId(2), NodeStatus.Revoked, NodeStatus.Offline), (removed.Node.Id, removed.Node.Status, removed.Previous));
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

        Assert.False(await sync.SyncNowAsync(CancellationToken.None, force: true));
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
    public async Task Sync_applies_the_list_and_resyncs_asked_for_in_a_burst_are_one()
    {
        var kernel = new FakeKernel { Nodes = [Kernel(RemoteId(1))] };
        var registry = Registry();
        await using var sync = new KernelNodeSync(registry, kernel, _ => { });

        Assert.True(await sync.SyncNowAsync(CancellationToken.None));
        Assert.False(await sync.SyncNowAsync(CancellationToken.None));

        Assert.NotNull(registry.Find(RemoteId(1)));
        Assert.Equal(1, kernel.Lists);
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

    internal sealed class FakeKernel : IKernelNodes
    {
        public IReadOnlyList<KernelNode>? Nodes { get; set; } = [];
        public int Lists { get; private set; }
        public List<JsonObject> Local { get; } = [];
        public List<(string Id, JsonObject Body)> Presence { get; } = [];

        public Task<IReadOnlyList<KernelNode>?> ListAsync(CancellationToken ct)
        {
            Lists++;
            return Task.FromResult(Nodes);
        }

        public Task<bool> PutLocalAsync(JsonObject body, CancellationToken ct)
        {
            Local.Add(body);
            return Task.FromResult(true);
        }

        public Task<bool> PutPresenceAsync(string nodeId, JsonObject body, CancellationToken ct)
        {
            Presence.Add((nodeId, body));
            return Task.FromResult(true);
        }
    }
}
