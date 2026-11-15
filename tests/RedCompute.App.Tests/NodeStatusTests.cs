using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, C5 (design 2.2, 2.4, 3): provider status is keyed by (provider, node), /status and
/// the hardware snapshot name their node, and accelerator gates are node-qualified without changing
/// which workloads meet on one gate.
/// </summary>
public sealed class NodeStatusTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-node-status-tests", Guid.NewGuid().ToString("N"));
    private readonly NodeRegistry _nodes = new(
        new NodeIdentityResult(new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_now"), true, false, null),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        var jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        var registry = new CapabilityRegistry();
        var provider = new JobApiContractTests.FakeProvider();
        registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [provider.Name] = provider }, provider.Name);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        GlobalEndpoints.Map(new EndpointRegistry(_app), registry, jobs, null!, _nodes);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    [Fact]
    public async Task Status_names_the_local_node_and_each_providers_node()
    {
        using var response = await _client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("node_now", body.RootElement.GetProperty("localNodeId").GetString());
        var capability = Assert.Single(body.RootElement.GetProperty("capabilities").EnumerateArray());
        var provider = Assert.Single(capability.GetProperty("providers").EnumerateArray());
        Assert.Equal("node_now", provider.GetProperty("nodeId").GetString());
        // The fields that were there before are still there.
        Assert.True(provider.TryGetProperty("name", out _));
        Assert.True(provider.TryGetProperty("status", out _));
        Assert.True(provider.TryGetProperty("statusDetail", out _));
    }

    [Fact]
    public void Every_provider_runs_on_node_zero()
    {
        INodeRegistry nodes = _nodes;
        Assert.Equal(new ProviderStatusKey("comfyui", "node_now"), nodes.StatusKey("comfyui"));
        Assert.Equal("comfyui@node_now", nodes.StatusKey("comfyui").ToString());
    }

    [Fact]
    public async Task A_hardware_snapshot_names_its_node()
    {
        using var monitor = new HardwareMonitorService { NodeId = "node_now" };
        var sampled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotUpdated += snapshot => sampled.TrySetResult(snapshot.NodeId);
        monitor.Start();
        Assert.Equal("node_now", await sampled.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("node_now", monitor.GetSnapshot()!.NodeId);
    }

    [Fact]
    public void Accelerator_ids_are_qualified_by_this_node_unless_they_name_one()
    {
        var coordinator = new AcceleratorAdmissionCoordinator(nodeId: "node_now");
        Assert.Equal("node_now/cuda:0", coordinator.Qualify("cuda:0"));
        Assert.Equal("node_other/cuda:0", coordinator.Qualify("node_other/cuda:0"));
        Assert.Equal("local/cuda:0", new AcceleratorAdmissionCoordinator().Qualify("cuda:0"));
    }

    [Fact]
    public async Task An_unqualified_and_a_qualified_id_of_this_node_meet_on_one_gate()
    {
        var coordinator = new AcceleratorAdmissionCoordinator(nodeId: "node_now");
        await using var held = await coordinator.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "image"));
        var waiting = coordinator.AcquireAsync(new AcceleratorWorkload("node_now/cuda:0", "comfyui", "image")).AsTask();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        // Another node's cuda:0 is another accelerator.
        await using (await coordinator.AcquireAsync(new AcceleratorWorkload("node_other/cuda:0", "comfyui", "image"))
                         .AsTask().WaitAsync(TimeSpan.FromSeconds(5))) { }

        await held.DisposeAsync();
        await using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_admission_error_names_the_configured_id()
    {
        var coordinator = new AcceleratorAdmissionCoordinator(nodeId: "node_now");
        using var _ = coordinator.RegisterPreemptibleResident(new AcceleratorResidentRegistration(
            "cuda:0", "comfyui", "comfyui:test", _ => Task.FromResult(AcceleratorReleaseResult.Refused("busy"))));
        var error = await Assert.ThrowsAsync<AcceleratorAdmissionException>(async () =>
            await coordinator.AcquireAsync(new AcceleratorWorkload("cuda:0", "ollama", "chat")));
        Assert.Equal("cuda:0", error.AcceleratorId);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
