using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Hardware;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>GET /nodes and /nodes/{id} (design section 3): node zero with its hardware and providers.</summary>
public sealed class NodeEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 14, 13, 0, 0, TimeSpan.Zero);
    private static readonly NodeIdentityResult Identity =
        new(new NodeIdentity("node_01JCZ0000000000000000GOLD", Seen.AddDays(-30), "node_01JCZ0000000000000000GOLD"), true, false, null);

    private WebApplication _app = null!;
    private EndpointRegistry _endpoints = null!;
    private HardwareSnapshot? _snapshot;
    private NodeRegistry _nodes = new(Identity, NodeRegistryTests.Platform(), "0.2.0", Seen.AddHours(-1), () => Seen);

    public async Task InitializeAsync()
    {
        _app = WebApplication.CreateSlimBuilder().Build();
        _endpoints = new EndpointRegistry(_app);
        NodeEndpoints.Map(_endpoints, _nodes, Registry(), () => _snapshot);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Nodes_lists_exactly_node_zero_with_its_platform_hardware_and_providers()
    {
        _snapshot = Snapshot();
        var (status, json) = await GetAsync("/nodes");

        Assert.Equal(200, status);
        var node = Assert.Single(json.RootElement.GetProperty("nodes").EnumerateArray());
        AssertNodeZero(node);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("node_01JCZ0000000000000000GOLD")]
    public async Task One_node_by_id_or_the_local_alias(string id)
    {
        _snapshot = Snapshot();
        var (status, json) = await GetAsync("/nodes/{id}", id);

        Assert.Equal(200, status);
        AssertNodeZero(json.RootElement);
    }

    [Fact]
    public async Task An_unknown_node_is_404_problem_details()
    {
        var (status, json) = await GetAsync("/nodes/{id}", "node_UNKNOWN");

        Assert.Equal(404, status);
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Unknown node", json.RootElement.GetProperty("title").GetString());
        Assert.Contains("node_UNKNOWN", json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Hardware_is_unavailable_until_the_monitor_has_a_snapshot()
    {
        _snapshot = null;
        var (_, json) = await GetAsync("/nodes/{id}", "local");

        var hardware = json.RootElement.GetProperty("hardware");
        Assert.False(hardware.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hardware.GetProperty("cpu").ValueKind);
        Assert.Empty(hardware.GetProperty("gpus").EnumerateArray());
        // The capabilities do not depend on the hardware monitor.
        Assert.Equal(3, json.RootElement.GetProperty("capabilities").GetArrayLength());
    }

    [Fact]
    public async Task An_unpersisted_identity_is_reported_on_the_node()
    {
        var problem = new NodeIdentityResult(Identity.Identity, Persisted: false, Created: false,
            "node.json is unreadable and was left as it is: bad");
        _nodes = new NodeRegistry(problem, NodeRegistryTests.Platform(), "0.2.0", Seen, () => Seen);
        await DisposeAsync();
        await InitializeAsync();

        var (_, json) = await GetAsync("/nodes/{id}", "local");

        var identity = json.RootElement.GetProperty("identity");
        Assert.False(identity.GetProperty("persisted").GetBoolean());
        Assert.Contains("unreadable", identity.GetProperty("problem").GetString());
    }

    [Fact]
    public void Both_routes_are_described_and_form_the_nodes_area_in_discovery()
    {
        Assert.Contains(_endpoints.GetEndpoints(), e => e.Method == "GET" && e.Path == "/nodes");
        Assert.Contains(_endpoints.GetEndpoints(), e => e.Method == "GET" && e.Path == "/nodes/{id}");

        var catalog = DiscoveryCatalog.Build(((IEndpointRouteBuilder)_app).DataSources, _endpoints.GetEndpoints(),
            [], [RedComputeServiceDescriptor.NodesArea]);
        var routes = catalog.Routes.Where(route => route.Area == "nodes").ToList();
        Assert.Equal(["/nodes", "/nodes/{id}"], routes.Select(route => route.Path).Order(StringComparer.Ordinal));
        // Each response schema is the record's own: nothing to maintain by hand.
        Assert.All(routes, route => Assert.NotNull(route.Response));
        var schema = JsonSerializer.Serialize(routes.Single(route => route.Path == "/nodes/{id}").Response);
        Assert.Contains("\"isLocal\"", schema);
        Assert.Contains("\"capabilities\"", schema);
        Assert.Contains("node", RedComputeServiceDescriptor.NodesArea.Concepts!.Select(concept => concept.Type));
    }

    private void AssertNodeZero(JsonElement node)
    {
        Assert.Equal("node_01JCZ0000000000000000GOLD", node.GetProperty("id").GetString());
        Assert.Equal("GOLDEN-PC", node.GetProperty("name").GetString());
        Assert.Equal("local", node.GetProperty("kind").GetString());
        Assert.Equal("online", node.GetProperty("status").GetString());
        Assert.True(node.GetProperty("isLocal").GetBoolean());
        Assert.Equal("0.2.0", node.GetProperty("version").GetString());
        Assert.Equal(Seen, node.GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.True(node.GetProperty("identity").GetProperty("persisted").GetBoolean());

        var platform = node.GetProperty("platform");
        Assert.Equal("windows", platform.GetProperty("os").GetString());
        Assert.Equal("x64", platform.GetProperty("architecture").GetString());
        Assert.Equal("GOLDEN-PC", platform.GetProperty("machineName").GetString());
        Assert.Equal("Golden CPU 9000", platform.GetProperty("processorName").GetString());

        var hardware = node.GetProperty("hardware");
        Assert.True(hardware.GetProperty("available").GetBoolean());
        Assert.Equal(24, hardware.GetProperty("cpu").GetProperty("coreCount").GetInt32());
        Assert.Equal(64UL << 30, hardware.GetProperty("ram").GetProperty("totalBytes").GetUInt64());
        var gpu = Assert.Single(hardware.GetProperty("gpus").EnumerateArray());
        Assert.Equal("NVIDIA GeForce RTX 5090", gpu.GetProperty("name").GetString());
        Assert.Equal(32UL << 30, gpu.GetProperty("memoryTotalBytes").GetUInt64());
        Assert.Equal(8UL << 30, gpu.GetProperty("memoryUsedBytes").GetUInt64());

        // Sorted by display name; each provider with its own status, type and default flag.
        var capabilities = node.GetProperty("capabilities").EnumerateArray().ToList();
        Assert.Equal(["Image Generation", "Text to Speech", "Workflow"],
            capabilities.Select(c => c.GetProperty("displayName").GetString()));

        var image = capabilities[0];
        Assert.Equal("image-gen", image.GetProperty("slug").GetString());
        Assert.Equal("Running", image.GetProperty("status").GetString());
        Assert.Equal("provider", image.GetProperty("executionMode").GetString());
        var providers = image.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(["comfyui", "remote"], providers.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("ComfyUI", providers[0].GetProperty("type").GetString());
        Assert.Equal("Running", providers[0].GetProperty("status").GetString());
        Assert.True(providers[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal("Error", providers[1].GetProperty("status").GetString());
        Assert.False(providers[1].GetProperty("isDefault").GetBoolean());

        var tts = capabilities[1];
        Assert.Equal("Unsupported", tts.GetProperty("status").GetString());
        Assert.Equal("no wsl here", Assert.Single(tts.GetProperty("providers").EnumerateArray())
            .GetProperty("statusDetail").GetString());
        Assert.True(tts.GetProperty("sleeping").GetBoolean());

        var workflow = capabilities[2];
        Assert.Equal("external", workflow.GetProperty("executionMode").GetString());
        Assert.Equal("Running", workflow.GetProperty("status").GetString());
        Assert.Empty(workflow.GetProperty("providers").EnumerateArray());
    }

    private async Task<(int Status, JsonDocument Json)> GetAsync(string pattern, string? id = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern);
        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Request.Method = "GET";
        if (id is not null) context.Request.RouteValues["id"] = id;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await JsonDocument.ParseAsync(context.Response.Body));
    }

    private static CapabilityRegistry Registry()
    {
        var registry = new CapabilityRegistry();
        registry.Register("image-gen",
            new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image Generation", Icon = "ph-bold ph-image" },
            new CapabilityConfig
            {
                Providers =
                {
                    ["comfyui"] = new ProviderConfig { Type = "ComfyUI" },
                    ["remote"] = new ProviderConfig { Type = "Remote" },
                },
            },
            new Dictionary<string, IBackendProvider>
            {
                ["remote"] = new FakeProvider(() => throw new InvalidOperationException("health check failed")),
                ["comfyui"] = new FakeProvider(() => BackendStatus.Running),
            }, "comfyui");
        registry.Register("tts",
            new CapabilityDefinition { Slug = "tts", DisplayName = "Text to Speech" },
            new CapabilityConfig { Providers = { ["local"] = new ProviderConfig { Type = "TtsLocal" } } },
            new Dictionary<string, IBackendProvider>
            {
                ["local"] = new FakeProvider(() => BackendStatus.Unsupported, "no wsl here"),
            }, "local");
        registry.Get("tts")!.IsSleeping = true;
        registry.Register("workflow",
            new CapabilityDefinition { Slug = "workflow", DisplayName = "Workflow", ExecutionMode = CapabilityExecutionMode.External },
            new CapabilityConfig(), [], null);
        return registry;
    }

    private static HardwareSnapshot Snapshot() => new()
    {
        Timestamp = Seen,
        Cpu = new CpuInfo { CoreCount = 24, UsagePercent = 7.5 },
        Ram = new RamInfo { TotalBytes = 64UL << 30, UsedBytes = 20UL << 30 },
        Gpus =
        [
            new GpuInfo
            {
                Index = 0, Name = "NVIDIA GeForce RTX 5090",
                Memory = new GpuMemoryInfo { TotalBytes = 32UL << 30, UsedBytes = 8UL << 30 },
            },
        ],
    };

    private sealed class FakeProvider(Func<BackendStatus> status, string? detail = null) : IBackendProvider
    {
        public string Name => "fake";
        public string CapabilitySlug => "fake";
        public string? StatusDetail => detail;
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(status());
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
