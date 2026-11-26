namespace RedCompute.App.Services.SessionBindings;

public sealed record BindingTarget(string ProviderId, string ProviderInstanceId, string NodeId, string Model, string? Effort);

public sealed record QueuedBindingChange(long ExpectedGeneration, BindingTarget Destination);

internal sealed record NativeSessionBinding(string RuntimeSessionId, string NativeThreadId, BindingTarget Target);

internal sealed record SessionBinding(long Generation, NativeSessionBinding Native, long HistoryThrough);

internal enum BindingMoveState { Pending, Prepared, Committed, Rejected }

internal sealed record SessionBindingMove(string Id, long SourceGeneration, BindingTarget Destination, long HistoryThrough,
    BindingMoveState State, NativeSessionBinding? Prepared = null, long? CommittedGeneration = null, string? Error = null);

internal sealed record SessionBindingSnapshot(string LogicalId, IReadOnlyList<SessionBinding> Threads)
{
    public SessionBinding Active => Threads[^1];

    internal static SessionBindingSnapshot Initial(string logicalId, NativeSessionBinding native)
        => new(logicalId, [new(0, native, 0)]);
}
