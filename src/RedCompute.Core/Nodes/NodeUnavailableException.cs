namespace RedCompute.Core.Nodes;

/// <summary>Why a known node cannot run work right now (<see cref="NodeUnavailableException.Reason"/>).</summary>
public static class NodeUnavailableReasons
{
    /// <summary>The node is linked, confirmed and up to date, but this RedCompute has no NodeHub to reach it.</summary>
    public const string RemoteExecutionNotAvailable = "remote_execution_not_available";

    /// <summary>The node's link is down. Retryable.</summary>
    public const string Offline = "offline";

    /// <summary>The node was revoked; it never runs work again.</summary>
    public const string Revoked = "revoked";

    /// <summary>The node's owner has not confirmed "This is my node".</summary>
    public const string NotConfirmed = "not_confirmed";

    /// <summary>The node's agent is older than the server accepts, or does not speak the runtime methods and forward (Phase 4).</summary>
    public const string NeedsUpdate = "needs_update";
}

/// <summary>
/// <c>INodeRegistry.HostFor</c> on a node the registry knows but cannot run work on (design 7.1).
/// Placement catches it and answers 409 <c>node_offline</c> (retryable) or 409 <c>node_not_eligible</c>
/// with <see cref="Reason"/>. An id the registry does not know is still a <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed class NodeUnavailableException(string nodeId, string reason)
    : InvalidOperationException($"Node '{nodeId}' cannot run work: {reason}")
{
    public string NodeId { get; } = nodeId;

    /// <summary>One of <see cref="NodeUnavailableReasons"/>.</summary>
    public string Reason { get; } = reason;

    /// <summary>Whether waiting may help (the node is only offline).</summary>
    public bool Retryable => Reason == NodeUnavailableReasons.Offline;
}
