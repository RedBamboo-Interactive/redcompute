using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// The Phase 4 MUST-FIX (design section 1): one rule decides who may see a node and who may run work on it,
/// and every route that names a node applies it. The matrix covers every caller type against every node
/// type, at the rule itself, at Automatic routing, and through generate, session and execute placement and
/// capability start/stop. A node the caller may not use answers exactly as an unknown id. With node zero
/// only, every caller keeps today's behaviour.
/// </summary>
public sealed class NodeAccessTests : IAsyncLifetime
{
    private const string Stranger = "user-stranger";
    private const string Admin = "user-admin";
    private const string Other = "user-other";

    private static readonly string Own = RemoteId(1);
    private static readonly string OwnOffline = RemoteId(2);
    private static readonly string Others = RemoteId(3);
    private static readonly string OwnUnconfirmed = RemoteId(4);
    private static readonly string Unknown = RemoteId(9);
    private static readonly string[] Remote = [Own, OwnOffline, Others, OwnUnconfirmed];
    private static readonly string[] Owned = [Own, OwnOffline, OwnUnconfirmed];

    private readonly JwtService _jwt = new(JwtOptions());
    private readonly NodeRegistry _nodes = Registry();
    private readonly RecordingProvider _provider = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-node-access-tests", Guid.NewGuid().ToString("N"));
    private JobTrackingService _jobs = null!;
    private WebApplication _app = null!;

    public Task InitializeAsync()
    {
        _nodes.ApplyKernelNodes([
            Kernel(Own, "Own box"),
            Kernel(OwnOffline, "Own offline"),
            Kernel(Others, "Other's box", owner: Other),
            Kernel(OwnUnconfirmed, "Own unconfirmed", confirmed: false),
        ]);
        _nodes.LinkUp(Own, new NodeLinkState("s1", Now, Now, null, null, 1, [], "0.1.0", false, null));
        _nodes.LinkUp(Others, new NodeLinkState("s3", Now, Now, null, null, 1, [], "0.1.0", false, null));

        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        _jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        var registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image", Description = "Image test" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [_provider.Name] = _provider }, _provider.Name);

        _app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, endpoints, registry, _jobs, (_, _) => { }, nodes: _nodes);
        GlobalEndpoints.Map(endpoints, registry, _jobs, null!, _nodes);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Every caller type, with the remote nodes it may see and place on.</summary>
    private IEnumerable<(string Who, ClaimsPrincipal? Caller, string[] Sees, string[] Places)> Callers()
    {
        string[] none = [];
        yield return ("the owner, in person", User(Owner), Owned, Owned);
        yield return ("another user", User(Stranger), none, none);
        yield return ("another user who is an admin", User(Admin, "admin"), Remote, Remote);
        yield return ("an Agent working for the owner", Execution(Owner, "nova", "agent", "user"), Owned, Owned);
        yield return ("an Agent working for someone else", Execution(Stranger, "nova", "agent", "user"), none, none);
        yield return ("an Agent working for an admin", Execution(Admin, "nova", "agent", "admin"), Remote, Remote);
        yield return ("a plugin call the kernel signed for the owner (Image Studio)", Execution(Owner, "image-studio", "user", "user"), Owned, Owned);
        yield return ("a background run re-signed for the owner (Flow)", Execution(Owner, "flow", "automation", "user"), Owned, Owned);
        yield return ("kernel work for no person (system)", Execution("system", "redleaf", "service", "admin"), none, none);
        yield return ("a local-user execution badge with roles admin", Execution("local-user", "nova", "agent", "admin"), none, none);
        yield return ("the anonymous local identity (roles admin)", LocalDefault(), none, none);
        yield return ("an unauthenticated request", new ClaimsPrincipal(new ClaimsIdentity()), none, none);
        yield return ("no identity at all", null, none, none);
        yield return ("another service with roles admin", Validate(_jwt.GenerateServiceAccessToken("nova", roles: ["service", "admin"])), none, none);
        yield return ("the kernel itself (service:redleaf)", RedLeafService(_jwt), Remote, none);
    }

    [Fact]
    public void The_rule_node_zero_is_everyones_and_a_remote_node_is_its_owners_and_admins()
    {
        var zero = _nodes.Find(LocalId)!;
        foreach (var (who, caller, sees, places) in Callers())
        {
            Assert.True(NodeAccess.Allows(caller, zero, NodeUse.See), $"{who} must see node zero");
            Assert.True(NodeAccess.Allows(caller, zero, NodeUse.Place), $"{who} must place on node zero");
            foreach (var id in Remote)
            {
                var node = _nodes.Find(id)!;
                Assert.True(sees.Contains(id) == NodeAccess.Allows(caller, node, NodeUse.See), $"{who}: See {node.Name}");
                Assert.True(places.Contains(id) == NodeAccess.Allows(caller, node, NodeUse.Place), $"{who}: Place {node.Name}");
            }
        }
    }

    [Fact]
    public void Automatic_considers_node_zero_and_the_persons_own_nodes_only_and_routes_everyone_to_node_zero_today()
    {
        foreach (var (who, caller, _, places) in Callers())
        {
            var candidates = NodeAccess.AutomaticCandidates(caller, _nodes).Select(node => node.Id).ToArray();
            // Never someone else's node, even for an admin or the kernel: Automatic is narrower than an explicit choice.
            string[] expected = places.SequenceEqual(Owned) ? [LocalId, Own, OwnOffline, OwnUnconfirmed] : [LocalId];
            Assert.True(expected.SequenceEqual(candidates), $"{who}: [{string.Join(", ", candidates)}]");

            // Production asks whether the node's host can run work: no remote node can, so node zero always.
            Assert.Equal(LocalId, NodeAccess.RouteAutomatic(caller, _nodes, node => NodeAccess.HostAvailable(_nodes, node)));
            // Were node zero unable to serve, Automatic would take the person's own node, and never another's.
            var withoutZero = NodeAccess.RouteAutomatic(caller, _nodes, node => node.Kind == NodeKind.Remote);
            Assert.True((places.SequenceEqual(Owned) ? Own : LocalId) == withoutZero, $"{who}: routed to {withoutZero}");
        }
    }

    [Fact]
    public async Task Session_and_execute_placement_apply_the_rule_and_Automatic_resolves_to_node_zero()
    {
        var resolver = new PlacementResolver(_nodes, null, null, null);
        foreach (var (who, caller, _, places) in Callers())
        {
            foreach (var route in new[] { PlacementRoute.Session, PlacementRoute.Execute })
            {
                foreach (var node in new[] { "local", LocalId })
                {
                    using var body = JsonDocument.Parse($$$"""{"placement":{"nodeId":"{{{node}}}"}}""");
                    Assert.Null(UnifiedSessionEndpoints.ReadPlacement(body.RootElement, route, false, caller, _nodes, out var placement));
                    Assert.Equal(LocalId, (await resolver.ResolveAsync(placement!, route, caller)).Value!.NodeId);
                }

                var automatic = await resolver.ResolveAsync(new PlacementRequest(null, null, null, null), route, caller);
                Assert.True(LocalId == automatic.Value?.NodeId, $"{who}: Automatic {route} went to {automatic.Value?.NodeId}");

                var unknown = await ReadPlacementAsync(Unknown, route, caller);
                Assert.Equal((404, "node_not_found"), (unknown.Status, unknown.Code));
                foreach (var id in Remote)
                {
                    var answer = await ReadPlacementAsync(id, route, caller);
                    if (places.Contains(id))
                        Assert.True(answer.Status == 409 && answer.Code is "node_offline" or "node_not_eligible", $"{who}: {route} {id} was {answer.Status} {answer.Code}");
                    else
                        Assert.True(SameAsUnknown(answer, id, unknown), $"{who}: {route} {id} answered {answer.Status} {answer.Body}, unlike an unknown id");

                    var resolved = await resolver.ResolveAsync(new PlacementRequest(id, null, null, null), route, caller);
                    Assert.Equal(places.Contains(id) ? 409 : 404, resolved.Error!.Status);
                }
            }
        }
    }

    [Fact]
    public async Task Generate_applies_the_rule_and_node_zero_and_Automatic_behave_as_without_placement()
    {
        foreach (var (who, caller, _, places) in Callers())
        {
            var baseline = await GenerateAsync(caller, placement: null);
            foreach (var node in new[] { "local", LocalId, null })
            {
                var answer = await GenerateAsync(caller, new { nodeId = node });
                Assert.True(baseline.Status == answer.Status, $"{who}: placement {node ?? "null"} gave {answer.Status}, without placement {baseline.Status}");
            }

            var unknown = await GenerateAsync(caller, new { nodeId = Unknown });
            Assert.Equal((404, "node_not_found"), (unknown.Status, unknown.Code));
            foreach (var id in Remote)
            {
                var answer = await GenerateAsync(caller, new { nodeId = id });
                if (places.Contains(id))
                    Assert.True(answer.Status == 409, $"{who}: generate on {id} was {answer.Status} {answer.Code}");
                else
                    Assert.True(SameAsUnknown(answer, id, unknown), $"{who}: generate on {id} answered {answer.Status} {answer.Body}, unlike an unknown id");
            }
        }
        // Work ran (node zero is the only place it can), and the provider never saw a placement.
        Assert.NotEmpty(_provider.Requests);
        Assert.All(_provider.Requests, request => Assert.Equal(["prompt"], request.Parameters.Keys));
        Assert.Equal(200, (await GenerateAsync(User(Owner), new { nodeId = "local" })).Status);
        Assert.Equal(200, (await GenerateAsync(LocalDefault(), new { nodeId = "local" })).Status);
    }

    [Fact]
    public async Task Capability_start_and_stop_apply_the_rule_and_without_a_node_mean_node_zero_as_before()
    {
        foreach (var pattern in new[] { "/control/start/{slug}", "/control/stop/{slug}", "/control/start/{slug}/{providerName}", "/control/stop/{slug}/{providerName}" })
        {
            foreach (var (who, caller, _, places) in Callers())
            {
                var before = await ControlAsync(pattern, caller, null);
                Assert.True(before.Status == 200, $"{who}: {pattern} without a node was {before.Status} {before.Body}");
                foreach (var node in new[] { "local", LocalId })
                    Assert.True(before.Body == (await ControlAsync(pattern, caller, node)).Body, $"{who}: {pattern} on {node}");

                var unknown = await ControlAsync(pattern, caller, Unknown);
                Assert.Equal((404, "node_not_found"), (unknown.Status, unknown.Code));
                foreach (var id in Remote)
                {
                    var answer = await ControlAsync(pattern, caller, id);
                    if (places.Contains(id))
                        Assert.True(answer.Status == 409, $"{who}: {pattern} on {id} was {answer.Status}");
                    else
                        Assert.True(SameAsUnknown(answer, id, unknown), $"{who}: {pattern} on {id} answered {answer.Status} {answer.Body}");
                }
            }
        }
    }

    [Fact]
    public void The_nodes_list_and_placement_share_one_rule()
    {
        foreach (var (who, caller, sees, _) in Callers())
            foreach (var node in _nodes.Nodes)
                Assert.True(NodeEndpoints.CanSee(caller ?? new ClaimsPrincipal(), node) == NodeAccess.Allows(caller ?? new ClaimsPrincipal(), node, NodeUse.See),
                    $"{who}: {node.Name}");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Answer(int Status, string? Code, string Body);

    /// <summary>The same status, error and body as the unknown id's, once the ids are swapped: nothing tells them apart.</summary>
    private static bool SameAsUnknown(Answer answer, string id, Answer unknown)
        => answer.Status == unknown.Status && answer.Code == unknown.Code && answer.Body.Replace(id, Unknown, StringComparison.Ordinal) == unknown.Body;

    private ClaimsPrincipal User(string subject, string role = "user")
        => Validate(_jwt.GenerateAccessToken(subject, subject + "@example.test", subject, [role]));

    /// <summary>A signed execution identity for a person (or <c>system</c>), as the kernel mints it for an app, Agent or plugin.</summary>
    private ClaimsPrincipal Execution(string person, string app, string actorKind, string role)
        => Validate(IngressToken(_jwt, actorKind == "agent" ? "agent-" + person : person, owner: person, app: app, actorKind: actorKind, roles: [role]));

    private ClaimsPrincipal Validate(string token) => _jwt.ValidateToken(token) ?? throw new InvalidOperationException("token");

    private async Task<Answer> ReadPlacementAsync(string nodeId, PlacementRoute route, ClaimsPrincipal? caller)
    {
        using var body = JsonDocument.Parse($$$"""{"placement":{"nodeId":"{{{nodeId}}}"}}""");
        var result = UnifiedSessionEndpoints.ReadPlacement(body.RootElement, route, false, caller, _nodes, out _)
            ?? throw new InvalidOperationException($"{nodeId} was accepted");
        var context = NewContext(caller);
        await result.ExecuteAsync(context);
        return await ReadAsync(context);
    }

    private Task<Answer> GenerateAsync(ClaimsPrincipal? caller, object? placement)
    {
        var body = placement is null
            ? JsonSerializer.Serialize(new { prompt = "a red leaf" })
            : JsonSerializer.Serialize(new { prompt = "a red leaf", placement });
        return SendAsync("POST", "/image-gen/generate", caller, body);
    }

    private Task<Answer> ControlAsync(string pattern, ClaimsPrincipal? caller, string? nodeId)
        => SendAsync("POST", pattern, caller, null, nodeId);

    private async Task<Answer> SendAsync(string method, string pattern, ClaimsPrincipal? caller, string? body, string? nodeId = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                         && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
        var context = NewContext(caller);
        context.Request.Method = method;
        context.Request.Headers["X-System-Reason"] = "node access matrix test";
        if (pattern.Contains("{slug}", StringComparison.Ordinal)) context.Request.RouteValues["slug"] = "image-gen";
        if (pattern.Contains("{providerName}", StringComparison.Ordinal)) context.Request.RouteValues["providerName"] = _provider.Name;
        if (nodeId is not null) context.Request.QueryString = new QueryString("?nodeId=" + Uri.EscapeDataString(nodeId));
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        await endpoint.RequestDelegate!(context);
        return await ReadAsync(context);
    }

    private DefaultHttpContext NewContext(ClaimsPrincipal? caller)
    {
        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (caller is not null)
        {
            context.User = caller;
            if (ExecutionIdentityClaims.TryRead(caller, out var identity, out _))
                context.Items[ExecutionIdentityClaims.HttpContextItemKey] = identity;
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<Answer> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        string? code = null;
        if (text.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) code = error.GetString();
        }
        return new Answer(context.Response.StatusCode, code, text);
    }

    private sealed class RecordingProvider : IPluginProvider
    {
        public List<JobRequest> Requests { get; } = [];

        public string Name => "recording";
        public string CapabilitySlug => "image-gen";
        public string DisplayName => "Recording";
        public string ProviderType => "Recording";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = "{}" });
        }
    }
}
