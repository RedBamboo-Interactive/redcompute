using System.Reflection;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// Review P4-P7, item 11: a remote node's provider is built only over that node's <see cref="RemoteExecutionHost"/>,
/// never over <see cref="LocalExecutionHost.Shared"/>. That holds end to end for the real ComfyUI, built as
/// RedCompute builds it (<see cref="RemoteProviderCreation"/>, ProviderDiscovery) and run through the real forward to a
/// fake ComfyUI behind the node's RuntimeForwardTarget. A remote GPU gate is the node's own, whatever id the provider
/// passes.
/// </summary>
public sealed class RemoteProviderCreationTests : IAsyncLifetime
{
    private ComfyNodeKit _kit = null!;
    private RemoteExecutionHost _host = null!;
    private readonly AcceleratorAdmissionCoordinator _admission = new(nodeId: LocalId);

    public async Task InitializeAsync()
    {
        _kit = await ComfyNodeKit.StartAsync();
        _host = _kit.Host; // a new ForRuntime host on every read: one for the test
    }

    public async Task DisposeAsync() => await _kit.DisposeAsync();

    /// <summary>The discovery RedCompute scans at startup, with the ComfyUI plugin loaded.</summary>
    private static ProviderDiscovery Discovery()
    {
        _ = typeof(ComfyUIProvider).Assembly;
        var discovery = new ProviderDiscovery(_ => { });
        discovery.ScanAssemblies();
        return discovery;
    }

    /// <summary>Node zero's constructor services, as RedComputeHost.InitializeCapabilities passes them.</summary>
    private object?[] NodeZeroServices() =>
    [
        (IAcceleratorAdmissionCoordinator)_admission,
        (Action<string, Guid?>)((_, _) => { }),
        (IExecutionHost)LocalExecutionHost.Shared,
        (INodeRegistry)_kit.Registry,
    ];

    private IPluginProvider Create(ProviderDiscovery discovery, IExecutionHost host, IAcceleratorAdmissionCoordinator? admission,
        string type = "ComfyUI")
        => RemoteProviderCreation.Create(discovery, NodeZeroServices(), LocalExecutionHost.Shared.Network, type,
            RemoteProviderConfig.For("ComfyUI", null, _kit.Runtime), "image-gen", host, admission, _ => { });

    private static T Field<T>(object provider, string name)
        => (T)provider.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;

    [Fact]
    public async Task A_remote_comfyui_built_as_redcompute_builds_it_runs_over_the_nodes_host_and_never_the_local_one()
    {
        var admission = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);
        await using var provider = Assert.IsType<ComfyUIProvider>(Create(Discovery(), _host, admission));

        var host = Field<IExecutionHost>(provider, "_executionHost");
        Assert.Same(_host, host);
        Assert.NotSame(LocalExecutionHost.Shared, host);

        var result = await provider.ExecuteAsync(ComfyNodeKit.Request()).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result!.Success, result.ErrorMessage);

        // Every HTTP request the node's ComfyUI saw came through the node's forward: none went to it directly. Only
        // /system_stats is left out: the node agent's own health probe (RuntimeManager, on the node) calls it too.
        var seen = _kit.Comfy.Requests.Where(request => request.Method != "WS" && !request.Target.StartsWith("/system_stats", StringComparison.Ordinal))
            .GroupBy(request => request.Target.Split('?')[0]).ToDictionary(group => group.Key, group => group.Count());
        var forwarded = _kit.Node.Forwarded.Where(exchange => exchange.Method is not null)
            .GroupBy(exchange => exchange.Path.Split('?')[0]).ToDictionary(group => group.Key, group => group.Count());
        Assert.Contains("/prompt", seen.Keys);
        Assert.Contains("/view", seen.Keys);
        foreach (var (path, count) in seen)
            Assert.True(forwarded.GetValueOrDefault(path) >= count, $"{path}: {count} seen by ComfyUI, {forwarded.GetValueOrDefault(path)} forwarded");
    }

    [Fact]
    public void A_host_that_is_not_the_nodes_or_an_admission_that_is_not_its_own_is_refused()
    {
        var discovery = Discovery();
        var own = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);

        Assert.Throws<InvalidOperationException>(() => Create(discovery, LocalExecutionHost.Shared, own));
        Assert.Throws<InvalidOperationException>(() => Create(discovery, _host, _admission));
        Assert.Throws<InvalidOperationException>(() => Create(discovery, _host, new NodeScopedAcceleratorAdmission(_admission, RemoteId(7))));
        Assert.Throws<InvalidOperationException>(() => Create(discovery, _host, own, type: "NoSuchType"));
    }

    [Fact]
    public void A_provider_that_would_fall_back_to_this_machines_host_is_never_built()
    {
        var discovery = Discovery();
        discovery.Register("NoHost", typeof(NoHostProvider));
        discovery.Register("OptionalHost", typeof(OptionalHostProvider));
        var own = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);

        // No execution-host parameter at all: it would run on this machine.
        var noHost = Assert.Throws<InvalidOperationException>(() => Create(discovery, _host, own, type: "NoHost"));
        Assert.Contains("cannot run on a remote node", noHost.Message);

        // `IExecutionHost? host = null` with no host among the services: the default null (LocalExecutionHost.Shared
        // inside the provider) is never taken.
        Assert.Throws<InvalidOperationException>(() => discovery.CreateOnHost("OptionalHost", new ProviderConfig { Type = "OptionalHost" },
            "image-gen", _ => { }, [], _host));
        // Nor is node zero's host when the services carry it instead of the node's.
        Assert.Throws<InvalidOperationException>(() => discovery.CreateOnHost("OptionalHost", new ProviderConfig { Type = "OptionalHost" },
            "image-gen", _ => { }, [LocalExecutionHost.Shared], _host));
        // With the node's host it is built over it, and only it.
        var built = Assert.IsType<OptionalHostProvider>(Create(discovery, _host, own, type: "OptionalHost"));
        Assert.Same(_host, built.Host);
        // Node zero's path is unchanged: Create still falls back as it always did.
        Assert.IsType<NoHostProvider>(discovery.Create("NoHost", new ProviderConfig { Type = "NoHost" }, "image-gen", _ => { }));
    }

    // ================================================================= NodeScopedAcceleratorAdmission

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("node_a/cuda:0")]
    [InlineData(" node_a")]
    public void The_scoped_admission_takes_only_a_remote_node_id(string nodeId)
    {
        Assert.Throws<ArgumentException>(() => new NodeScopedAcceleratorAdmission(_admission, nodeId));
    }

    [Fact]
    public void The_scoped_admission_cannot_be_scoped_twice()
    {
        var own = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);
        Assert.Throws<ArgumentException>(() => new NodeScopedAcceleratorAdmission(own, RemoteId(7)));
    }

    [Fact]
    public async Task An_accelerator_id_that_names_node_zero_still_lands_on_the_nodes_own_gate()
    {
        var own = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);
        Assert.Equal($"{ComfyNodeKit.NodeId}/cuda:0", own.Scoped("cuda:0"));
        Assert.Equal($"{ComfyNodeKit.NodeId}/cuda:0", own.Scoped($"{LocalId}/cuda:0"));
        Assert.Equal($"{ComfyNodeKit.NodeId}/cuda:1", own.Scoped("a/b/cuda:1"));
        Assert.Throws<ArgumentException>(() => own.Scoped($"{LocalId}/"));

        // Node zero holds its cuda:0. The node's provider naming node zero's gate is not held by it...
        await using var zero = await _admission.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "node zero"));
        var first = await own.AcquireAsync(new AcceleratorWorkload($"{LocalId}/cuda:0", "comfyui", "remote"))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // ...because it is on the node's cuda:0, which it now holds.
        var second = own.AcquireAsync(new AcceleratorWorkload("cuda:0", "comfyui", "remote 2")).AsTask();
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await (await second.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }

    [Fact]
    public async Task A_resident_registered_under_node_zeros_id_is_the_nodes_and_node_zero_never_releases_it()
    {
        var own = new NodeScopedAcceleratorAdmission(_admission, ComfyNodeKit.NodeId);
        var released = 0;
        using var resident = own.RegisterPreemptibleResident(new AcceleratorResidentRegistration($"{LocalId}/cuda:0", "comfyui", "remote comfy",
            _ =>
            {
                Interlocked.Increment(ref released);
                return Task.FromResult(AcceleratorReleaseResult.Released());
            }));

        // A node zero workload of another residency class releases node zero's residents on cuda:0: not this one.
        await using (await _admission.AcquireAsync(new AcceleratorWorkload("cuda:0", "llm", "node zero llm")).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Equal(0, released);
        // The same on the node's cuda:0 does release it.
        await using (await own.AcquireAsync(new AcceleratorWorkload("cuda:0", "llm", "node llm")).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Equal(1, released);
    }

    /// <summary>A provider with no execution-host parameter (it runs wherever RedCompute runs).</summary>
    private sealed class NoHostProvider(ProviderConfig config, string capabilitySlug, Action<string> log) : TestProvider(config, capabilitySlug, log);

    /// <summary>A provider whose host is optional, falling back to this machine's, as ComfyUI's is.</summary>
    private sealed class OptionalHostProvider(ProviderConfig config, string capabilitySlug, Action<string> log, IExecutionHost? host = null)
        : TestProvider(config, capabilitySlug, log)
    {
        public IExecutionHost Host { get; } = host ?? LocalExecutionHost.Shared;
    }

    private abstract class TestProvider(ProviderConfig config, string capabilitySlug, Action<string> log) : IPluginProvider
    {
        public ProviderConfig Config => config;
        public Action<string> Log => log;
        public string Name => GetType().Name;
        public string CapabilitySlug => capabilitySlug;
        public string DisplayName => Name;
        public string ProviderType => config.Type;
        public Dictionary<string, ParameterSchema> InputParameters => [];
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
