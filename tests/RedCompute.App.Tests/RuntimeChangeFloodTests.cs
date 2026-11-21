using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk.Hosting;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// A node that floods <c>runtime.changed</c> (review P4-P7, item 8). NodeHub gathers the pushes per node for a short
/// window, the latest state of each runtime winning, so the registry, its observers and the cache file see a handful of
/// updates rather than one per push. The cache write is deferred out of the registry's lock and versioned: a write
/// snapshotted before a revoke never overwrites the revoke, and nothing brings back a forgotten node's file.
/// </summary>
public sealed class RuntimeChangeFloodTests : IAsyncLifetime
{
    private const int Pushes = 5000;
    private readonly string _node = RemoteId(1);
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "rc-flood-cache-" + Guid.NewGuid().ToString("N"));
    private NodeRegistry _registry = null!;
    private NodeHub _hub = null!;
    private PipedNode? _piped;

    public Task InitializeAsync()
    {
        _registry = Registry(_cache);
        _registry.ApplyKernelNodes([Kernel(_node)]);
        _hub = new NodeHub(_registry, _ => { });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_piped is not null) await _piped.DisposeAsync();
        await _hub.DisposeAsync();
        await _registry.FlushCacheAsync();
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private string CacheFile => Path.Combine(_cache, _node + ".json");

    [Fact]
    public async Task A_flood_of_runtime_changes_costs_a_few_updates_and_the_latest_state_wins()
    {
        _piped = new PipedNode(_hub, _node, nodeHandler: new RuntimeNodeHandler(), features: RuntimeNodeHandler.Phase4Features);
        await WaitAsync(() => _registry.RuntimesOf(_node).Count == 1, "the runtime list");
        var updates = 0;
        var raised = new List<HostRuntime>();
        _registry.Updated += id => { if (id == _node) Interlocked.Increment(ref updates); };
        _hub.RemoteHost(_node).Runtimes.Changed += runtime => { lock (raised) raised.Add(runtime); };
        await _registry.FlushCacheAsync();
        var writesBefore = _registry.CacheWrites;

        var session = _piped.Client.Session!;
        var started = DateTime.UtcNow;
        for (var i = 0; i < Pushes - 1; i++)
            await session.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi(status: i % 2 == 0 ? "starting" : "stopped", port: 9000 + i % 7));
        await session.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi(status: "running", port: 8199));
        var pushedFor = DateTime.UtcNow - started;

        await WaitAsync(() => _registry.RuntimesOf(_node).Single() is { Status: BackendStatus.Running, Ports: [8199] }, "the last push applied");
        await _registry.FlushCacheAsync();
        await Task.Delay(_hub.RuntimeChangeWindow * 3); // nothing more is coming

        // One update per window at most, whatever the number of pushes.
        var windows = (int)Math.Ceiling(pushedFor / _hub.RuntimeChangeWindow) + 3;
        Assert.InRange(updates, 1, windows);
        Assert.InRange(_registry.CacheWrites - writesBefore, 1, windows);
        lock (raised)
        {
            Assert.InRange(raised.Count, 1, windows);
            Assert.Equal((BackendStatus.Running, 8199), (raised[^1].Status, raised[^1].Ports.Single()));
        }
        Assert.True(updates < Pushes / 20, $"{updates} updates for {Pushes} pushes");

        // The cache holds the last state.
        var cached = JsonNode.Parse(File.ReadAllText(CacheFile))!["runtimes"]!.AsArray().Single()!;
        Assert.Equal(("running", 8199), ((string?)cached["status"], (int)cached["ports"]![0]!));
    }

    [Fact]
    public async Task A_deferred_cache_write_never_overwrites_a_later_revoke()
    {
        for (var round = 0; round < 50; round++)
        {
            var node = RemoteId(100 + round);
            _registry.ApplyKernelNodes([Kernel(_node), Kernel(node)]);
            // The change's cache write is snapshotted now and written by the drainer, most likely after the revoke below.
            Assert.True(_registry.ApplyRuntimeChanges(node, [RemoteRuntimeReport.Parse(System.Text.Json.JsonSerializer.SerializeToElement(
                RuntimeNodeHandler.ComfyUi(status: "running")))!]));
            _registry.MarkRevoked(node, "revoked_by_owner");
            await _registry.FlushCacheAsync();
            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_cache, node + ".json")))!;
            Assert.True((bool)file["revoked"]!, $"round {round}: the revoke was overwritten by an older snapshot");
        }
    }

    [Fact]
    public async Task A_forgotten_nodes_cache_is_never_written_back()
    {
        for (var round = 0; round < 50; round++)
        {
            var node = RemoteId(200 + round);
            _registry.ApplyKernelNodes([Kernel(_node), Kernel(node)]);
            Assert.True(_registry.ApplyRuntimeChanges(node, [RemoteRuntimeReport.Parse(System.Text.Json.JsonSerializer.SerializeToElement(
                RuntimeNodeHandler.ComfyUi(status: "running")))!]));
            _registry.ApplyKernelNodes([Kernel(_node)]); // forgotten: its file is deleted
            await _registry.FlushCacheAsync();
            Assert.False(File.Exists(Path.Combine(_cache, node + ".json")), $"round {round}: a deferred write brought the file back");
        }
    }
}
