using RedCompute.Core.Hardware;

namespace RedCompute.Core.Nodes;

/// <summary>
/// Where a node runs: <see cref="Local"/> is the machine RedCompute itself runs on (node zero);
/// <see cref="Remote"/> is a paired machine running the node agent, reached over its link.
/// </summary>
public enum NodeKind { Local, Remote }

/// <summary>
/// Whether a node can take work. The local node is always online while RedCompute runs. A remote node
/// is online while its link is up, <see cref="NeedsUpdate"/> when its agent is too old to link,
/// <see cref="PendingConfirmation"/> while it is online but its owner has not confirmed it, and
/// <see cref="Revoked"/> for good once revoked.
/// </summary>
public enum NodeStatus { Online, Offline, Revoked, NeedsUpdate, PendingConfirmation }

/// <summary>
/// A computer RedCompute runs work on. Node zero is the machine this RedCompute runs on; its id is
/// generated once and persisted in <c>node.json</c>, so it stays the same across restarts. A remote
/// node's identity comes from the kernel (its <c>node</c> entity), its liveness from the link.
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
    DateTimeOffset LastSeenAt)
{
    /// <summary>The person the node belongs to (the kernel's <c>ownerId</c>); null for node zero.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>"This is my node" was confirmed. Node zero is always confirmed; an unconfirmed node is never eligible.</summary>
    public bool Confirmed { get; init; } = true;

    public DateTimeOffset? PairedAt { get; init; }

    /// <summary>Why the node is in its status (<c>heartbeat_timeout</c>, <c>revoked_by_owner</c>, …), when it says.</summary>
    public string? StatusReason { get; init; }

    /// <summary>The live link of a remote node, while it is up; null for node zero and offline nodes.</summary>
    public NodeLinkState? Link { get; init; }

    /// <summary>What a remote node last reported (CLIs, hardware), kept across restarts.</summary>
    public NodeReport? Report { get; init; }

    /// <summary>
    /// What kind of machine its firmware says it is (desktop, all-in-one, laptop, tablet, mini, server), when
    /// it says: the hint the kernel draws the node's default icon from.
    /// </summary>
    public string? Chassis { get; init; }

    /// <summary>The node's icon (a curated Phosphor name) as the kernel's node entity has it; null from an older kernel.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// The node's link must be end-to-end encrypted (Phase 4 P0.5): it pinned RedCompute's link key at
    /// pairing (the kernel's <c>linkE2e: required</c>) or has linked encrypted before. Plaintext from it is
    /// refused. Always false for node zero, which has no link.
    /// </summary>
    public bool E2eRequired { get; init; }

    /// <summary>
    /// RedCompute's own pin of a remote node's identity key: <c>pinned</c>, <c>conflict</c> (the kernel lists another
    /// key; the pin stays), <c>unreadable</c> (its trust record does not open; every link is refused), or null.
    /// </summary>
    public string? IdentityPin { get; init; }

    /// <summary>The node's colour (a palette token) as the kernel's node entity has it; null from an older kernel.</summary>
    public string? Color { get; init; }
}

/// <summary>A remote node's live link (design 7.1).</summary>
public sealed record NodeLinkState(
    string LinkSessionId,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastHeartbeatAt,
    double? RttMs,
    double? ClockOffsetMs,
    int Protocol,
    IReadOnlyList<string> Features,
    string? AgentVersion,
    bool UpdateAvailable,
    string? CredentialStore,
    bool Encrypted = false);

/// <summary>One CLI as the node's fixed probe found it: never its output, auth files or tokens.</summary>
public sealed record NodeCliReport(string Name, bool Installed, string? Version, bool SignedIn, string? AuthMethod, string? Path);

/// <summary>What a remote node last reported: its CLIs and its last hardware sample.</summary>
public sealed record NodeReport(IReadOnlyList<NodeCliReport> Clis, HardwareSnapshot? Hardware, DateTimeOffset? ReportedAt);
