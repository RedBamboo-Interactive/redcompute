using RedCompute.Core.Nodes;

namespace RedCompute.PluginSdk;

/// <summary>
/// The computers RedCompute runs work on. In Phase 1 this is exactly one node, the local one
/// ("node zero"). Read-only: nodes are not added, renamed or removed through this interface.
/// </summary>
public interface INodeRegistry
{
    /// <summary>The API alias for the local node, accepted wherever a node id is.</summary>
    const string LocalAlias = "local";

    /// <summary>The persisted id of the machine this RedCompute runs on.</summary>
    string LocalNodeId { get; }

    /// <summary>
    /// What a row stored before node ids existed (a NULL node id) means, forever. Set equal to the
    /// local id when the identity is first created and never changed afterwards.
    /// </summary>
    string LegacyNodeId { get; }

    /// <summary>The local node, with <see cref="NodeDescriptor.LastSeenAt"/> set to now.</summary>
    NodeDescriptor Local { get; }

    /// <summary>Every known node: exactly [<see cref="Local"/>] in Phase 1.</summary>
    IReadOnlyList<NodeDescriptor> Nodes { get; }

    /// <summary>The node with this id (or <see cref="LocalAlias"/>), or null when none is known.</summary>
    NodeDescriptor? Find(string nodeId);

    /// <summary>A stored node id as a real id: NULL (a row older than node ids) is the legacy node.</summary>
    string Resolve(string? storedNodeId) => storedNodeId ?? LegacyNodeId;
}
