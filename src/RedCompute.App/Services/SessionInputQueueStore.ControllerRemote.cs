using System.Text.Json;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.SessionBindings;
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
            placement.Model ?? string.Empty, placement.Effort);
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

    private const string ControllerRemoteLease = "remote-session-runtime";

    // An attempted network delivery has a stable destination receipt. Keep it at the head until
    // reconciliation, even when the controller restarts or the node does not reply.
    internal async Task<SessionInputQueueItem?> ClaimControllerRemoteInputAsync(ControllerRemoteSession binding, CancellationToken ct)
    {
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
        if (head is null || head.OwnerUserId != scope.OwnerId || head.IsControl
            || head.DeliveryPolicy != SessionInputDeliveryPolicy.AfterCurrent
            || head.Input is not [{ Type: "text" }] || head.AttachmentIds.Count != 0
            || IsDeferred(head.NextAttemptAt, now))
            return null;

        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = """
            UPDATE SessionQueuedInputs
            SET State='Delivering', LeaseOwner=$remote, LeaseExpiresAt=NULL,
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

    private sealed record ControllerRemoteStart(SessionRuntimeScope Scope, RemoteSessionPlacement Placement,
        RemoteSessionPlacementPreview Preview);
}
