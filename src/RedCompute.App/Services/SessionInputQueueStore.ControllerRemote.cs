using System.Text.Json;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;

namespace RedCompute.App.Services;

public sealed partial class SessionInputQueueStore
{
    internal async Task ReserveControllerRemoteStartAsync(SessionRuntimeScope scope, RemoteSessionPlacement placement,
        RemoteSessionPlacementPreview preview, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new ControllerRemoteStart(scope, placement, preview), JsonOptions);
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ControllerRemoteSessionStarts (SessionId, RequestJson) VALUES ($id, $request)
            ON CONFLICT (SessionId) DO NOTHING
            """;
        Add(insert, "$id", scope.LogicalSessionId);
        Add(insert, "$request", body);
        if (await insert.ExecuteNonQueryAsync(ct) == 0)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT RequestJson FROM ControllerRemoteSessionStarts WHERE SessionId=$id";
            Add(read, "$id", scope.LogicalSessionId);
            if (await read.ExecuteScalarAsync(ct) as string != body)
                throw new ControllerRemoteSessionException(409, "remote_start_conflict");
        }
        await transaction.CommitAsync(ct);
    }

    internal async Task<SessionBindingSnapshot> CommitControllerRemoteStartAsync(ControllerRemoteSession session, CancellationToken ct)
    {
        var scope = session.Scope;
        var placement = session.Placement;
        var thread = session.Thread;
        var id = scope.LogicalSessionId;
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO SessionThreadBindings (SessionId, ActiveGeneration, ThreadsJson)
            SELECT SessionId, 0, $threads FROM ControllerRemoteSessionStarts WHERE SessionId=$id AND RequestJson=$request
            ON CONFLICT (SessionId) DO NOTHING
            """;
        var target = new BindingTarget(placement.ProviderId, placement.ProviderInstanceId, placement.NodeId,
            thread.Model ?? placement.Model ?? string.Empty, thread.Effort ?? placement.Effort,
            new(placement.RuntimeId, placement.Workspace, placement.Revision, session.Preview.AccessLevel, session.Preview.MaxAccessLevel));
        var snapshot = SessionBindingSnapshot.Initial(id, new(thread.RuntimeSessionId, thread.NativeThreadId, target, Remote: session));
        Add(insert, "$id", id);
        Add(insert, "$request", JsonSerializer.Serialize(new ControllerRemoteStart(scope, placement, session.Preview), JsonOptions));
        Add(insert, "$threads", JsonSerializer.Serialize(snapshot.Threads, JsonOptions));
        var inserted = await insert.ExecuteNonQueryAsync(ct);
        if (inserted == 0)
            return await ReadBindingSnapshotAsync(connection, transaction, id, ct)
                ?? throw new ControllerRemoteSessionException(409, "remote_start_conflict");
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    internal async Task<ControllerRemoteSession?> ConfirmControllerRemoteActivationAsync(ControllerRemoteSession pending,
        CancellationToken ct)
    {
        if (!pending.ActivationPending) return null;
        var scope = pending.Scope;
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadBindingSnapshotAsync(connection, transaction, scope.LogicalSessionId, ct);
        if (current?.Active.Generation != scope.Generation || current.Active.Native.Remote is not { } active
            || active != pending && active != pending with { ActivationConfirmed = true })
            return null;
        if (active.ActivationConfirmed) return active;

        var confirmed = pending with { ActivationConfirmed = true };
        var next = current.Active with { Native = current.Active.Native with { Remote = confirmed } };
        var threads = current.Threads.Take(current.Threads.Count - 1).Append(next).ToArray();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE SessionThreadBindings SET ThreadsJson=$threads WHERE SessionId=$session AND ActiveGeneration=$generation
            """;
        Add(update, "$threads", JsonSerializer.Serialize(threads, JsonOptions));
        Add(update, "$session", scope.LogicalSessionId);
        Add(update, "$generation", scope.Generation);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return null;
        await transaction.CommitAsync(ct);
        BindingCommitted?.Invoke(new(scope.LogicalSessionId, threads));
        return confirmed;
    }

    private const string ControllerRemoteLease = "remote-session-runtime";

    // An attempted network delivery has a stable destination receipt. Keep it at the head until
    // reconciliation, even when the controller restarts or the node does not reply.
    internal async Task<SessionInputQueueItem?> ClaimControllerRemoteInputAsync(ControllerRemoteSession binding,
        string expectedItemId, CancellationToken ct)
    {
        if (binding.ActivationPending) return null;
        var scope = binding.Scope;
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadBindingSnapshotAsync(connection, transaction, scope.LogicalSessionId, ct);
        if (current?.Active.Native.Remote != binding || current.Active.Generation != scope.Generation) return null;

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT * FROM SessionQueuedInputs
            WHERE SessionId=$session AND State IN ('Pending','Delivering','Failed') ORDER BY Sequence LIMIT 1
            """;
        Add(read, "$session", scope.LogicalSessionId);
        var head = (await ReadItemsAsync(read, ct)).FirstOrDefault();
        var now = DateTimeOffset.UtcNow;
        if (head is null || head.Id != expectedItemId || head.OwnerUserId != scope.OwnerId || head.IsControl
            || head.DeliveryPolicy != SessionInputDeliveryPolicy.AfterCurrent
            || head.Input is not [{ Type: "text" }] || head.AttachmentIds.Count != 0
            || IsDeferred(head.NextAttemptAt, now))
            return null;

        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = """
            UPDATE SessionQueuedInputs
            SET State='Delivering', LeaseOwner=$remote, LeaseExpiresAt=NULL,
                RemoteSendMayHaveStarted=CASE WHEN State='Pending' THEN 0 ELSE RemoteSendMayHaveStarted END,
                AttemptCount=AttemptCount+CASE WHEN State='Pending' THEN 1 ELSE 0 END, UpdatedAt=$now
            WHERE Id=$id AND (State='Pending' OR (State='Delivering' AND LeaseOwner=$remote))
            """;
        Add(claim, "$remote", ControllerRemoteLease);
        Add(claim, "$now", now.ToString("O"));
        Add(claim, "$id", head.Id);
        if (await claim.ExecuteNonQueryAsync(ct) != 1) return null;
        var claimed = await GetCoreAsync(connection, transaction, scope.LogicalSessionId, head.Id, scope.OwnerId, ct);
        await transaction.CommitAsync(ct);
        return claimed;
    }

    internal async Task<bool> HasUnsettledControllerRemoteDispatchAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = Open();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM SessionQueuedInputs
                WHERE SessionId=$session AND State='Delivering' AND LeaseOwner=$remote AND RemoteSendMayHaveStarted=1
            )
            """;
        Add(read, "$session", sessionId);
        Add(read, "$remote", ControllerRemoteLease);
        return await read.ExecuteScalarAsync(ct) is 1L;
    }

    // The worker records the send boundary before asking the node to accept input. A lost reply or
    // controller restart then leaves conservative, durable evidence of a potentially issued prompt.
    internal async Task<bool> MarkControllerRemoteDispatchIssuedAsync(ControllerRemoteSession binding, string itemId,
        CancellationToken ct)
    {
        if (binding.ActivationPending) return false;
        var scope = binding.Scope;
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadBindingSnapshotAsync(connection, transaction, scope.LogicalSessionId, ct);
        if (current?.Active.Native.Remote != binding || current.Active.Generation != scope.Generation) return false;

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE SessionQueuedInputs SET RemoteSendMayHaveStarted=1
            WHERE Id=$id AND SessionId=$session AND OwnerUserId=$owner AND State='Delivering' AND LeaseOwner=$remote
            """;
        Add(update, "$id", itemId);
        Add(update, "$session", scope.LogicalSessionId);
        Add(update, "$owner", scope.OwnerId);
        Add(update, "$remote", ControllerRemoteLease);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return false;
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task<bool> CompleteControllerRemoteInputAsync(SessionInputQueueItem item, SessionTurnSummary turn,
        CancellationToken ct)
    {
        if (turn.TurnId != item.MessageUid || turn.EndedAt is not { } endedAt
            || turn.Outcome is not (SessionTurnOutcomes.Completed or SessionTurnOutcomes.Interrupted
                or SessionTurnOutcomes.Failed or SessionTurnOutcomes.Stopped))
            return false;

        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = """
            UPDATE SessionQueuedInputs
            SET State='Delivered', TurnId=$turn, DeliveredMessageUid=$uid, UpdatedAt=$now, CompletedAt=$now,
                NextAttemptAt=NULL, LeaseOwner=NULL, LeaseExpiresAt=NULL, ErrorCode=NULL, ErrorMessage=NULL, ErrorRetryable=0
            WHERE Id=$id AND SessionId=$session AND OwnerUserId=$owner AND State='Delivering' AND LeaseOwner=$remote
            """;
        Add(complete, "$turn", turn.TurnId);
        Add(complete, "$uid", item.MessageUid);
        Add(complete, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(complete, "$id", item.Id);
        Add(complete, "$session", item.SessionId);
        Add(complete, "$owner", item.OwnerUserId);
        Add(complete, "$remote", ControllerRemoteLease);
        if (await complete.ExecuteNonQueryAsync(ct) != 1) return false;

        await using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO SessionTurns
                (SessionId, TurnId, OpenedAt, ActiveSeen, EndedAt, Status, Outcome, InterruptSource, InterruptedByMessageUid, Error)
            VALUES ($session,$turn,$opened,1,$ended,$status,$outcome,$interrupt,$interruptedBy,$error)
            ON CONFLICT (SessionId, TurnId) DO NOTHING
            """;
        Add(save, "$session", item.SessionId);
        Add(save, "$turn", turn.TurnId);
        Add(save, "$opened", item.CreatedAt.ToUniversalTime().ToString("O"));
        Add(save, "$ended", endedAt.ToUniversalTime().ToString("O"));
        Add(save, "$status", turn.Outcome == SessionTurnOutcomes.Stopped ? "Stopped" : "Idle");
        Add(save, "$outcome", turn.Outcome);
        AddNullable(save, "$interrupt", turn.InterruptSource);
        AddNullable(save, "$interruptedBy", turn.InterruptedByMessageUid);
        AddNullable(save, "$error", turn.Error);
        await save.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        _lastEnded.TryRemove(item.SessionId, out _);
        return true;
    }

    internal async Task SetControllerRemoteInputErrorAsync(SessionInputQueueItem item, string? error, CancellationToken ct)
    {
        await using var connection = Open();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE SessionQueuedInputs SET ErrorCode=$error, ErrorMessage=$error, ErrorRetryable=0, UpdatedAt=$now
            WHERE Id=$id AND SessionId=$session AND OwnerUserId=$owner AND State='Delivering' AND LeaseOwner=$remote
            """;
        AddNullable(update, "$error", error);
        Add(update, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(update, "$id", item.Id);
        Add(update, "$session", item.SessionId);
        Add(update, "$owner", item.OwnerUserId);
        Add(update, "$remote", ControllerRemoteLease);
        await update.ExecuteNonQueryAsync(ct);
    }

    private sealed record ControllerRemoteStart(SessionRuntimeScope Scope, RemoteSessionPlacement Placement,
        RemoteSessionPlacementPreview Preview);
}
