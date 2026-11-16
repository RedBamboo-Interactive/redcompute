using RedCompute.Core.Nodes;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.PluginSdk;

/// <summary>
/// The computers RedCompute runs work on: the local one ("node zero") and, since Phase 3, the
/// remote nodes paired with the kernel. Read-only: nodes are paired, renamed and revoked in the
/// kernel, never through this interface.
/// </summary>
public interface INodeRegistry
{
    /// <summary>The API alias for the local node, accepted wherever a node id is.</summary>
    const string LocalAlias = Hosting.Local.LocalExecutionHost.LocalNodeAlias;

    /// <summary>The persisted id of the machine this RedCompute runs on.</summary>
    string LocalNodeId { get; }

    /// <summary>
    /// What a row stored before node ids existed (a NULL node id) means, forever. Set equal to the
    /// local id when the identity is first created and never changed afterwards.
    /// </summary>
    string LegacyNodeId { get; }

    /// <summary>The local node, with <see cref="NodeDescriptor.LastSeenAt"/> set to now.</summary>
    NodeDescriptor Local { get; }

    /// <summary>Every known node: <see cref="Local"/> first, then the remote nodes by name.</summary>
    IReadOnlyList<NodeDescriptor> Nodes { get; }

    /// <summary>The node with this id (or <see cref="LocalAlias"/>), or null when none is known.</summary>
    NodeDescriptor? Find(string nodeId);

    /// <summary>
    /// The id new rows are stamped with: <see cref="LocalNodeId"/> when it is persisted in
    /// <c>node.json</c>, otherwise null. A run whose identity could not be persisted uses an id no
    /// later run shares, so it never writes it: its rows stay NULL and read as <see cref="LegacyNodeId"/>.
    /// </summary>
    string? StampNodeId { get; }

    /// <summary>A stored node id as a real id: NULL (a row older than node ids) is the legacy node.</summary>
    string Resolve(string? storedNodeId) => storedNodeId ?? LegacyNodeId;

    /// <summary>
    /// Where a configured provider runs. Phase 1: every provider runs on node zero; a cloud-only
    /// provider runs on the server, which is node zero too.
    /// </summary>
    ProviderStatusKey StatusKey(string providerName) => new(providerName, LocalNodeId);

    /// <summary>
    /// The execution host of a node: null, the legacy id, the local id and <see cref="LocalAlias"/>
    /// are the local host. A known node that cannot run work throws
    /// <see cref="Core.Nodes.NodeUnavailableException"/> with its reason (in Phase 3, every remote
    /// node: <c>remote_execution_not_available</c>, <c>offline</c>, <c>revoked</c>, <c>not_confirmed</c>
    /// or <c>needs_update</c>). An unknown id throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    IExecutionHost HostFor(string? nodeId);
}
