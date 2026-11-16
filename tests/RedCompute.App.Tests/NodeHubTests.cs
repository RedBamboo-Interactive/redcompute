using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Extensions;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.NodeLink;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// NodeHub (design 5.2, 8.3): it accepts only the kernel's single-use node-link identity on a clean
/// loopback hop for a known, unrevoked node, and it turns a link into the registry's live state.
/// </summary>
public sealed class NodeHubTests : IAsyncLifetime
{
    private readonly JwtService _jwt = new(JwtOptions());
    private readonly NodeRegistry _registry = Registry();
    private NodeHub _hub = null!;
    private readonly string _node = RemoteId(1);

    public Task InitializeAsync()
    {
        _registry.ApplyKernelNodes([Kernel(_node)]);
        _hub = new NodeHub(_registry, _ => { });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    private Task<(NodeHubAdmission? Admission, NodeHubRefusal? Refusal)> AdmitAsync(string? token, IPAddress? peer = null,
        Action<HttpContext>? configure = null)
    {
        var context = Context(_jwt, token, peer);
        configure?.Invoke(context);
        return _hub.AdmitAsync(context, CancellationToken.None);
    }

    // ================================================================= admission

    [Fact]
    public async Task Admits_the_kernels_node_link_identity_for_a_known_node()
    {
        var (admission, refusal) = await AdmitAsync(IngressToken(_jwt, _node));

        Assert.Null(refusal);
        Assert.Equal(_node, admission!.NodeId);
        Assert.Equal(Owner, admission.OwnerId);
    }

    [Fact]
    public async Task Refuses_the_anonymous_local_identity()
    {
        var (admission, refusal) = await AdmitAsync(token: null);

        Assert.Null(admission);
        Assert.Equal((401, "node_identity_required"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public async Task Refuses_user_and_browser_badges()
    {
        var (_, user) = await AdmitAsync(_jwt.GenerateAccessToken(Owner, "owner@example.test", "Owner", ["admin"]));
        var (_, browser) = await AdmitAsync(_jwt.GenerateBrowserAccessToken(Owner, "owner@example.test", "Owner", ["admin"]));

        Assert.Equal((403, "node_identity_required"), (user!.Status, user.Code));
        Assert.Equal((403, "node_identity_required"), (browser!.Status, browser.Code));
    }

    [Fact]
    public async Task Refuses_service_tokens()
    {
        var (_, redleaf) = await AdmitAsync(_jwt.GenerateServiceAccessToken("redleaf", computeProvenance: true));
        var (_, compute) = await AdmitAsync(_jwt.GenerateServiceAccessToken("redcompute", roles: ["service", "admin"]));

        Assert.Equal(403, redleaf!.Status);
        Assert.Equal(403, compute!.Status);
    }

    [Fact]
    public async Task Refuses_another_apps_execution_token()
    {
        var (_, nova) = await AdmitAsync(IngressToken(_jwt, _node, app: "nova", actorKind: "agent"));
        var (_, novaAsNode) = await AdmitAsync(IngressToken(_jwt, _node, app: "nova"));

        Assert.Equal((403, "node_identity_required"), (nova!.Status, nova.Code));
        Assert.Equal((403, "node_identity_required"), (novaAsNode!.Status, novaAsNode.Code));
    }

    [Fact]
    public async Task Refuses_the_long_lived_session_execution_tokens_given_to_clis()
    {
        var (_, session) = await AdmitAsync(IngressToken(_jwt, _node, app: "redcompute", lifetime: TimeSpan.FromDays(365)));
        // Even the ingress app id does not make a year-long token the kernel's 60-second identity.
        var (_, longIngress) = await AdmitAsync(IngressToken(_jwt, _node, lifetime: TimeSpan.FromDays(365)));

        Assert.Equal(403, session!.Status);
        Assert.Equal((403, "node_identity_required"), (longIngress!.Status, longIngress.Code));
        Assert.Contains("too long", longIngress.Message);
    }

    [Fact]
    public async Task Refuses_an_actor_that_is_not_a_node()
    {
        var (_, refusal) = await AdmitAsync(IngressToken(_jwt, _node, actorKind: "app"));

        Assert.Equal((403, "node_identity_required"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public void Refuses_an_expired_identity()
    {
        var context = Context(_jwt, IngressToken(_jwt, _node));

        var (identity, refusal) = NodeHub.ReadIdentity(context, DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Null(identity);
        Assert.Equal((401, "token_expired"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public async Task Refuses_a_reused_execution_id()
    {
        var token = IngressToken(_jwt, _node);

        var (first, _) = await AdmitAsync(token);
        var (second, replayed) = await AdmitAsync(token);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal((401, "token_replayed"), (replayed!.Status, replayed.Code));
    }

    [Fact]
    public async Task Refuses_a_peer_that_is_not_loopback()
    {
        var (_, lan) = await AdmitAsync(IngressToken(_jwt, _node), IPAddress.Parse("192.168.1.20"));
        var (_, none) = await AdmitAsync(IngressToken(_jwt, _node), configure: context => context.Connection.RemoteIpAddress = null);

        Assert.Equal((403, "loopback_required"), (lan!.Status, lan.Code));
        Assert.Equal((403, "loopback_required"), (none!.Status, none.Code));
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Original-For", "203.0.113.9:443")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    [InlineData("Cf-Connecting-Ip", "203.0.113.9")]
    [InlineData("Cf-Ray", "8a1b2c3d4e5f-CDG")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    public async Task Refuses_a_loopback_request_that_carries_forwarding_headers(string header, string value)
    {
        var (_, refusal) = await AdmitAsync(IngressToken(_jwt, _node), configure: context => context.Request.Headers[header] = value);

        Assert.Equal((403, "loopback_required"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public async Task An_unknown_node_is_404_only_when_the_kernel_answered_without_it()
    {
        var resyncs = 0;
        await using var hub = new NodeHub(_registry, _ => { }, new NodeHubOptions
        {
            Resync = _ =>
            {
                resyncs++;
                _registry.ApplyKernelNodes([Kernel(_node)]);
                return Task.FromResult(true);
            },
        });

        var (_, refusal) = await hub.AdmitAsync(Context(_jwt, IngressToken(_jwt, RemoteId(99))), CancellationToken.None);

        Assert.Equal((404, "node_unknown"), (refusal!.Status, refusal.Code));
        Assert.Null(refusal.RetryAfter);
        Assert.Equal(1, resyncs);
    }

    [Fact]
    public async Task An_unknown_node_the_kernel_cannot_be_asked_about_is_503_with_retry_after_never_404()
    {
        // Review finding: a node that dialled straight after pairing got 404 node_unknown (a verdict: the agent
        // deletes its key) whenever the registry had not caught up. Only the kernel's answer may say unknown.
        await using var failing = new NodeHub(_registry, _ => { }, new NodeHubOptions { Resync = _ => Task.FromResult(false) });
        await using var throwing = new NodeHub(_registry, _ => { }, new NodeHubOptions { Resync = _ => throw new HttpRequestException("refused") });

        foreach (var hub in new[] { _hub, failing, throwing })
        {
            var (admission, refusal) = await hub.AdmitAsync(Context(_jwt, IngressToken(_jwt, RemoteId(99))), CancellationToken.None);
            Assert.Null(admission);
            Assert.Equal((503, "node_registry_unavailable"), (refusal!.Status, refusal.Code));
            Assert.Equal(NodeHub.RegistryRetryAfter, refusal.RetryAfter);
        }
    }

    [Fact]
    public async Task A_node_that_dials_while_a_sync_from_before_its_pairing_is_under_way_is_admitted()
    {
        var paired = RemoteId(2);
        var kernel = new NodeRegistryRemoteTests.GatedKernel();
        await using var sync = new KernelNodeSync(_registry, kernel, _ => { });
        await using var hub = new NodeHub(_registry, _ => { }, new NodeHubOptions { Resync = sync.RefreshAsync });

        var before = sync.RefreshAsync(CancellationToken.None);
        await WaitAsync(() => kernel.Lists == 1, "the sync under way");
        var admitting = hub.AdmitAsync(Context(_jwt, IngressToken(_jwt, paired)), CancellationToken.None);
        kernel.Answer(0, [Kernel(_node)]);
        Assert.True(await before);
        Assert.False(admitting.IsCompleted);

        await WaitAsync(() => kernel.Lists == 2, "a read after the dial", 5_000);
        kernel.Answer(1, [Kernel(_node), Kernel(paired, "New box")]);
        var (admission, refusal) = await admitting;

        Assert.Null(refusal);
        Assert.Equal(paired, admission!.NodeId);
    }

    [Fact]
    public async Task A_503_refusal_carries_its_retry_after_header()
    {
        var context = Context(_jwt, IngressToken(_jwt, RemoteId(99)));
        context.Response.Body = new MemoryStream();

        await _hub.HandleAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(((int)NodeHub.RegistryRetryAfter.TotalSeconds).ToString(), context.Response.Headers.RetryAfter.ToString());
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(90, false)]
    [InlineData(300, false)]
    public async Task Only_a_token_living_at_most_60_seconds_is_the_kernels_identity(int seconds, bool admitted)
    {
        var (admission, refusal) = await AdmitAsync(IngressToken(_jwt, _node, lifetime: TimeSpan.FromSeconds(seconds)));

        Assert.Equal(TimeSpan.FromSeconds(60), NodeHub.MaxTokenLifetime);
        Assert.Equal(admitted, admission is not null);
        if (!admitted) Assert.Equal((403, "node_identity_required"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public async Task A_node_whose_owner_the_kernel_did_not_say_is_refused()
    {
        _registry.ApplyKernelNodes([Kernel(_node, owner: null!)]);

        var (admission, refusal) = await AdmitAsync(IngressToken(_jwt, _node));

        Assert.Null(admission);
        Assert.Equal((403, "node_owner_unknown"), (refusal!.Status, refusal.Code));
    }

    [Fact]
    public async Task A_node_paired_since_the_last_sync_is_admitted_after_one_resync()
    {
        var paired = RemoteId(2);
        var resyncs = 0;
        await using var hub = new NodeHub(_registry, _ => { }, new NodeHubOptions
        {
            Resync = _ =>
            {
                resyncs++;
                _registry.ApplyKernelNodes([Kernel(_node), Kernel(paired, "New box")]);
                return Task.FromResult(true);
            },
        });

        var (admission, refusal) = await hub.AdmitAsync(Context(_jwt, IngressToken(_jwt, paired)), CancellationToken.None);

        Assert.Null(refusal);
        Assert.Equal(paired, admission!.NodeId);
        Assert.Equal(1, resyncs);
    }

    [Fact]
    public async Task A_revoked_node_is_403_and_node_zero_is_never_a_link()
    {
        _registry.MarkRevoked(_node, "revoked_by_owner");

        var (_, revoked) = await AdmitAsync(IngressToken(_jwt, _node));
        var (_, zero) = await AdmitAsync(IngressToken(_jwt, LocalId));

        Assert.Equal((403, "node_revoked"), (revoked!.Status, revoked.Code));
        Assert.Equal((404, "node_unknown"), (zero!.Status, zero.Code));
    }

    [Fact]
    public async Task An_identity_for_someone_other_than_the_owner_is_refused()
    {
        var (_, refusal) = await AdmitAsync(IngressToken(_jwt, _node, owner: "someone-else"));

        Assert.Equal((403, "node_owner_mismatch"), (refusal!.Status, refusal.Code));
    }

    // ================================================================= the link

    [Fact]
    public async Task A_linked_node_is_online_with_its_description_clis_and_hardware()
    {
        var updated = new List<string>();
        _registry.Updated += updated.Add;
        await using var node = new PipedNode(_hub, _node);

        await WaitAsync(() => _registry.Find(_node)?.Report?.Hardware is not null, "the first report");
        var linked = _registry.Find(_node)!;

        Assert.Equal(NodeStatus.Online, linked.Status);
        Assert.Equal(NodeKind.Remote, linked.Kind);
        Assert.Equal("leaflinux", linked.MachineName);
        Assert.Equal("Virtual CPU", linked.ProcessorName);
        Assert.Equal("Fedora Linux 41 (Workstation)", linked.OsDescription);
        Assert.Equal("0.1.0", linked.Link!.AgentVersion);
        Assert.Equal("secret-service", linked.Link.CredentialStore);
        Assert.Equal(LinkProtocol.Current, linked.Link.Protocol);
        Assert.Contains(LinkFeatures.DiagEcho, linked.Link.Features);
        Assert.Collection(linked.Report!.Clis,
            claude => Assert.Equal(("claude", true, "2.1.0", true, "subscription"), (claude.Name, claude.Installed, claude.Version, claude.SignedIn, claude.AuthMethod)),
            codex => Assert.False(codex.Installed));
        Assert.Equal(8, linked.Report.Hardware!.Cpu.CoreCount);
        Assert.Equal(_node, linked.Report.Hardware.NodeId);
        Assert.Contains(_node, updated);
        Assert.Equal(new NodePlacement(false, NodeUnavailableReasons.RemoteExecutionNotAvailable), NodeRegistry.PlacementOf(linked));
    }

    [Fact]
    public async Task An_unconfirmed_node_links_as_pending_confirmation_and_is_never_eligible()
    {
        _registry.ApplyKernelNodes([Kernel(_node, confirmed: false)]);
        await using var node = new PipedNode(_hub, _node);

        await WaitAsync(() => _registry.Find(_node)?.Link is not null, "the link");

        var pending = _registry.Find(_node)!;
        Assert.Equal(NodeStatus.PendingConfirmation, pending.Status);
        Assert.Equal(NodeUnavailableReasons.NotConfirmed, NodeRegistry.PlacementOf(pending).Reason);
    }

    [Fact]
    public async Task Hardware_reports_refresh_the_node_without_an_update_event_and_cli_changes_raise_one()
    {
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Report?.Hardware is not null, "the first report");
        var updated = new List<string>();
        _registry.Updated += updated.Add;

        var hardware = System.Text.Json.JsonSerializer.SerializeToNode(Hardware(usage: 77), System.Text.Json.JsonSerializerOptions.Web);
        await node.Client.Session!.NotifyAsync(NodeHub.ReportMethod, new System.Text.Json.Nodes.JsonObject { ["hardware"] = hardware });
        await WaitAsync(() => _registry.Find(_node)!.Report!.Hardware!.Cpu.UsagePercent == 77, "the hardware report");
        Assert.Empty(updated);

        await node.Client.Session!.NotifyAsync(NodeHub.ReportMethod, new System.Text.Json.Nodes.JsonObject
        {
            ["clis"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "codex", ["installed"] = true, ["version"] = "0.9.0" }),
        });
        await WaitAsync(() => updated.Count > 0, "the CLI change");
        Assert.Equal("codex", Assert.Single(_registry.Find(_node)!.Report!.Clis).Name);
    }

    [Fact]
    public async Task A_goodbye_leaves_its_reason_when_the_link_closes()
    {
        var changes = new List<NodeStatusChange>();
        _registry.StatusChanged += changes.Add;
        var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");

        await node.Client.Session!.CallAsync(NodeHub.ByeMethod, new System.Text.Json.Nodes.JsonObject { ["reason"] = "unpair" });
        await node.DisposeAsync();
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Offline, "the link to close");

        Assert.Equal("unpaired_by_node", _registry.Find(_node)!.StatusReason);
        Assert.Equal([NodeStatus.Online, NodeStatus.Offline], changes.Select(change => change.Node.Status));
    }

    [Theory]
    [InlineData("unpair", "unpaired_by_node")]
    [InlineData("stop", "agent_stop")]
    [InlineData("update", "agent_update")]
    [InlineData("Not A Reason!", "agent_closed")]
    [InlineData(null, "agent_closed")]
    public void Goodbye_reasons_become_status_reasons(string? reason, string expected)
        => Assert.Equal(expected, NodeHub.ByeReason(reason));

    [Fact]
    public async Task Revocation_closes_the_link_at_once_and_refuses_every_later_link()
    {
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");

        await _hub.RevokeAsync(_node, "revoked_by_owner");

        await WaitAsync(() => node.Client.Status is LinkClientStatus.Refused or LinkClientStatus.Stopped
                              || node.Client.Session is null or { IsAttached: false }, "the link to close", 2_000);
        Assert.Null(_hub.Server.FindSession(_node));
        var revoked = _registry.Find(_node)!;
        Assert.Equal((NodeStatus.Revoked, "revoked_by_owner"), (revoked.Status, revoked.StatusReason));
        var (_, refusal) = await AdmitAsync(IngressToken(_jwt, _node));
        Assert.Equal("node_revoked", refusal!.Code);
        Assert.Throws<NodeUnavailableException>(() => _registry.HostFor(_node));
    }

    [Fact]
    public async Task A_revoke_between_admission_and_the_handshake_never_lets_the_link_up()
    {
        // Review finding: NodeHub checked revocation only at admission, and LinkServer remembered nothing.
        var (admission, _) = await AdmitAsync(IngressToken(_jwt, _node));
        Assert.NotNull(admission);

        await _hub.RevokeAsync(_node, "revoked_by_owner");
        await using var node = new PipedNode(_hub, _node);

        await WaitAsync(() => node.Client.Status == LinkClientStatus.Refused, "the refusal", 5_000);
        Assert.Equal("node_revoked", node.Client.StatusReason);
        Assert.Null(_hub.Server.FindSession(_node));
        Assert.Null(_registry.Find(_node)!.Link);
        Assert.Equal(NodeStatus.Revoked, _registry.Find(_node)!.Status);
    }

    [Fact]
    public async Task A_node_missing_from_one_sync_keeps_its_link_and_comes_back_online()
    {
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");
        var session = _hub.Server.FindSession(_node)!;

        _registry.ApplyKernelNodes([]);
        await Task.Delay(300);

        Assert.Null(_registry.Find(_node));
        Assert.False(_hub.Server.IsRevoked(_node));
        Assert.Same(session, _hub.Server.FindSession(_node));
        Assert.True(session.IsAttached);
        Assert.Equal(LinkClientStatus.Online, node.Client.Status);

        _registry.ApplyKernelNodes([Kernel(_node)]);
        await WaitAsync(() => _registry.Find(_node) is { Status: NodeStatus.Online, Report.Hardware: not null }, "the node to come back");
        Assert.Equal(session.LinkSessionId, _registry.Find(_node)!.Link!.LinkSessionId);
    }

    [Fact]
    public async Task A_kernel_sync_that_finds_the_node_revoked_also_closes_its_link()
    {
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");

        _registry.ApplyKernelNodes([Kernel(_node, status: "revoked", reason: "revoked_by_owner")]);

        await WaitAsync(() => _hub.Server.FindSession(_node) is null, "the session to end", 2_000);
        Assert.Equal(NodeStatus.Revoked, _registry.Find(_node)!.Status);
    }

    [Fact]
    public async Task A_hello_that_names_another_node_than_the_bound_one_never_links()
    {
        var other = RemoteId(3);
        _registry.ApplyKernelNodes([Kernel(_node), Kernel(other, "Other")]);
        await using var node = new PipedNode(_hub, other, boundNodeId: _node);

        await WaitAsync(() => node.Client.Connections == 0 && node.Client.StatusReason is not null || node.Client.Status == LinkClientStatus.Refused,
            "the refusal", 3_000);
        await Task.Delay(100);

        Assert.Null(_hub.Server.FindSession(_node));
        Assert.Null(_hub.Server.FindSession(other));
        Assert.Equal(NodeStatus.Offline, _registry.Find(_node)!.Status);
        Assert.Equal(NodeStatus.Offline, _registry.Find(other)!.Status);
    }

    [Fact]
    public async Task Diag_echo_returns_every_word_once_and_in_order()
    {
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");

        var result = await _hub.EchoAsync(_node, (1 << 20) + 3, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal((1 << 20) + 8, result.Bytes);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.OutOfOrder);
        Assert.True(result.FramesReceived > 0);
        Assert.True(result.FramesSent > 0);
    }

    [Fact]
    public async Task Diag_echo_on_an_offline_node_says_so()
    {
        var error = await Assert.ThrowsAsync<NodeHubException>(() => _hub.EchoAsync(_node, 1024, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal("node_offline", error.Code);
    }

    // ================================================================= a real upgrade through AppHost's auth

    [Fact]
    public async Task A_real_upgrade_through_apphost_auth_links_only_the_kernels_identity()
    {
        var options = JwtOptions();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAppHostAuth(new AuthOptions { Jwt = options, Mode = AuthMode.LocalDefault });
        await using var app = builder.Build();
        app.UseWebSockets();
        app.UseAppHostJwtAuth();
        NodeHub.MapRoute(app, _hub);
        await app.StartAsync();
        var hubUri = new Uri(app.Urls.First().Replace("http://", "ws://") + NodeHub.Route);
        var jwt = new JwtService(options);

        async Task<(ClientWebSocket Socket, HttpStatusCode? Status)> UpgradeAsync(string? token)
        {
            var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.AddSubProtocol(LinkProtocol.SubProtocol);
            if (token is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            try
            {
                await socket.ConnectAsync(hubUri, CancellationToken.None);
                return (socket, null);
            }
            catch (WebSocketException)
            {
                return (socket, socket.HttpStatusCode);
            }
        }

        var (anonymous, anonymousStatus) = await UpgradeAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus);
        anonymous.Dispose();

        var token = IngressToken(jwt, _node);
        var (linked, linkedStatus) = await UpgradeAsync(token);
        Assert.Null(linkedStatus);
        Assert.Equal(LinkProtocol.SubProtocol, linked.SubProtocol);
        var transport = new WebSocketLinkTransport(linked);
        await using var client = new LinkClient(new LinkClientOptions
        {
            NodeId = _node,
            Agent = new AgentInfo("0.1.0", "linux", "x64"),
            Handler = new FakeNodeHandler(),
            Features = [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho],
            Dial = _ => Task.FromResult<ILinkTransport>(transport),
        });
        using var run = new CancellationTokenSource();
        var loop = client.RunAsync(run.Token);
        await WaitAsync(() => _registry.Find(_node)?.Report?.Hardware is not null, "the link over the real upgrade");

        var (_, replayStatus) = await UpgradeAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, replayStatus);

        run.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        await app.StopAsync();
    }
}
