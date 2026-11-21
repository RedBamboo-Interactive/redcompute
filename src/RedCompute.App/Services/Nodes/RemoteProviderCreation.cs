using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// How a remote node's provider is built (Phase 4 P7, design 3.1; review P4-P7, item 11): exactly as node zero's, by
/// type through <see cref="ProviderDiscovery"/>, from node zero's constructor services with that node's execution host
/// and accelerator admission in their place, so nothing in it can reach this machine's processes, files or GPU gates.
/// </summary>
internal static class RemoteProviderCreation
{
    /// <summary>
    /// The provider for <paramref name="host"/>'s runtime. Throws (never a null, never a provider over node zero) when
    /// <paramref name="host"/> is not a node's <see cref="RemoteExecutionHost"/>, when the admission is not scoped to
    /// that node, or when the provider type has no constructor that receives the host
    /// (<see cref="ProviderDiscovery.CreateOnHost"/>).
    /// </summary>
    public static IPluginProvider Create(ProviderDiscovery discovery, IReadOnlyList<object?> nodeZeroServices,
        IHostNetwork serverNetwork, string providerType, ProviderConfig config, string capabilitySlug,
        IExecutionHost host, IAcceleratorAdmissionCoordinator? admission, Action<string> log)
    {
        if (host is not RemoteExecutionHost remote)
            throw new InvalidOperationException(
                $"A remote node's provider runs only over that node's execution host, not {host?.GetType().Name ?? "none"}");
        if (admission is not null && (admission is not NodeScopedAcceleratorAdmission scoped || scoped.NodeId != remote.NodeId))
            throw new InvalidOperationException($"A remote node's provider takes only the admission scoped to {remote.NodeId}");

        var services = nodeZeroServices
            .Where(service => service is not IExecutionHost and not IAcceleratorAdmissionCoordinator and not ServerNetwork)
            .Prepend(admission)
            .Prepend(host)
            // The server's own network, for what a job names on the server (input assets): fetched here, never by the node.
            .Append(new ServerNetwork(serverNetwork))
            .ToArray();
        return discovery.CreateOnHost(providerType, config, capabilitySlug, log, services, host);
    }
}
