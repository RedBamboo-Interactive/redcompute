using RedCompute.App.Services;

namespace RedCompute.App.Services.SessionBindings;

internal sealed record BindingHistoryText(long RecordId, string Role, string EventType, string? MessageUid, string? Content,
    IReadOnlyList<BindingHandoffArtifact>? Artifacts = null);
internal sealed record BindingHandoffText(string Role, string? MessageUid, string Content);

internal sealed record BindingPreparation(string LogicalId, SessionBindingMove Move, SessionBinding Source,
    SessionBinding? PreviousDestination, IReadOnlyList<BindingHandoffText> History);

internal sealed record BindingPreparationResult(NativeSessionBinding? Binding = null, string? Refusal = null);

/// <summary>Destination operations use the original move id. Prepare may be repeated after a lost response.</summary>
internal interface ISessionBindingRuntime
{
    Task<bool> CanMoveAsync(string logicalId, SessionBinding source, BindingTarget destination, CancellationToken ct);
    Task<long> ReadHistoryBoundaryAsync(string logicalId, CancellationToken ct);
    Task<IReadOnlyList<BindingHistoryText>> ReadHistoryAsync(string logicalId, long after, long through, CancellationToken ct);
    Task<BindingPreparationResult> PrepareAsync(BindingPreparation preparation, CancellationToken ct);
    Task<bool> IsReadyAsync(string logicalId, SessionBindingMove move, CancellationToken ct);
    // Tombstone the preparation by id so an in-flight or replayed prepare cannot revive it.
    Task DiscardAsync(string logicalId, string moveId, CancellationToken ct);
}

/// <summary>Runs underneath the logical session's delivery lock after earlier turns settle.</summary>
internal sealed class SessionBindingSwitch(SessionInputQueueStore queue, ISessionBindingRuntime runtime)
{
    public async Task<SessionBindingMove?> ApplyAsync(SessionInputQueueItem admitted, NativeSessionBinding initial, CancellationToken ct)
    {
        var request = admitted.BindingChange ?? throw new ArgumentException("Expected a binding operation", nameof(admitted));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var current = await queue.GetAsync(admitted.SessionId, admitted.Id, admitted.OwnerUserId, ct);
            if (current is null || current.BindingChange != request)
                throw new InvalidOperationException("Binding operation is not in the session queue");

            var move = current.BindingMove;
            if (move?.State == BindingMoveState.Committed) return move;
            if (move?.State == BindingMoveState.Rejected || current.State == SessionInputQueueState.Cancelled)
            {
                if (move is not null) await runtime.DiscardAsync(current.SessionId, current.Id, ct);
                return move?.State == BindingMoveState.Rejected ? move : null;
            }
            if (!await queue.IsBindingChangeReadyAsync(current, ct)) return null;

            var snapshot = await queue.ReadBindingSnapshotAsync(current.SessionId, ct)
                ?? SessionBindingSnapshot.Initial(current.SessionId, initial);
            move ??= new(current.Id, request.ExpectedGeneration, request.Destination, 0, BindingMoveState.Pending);

            if (snapshot.Active.Generation != request.ExpectedGeneration)
            {
                await RejectAsync(current, initial, move, "binding_changed", ct);
                continue;
            }
            if (SameRuntime(snapshot.Active.Native.Target, request.Destination))
            {
                await RejectAsync(current, initial, move, "binding_already_active", ct);
                continue;
            }
            if (!await runtime.CanMoveAsync(current.SessionId, snapshot.Active, request.Destination, ct))
            {
                await RejectAsync(current, initial, move, "binding_destination_unavailable", ct);
                continue;
            }

            if (current.BindingMove is null)
            {
                var boundary = await runtime.ReadHistoryBoundaryAsync(current.SessionId, ct);
                if (boundary < snapshot.Active.HistoryThrough)
                    throw new InvalidOperationException("The authorized transcript boundary moved backward.");
                await queue.TryAdvanceBindingMoveAsync(current, initial, move with { HistoryThrough = boundary }, ct);
            }
            else if (move.State == BindingMoveState.Pending)
            {
                await PrepareAsync(current, initial, snapshot, move, ct);
            }
            else if (!await runtime.IsReadyAsync(current.SessionId, move, ct))
            {
                await RejectAsync(current, initial, move, "binding_destination_unavailable", ct);
            }
            else
            {
                var committed = move with { State = BindingMoveState.Committed, CommittedGeneration = snapshot.Active.Generation + 1 };
                if (await queue.TryAdvanceBindingMoveAsync(current, initial, committed, ct)) return committed;
            }
        }
    }

    private async Task PrepareAsync(SessionInputQueueItem current, NativeSessionBinding initial,
        SessionBindingSnapshot snapshot, SessionBindingMove move, CancellationToken ct)
    {
        var previous = snapshot.Threads.LastOrDefault(thread => SameRuntime(thread.Native.Target, move.Destination));
        var after = previous?.HistoryThrough ?? 0;
        var history = await runtime.ReadHistoryAsync(current.SessionId, after, move.HistoryThrough, ct);
        var preparation = new BindingPreparation(current.SessionId, move, snapshot.Active, previous,
            BoundedHandoff(history, after, move.HistoryThrough));
        var result = await runtime.PrepareAsync(preparation, ct);
        if (result.Binding is not { } binding)
        {
            await RejectAsync(current, initial, move, result.Refusal ?? "binding_prepare_refused", ct);
            return;
        }
        if (binding.Target != move.Destination || string.IsNullOrWhiteSpace(binding.NativeThreadId)
            || string.IsNullOrWhiteSpace(binding.RuntimeSessionId))
        {
            await RejectAsync(current, initial, move, "binding_prepare_mismatch", ct);
            return;
        }
        await queue.TryAdvanceBindingMoveAsync(current, initial,
            move with { State = BindingMoveState.Prepared, Prepared = binding }, ct);
    }

    private Task<bool> RejectAsync(SessionInputQueueItem current, NativeSessionBinding initial,
        SessionBindingMove move, string code, CancellationToken ct)
        => queue.TryAdvanceBindingMoveAsync(current, initial, move with { State = BindingMoveState.Rejected, Error = code }, ct);


    private static bool SameRuntime(BindingTarget previous, BindingTarget destination)
        => previous.ProviderId == destination.ProviderId && previous.ProviderInstanceId == destination.ProviderInstanceId
            && previous.NodeId == destination.NodeId;

    internal static IReadOnlyList<BindingHandoffText> BoundedHandoff(
        IReadOnlyList<BindingHistoryText> history, long after, long through)
    {
        const int maxRecords = 24;
        var remaining = 12_000;
        var records = new List<BindingHandoffText>();
        foreach (var item in history.Where(item => item.RecordId > after && item.RecordId <= through
                && item.Role is "user" or "assistant" && item.EventType == "text"
                && !string.IsNullOrWhiteSpace(item.Content))
            .OrderByDescending(item => item.RecordId).Take(maxRecords))
        {
            var content = item.Content!;
            if (content.Length > remaining)
            {
                var start = content.Length - remaining;
                if (char.IsLowSurrogate(content[start])) start++;
                content = content[start..];
            }
            if (content.Length > 0) records.Add(new(item.Role, item.MessageUid, content));
            remaining -= content.Length;
            if (remaining == 0) break;
        }
        records.Reverse();
        return records;
    }
}
