using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

/// <summary>A provider on a remote node (design 3.1): node zero's provider name, and the node.</summary>
public readonly record struct NodeProviderKey(string Provider, string NodeId)
{
    public override string ToString() => $"{Provider}@{NodeId}";
}

/// <summary>
/// Creates a capability's provider for a remote node's runtime, exactly as node zero's are created
/// (<see cref="ProviderDiscovery.Create"/>), with the node's execution host and its accelerator admission instead of
/// node zero's.
/// </summary>
public delegate IPluginProvider? RemoteProviderFactory(string providerType, ProviderConfig config, string capabilitySlug,
    IExecutionHost host, IAcceleratorAdmissionCoordinator? admission);

/// <summary>
/// The per-node providers (Phase 4 P7, design 3.1). When a remote node reports a runtime that serves a capability
/// (the server's kind table decides which), RedCompute creates that capability's provider for it under node zero's
/// provider name, over a <see cref="RemoteExecutionHost"/> bound to the runtime, and keeps it in
/// <see cref="CapabilityEntry.NodeProviders"/>. When the node stops reporting the runtime, is forgotten or is
/// revoked, the instance is retired (disposed once no job runs on it). Status is the node's last report
/// (<c>runtime.changed</c>), never a poll over the link. Node zero's providers are never touched: with no remote
/// runtime this holds nothing.
/// </summary>
public sealed class NodeProviderManager : IAsyncDisposable
{
    private readonly CapabilityRegistry _capabilities;
    private readonly NodeRegistry _nodes;
    private readonly Func<string, RemoteExecutionHost> _hostOf;
    private readonly RemoteProviderFactory _factory;
    private readonly IAcceleratorAdmissionCoordinator? _admission;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private bool _disposed;

    public NodeProviderManager(CapabilityRegistry capabilities, NodeRegistry nodes, Func<string, RemoteExecutionHost> hostOf,
        RemoteProviderFactory factory, IAcceleratorAdmissionCoordinator? admission, Action<string> log)
    {
        _capabilities = capabilities;
        _nodes = nodes;
        _hostOf = hostOf;
        _factory = factory;
        _admission = admission;
        _log = log;
        _nodes.Updated += Reconcile;
        _nodes.StatusChanged += OnStatusChanged;
        foreach (var node in _nodes.Nodes.Where(node => node.Kind == NodeKind.Remote).ToList())
            Reconcile(node.Id);
    }

    private void OnStatusChanged(NodeStatusChange change) => Reconcile(change.Node.Id);

    /// <summary>Brings one node's instances in line with what it reported. Idempotent; safe from any thread.</summary>
    public void Reconcile(string nodeId)
    {
        var retired = new List<RemoteNodeProvider>();
        lock (_gate)
        {
            if (_disposed) return;
            var node = _nodes.Find(nodeId);
            var runtimes = node is { Kind: NodeKind.Remote, Status: not NodeStatus.Revoked } ? _nodes.RuntimesOf(nodeId) : [];
            foreach (var (slug, entry) in _capabilities.Capabilities)
            {
                if (entry.IsExternal) continue;
                var wanted = Wanted(slug, entry, runtimes);
                foreach (var (key, current) in entry.NodeProviders.Where(pair => pair.Key.NodeId == nodeId).ToList())
                {
                    if (current is not RemoteNodeProvider remote) continue;
                    if (wanted.TryGetValue(key.Provider, out var runtime) && remote.Serves(runtime)) continue;
                    if (entry.NodeProviders.TryRemove(new KeyValuePair<NodeProviderKey, IBackendProvider>(key, current)))
                        retired.Add(remote);
                }
                foreach (var (providerName, runtime) in wanted)
                {
                    var key = new NodeProviderKey(providerName, nodeId);
                    if (entry.NodeProviders.ContainsKey(key)) continue;
                    if (Create(slug, entry, providerName, nodeId, runtime) is { } created)
                        entry.NodeProviders[key] = created;
                }
            }
        }
        foreach (var provider in retired)
        {
            _log($"[Nodes] Retired {provider.Name} for {provider.CapabilitySlug} on {nodeId} (runtime {provider.RuntimeId})");
            provider.Retire();
        }
    }

    /// <summary>
    /// For each provider name, the runtime that serves the capability under it: a running one first, then an enabled
    /// one, then one the node spawns, then by id, so the choice is stable.
    /// </summary>
    private static Dictionary<string, HostRuntime> Wanted(string slug, CapabilityEntry entry, IReadOnlyList<HostRuntime> runtimes)
        => runtimes
            .Where(runtime => runtime.Capabilities.Contains(slug, StringComparer.Ordinal)
                && RemoteRuntimeKinds.ProviderType(runtime.Kind) is not null)
            .GroupBy(runtime => RemoteRuntimeKinds.ProviderName(entry, runtime.Kind), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(runtime => runtime.Status == BackendStatus.Running)
                .ThenByDescending(runtime => runtime.Enabled)
                .ThenByDescending(runtime => runtime.Managed == HostRuntimeManagement.Spawn)
                .ThenBy(runtime => runtime.Id, StringComparer.Ordinal)
                .First(), StringComparer.Ordinal);

    private RemoteNodeProvider? Create(string slug, CapabilityEntry entry, string providerName, string nodeId, HostRuntime runtime)
    {
        var type = RemoteRuntimeKinds.ProviderType(runtime.Kind)!;
        entry.Config.Providers.TryGetValue(providerName, out var nodeZeroConfig);
        var config = RemoteProviderConfig.For(type, nodeZeroConfig, runtime);
        var host = _hostOf(nodeId).ForRuntime(runtime.Id);
        var admission = _admission is null ? null : new NodeScopedAcceleratorAdmission(_admission, nodeId);
        IPluginProvider? inner;
        try
        {
            inner = _factory(type, config, slug, host, admission);
        }
        catch (Exception error)
        {
            _log($"[Nodes] Could not create {type} for {slug} on {nodeId}: {error.Message}");
            return null;
        }
        if (inner is null)
        {
            _log($"[Nodes] No {type} provider for {slug} on {nodeId}");
            return null;
        }
        _log($"[Nodes] {providerName} serves {slug} on {nodeId} (runtime {runtime.Id}, {runtime.Accelerator ?? "no accelerator reported"})");
        return new RemoteNodeProvider(inner, nodeId, runtime, _nodes, host);
    }

    public async ValueTask DisposeAsync()
    {
        List<RemoteNodeProvider> all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _nodes.Updated -= Reconcile;
            _nodes.StatusChanged -= OnStatusChanged;
            all = [];
            foreach (var entry in _capabilities.Capabilities.Values)
                foreach (var (key, provider) in entry.NodeProviders.ToList())
                    if (provider is RemoteNodeProvider remote && entry.NodeProviders.TryRemove(key, out _))
                        all.Add(remote);
        }
        // Never runtime.stop: the runtime is the node owner's, and RedCompute stopping does not stop it.
        foreach (var provider in all) await provider.RetireAsync();
    }
}

/// <summary>
/// A capability's provider on a remote node: the plugin's own provider over the node's execution host for
/// <see cref="ExecuteAsync"/> and its schema, and the node's runtime for everything about its state. Status is the
/// node's last report and its eligibility (an offline node's provider reads Stopped, not an error, design 3.1);
/// start and stop are <c>runtime.start</c>/<c>runtime.stop</c>.
/// </summary>
public sealed class RemoteNodeProvider : IPluginProvider
{
    private readonly IPluginProvider _inner;
    private readonly NodeRegistry _nodes;
    private readonly RemoteExecutionHost _host;
    private readonly object _gate = new();
    private int _active;
    private bool _retired;
    private Task? _disposed;

    internal RemoteNodeProvider(IPluginProvider inner, string nodeId, HostRuntime runtime, NodeRegistry nodes, RemoteExecutionHost host)
    {
        _inner = inner;
        _nodes = nodes;
        _host = host;
        NodeId = nodeId;
        RuntimeId = runtime.Id;
        Kind = runtime.Kind;
        BoundAccelerator = runtime.Accelerator;
        BoundPort = runtime.Ports.Count > 0 ? runtime.Ports[0] : null;
    }

    public string NodeId { get; }
    public string RuntimeId { get; }
    public string Kind { get; }
    private string? BoundAccelerator { get; }
    private int? BoundPort { get; }

    /// <summary>The plugin's provider this wraps (tests and diagnostics).</summary>
    internal IPluginProvider Inner => _inner;

    /// <summary>True while this instance still matches what the node reports for its runtime.</summary>
    internal bool Serves(HostRuntime runtime)
        => runtime.Id == RuntimeId && runtime.Kind == Kind && runtime.Accelerator == BoundAccelerator
           && (runtime.Ports.Count > 0 ? runtime.Ports[0] : (int?)null) == BoundPort;

    /// <summary>The runtime as the node last reported it, or null once it no longer does.</summary>
    public HostRuntime? Runtime => _nodes.RuntimesOf(NodeId).FirstOrDefault(runtime => runtime.Id == RuntimeId);

    /// <summary>The runtime's accelerator as reported (<c>cuda:0</c>, <c>cpu</c>…), or null.</summary>
    public string? Accelerator => Runtime?.Accelerator ?? BoundAccelerator;

    /// <summary>
    /// Status and availability (design 3.1): the runtime's reported status while the node is eligible; Stopped with
    /// <c>node_offline</c>, <c>node_not_eligible</c>, <c>runtime_disabled</c> or <c>runtime_unknown</c> otherwise.
    /// </summary>
    public (BackendStatus Status, string? Reason) Availability()
    {
        if (_nodes.Find(NodeId) is not { } node) return (BackendStatus.Stopped, RemoteRuntimeKinds.NodeOffline);
        if (Runtime is not { } runtime) return (BackendStatus.Stopped, RemoteRuntimeKinds.RuntimeUnknown);
        return RemoteRuntimeKinds.Availability(_nodes.Placement(node), runtime);
    }

    public string Name => _inner.Name;
    public string CapabilitySlug => _inner.CapabilitySlug;
    public string DisplayName => _inner.DisplayName;
    public string ProviderType => _inner.ProviderType;
    public Dictionary<string, ParameterSchema> InputParameters => _inner.InputParameters;
    public ReturnSchema OutputSchema => _inner.OutputSchema;
    public object? RequestSchema => _inner.RequestSchema;
    public object? ResponseSchema => _inner.ResponseSchema;
    public string? ContractVersion => _inner.ContractVersion;
    public bool IsProxy => _inner.IsProxy;
    public string ProxyGeneratePath => _inner.ProxyGeneratePath;
    public bool SupportsProgress => _inner.SupportsProgress;
    public bool SupportsRerun => _inner.SupportsRerun;
    public TimeSpan HealthCheckInterval => _inner.HealthCheckInterval;
    public int? ProcessId => null;
    public string? StatusDetail => Availability() is { Reason: { } reason } ? reason : Runtime?.StatusDetail;

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];
    public Dictionary<string, string> ValidateParameters(Dictionary<string, object?> parameters) => _inner.ValidateParameters(parameters);
    public Dictionary<string, object?> TransformParameters(Dictionary<string, object?> parameters) => _inner.TransformParameters(parameters);
    public Task<string?> PrepareAsync(Dictionary<string, object?> body, string proxyUrl, CancellationToken ct = default)
        => _inner.PrepareAsync(body, proxyUrl, ct);
    public void SetProgressCallback(Action<double>? callback) => _inner.SetProgressCallback(callback);

    /// <summary>Never a resolved address: a remote provider is reached only through its host's forward.</summary>
    public string? GetProxyTargetUrl() => null;

    /// <summary>Running only while the provider is available: an unavailable one (any reason) is Stopped, so no job starts on it.</summary>
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(Availability() is { Reason: null } available ? available.Status : BackendStatus.Stopped);

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        try
        {
            var runtime = await _host.Runtimes.StartAsync(RuntimeId, ct);
            return runtime.Status is BackendStatus.Running or BackendStatus.Starting;
        }
        catch (Exception error) when (error is HostRuntimeException or NodeUnavailableException or TimeoutException
                                          or RedCompute.NodeLink.LinkRpcException)
        {
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            await _host.Runtimes.StopAsync(RuntimeId, ct: ct);
        }
        catch (Exception error) when (error is HostRuntimeException or NodeUnavailableException or TimeoutException
                                          or RedCompute.NodeLink.LinkRpcException)
        {
        }
    }

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_retired) return Gone();
            _active++;
        }
        try
        {
            return await _inner.ExecuteAsync(request, ct);
        }
        finally
        {
            bool dispose;
            lock (_gate) dispose = --_active == 0 && _retired;
            if (dispose) _ = RetireAsync();
        }
    }

    private JobResult Gone() => new()
    {
        Success = false,
        ErrorCode = "provider_unavailable_on_node",
        ErrorStatusCode = 409,
        ErrorMessage = $"Runtime '{RuntimeId}' on node '{NodeId}' is no longer reported",
    };

    /// <summary>No new job starts; the plugin's provider is disposed once the running ones end.</summary>
    internal void Retire()
    {
        bool dispose;
        lock (_gate)
        {
            _retired = true;
            dispose = _active == 0;
        }
        if (dispose) _ = RetireAsync();
    }

    internal Task RetireAsync()
    {
        lock (_gate)
        {
            _retired = true;
            return _disposed ??= DisposeInnerAsync();
        }
    }

    private async Task DisposeInnerAsync()
    {
        try { await _inner.DisposeAsync(); }
        catch { }
    }

    /// <summary>Disposing this wrapper retires it; it never stops the node's runtime.</summary>
    public ValueTask DisposeAsync() => new(RetireAsync());
}

/// <summary>
/// The config a remote node's provider is built from (design 3.1): never node zero's machine settings. It is an
/// allowlist, stricter than the design's strip list: the type, the display name, the timeouts and the tuning keys
/// that start with <c>_</c> (<c>_pollTimeoutSeconds</c>), plus what the node's report says (the runtime's first port
/// as <c>BackendPort</c>, its accelerator as <c>AcceleratorId</c>). Paths, <c>Host</c>, <c>WslDistro</c>, the
/// <c>*Directory</c> keys, launch commands, endpoints, API keys and fixed job costs are never copied.
/// </summary>
public static class RemoteProviderConfig
{
    public static ProviderConfig For(string type, ProviderConfig? nodeZero, HostRuntime runtime)
    {
        var extra = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (nodeZero?.Extra is { } source)
            foreach (var (key, value) in source)
                if (key.StartsWith('_') && key.Length > 1) extra[key] = value;
        // A runtime with no reported accelerator gets a gate of its own, never node zero's cuda:0.
        extra["AcceleratorId"] = runtime.Accelerator ?? $"runtime-{runtime.Id}";
        return new ProviderConfig
        {
            Type = type,
            DisplayName = nodeZero?.DisplayName,
            StartupTimeoutSeconds = nodeZero?.StartupTimeoutSeconds ?? 180,
            TimeoutSeconds = nodeZero?.TimeoutSeconds,
            BackendPort = runtime.Ports.Count > 0 ? runtime.Ports[0] : null,
            Extra = extra,
        };
    }
}

/// <summary>
/// A remote node's view of the accelerator admission (design 3.2): every workload and resident it registers is on that
/// node (<see cref="AcceleratorWorkload.NodeId"/> from the host's node), so a remote GPU has its own gate and resident
/// set and never meets node zero's <c>cuda:0</c>.
/// </summary>
internal sealed class NodeScopedAcceleratorAdmission(IAcceleratorAdmissionCoordinator inner, string nodeId) : IAcceleratorAdmissionCoordinator
{
    public string NodeId { get; } = nodeId;

    public IDisposable RegisterPreemptibleResident(AcceleratorResidentRegistration registration)
        => inner.RegisterPreemptibleResident(registration with { NodeId = NodeId });

    public ValueTask<IAsyncDisposable> AcquireAsync(AcceleratorWorkload workload, CancellationToken cancellationToken = default)
        => inner.AcquireAsync(workload with { NodeId = NodeId }, cancellationToken);
}
