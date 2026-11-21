namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// A host member a remote node does not offer (Phase 4, design 2.4): starting or listing processes, containment,
/// every file member, host queries, the hardware sampler, the environment and folders, a reverse forward. A remote
/// node runs only what its owner registered there, reached through <see cref="IExecutionHost.Runtimes"/> and
/// <see cref="IHostNetwork"/>'s handler and WebSocket; everything else fails closed, by construction, with this.
/// </summary>
public sealed class RemoteOperationNotAllowedException(string nodeId, string operation)
    : InvalidOperationException($"'{operation}' is not available on remote node '{nodeId}': a remote node runs only the runtimes its owner registered there.")
{
    public string NodeId { get; } = nodeId;

    /// <summary>The member that was called, as <c>Interface.Member</c> (for example <c>IHostProcesses.Start</c>).</summary>
    public string Operation { get; } = operation;
}
