using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Configuration;
using RedCompute.Core.Nodes;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// The Phase 3 node API (design 7.2): remote nodes on <c>/nodes</c> with <c>placement</c>, the kernel's
/// revocation and change routes, admin echo diagnostics, the node events, placement errors, and
/// <c>/nodes/hub</c> kept out of <c>/discover</c> and OpenAPI.
/// </summary>
public sealed class NodeEndpointRemoteTests : IAsyncLifetime
{
    private readonly JwtService _jwt = new(JwtOptions());
    private readonly NodeRegistry _nodes = Registry();
    private readonly NodeRegistryRemoteTests.FakeKernel _kernel = new();
    private WebApplication _app = null!;
    private NodeHub _hub = null!;
    private KernelNodeSync _sync = null!;

    public Task InitializeAsync()
    {
        _nodes.ApplyKernelNodes([Kernel(RemoteId(1), "Lab VM"), Kernel(RemoteId(2), "Spare", confirmed: false, status: "needs-update")]);
        _hub = new NodeHub(_nodes, _ => { });
        _sync = new KernelNodeSync(_nodes, _kernel, _ => { });
        _app = WebApplication.CreateSlimBuilder().Build();
        NodeEndpoints.Map(new EndpointRegistry(_app), _nodes, new CapabilityRegistry(), () => null, _hub, _sync);
        NodeHub.MapRoute(_app, _hub);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _hub.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Nodes_lists_node_zero_first_then_remote_nodes_with_placement()
    {
        var (status, json) = await SendAsync("GET", "/nodes", user: User(Owner));

        Assert.Equal(200, status);
        var nodes = json.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal([LocalId, RemoteId(1), RemoteId(2)], nodes.Select(node => node.GetProperty("id").GetString()));

        var zero = nodes[0];
        Assert.Equal(("local", "online", true, true), (zero.GetProperty("kind").GetString(), zero.GetProperty("status").GetString(),
            zero.GetProperty("isLocal").GetBoolean(), zero.GetProperty("confirmed").GetBoolean()));
        Assert.True(zero.GetProperty("placement").GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("placement").GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("owner").ValueKind);
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("link").ValueKind);
        Assert.Empty(zero.GetProperty("clis").EnumerateArray());

        var lab = nodes[1];
        Assert.Equal(("remote", "offline", false, Owner), (lab.GetProperty("kind").GetString(), lab.GetProperty("status").GetString(),
            lab.GetProperty("isLocal").GetBoolean(), lab.GetProperty("owner").GetString()));
        Assert.False(lab.GetProperty("placement").GetProperty("eligible").GetBoolean());
        Assert.Equal("offline", lab.GetProperty("placement").GetProperty("reason").GetString());
        Assert.Empty(lab.GetProperty("capabilities").EnumerateArray());
        Assert.False(lab.GetProperty("hardware").GetProperty("available").GetBoolean());

        var spare = nodes[2];
        Assert.Equal("needs-update", spare.GetProperty("status").GetString());
        Assert.False(spare.GetProperty("confirmed").GetBoolean());
        Assert.Equal("not_confirmed", spare.GetProperty("placement").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_linked_node_shows_its_link_clis_and_hardware()
    {
        _nodes.LinkUp(RemoteId(1), new NodeLinkState("s1", Now, Now, 18.25, -4, 1, ["describe.v1", "diag.echo"], "0.1.0", true, "cng"));
        _nodes.ApplyReport(RemoteId(1), [new NodeCliReport("claude", true, "2.1.0", true, "subscription", "C:\\Users\\lab\\claude.exe")], Hardware());

        var (status, json) = await SendAsync("GET", "/nodes/{id}", RemoteId(1), user: User(Owner));

        Assert.Equal(200, status);
        var node = json.RootElement;
        Assert.Equal("online", node.GetProperty("status").GetString());
        Assert.Equal("remote_execution_not_available", node.GetProperty("placement").GetProperty("reason").GetString());
        var link = node.GetProperty("link");
        Assert.Equal((18.25, true, "cng"), (link.GetProperty("rttMs").GetDouble(), link.GetProperty("updateAvailable").GetBoolean(),
            link.GetProperty("credentialStore").GetString()));
        var cli = Assert.Single(node.GetProperty("clis").EnumerateArray());
        Assert.Equal("claude", cli.GetProperty("name").GetString());
        Assert.False(cli.TryGetProperty("path", out _));
        Assert.True(node.GetProperty("hardware").GetProperty("available").GetBoolean());
        Assert.Equal(8, node.GetProperty("hardware").GetProperty("cpu").GetProperty("coreCount").GetInt32());
    }

    [Fact]
    public async Task Remote_nodes_are_visible_only_to_their_owner_and_admins_as_in_the_kernel()
    {
        // Review finding: GET /nodes showed every user's remote nodes to any caller, LocalDefault and Agents included.
        _nodes.ApplyKernelNodes([Kernel(RemoteId(1), "Lab VM"), Kernel(RemoteId(2), "Spare", confirmed: false, status: "needs-update"),
            Kernel(RemoteId(3), "Other's box", owner: "user-other")]);
        // Node zero first, then by name: Lab VM, Other's box, Spare.
        var all = new[] { LocalId, RemoteId(1), RemoteId(3), RemoteId(2) };
        var owners = new[] { LocalId, RemoteId(1), RemoteId(2) };
        var zeroOnly = new[] { LocalId };
        var cases = new (string Who, ClaimsPrincipal? User, string[] Sees)[]
        {
            ("the owner", User(Owner), owners),
            ("another user who is an admin", User("user-admin", "admin"), all),
            ("another user", User("user-stranger"), zeroOnly),
            ("the anonymous local identity (roles admin)", LocalDefault(), zeroOnly),
            ("no identity", null, zeroOnly),
            ("an Agent working for the owner", Validate(IngressToken(_jwt, "agent-1", owner: Owner, app: "nova", actorKind: "agent", roles: ["user"])), owners),
            ("an Agent working for someone else", Validate(IngressToken(_jwt, "agent-2", owner: "user-stranger", app: "nova", actorKind: "agent", roles: ["user"])), zeroOnly),
            ("a local-user execution badge", Validate(IngressToken(_jwt, "agent-3", owner: "local-user", app: "nova", actorKind: "agent", roles: ["admin"])), zeroOnly),
            ("another service", Validate(_jwt.GenerateServiceAccessToken("nova", roles: ["service", "admin"])), zeroOnly),
            ("the kernel", RedLeafService(_jwt), all),
        };

        foreach (var (who, user, sees) in cases)
        {
            var (status, json) = await SendAsync("GET", "/nodes", user: user);
            Assert.Equal(200, status);
            var seen = json.RootElement.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()!).ToList();
            Assert.True(sees.SequenceEqual(seen), $"{who} sees [{string.Join(", ", seen)}], not [{string.Join(", ", sees)}]");
            foreach (var id in all.Skip(1))
            {
                var (one, _) = await SendAsync("GET", "/nodes/{id}", id, user: user);
                Assert.True((sees.Contains(id) ? 200 : 404) == one, $"{who}: GET /nodes/{id} was {one}");
            }
            var (local, _) = await SendAsync("GET", "/nodes/{id}", "local", user: user);
            Assert.Equal(200, local);
        }
    }

    private ClaimsPrincipal User(string subject, string role = "user")
        => Validate(_jwt.GenerateAccessToken(subject, subject + "@example.test", subject, [role]));

    private ClaimsPrincipal Validate(string token) => _jwt.ValidateToken(token) ?? throw new InvalidOperationException("token");

    [Fact]
    public async Task Only_the_kernel_reports_a_revocation()
    {
        foreach (var user in new[]
                 {
                     LocalDefault(),
                     _jwt.ValidateToken(_jwt.GenerateAccessToken(Owner, "o@example.test", "Owner", ["admin"]))!,
                     _jwt.ValidateToken(_jwt.GenerateServiceAccessToken("nova"))!,
                 })
        {
            var (status, json) = await SendAsync("POST", "/nodes/{id}/revoked", RemoteId(1), user: user);
            Assert.Equal(403, status);
            Assert.Equal("service_only", json.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(NodeStatus.Offline, _nodes.Find(RemoteId(1))!.Status);

        var (revoked, body) = await SendAsync("POST", "/nodes/{id}/revoked", RemoteId(1), """{"reason":"revoked_by_owner"}""", RedLeafService(_jwt));

        Assert.Equal(200, revoked);
        Assert.Equal(("revoked", "revoked_by_owner"), (body.RootElement.GetProperty("status").GetString(), body.RootElement.GetProperty("reason").GetString()));
        Assert.Equal(NodeStatus.Revoked, _nodes.Find(RemoteId(1))!.Status);
    }

    [Fact]
    public async Task Node_zero_cannot_be_revoked_and_ids_are_checked()
    {
        var (zero, _) = await SendAsync("POST", "/nodes/{id}/revoked", LocalId, user: RedLeafService(_jwt));
        var (bad, _) = await SendAsync("POST", "/nodes/{id}/revoked", "node_../x", user: RedLeafService(_jwt));
        var (unknown, json) = await SendAsync("POST", "/nodes/{id}/revoked", RemoteId(5), user: RedLeafService(_jwt));

        Assert.Equal((409, 400, 200), (zero, bad, unknown));
        // A revocation that arrives before the node's entity is synced still sticks.
        Assert.Equal("revoked", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("node_revoked", _nodes.LinkRefusal(RemoteId(5)));
    }

    [Fact]
    public async Task Only_the_kernel_reports_a_node_change()
    {
        var (refused, _) = await SendAsync("POST", "/nodes/changed", user: LocalDefault());
        var (accepted, _) = await SendAsync("POST", "/nodes/changed", user: RedLeafService(_jwt));

        Assert.Equal((403, 202), (refused, accepted));
    }

    [Fact]
    public async Task Echo_diagnostics_need_an_admin_in_person_and_a_linked_remote_node()
    {
        var admin = _jwt.ValidateToken(_jwt.GenerateAccessToken(Owner, "o@example.test", "Owner", ["admin"]))!;
        var member = _jwt.ValidateToken(_jwt.GenerateAccessToken("member", "m@example.test", "Member", ["user"]))!;
        var agent = Context(_jwt, IngressToken(_jwt, RemoteId(1), app: "nova", actorKind: "agent")).User;

        Assert.Equal(403, (await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), user: LocalDefault())).Status);
        Assert.Equal(403, (await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), user: member)).Status);
        Assert.Equal(403, (await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), user: agent)).Status);
        Assert.Equal(404, (await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(9), user: admin)).Status);
        Assert.Equal(409, (await SendAsync("POST", "/nodes/{id}/diag/echo", "local", user: admin)).Status);
        var (offline, json) = await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), """{"bytes":4096}""", admin);
        Assert.Equal(409, offline);
        Assert.Equal("node_offline", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(400, (await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), """{"bytes":0}""", admin)).Status);
    }

    [Fact]
    public async Task Echo_diagnostics_run_through_the_endpoint()
    {
        await using var node = new PipedNode(_hub, RemoteId(1));
        await WaitAsync(() => _nodes.Find(RemoteId(1))?.Status == NodeStatus.Online, "the link");
        var admin = _jwt.ValidateToken(_jwt.GenerateAccessToken(Owner, "o@example.test", "Owner", ["admin"]))!;

        var (status, json) = await SendAsync("POST", "/nodes/{id}/diag/echo", RemoteId(1), """{"bytes":65536}""", admin);

        Assert.Equal(200, status);
        Assert.Equal((65536, 0, 0), (json.RootElement.GetProperty("bytes").GetInt64(), json.RootElement.GetProperty("duplicates").GetInt64(),
            json.RootElement.GetProperty("outOfOrder").GetInt64()));
    }

    [Fact]
    public async Task The_hub_is_hidden_from_discover_and_openapi_and_the_node_routes_are_listed()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);
        var descriptor = new RedComputeServiceDescriptor(new RedComputeConfig { ApiPort = 18800 }, new CapabilityRegistry(), null, endpoints);
        NodeEndpoints.Map(endpoints, _nodes, new CapabilityRegistry(), () => null, _hub, _sync);
        NodeHub.MapRoute(app, _hub);

        var catalog = await new DiscoveryCatalogProvider(app, descriptor).GetAsync();
        var openApi = JsonSerializer.Serialize(OpenApiGenerator.Generate(descriptor, catalog));

        Assert.DoesNotContain(catalog.Routes, route => route.Path.StartsWith(NodeHub.Route, StringComparison.Ordinal));
        Assert.DoesNotContain("/nodes/hub", openApi);
        Assert.Contains("/nodes/{id}/revoked", openApi);
        Assert.Contains("/nodes/{id}/diag/echo", openApi);
        Assert.Contains("placement", openApi);
    }

    [Fact]
    public void Placement_on_a_remote_node_is_a_409_with_the_registrys_reason()
    {
        _nodes.LinkUp(RemoteId(1), new NodeLinkState("s1", Now, Now, null, null, 1, [], "0.1.0", false, null));
        _nodes.MarkRevoked(RemoteId(3), "revoked_by_owner");

        var online = PlacementParser.ResolveNode(new PlacementRequest(RemoteId(1), null, null, null), _nodes, User(Owner), out var onlineId);
        var offline = PlacementParser.ResolveNode(new PlacementRequest(RemoteId(2), null, null, null), _nodes, User(Owner), out _);
        // Revoked before its entity synced, so it has no owner: only an admin may name it.
        var revoked = PlacementParser.ResolveNode(new PlacementRequest(RemoteId(3), null, null, null), _nodes, User("user-admin", "admin"), out _);
        var unknown = PlacementParser.ResolveNode(new PlacementRequest(RemoteId(9), null, null, null), _nodes, User(Owner), out _);
        var local = PlacementParser.ResolveNode(new PlacementRequest("local", null, null, null), _nodes, User(Owner), out var localId);

        Assert.Equal((409, "node_not_eligible", "remote_execution_not_available"), (online!.Status, online.Code, online.Reason));
        Assert.Null(onlineId);
        Assert.Equal((409, "node_not_eligible", "not_confirmed"), (offline!.Status, offline.Code, offline.Reason));
        Assert.Equal((409, "node_not_eligible", "revoked"), (revoked!.Status, revoked.Code, revoked.Reason));
        Assert.Equal((404, "node_not_found"), (unknown!.Status, unknown.Code));
        Assert.Null(local);
        Assert.Equal(LocalId, localId);

        _nodes.LinkDown(RemoteId(1), "s1", "heartbeat_timeout");
        var down = PlacementParser.ResolveNode(new PlacementRequest(RemoteId(1), null, null, null), _nodes, User(Owner), out _);
        Assert.Equal((409, "node_offline", "offline"), (down!.Status, down.Code, down.Reason));
    }

    [Fact]
    public async Task Node_events_are_transitions_and_one_debounced_update_per_burst()
    {
        var events = new List<(string Type, string Json)>();
        using var publisher = new NodeEvents(_nodes, (type, data) => { lock (events) events.Add((type, JsonSerializer.Serialize(data))); },
            TimeSpan.FromMilliseconds(150));

        _nodes.LinkUp(RemoteId(1), new NodeLinkState("s1", Now, Now, null, null, 1, [], "0.1.0", false, null));
        _nodes.ApplyDescribe(RemoteId(1), new NodeDescribe("linux", "x64", "Fedora", "leaflinux", "CPU", "0.1.0", "cng"));
        _nodes.ApplyReport(RemoteId(1), [new NodeCliReport("claude", true, "2.1.0", true, null, null)], Hardware());
        _nodes.ApplyReport(RemoteId(1), null, Hardware(80));
        await WaitAsync(() => { lock (events) return events.Any(e => e.Type == NodeEvents.UpdatedEvent); }, "the update");
        await Task.Delay(300);

        lock (events)
        {
            var status = Assert.Single(events, e => e.Type == NodeEvents.StatusEvent);
            Assert.Contains("\"status\":\"online\"", status.Json);
            Assert.Contains(RemoteId(1), status.Json);
            Assert.Single(events, e => e.Type == NodeEvents.UpdatedEvent);
        }
    }

    private async Task<(int Status, JsonDocument Json)> SendAsync(string method, string pattern, string? id = null, string? body = null,
        ClaimsPrincipal? user = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                         && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Request.Method = method;
        if (id is not null) context.Request.RouteValues["id"] = id;
        if (user is not null) context.User = user;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text));
    }
}
