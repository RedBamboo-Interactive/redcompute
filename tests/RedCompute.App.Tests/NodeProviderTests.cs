using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
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
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Hardware;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// Phase 4 P7 (design 3.1, 3.2, 3.3, without the GPU preference): a provider per remote node and runtime, under node
/// zero's name and over the node's host; <c>/{slug}/generate</c> with that node's id runs it; Automatic never leaves
/// node zero; <c>/status</c> lists remote providers by See; a remote GPU has its own gate; a remote job is costed from
/// its node's GPU; and RedCompute stopping never stops a node's runtime.
/// </summary>
public sealed class NodeProviderTests : IAsyncLifetime
{
    private const string Admin = "user-admin";
    private const string Stranger = "user-stranger";
    private static readonly string Own = RemoteId(1);
    private static readonly string Others = RemoteId(3);

    private readonly JwtService _jwt = new(JwtOptions());
    private readonly NodeRegistry _nodes = Registry();
    private readonly ZeroProvider _zero = new("comfyui");
    private readonly ZeroProvider _other = new("other");
    private readonly RuntimeNodeHandler _ownAgent = new();
    private readonly RuntimeNodeHandler _othersAgent = new();
    private readonly AcceleratorAdmissionCoordinator _admission = new(nodeId: LocalId);
    private readonly ConcurrentQueue<ForwardingProvider> _created = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-node-provider-tests", Guid.NewGuid().ToString("N"));
    private readonly List<PipedNode> _piped = [];
    private CapabilityRegistry _registry = null!;
    private NodeHub _hub = null!;
    private NodeProviderManager _manager = null!;
    private JobTrackingService _jobs = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        _nodes.ApplyKernelNodes([Kernel(Own, "Own box"), Kernel(Others, "Other's box", owner: "user-other")]);
        _hub = new NodeHub(_nodes, _ => { });
        _ownAgent.Runtimes[0] = RuntimeNodeHandler.ComfyUi(status: "running");
        _othersAgent.Runtimes[0] = RuntimeNodeHandler.ComfyUi(status: "running");
        _piped.Add(new PipedNode(_hub, Own, nodeHandler: _ownAgent, features: RuntimeNodeHandler.Phase4Features));
        _piped.Add(new PipedNode(_hub, Others, nodeHandler: _othersAgent, features: RuntimeNodeHandler.Phase4Features));
        await WaitAsync(() => _nodes.RuntimesOf(Own).Count == 1 && _nodes.RuntimesOf(Others).Count == 1, "both runtime lists");

        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        _jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        _jobs.StampNodeId = _nodes.StampNodeId;
        _registry = new CapabilityRegistry();
        _registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image generation", Description = "Images" },
            new CapabilityConfig
            {
                Providers =
                {
                    ["comfyui"] = NodeZeroComfyConfig(),
                    ["other"] = new ProviderConfig { Type = "Suno" },
                },
            },
            new Dictionary<string, IBackendProvider> { ["comfyui"] = _zero, ["other"] = _other }, "comfyui");
        _registry.Register("tts", new CapabilityDefinition { Slug = "tts", DisplayName = "Speech", Description = "Speech" },
            new CapabilityConfig { Providers = { ["tts-local"] = new ProviderConfig { Type = "TtsLocal" } } },
            new Dictionary<string, IBackendProvider> { ["tts-local"] = new ZeroProvider("tts-local", "tts") }, "tts-local");
        _manager = new NodeProviderManager(_registry, _nodes, _hub.RemoteHost, Create, _admission, _ => { });

        _app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, endpoints, _registry, _jobs, (_, _) => { }, nodes: _nodes);
        GlobalEndpoints.Map(endpoints, _registry, _jobs, null!, _nodes);
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        foreach (var piped in _piped) await piped.DisposeAsync();
        await _hub.DisposeAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private IPluginProvider Create(string type, ProviderConfig config, string slug, IExecutionHost host, IAcceleratorAdmissionCoordinator? admission)
    {
        var provider = new ForwardingProvider(type, config, slug, host, admission);
        _created.Enqueue(provider);
        return provider;
    }

    private static ProviderConfig NodeZeroComfyConfig() => new()
    {
        Type = "ComfyUI",
        DisplayName = "ComfyUI",
        WslDistro = "Ubuntu-24.04",
        VenvPath = "/home/me/venv",
        ServerPath = "/home/me/ComfyUI",
        BackendPort = 8188,
        LaunchCommand = "python main.py",
        Endpoint = "http://10.0.0.5:8188",
        ApiKey = "secret-key",
        TimeoutSeconds = 600,
        Extra = new Dictionary<string, object?>
        {
            ["Host"] = "0.0.0.0",
            ["ModelsDirectory"] = "D:/models",
            ["OutputDirectory"] = "D:/out",
            ["ServerArgs"] = "--listen",
            ["DatabaseUrl"] = "sqlite:///D:/comfy.db",
            ["AcceleratorId"] = "cuda:1",
            ["costPerJob"] = 0.5,
            ["_pollTimeoutSeconds"] = "900",
        },
    };

    private RemoteNodeProvider NodeProvider(string nodeId, string provider = "comfyui", string slug = "image-gen")
        => Assert.IsType<RemoteNodeProvider>(_registry.Get(slug)!.NodeProviders[new NodeProviderKey(provider, nodeId)]);

    private async Task ReportAsync(string nodeId, string status = "running", bool enabled = true, string accelerator = "cuda:0")
    {
        var agent = nodeId == Own ? _ownAgent : _othersAgent;
        var runtime = RuntimeNodeHandler.ComfyUi(status: status, enabled: enabled, accelerator: accelerator);
        agent.Runtimes[0] = runtime;
        var expected = RemoteRuntimeReport.Status(status);
        await _piped[nodeId == Own ? 0 : 1].Client.Session!.NotifyAsync("runtime.changed", runtime);
        await WaitAsync(() => _registry.Get("image-gen")!.NodeProviders.TryGetValue(new NodeProviderKey("comfyui", nodeId), out var p)
                              && p is RemoteNodeProvider { Runtime: { } r } remote
                              && r.Status == expected && r.Enabled == enabled && remote.Accelerator == accelerator, "the report");
    }

    // ================================================================= instances

    [Fact]
    public void A_reported_runtime_gets_a_provider_under_node_zeros_name_over_its_node_and_runtime()
    {
        var entry = _registry.Get("image-gen")!;
        Assert.Equal([new NodeProviderKey("comfyui", Own), new NodeProviderKey("comfyui", Others)],
            entry.NodeProviders.Keys.OrderBy(key => key.NodeId, StringComparer.Ordinal));
        // Node zero's providers are exactly what they were; tts has no remote runtime, so nothing.
        Assert.Equal(["comfyui", "other"], entry.Providers.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(_registry.Get("tts")!.NodeProviders);

        var own = NodeProvider(Own);
        var inner = Assert.IsType<ForwardingProvider>(own.Inner);
        var host = Assert.IsType<RemoteExecutionHost>(inner.Host);
        Assert.Equal((Own, "comfyui-1"), (host.NodeId, host.RuntimeId));
        Assert.Equal(("ComfyUI", "image-gen"), (inner.Type, inner.CapabilitySlug));
        Assert.Equal(Own, Assert.IsType<NodeScopedAcceleratorAdmission>(inner.Admission).NodeId);
        Assert.Equal(("comfyui-1", "cuda:0", Own), (own.RuntimeId, own.Accelerator, own.NodeId));
    }

    [Fact]
    public void A_remote_providers_config_carries_none_of_node_zeros_machine_settings()
    {
        var runtime = _nodes.RuntimesOf(Own)[0];
        var config = RemoteProviderConfig.For("ComfyUI", NodeZeroComfyConfig(), runtime);

        Assert.Equal(("ComfyUI", "ComfyUI", 600, 8188), (config.Type, config.DisplayName, config.TimeoutSeconds, config.BackendPort));
        Assert.Equal(["AcceleratorId", "_pollTimeoutSeconds"], config.Extra!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("cuda:0", config.Extra["AcceleratorId"]);
        Assert.Equal("900", config.Extra["_pollTimeoutSeconds"]);
        Assert.Null(config.WslDistro);
        Assert.Null(config.VenvPath);
        Assert.Null(config.ServerPath);
        Assert.Null(config.LaunchCommand);
        Assert.Null(config.Endpoint);
        Assert.Null(config.ApiKey);
        var serialized = JsonSerializer.Serialize(config);
        foreach (var leak in new[] { "Ubuntu", "/home/me", "D:/", "secret-key", "10.0.0.5", "0.0.0.0", "--listen", "cuda:1", "costPerJob" })
            Assert.DoesNotContain(leak, serialized);

        // No accelerator reported: a gate of its own, never node zero's cuda:0.
        var bare = RemoteProviderConfig.For("ComfyUI", null, runtime with { Accelerator = null, Ports = [] });
        Assert.Equal(("runtime-comfyui-1", null), ((string?)bare.Extra!["AcceleratorId"], bare.BackendPort));
    }

    [Fact]
    public async Task A_forgotten_or_revoked_node_or_a_changed_runtime_retires_its_provider()
    {
        var first = NodeProvider(Own);
        var firstInner = (ForwardingProvider)first.Inner;

        // The accelerator changes: a new instance, the old one disposed.
        await ReportAsync(Own, accelerator: "cuda:1");
        var second = NodeProvider(Own);
        Assert.NotSame(first, second);
        await WaitAsync(() => firstInner.Disposed == 1, "the old instance to be disposed");
        Assert.Equal("cuda:1", ((ForwardingProvider)second.Inner).Config.Extra!["AcceleratorId"]);

        // Revoked: gone. Forgotten: gone.
        _nodes.MarkRevoked(Others, "revoked_by_owner");
        await WaitAsync(() => !_registry.Get("image-gen")!.NodeProviders.ContainsKey(new("comfyui", Others)), "the revoked node's provider");
        _nodes.ApplyKernelNodes([Kernel(Others, "Other's box", owner: "user-other")]);
        await WaitAsync(() => _registry.Get("image-gen")!.NodeProviders.IsEmpty, "the forgotten node's provider");
        await WaitAsync(() => _created.All(created => created.Disposed == 1), "every instance to be disposed");
        // Never a runtime.stop for any of it.
        Assert.DoesNotContain(_ownAgent.Calls.Concat(_othersAgent.Calls), call => call.Method == "runtime.stop");
    }

    [Fact]
    public async Task A_retired_provider_finishes_its_running_job_before_it_is_disposed()
    {
        var provider = NodeProvider(Own);
        var inner = (ForwardingProvider)provider.Inner;
        inner.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = provider.ExecuteAsync(new JobRequest { CapabilitySlug = "image-gen", Parameters = new() { ["prompt"] = "x" } });
        await WaitAsync(() => inner.Requests.Count == 1, "the job to start");

        _nodes.ApplyKernelNodes([Kernel(Others, "Other's box", owner: "user-other")]);
        await WaitAsync(() => !_registry.Get("image-gen")!.NodeProviders.ContainsKey(new("comfyui", Own)), "the retirement");
        await Task.Delay(100);
        Assert.Equal(0, inner.Disposed);

        inner.Hold.SetResult();
        Assert.True((await running)!.Success);
        await WaitAsync(() => inner.Disposed == 1, "the disposal after the job");
        // A job that arrives after retirement is refused, not run.
        var late = await provider.ExecuteAsync(new JobRequest { CapabilitySlug = "image-gen", Parameters = new() { ["prompt"] = "y" } });
        Assert.Equal(("provider_unavailable_on_node", 1), (late!.ErrorCode, inner.Requests.Count));
    }

    // ================================================================= generate on a node

    [Fact]
    public async Task Generate_on_the_node_runs_its_provider_over_the_forward_and_the_job_records_the_node()
    {
        var answer = await GenerateAsync(User(Owner), Own);

        Assert.Equal(200, answer.Status);
        using var json = JsonDocument.Parse(answer.Body);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(MemoryRuntime.Blob(4096))), json.RootElement.GetProperty("sha256").GetString());
        var inner = (ForwardingProvider)NodeProvider(Own).Inner;
        var request = Assert.Single(inner.Requests);
        // The provider sees exactly what node zero's would: no placement.
        Assert.Equal(["prompt"], request.Parameters.Keys);
        var job = _jobs.GetJob(request.JobId!.Value)!;
        Assert.Equal((Own, "explicit", "ComfyUI", """{"prompt":"a red leaf"}"""), (job.NodeId, job.PlacementWhy, job.ProviderName, job.InputJson));
        Assert.Empty(_zero.Requests);
        Assert.Contains(_ownAgent.Runtime.Opens, open => open is { Method: "GET", Path: "/blob/4096", Runtime: "comfyui-1" });
        Assert.Empty(_othersAgent.Runtime.Opens);
    }

    [Fact]
    public async Task A_stopped_or_disabled_runtime_is_503_provider_not_running_with_the_node_and_no_job()
    {
        await ReportAsync(Own, status: "stopped");
        var stopped = await GenerateAsync(User(Owner), Own);
        Assert.Equal((503, "provider_not_running"), (stopped.Status, stopped.Code));
        using (var json = JsonDocument.Parse(stopped.Body))
        {
            Assert.Equal((Own, "Stopped"), (json.RootElement.GetProperty("nodeId").GetString(), json.RootElement.GetProperty("status").GetString()));
            Assert.Contains($"/control/start/image-gen?nodeId={Own}", json.RootElement.GetProperty("message").GetString());
        }

        await ReportAsync(Own, status: "running", enabled: false);
        var disabled = await GenerateAsync(User(Owner), Own);
        Assert.Equal((503, "provider_not_running"), (disabled.Status, disabled.Code));
        using (var json = JsonDocument.Parse(disabled.Body))
            Assert.Equal("runtime_disabled", json.RootElement.GetProperty("reason").GetString());

        Assert.Equal(0, _jobs.GetJobs(null, null, null, 100, 0).TotalCount);
        Assert.Empty(_ownAgent.Runtime.Opens);
        // Nothing starts by itself.
        Assert.DoesNotContain(_ownAgent.Calls, call => call.Method == "runtime.start");
    }

    [Fact]
    public async Task Provider_names_mean_the_same_on_every_node()
    {
        var named = await GenerateAsync(User(Owner), Own, provider: "comfyui");
        Assert.Equal(200, named.Status);
        Assert.Single(((ForwardingProvider)NodeProvider(Own).Inner).Requests);

        // A name no node has: node zero's 404, word for word.
        var unknownRemote = await GenerateAsync(User(Owner), Own, provider: "nope");
        var unknownZero = await GenerateAsync(User(Owner), "local", provider: "nope");
        Assert.Equal((404, "provider_not_found"), (unknownRemote.Status, unknownRemote.Code));
        Assert.Equal(unknownZero.Body, unknownRemote.Body);

        // Node zero's other provider has no runtime on that node: 422 with where it is, as the caller may see it.
        var elsewhere = await GenerateAsync(User(Owner), Own, provider: "other");
        Assert.Equal((422, "provider_unavailable_on_node"), (elsewhere.Status, elsewhere.Code));
        using var json = JsonDocument.Parse(elsewhere.Body);
        Assert.Equal([LocalId, Own], json.RootElement.GetProperty("availableOn").EnumerateArray().Select(e => e.GetString()));

        // tts: nothing on that node.
        var tts = await GenerateAsync(User(Owner), Own, slug: "tts");
        Assert.Equal((422, "provider_unavailable_on_node"), (tts.Status, tts.Code));
        Assert.Empty(_zero.Requests);
    }

    [Fact]
    public async Task A_name_only_a_node_the_caller_may_not_see_has_is_404_like_a_name_no_node_has()
    {
        // Review P4-P7, item 7: "only-on-others" exists on someone else's node alone (not on node zero, not on the
        // caller's). 404 or 422 is decided on the nodes the caller may see, so the owner learns nothing about it.
        var entry = _registry.Get("image-gen")!;
        entry.NodeProviders[new NodeProviderKey("only-on-others", Others)] = new ZeroProvider("only-on-others");

        var owner = await GenerateAsync(User(Owner), Own, provider: "only-on-others");
        var zero = await GenerateAsync(User(Owner), "local", provider: "only-on-others");
        Assert.Equal((404, "provider_not_found"), (owner.Status, owner.Code));
        Assert.Equal(zero.Body, owner.Body);

        // An admin, who may see that node, gets the 422 that says where it is.
        var admin = await GenerateAsync(User(Admin, "admin"), Own, provider: "only-on-others");
        Assert.Equal((422, "provider_unavailable_on_node"), (admin.Status, admin.Code));
        Assert.Empty(_ownAgent.Runtime.Opens);
    }

    [Fact]
    public async Task Someone_elses_node_and_the_kernel_reach_no_provider_and_an_admin_may_name_it()
    {
        var others = await GenerateAsync(User(Owner), Others);
        var unknown = await GenerateAsync(User(Owner), RemoteId(9));
        Assert.Equal((404, "node_not_found"), (others.Status, others.Code));
        Assert.Equal(unknown.Body.Replace(RemoteId(9), Others, StringComparison.Ordinal), others.Body);
        var kernel = await GenerateAsync(RedLeafService(_jwt), Own);
        Assert.Equal((404, "node_not_found"), (kernel.Status, kernel.Code));
        Assert.Empty(_othersAgent.Runtime.Opens);
        Assert.Empty(_ownAgent.Runtime.Opens);

        var admin = await GenerateAsync(User(Admin, "admin"), Others);
        Assert.Equal(200, admin.Status);
        Assert.Single(((ForwardingProvider)NodeProvider(Others).Inner).Requests);
    }

    [Fact]
    public async Task An_offline_nodes_provider_reads_stopped_and_unavailable_and_generate_there_is_409()
    {
        await _piped[0].DisposeAsync();
        _piped.RemoveAt(0);
        await WaitAsync(() => _nodes.Find(Own)?.Status == Core.Nodes.NodeStatus.Offline, "the link to close");

        Assert.Equal((BackendStatus.Stopped, "node_offline"), NodeProvider(Own).Availability());
        Assert.Equal(BackendStatus.Stopped, await NodeProvider(Own).GetStatusAsync());
        var answer = await GenerateAsync(User(Owner), Own);
        Assert.Equal((409, "node_offline"), (answer.Status, answer.Code));
    }

    // ================================================================= Automatic (design 3.3 without the GPU preference)

    /// <summary>The routing matrix: where the work runs for each placement, node zero's state and the remote runtime's accelerator.</summary>
    [Theory]
    [InlineData("absent", BackendStatus.Running, "cuda:0", "zero")]
    [InlineData("absent", BackendStatus.Stopped, "cuda:0", "zero-503")]
    [InlineData("null", BackendStatus.Running, "cuda:0", "zero")]
    [InlineData("null", BackendStatus.Stopped, "cuda:0", "zero-503")]
    [InlineData("local", BackendStatus.Stopped, "cuda:0", "zero-503")]
    [InlineData("zero-id", BackendStatus.Running, "cpu", "zero")]
    [InlineData("absent", BackendStatus.Stopped, "cpu", "zero-503")]
    [InlineData("own", BackendStatus.Running, "cuda:0", "remote")]
    [InlineData("own", BackendStatus.Stopped, "cuda:0", "remote")]
    [InlineData("own", BackendStatus.Running, "cpu", "remote")]
    [InlineData("own", BackendStatus.Stopped, "cpu", "remote")]
    public async Task Automatic_never_leaves_node_zero_and_a_named_node_runs_there_whatever_its_accelerator(
        string placement, BackendStatus zeroStatus, string accelerator, string expected)
    {
        _zero.Status = zeroStatus;
        await ReportAsync(Own, accelerator: accelerator);
        var nodeId = placement switch { "own" => Own, "local" => "local", "zero-id" => LocalId, _ => null };

        foreach (var caller in new[] { User(Owner), User(Admin, "admin") })
        {
            var answer = await GenerateAsync(caller, nodeId, placementNull: placement == "null", noPlacement: placement == "absent");
            switch (expected)
            {
                case "zero":
                    Assert.Equal(200, answer.Status);
                    break;
                case "zero-503":
                    // Node zero's answer, exactly as before P7.
                    Assert.Equal((503, """{"error":"provider_not_running","message":"Backend for 'image-gen' is Stopped. Start via POST /control/start/image-gen"}"""),
                        (answer.Status, answer.Body));
                    break;
                default:
                    Assert.Equal(200, answer.Status);
                    break;
            }
        }
        var remoteRuns = ((ForwardingProvider)NodeProvider(Own).Inner).Requests.Count;
        Assert.Equal(expected == "remote" ? 2 : 0, remoteRuns);
        Assert.Equal(expected == "zero" ? 2 : 0, _zero.Requests.Count);
        if (expected != "remote") Assert.Empty(_ownAgent.Runtime.Opens);
    }

    // ================================================================= /status

    [Fact]
    public async Task Status_lists_remote_providers_for_the_nodes_the_caller_may_see()
    {
        await ReportAsync(Own, status: "running", enabled: false);

        var owner = await StatusNodesAsync(User(Owner));
        Assert.Equal([(Own, "comfyui", "Running", false, "runtime_disabled", "cuda:0")], owner);
        var admin = await StatusNodesAsync(User(Admin, "admin"));
        Assert.Equal([Own, Others], admin.Select(entry => entry.NodeId));
        Assert.Equal((Others, "comfyui", "Running", true, (string?)null, "cuda:0"), admin[1]);
        Assert.Equal([Own, Others], (await StatusNodesAsync(RedLeafService(_jwt))).Select(entry => entry.NodeId));
        Assert.Empty(await StatusNodesAsync(User(Stranger)));
        Assert.Empty(await StatusNodesAsync(LocalDefault()));

        // providers[] is node zero's, unchanged.
        var answer = await SendAsync("GET", "/status", User(Admin, "admin"));
        using var json = JsonDocument.Parse(answer.Body);
        var image = json.RootElement.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "image-gen");
        Assert.Equal([LocalId, LocalId], image.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("nodeId").GetString()));
        var tts = json.RootElement.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "tts");
        Assert.Equal(JsonValueKind.Null, tts.GetProperty("nodes").ValueKind);
    }

    private async Task<List<(string NodeId, string Provider, string Status, bool Available, string? Reason, string? Accelerator)>> StatusNodesAsync(ClaimsPrincipal caller)
    {
        var answer = await SendAsync("GET", "/status", caller);
        using var json = JsonDocument.Parse(answer.Body);
        var image = json.RootElement.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "image-gen");
        if (image.GetProperty("nodes").ValueKind == JsonValueKind.Null) return [];
        return image.GetProperty("nodes").EnumerateArray().Select(entry => (
            entry.GetProperty("nodeId").GetString()!, entry.GetProperty("provider").GetString()!, entry.GetProperty("status").GetString()!,
            entry.GetProperty("available").GetBoolean(),
            entry.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("reason").GetString(),
            entry.GetProperty("accelerator").GetString())).ToList();
    }

    [Fact]
    public void Discover_advertises_placement_nodes_v1()
    {
        var feature = Assert.Single(RedComputeServiceDescriptor.NodesArea.Concepts!, concept => concept.Type == "feature");
        Assert.Equal("placement.nodes.v1", feature.Name);
        Assert.DoesNotContain("remote_execution_not_available", RedComputeServiceDescriptor.NodesArea.Summary);
    }

    // ================================================================= accelerators (design 3.2)

    [Fact]
    public async Task A_remote_gpu_has_its_own_gate_and_node_zeros_ids_are_unchanged()
    {
        var coordinator = new AcceleratorAdmissionCoordinator(nodeId: LocalId);
        Assert.Equal($"{LocalId}/cuda:0", coordinator.Qualify("cuda:0"));
        Assert.Equal($"{LocalId}/cuda:0", coordinator.Qualify("cuda:0", null));
        Assert.Equal($"{Own}/cuda:0", coordinator.Qualify("cuda:0", Own));
        Assert.Equal("node_x/cuda:1", coordinator.Qualify("node_x/cuda:1", Own));
        Assert.Null(new AcceleratorWorkload("cuda:0", "comfyui", "x").NodeId);

        await using var zero = await coordinator.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "node zero"));
        var own = new NodeScopedAcceleratorAdmission(coordinator, Own);
        // Node zero's lease does not hold the remote GPU...
        var first = await own.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "remote 1")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // ...two jobs on the same remote GPU serialise...
        var second = own.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "remote 2")).AsTask();
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        // ...and another node's cuda:0 is another gate.
        await (await new NodeScopedAcceleratorAdmission(coordinator, Others)
            .AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "other node")).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
        await first.DisposeAsync();
        await (await second.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }

    [Fact]
    public async Task A_real_comfyui_on_a_remote_host_registers_its_resident_on_that_node_not_on_node_zero()
    {
        var runtime = _nodes.RuntimesOf(Own)[0];
        var host = _hub.RemoteHost(Own).ForRuntime(runtime.Id);
        await using var comfy = new ComfyUIProvider(RemoteProviderConfig.For("ComfyUI", NodeZeroComfyConfig(), runtime), "image-gen", _ => { },
            new NodeScopedAcceleratorAdmission(_admission, Own), host);

        // A node-zero workload of another residency class releases node zero's residents only: had the remote
        // ComfyUI registered on node zero's cuda:0, its release would go over the forward and be refused.
        await using (await _admission.AcquireAsync(new AcceleratorWorkload("cuda:0", "llm", "node zero llm")).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Empty(_ownAgent.Runtime.Opens);
        Assert.Null(comfy.GetProxyTargetUrl());
    }

    // ================================================================= cost (design 3.2)

    [Fact]
    public void A_remote_job_is_costed_from_its_nodes_gpu_and_a_cpu_runtime_has_no_estimate()
    {
        var snapshot = Hardware();
        snapshot.Gpus = [new GpuInfo { Index = 0, Name = "RTX 4070", PowerWatts = 300 }, new GpuInfo { Index = 1, Name = "other", PowerWatts = 50 }];
        _nodes.ApplyReport(Own, null, snapshot);
        var job = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "ComfyUI", NodeId = Own, StartedAt = Now, CompletedAt = Now.AddHours(1) };

        Assert.Equal(0.075, GenericCapabilityEndpoints.RemoteJobCost(job, _registry, _nodes, 0.25));
        Assert.Null(GenericCapabilityEndpoints.RemoteJobCost(new JobRecord { CapabilitySlug = "image-gen", ProviderName = "ComfyUI", NodeId = Others, StartedAt = Now, CompletedAt = Now.AddHours(1) },
            _registry, _nodes, 0.25));

        Assert.Equal((int?)0, GenericCapabilityEndpoints.GpuIndex("cuda:0"));
        Assert.Equal((int?)1, GenericCapabilityEndpoints.GpuIndex("rocm:1"));
        Assert.Null(GenericCapabilityEndpoints.GpuIndex("cpu"));
        Assert.Null(GenericCapabilityEndpoints.GpuIndex("mps"));
        Assert.Null(GenericCapabilityEndpoints.GpuIndex("cuda:-1"));
        Assert.Null(GenericCapabilityEndpoints.GpuIndex(null));
    }

    [Fact]
    public async Task A_cpu_runtime_is_never_costed_from_a_gpu()
    {
        await ReportAsync(Own, accelerator: "cpu");
        var snapshot = Hardware();
        snapshot.Gpus = [new GpuInfo { Index = 0, Name = "RTX 4070", PowerWatts = 300 }];
        _nodes.ApplyReport(Own, null, snapshot);
        var job = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "ComfyUI", NodeId = Own, StartedAt = Now, CompletedAt = Now.AddHours(1) };
        Assert.Null(GenericCapabilityEndpoints.RemoteJobCost(job, _registry, _nodes, 0.25));
    }

    // ================================================================= shutdown

    [Fact]
    public async Task Stopping_redcompute_never_stops_a_nodes_runtime()
    {
        await _registry.StopAll();
        await _manager.DisposeAsync();

        Assert.Equal((1, 1), (_zero.Stops, _other.Stops));
        Assert.DoesNotContain(_ownAgent.Calls.Concat(_othersAgent.Calls), call => call.Method == "runtime.stop");
        Assert.All(_created, created => Assert.Equal(1, created.Disposed));
        Assert.All(_registry.Capabilities.Values, entry => Assert.Empty(entry.NodeProviders));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private sealed record Answer(int Status, string? Code, string Body);

    private ClaimsPrincipal User(string subject, string role = "user")
        => _jwt.ValidateToken(_jwt.GenerateAccessToken(subject, subject + "@example.test", subject, [role]))!;

    private Task<Answer> GenerateAsync(ClaimsPrincipal caller, string? nodeId, string? provider = null, string slug = "image-gen",
        bool placementNull = false, bool noPlacement = false)
    {
        var body = new Dictionary<string, object?> { ["prompt"] = "a red leaf" };
        if (provider is not null) body["provider"] = provider;
        if (!noPlacement) body["placement"] = placementNull ? new Dictionary<string, object?> { ["nodeId"] = null } : new { nodeId };
        return SendAsync("POST", $"/{slug}/generate", caller, JsonSerializer.Serialize(body));
    }

    private async Task<Answer> SendAsync(string method, string routePattern, ClaimsPrincipal? caller, string? body = null)
    {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern
                         && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (caller is not null)
        {
            context.User = caller;
            if (ExecutionIdentityClaims.TryRead(caller, out var identity, out _))
                context.Items[ExecutionIdentityClaims.HttpContextItemKey] = identity;
        }
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;
        context.Request.Headers["X-System-Reason"] = "node provider test";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        await endpoint.RequestDelegate!(context);
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

    /// <summary>Node zero's provider: a settable status, and what it was asked.</summary>
    private sealed class ZeroProvider(string name, string slug = "image-gen") : IPluginProvider
    {
        public List<JobRequest> Requests { get; } = [];
        public BackendStatus Status { get; set; } = BackendStatus.Running;
        public int Stops;

        public string Name => name;
        public string CapabilitySlug => slug;
        public string DisplayName => name;
        public string ProviderType => "ComfyUI";
        public Dictionary<string, ParameterSchema> InputParameters => new() { ["prompt"] = new ParameterSchema { Type = "string", Required = true } };
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = """{"node":"zero"}""" });
        }
    }

    /// <summary>
    /// What the factory builds for a node: it records its host, config and admission, and a job fetches
    /// <c>/blob/4096</c> from the runtime through the host's handler (the forward), holding the node's GPU gate.
    /// </summary>
    private sealed class ForwardingProvider(string type, ProviderConfig config, string slug, IExecutionHost host,
        IAcceleratorAdmissionCoordinator? admission) : IPluginProvider
    {
        public string Type { get; } = type;
        public ProviderConfig Config { get; } = config;
        public IExecutionHost Host { get; } = host;
        public IAcceleratorAdmissionCoordinator? Admission { get; } = admission;
        public List<JobRequest> Requests { get; } = [];
        public TaskCompletionSource? Hold { get; set; }
        public int Disposed;

        public string Name => "ComfyUI";
        public string CapabilitySlug => slug;
        public string DisplayName => "ComfyUI";
        public string ProviderType => Type;
        public Dictionary<string, ParameterSchema> InputParameters => new() { ["prompt"] = new ParameterSchema { Type = "string", Required = true } };
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
        public Task<bool> StartAsync(CancellationToken ct = default) => throw new InvalidOperationException("never on a remote node");
        public Task StopAsync(CancellationToken ct = default) => throw new InvalidOperationException("never on a remote node");
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => throw new InvalidOperationException("status is the node's report");
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            return ValueTask.CompletedTask;
        }

        public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            if (Hold is { } hold) await hold.Task;
            if (request.JobId is null) return new JobResult { Success = true, ResultJson = "{}" };
            await using var lease = Admission is null ? null : await Admission.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "test"), ct);
            using var client = new HttpClient(Host.Network.CreateServiceHandler(new Uri("http://127.0.0.1:8188/")));
            var bytes = await client.GetByteArrayAsync("http://127.0.0.1:8188/blob/4096", ct);
            return new JobResult { Success = true, ResultJson = JsonSerializer.Serialize(new { sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }) };
        }
    }
}
