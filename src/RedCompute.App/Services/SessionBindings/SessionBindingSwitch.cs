namespace RedCompute.App.Services.SessionBindings;

internal sealed record BindingHistoryText(long RecordId, string Role, string EventType, string? MessageUid, string? Content);
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

internal sealed class SessionBindingConflictException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

/// <summary>Runs underneath the logical session's delivery lock after earlier turns settle.</summary>
internal sealed class SessionBindingSwitch(SessionBindingJournal journal, ISessionBindingRuntime runtime)
{
    public async Task<SessionBindingMove> ApplyAsync(string logicalId, NativeSessionBinding initial,
        string moveId, long expectedGeneration, BindingTarget destination, CancellationToken ct)
    {
        var snapshot = journal.OpenOrAttach(logicalId, initial);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var move = snapshot.Moves.SingleOrDefault(item => item.Id == moveId);
            if (move is not null && (move.SourceGeneration != expectedGeneration || move.Destination != destination))
                throw new SessionBindingConflictException("idempotency_conflict");
            if (move?.State == BindingMoveState.Committed) return move;
            if (move?.State == BindingMoveState.Rejected)
            {
                await runtime.DiscardAsync(logicalId, move.Id, ct);
                return move;
            }
            if (snapshot.Active.Generation != expectedGeneration)
                throw new SessionBindingConflictException("binding_changed");
            if (snapshot.Moves.Any(item => item.Id != moveId && item.State is BindingMoveState.Pending or BindingMoveState.Prepared))
                throw new SessionBindingConflictException("binding_change_pending");

            if (move is null)
            {
                if (SameRuntime(snapshot.Active.Native.Target, destination))
                    throw new SessionBindingConflictException("binding_already_active");
                if (!await runtime.CanMoveAsync(logicalId, snapshot.Active, destination, ct))
                    throw new SessionBindingConflictException("binding_destination_unavailable");
                var boundary = await runtime.ReadHistoryBoundaryAsync(logicalId, ct);
                if (boundary < snapshot.Active.HistoryThrough)
                    throw new InvalidOperationException("The authorized transcript boundary moved backward.");
                var pending = new SessionBindingMove(moveId, expectedGeneration, destination, boundary, BindingMoveState.Pending);
                Save(snapshot, pending);
            }
            else if (move.State == BindingMoveState.Pending)
            {
                await PrepareAsync(snapshot, move, ct);
            }
            else
            {
                var committed = await CommitAsync(snapshot, move, ct);
                if (committed is not null) return committed;
            }
            snapshot = journal.Read(logicalId)!;
        }
    }

    private async Task PrepareAsync(SessionBindingSnapshot snapshot, SessionBindingMove move, CancellationToken ct)
    {
        if (!await runtime.CanMoveAsync(snapshot.LogicalId, snapshot.Active, move.Destination, ct))
        {
            Reject(snapshot, move, "binding_destination_unavailable");
            return;
        }

        var previous = snapshot.Threads.LastOrDefault(thread => SameRuntime(thread.Native.Target, move.Destination));
        var after = previous?.HistoryThrough ?? 0;
        var history = await runtime.ReadHistoryAsync(snapshot.LogicalId, after, move.HistoryThrough, ct);
        var preparation = new BindingPreparation(snapshot.LogicalId, move, snapshot.Active, previous,
            BoundedHandoff(history, after, move.HistoryThrough));
        var result = await runtime.PrepareAsync(preparation, ct);
        if (result.Binding is not { } binding)
        {
            Reject(snapshot, move, result.Refusal ?? "binding_prepare_refused");
            return;
        }
        if (binding.Target != move.Destination || string.IsNullOrWhiteSpace(binding.NativeThreadId)
            || string.IsNullOrWhiteSpace(binding.RuntimeSessionId))
        {
            Reject(snapshot, move, "binding_prepare_mismatch");
            return;
        }
        Save(snapshot, move with { State = BindingMoveState.Prepared, Prepared = binding });
    }

    private async Task<SessionBindingMove?> CommitAsync(SessionBindingSnapshot snapshot, SessionBindingMove move,
        CancellationToken ct)
    {
        if (!await runtime.CanMoveAsync(snapshot.LogicalId, snapshot.Active, move.Destination, ct)
            || !await runtime.IsReadyAsync(snapshot.LogicalId, move, ct))
        {
            Reject(snapshot, move, "binding_destination_unavailable");
            return null;
        }

        var binding = new SessionBinding(snapshot.Active.Generation + 1, move.Prepared!, move.HistoryThrough);
        var previous = snapshot.Active with { HistoryThrough = move.HistoryThrough };
        var threads = snapshot.Threads.Take(snapshot.Threads.Count - 1).Append(previous).Append(binding).ToArray();
        var committed = move with { State = BindingMoveState.Committed, CommittedGeneration = binding.Generation };
        return Save(snapshot, committed, threads) ? committed : null;
    }

    private void Reject(SessionBindingSnapshot snapshot, SessionBindingMove move, string code)
        => Save(snapshot, move with { State = BindingMoveState.Rejected, Error = code });

    private bool Save(SessionBindingSnapshot snapshot, SessionBindingMove move, IReadOnlyList<SessionBinding>? threads = null)
        => journal.TrySave(snapshot, snapshot with
        {
            Revision = snapshot.Revision + 1,
            Threads = threads ?? snapshot.Threads,
            Moves = snapshot.Moves.Where(item => item.Id != move.Id).Append(move).ToArray(),
        });

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
