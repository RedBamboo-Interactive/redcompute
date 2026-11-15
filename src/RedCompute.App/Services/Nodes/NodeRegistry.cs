using System.IO;
using System.Runtime.InteropServices;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.Nodes;

/// <summary>The machine facts a node reports, read once at boot.</summary>
public sealed record NodePlatform(string Os, string OsDescription, string Architecture, string MachineName,
    string? ProcessorName)
{
    /// <summary>This machine's facts, read through the shared local host.</summary>
    public static NodePlatform Detect() => Detect(LocalExecutionHost.Shared);

    /// <summary>The facts of the machine <paramref name="host"/> executes on.</summary>
    public static NodePlatform Detect(IExecutionHost host)
    {
        var platform = host.Platform.Info;
        return new(platform.Os, platform.OsDescription, platform.Architecture, platform.MachineName,
            host.Hardware.ProcessorName());
    }
}

/// <summary>
/// Phase 1 node registry: exactly one node, the machine RedCompute runs on. Read-only; built once in
/// <see cref="RedComputeHost"/> before providers are created.
/// </summary>
public sealed class NodeRegistry : INodeRegistry
{
    private readonly NodePlatform _platform;
    private readonly string _version;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<DateTimeOffset> _clock;

    public NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock = null, IExecutionHost? localHost = null)
    {
        Identity = identity;
        _platform = platform;
        _version = version;
        _startedAt = startedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LocalHost = localHost ?? new LocalExecutionHost(identity.Identity.Id);
    }

    /// <summary>Node zero's execution host: the code that ran every launch before nodes existed.</summary>
    public IExecutionHost LocalHost { get; }

    /// <summary>Opens node.json in <paramref name="directory"/> (creating it on first boot) and detects the platform.</summary>
    public static NodeRegistry Open(string directory, string version, Action<string> log)
    {
        var identity = NodeIdentityStore.LoadOrCreate(directory, log);
        var host = new LocalExecutionHost(identity.Identity.Id);
        return new(identity, NodePlatform.Detect(host), version, DateTimeOffset.UtcNow, localHost: host);
    }

    /// <summary>The identity this run uses, and whether it is the persisted one.</summary>
    public NodeIdentityResult Identity { get; }

    public string LocalNodeId => Identity.Identity.Id;
    public string LegacyNodeId => Identity.Identity.LegacyRowsNodeId;
    public string? StampNodeId => Identity.Persisted ? LocalNodeId : null;

    public NodeDescriptor Local => new(
        LocalNodeId,
        _platform.MachineName,
        NodeKind.Local,
        NodeStatus.Online,
        _platform.Os,
        _platform.OsDescription,
        _platform.Architecture,
        _platform.MachineName,
        _platform.ProcessorName,
        _version,
        _startedAt,
        _clock());

    public IReadOnlyList<NodeDescriptor> Nodes => [Local];

    public NodeDescriptor? Find(string nodeId)
        => string.Equals(nodeId, INodeRegistry.LocalAlias, StringComparison.OrdinalIgnoreCase)
           || string.Equals(nodeId, LocalNodeId, StringComparison.Ordinal)
            ? Local
            : null;

    public IExecutionHost HostFor(string? nodeId)
        => nodeId is null
           || string.Equals(nodeId, LegacyNodeId, StringComparison.Ordinal)
           || Find(nodeId) is not null
            ? LocalHost
            : throw new KeyNotFoundException($"No node '{nodeId}' is known");
}
