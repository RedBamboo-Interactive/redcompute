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
using RedCompute.Core.Providers;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// Phase 4 P6 at the API (design 3.1, 3.3, 5.1, 1.2): an eligible node in <c>/nodes</c> with its capabilities,
/// <c>/control/start|stop/{slug}?nodeId=</c> driving its runtime (never node zero's provider), and the routes that
/// cannot run on it yet answering 422 <c>provider_unavailable_on_node</c>, in the design's check order.
/// </summary>
public sealed class RemoteNodeEndpointTests : IAsyncLifetime
{
    private const string Admin = "user-admin";
    private const string Stranger = "user-stranger";
    private static readonly string Own = RemoteId(1);
    private static readonly string Others = RemoteId(3);
    private static readonly string Unknown = RemoteId(9);

    private readonly JwtService _jwt = new(JwtOptions());
    private readonly NodeRegistry _nodes = Registry();
    private readonly CountingProvider _provider = new();
    private readonly RuntimeNodeHandler _ownAgent = new();
    private readonly RuntimeNodeHandler _othersAgent = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-remote-endpoint-tests", Guid.NewGuid().ToString("N"));
    private readonly List<PipedNode> _piped = [];
    private CapabilityRegistry _registry = null!;
    private NodeHub _hub = null!;
    private JobTrackingService _jobs = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        _nodes.ApplyKernelNodes([Kernel(Own, "Own box"), Kernel(Others, "Other's box", owner: "user-other")]);
        _hub = new NodeHub(_nodes, _ => { });
        _ownAgent.Runtimes.Add(RuntimeNodeHandler.ComfyUi("comfyui-mine", managed: "attach", status: "running", port: 8190, accelerator: "cpu"));
        _piped.Add(new PipedNode(_hub, Own, nodeHandler: _ownAgent, features: RuntimeNodeHandler.Phase4Features));
        _piped.Add(new PipedNode(_hub, Others, nodeHandler: _othersAgent, features: RuntimeNodeHandler.Phase4Features));
        await WaitAsync(() => _nodes.RuntimesOf(Own).Count == 2 && _nodes.RuntimesOf(Others).Count == 1, "both runtime lists");

        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        _jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        _registry = new CapabilityRegistry();
        _registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image generation", Description = "Images" },
            new CapabilityConfig { Providers = { ["comfyui"] = new ProviderConfig { Type = "ComfyUI" } } },
            new Dictionary<string, IBackendProvider> { ["comfyui"] = _provider }, "comfyui");
        _registry.Register("tts", new CapabilityDefinition { Slug = "tts", DisplayName = "Speech", Description = "Speech" },
            new CapabilityConfig { Providers = { ["tts-local"] = new ProviderConfig { Type = "TtsLocal" } } },
            new Dictionary<string, IBackendProvider> { ["tts-local"] = new CountingProvider("tts-local", "tts") }, "tts-local");

        _app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, endpoints, _registry, _jobs, (_, _) => { }, nodes: _nodes);
        GlobalEndpoints.Map(endpoints, _registry, _jobs, null!, _nodes);
        NodeEndpoints.Map(endpoints, _nodes, _registry, () => null, _hub);
    }

    public async Task DisposeAsync()
    {
        foreach (var piped in _piped) await piped.DisposeAsync();
        await _hub.DisposeAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // ================================================================= /nodes

    [Fact]
    public async Task Nodes_shows_an_eligible_node_with_one_capability_per_runtime_and_node_zeros_shape_is_unchanged()
    {
        var answer = await SendAsync("GET", "/nodes", User(Owner));
        using var json = JsonDocument.Parse(answer.Body);
        var nodes = json.RootElement.GetProperty("nodes").EnumerateArray().ToList();

        var zero = nodes.Single(node => node.GetProperty("isLocal").GetBoolean());
        foreach (var capability in zero.GetProperty("capabilities").EnumerateArray())
            Assert.Equal(["slug", "displayName", "icon", "color", "category", "status", "executionMode", "sleeping", "disabled", "providers"],
                capability.EnumerateObject().Select(member => member.Name));

        var own = nodes.Single(node => node.GetProperty("id").GetString() == Own);
        Assert.True(own.GetProperty("placement").GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, own.GetProperty("placement").GetProperty("reason").ValueKind);
        var capabilities = own.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.Equal(["comfyui-1", "comfyui-mine"], capabilities.Select(c => c.GetProperty("runtime").GetProperty("id").GetString()));
        var managed = capabilities[0];
        Assert.Equal(("image-gen", "Image generation", "comfyui", "Stopped", true, "cuda:0"),
            (managed.GetProperty("slug").GetString(), managed.GetProperty("displayName").GetString(), managed.GetProperty("provider").GetString(),
             managed.GetProperty("status").GetString(), managed.GetProperty("available").GetBoolean(), managed.GetProperty("accelerator").GetString()));
        Assert.False(managed.TryGetProperty("reason", out _));
        Assert.Equal(("spawn", "0.3.40"), (managed.GetProperty("runtime").GetProperty("managed").GetString(),
            managed.GetProperty("runtime").GetProperty("version").GetString()));
        Assert.Equal("attach", capabilities[1].GetProperty("runtime").GetProperty("managed").GetString());
        // Never a path or an argument, and never a capability its kind does not serve.
        Assert.DoesNotContain("ai-session", answer.Body);
        Assert.DoesNotContain(nodes, node => node.GetProperty("id").GetString() == Others);
    }

    [Fact]
    public async Task An_offline_nodes_capabilities_read_stopped_and_unavailable_never_an_error()
    {
        await _piped[0].DisposeAsync();
        _piped.RemoveAt(0);
        await WaitAsync(() => _nodes.Find(Own)?.Status == Core.Nodes.NodeStatus.Offline, "the link to close");

        var answer = await SendAsync("GET", "/nodes/{id}", User(Owner), id: Own);
        using var json = JsonDocument.Parse(answer.Body);

        Assert.Equal("offline", json.RootElement.GetProperty("placement").GetProperty("reason").GetString());
        foreach (var capability in json.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            Assert.Equal("Stopped", capability.GetProperty("status").GetString());
            Assert.False(capability.GetProperty("available").GetBoolean());
            Assert.Equal("node_offline", capability.GetProperty("reason").GetString());
        }
    }

    // ================================================================= /control/start|stop/{slug}?nodeId=

    [Theory]
    [InlineData("/control/start/{slug}", "runtime.start", "Starting")]
    [InlineData("/control/start/{slug}/{providerName}", "runtime.start", "Starting")]
    [InlineData("/control/stop/{slug}", "runtime.stop", "Stopped")]
    [InlineData("/control/stop/{slug}/{providerName}", "runtime.stop", "Stopped")]
    public async Task Control_on_an_eligible_node_drives_its_runtime_by_id_and_never_node_zeros_provider(string pattern, string method, string status)
    {
        var answer = await SendAsync("POST", pattern, User(Owner), nodeId: Own);

        Assert.Equal(200, answer.Status);
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal(("image-gen", "comfyui", Own, "comfyui-1", status),
            (json.RootElement.GetProperty("slug").GetString(), json.RootElement.GetProperty("provider").GetString(),
             json.RootElement.GetProperty("nodeId").GetString(), json.RootElement.GetProperty("runtime").GetString(),
             json.RootElement.GetProperty("status").GetString()));
        Assert.Equal((method, """{"id":"comfyui-1"}"""), _ownAgent.Calls.Last());
        Assert.Equal((0, 0), (_provider.Starts, _provider.Stops));
        Assert.False(_registry.Get("image-gen")!.IsManuallyDisabled);
    }

    [Fact]
    public async Task Control_without_a_node_or_on_node_zero_is_node_zeros_as_before()
    {
        foreach (var node in new[] { null, "local", LocalId })
        {
            var callsBefore = _ownAgent.Calls.Count;
            var started = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: node);
            Assert.Equal((200, """{"slug":"image-gen","status":"Running"}"""), (started.Status, started.Body));
            Assert.Equal(callsBefore, _ownAgent.Calls.Count);
        }
        Assert.Equal(3, _provider.Starts);
    }

    [Fact]
    public async Task A_start_while_the_node_is_still_stopping_the_runtime_is_409_runtime_stopping()
    {
        _ownAgent.Stopping["comfyui-1"] = true;

        var refused = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own);

        Assert.Equal((409, "runtime_stopping"), (refused.Status, refused.Code));
        Assert.Equal(("runtime.start", """{"id":"comfyui-1"}"""), _ownAgent.Calls.Last());
        Assert.Equal((0, 0), (_provider.Starts, _provider.Stops));
    }

    [Fact]
    public async Task An_attach_runtime_is_runtime_not_managed_and_a_disabled_one_runtime_disabled()
    {
        // The owner disables the managed one: the enabled attach-mode one is what start means now.
        _ownAgent.Runtimes[0]["enabled"] = false;
        await _piped[0].Client.Session!.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi(enabled: false));
        await WaitAsync(() => _nodes.RuntimesOf(Own).Any(r => r.Id == "comfyui-1" && !r.Enabled), "the disable");

        var attach = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own);
        Assert.Equal((409, "runtime_not_managed"), (attach.Status, attach.Code));
        Assert.Equal(("runtime.start", """{"id":"comfyui-mine"}"""), _ownAgent.Calls.Last());

        _ownAgent.Runtimes[1]["enabled"] = false;
        await _piped[0].Client.Session!.NotifyAsync("runtime.changed",
            RuntimeNodeHandler.ComfyUi("comfyui-mine", managed: "attach", status: "running", enabled: false, port: 8190, accelerator: "cpu"));
        await WaitAsync(() => _nodes.RuntimesOf(Own).All(r => !r.Enabled), "the second disable");

        var disabled = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own);
        Assert.Equal((409, "runtime_disabled"), (disabled.Status, disabled.Code));
        Assert.Equal((0, 0), (_provider.Starts, _provider.Stops));
    }

    [Fact]
    public async Task A_capability_no_runtime_serves_there_is_422_with_where_it_is_available()
    {
        var answer = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own, slug: "tts");

        Assert.Equal((422, "provider_unavailable_on_node"), (answer.Status, answer.Code));
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal(Own, json.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal([LocalId], json.RootElement.GetProperty("availableOn").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_ownAgent.Calls.Where(call => call.Method != "runtime.list"));
    }

    [Fact]
    public async Task Someone_elses_node_is_the_unknown_answer_and_an_admin_may_use_it_and_the_kernel_may_not()
    {
        var unknown = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Unknown);
        var others = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Others);
        Assert.Equal((404, "node_not_found"), (unknown.Status, unknown.Code));
        Assert.Equal(unknown.Body.Replace(Unknown, Others, StringComparison.Ordinal), others.Body);

        var kernel = await SendAsync("POST", "/control/start/{slug}", RedLeafService(_jwt), nodeId: Own);
        Assert.Equal((404, "node_not_found"), (kernel.Status, kernel.Code));
        Assert.Empty(_othersAgent.Calls.Where(call => call.Method != "runtime.list"));

        var admin = await SendAsync("POST", "/control/start/{slug}", User(Admin, "admin"), nodeId: Others);
        Assert.Equal(200, admin.Status);
        Assert.Equal(("runtime.start", """{"id":"comfyui-1"}"""), _othersAgent.Calls.Last());

        // The admin's answer lists only nodes the caller may see.
        var noRuntime = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own, slug: "tts");
        Assert.DoesNotContain(Others, noRuntime.Body);
    }

    [Fact]
    public async Task An_offline_node_is_node_offline_and_nothing_is_sent()
    {
        await _piped[0].DisposeAsync();
        _piped.RemoveAt(0);
        await WaitAsync(() => _nodes.Find(Own)?.Status == Core.Nodes.NodeStatus.Offline, "the link to close");

        var answer = await SendAsync("POST", "/control/start/{slug}", User(Owner), nodeId: Own);

        Assert.Equal((409, "node_offline"), (answer.Status, answer.Code));
    }

    // ================================================================= routes that cannot run there yet

    [Fact]
    public async Task Generate_on_an_eligible_node_is_422_provider_unavailable_on_node_and_reaches_no_provider()
    {
        var answer = await SendAsync("POST", "/image-gen/generate", User(Owner),
            body: JsonSerializer.Serialize(new { prompt = "a red leaf", placement = new { nodeId = Own } }));

        Assert.Equal((422, "provider_unavailable_on_node"), (answer.Status, answer.Code));
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(PlacementRoute.Session)]
    [InlineData(PlacementRoute.Execute)]
    public async Task Sessions_and_execute_on_an_eligible_node_are_422_and_the_resolver_agrees(PlacementRoute route)
    {
        using var body = JsonDocument.Parse($$$"""{"placement":{"nodeId":"{{{Own}}}"}}""");
        var result = UnifiedSessionEndpoints.ReadPlacement(body.RootElement, route, false, User(Owner), _nodes, out var placement);

        Assert.Null(placement);
        var context = NewContext(User(Owner));
        await result!.ExecuteAsync(context);
        var answer = await ReadAsync(context);
        Assert.Equal((422, "provider_unavailable_on_node"), (answer.Status, answer.Code));
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal([LocalId], json.RootElement.GetProperty("availableOn").EnumerateArray().Select(e => e.GetString()));

        var resolved = await new PlacementResolver(_nodes, null, null, null)
            .ResolveAsync(new PlacementRequest(Own, null, null, null), route, User(Owner));
        Assert.Equal((422, "provider_unavailable_on_node"), (resolved.Error!.Status, resolved.Error.Code));

        // Automatic still lands on node zero for everyone (node zero can always take it).
        var automatic = await new PlacementResolver(_nodes, null, null, null)
            .ResolveAsync(new PlacementRequest(null, null, null, null), route, User(Owner));
        Assert.Equal(LocalId, automatic.Value!.NodeId);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Answer(int Status, string? Code, string Body);

    private ClaimsPrincipal User(string subject, string role = "user")
        => _jwt.ValidateToken(_jwt.GenerateAccessToken(subject, subject + "@example.test", subject, [role]))!;

    private async Task<Answer> SendAsync(string method, string routePattern, ClaimsPrincipal? caller, string? nodeId = null,
        string slug = "image-gen", string? id = null, string? body = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern
                         && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
        var context = NewContext(caller);
        context.Request.Method = method;
        context.Request.Headers["X-System-Reason"] = "remote node endpoint test";
        if (routePattern.Contains("{slug}", StringComparison.Ordinal)) context.Request.RouteValues["slug"] = slug;
        if (routePattern.Contains("{providerName}", StringComparison.Ordinal))
            context.Request.RouteValues["providerName"] = _registry.Get(slug)!.DefaultProviderName;
        if (id is not null) context.Request.RouteValues["id"] = id;
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

    private sealed class CountingProvider(string name = "comfyui", string slug = "image-gen") : IPluginProvider
    {
        public List<JobRequest> Requests { get; } = [];
        public int Starts;
        public int Stops;

        public string Name => name;
        public string CapabilitySlug => slug;
        public string DisplayName => name;
        public string ProviderType => "ComfyUI";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) { Interlocked.Increment(ref Starts); return Task.FromResult(true); }
        public Task StopAsync(CancellationToken ct = default) { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
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
