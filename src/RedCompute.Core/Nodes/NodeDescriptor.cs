namespace RedCompute.Core.Nodes;

/// <summary>Where a node runs. Phase 1 knows only the machine RedCompute itself runs on.</summary>
public enum NodeKind { Local }

/// <summary>Whether a node can take work. The local node is always online while RedCompute runs.</summary>
public enum NodeStatus { Online, Offline, Revoked }

/// <summary>
/// A computer RedCompute runs work on. Node zero is the machine this RedCompute runs on; its id is
/// generated once and persisted in <c>node.json</c>, so it stays the same across restarts.
/// </summary>
public sealed record NodeDescriptor(
    string Id,
    string Name,
    NodeKind Kind,
    NodeStatus Status,
    string Os,
    string OsDescription,
    string Architecture,
    string MachineName,
    string? ProcessorName,
    string AgentVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt);
