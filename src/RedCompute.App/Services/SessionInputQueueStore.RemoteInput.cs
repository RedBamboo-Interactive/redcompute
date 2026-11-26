using System.Security.Cryptography;
using RedCompute.NodeLink;

namespace RedCompute.App.Services;

internal sealed record RemoteInputReceipt(string QueueItemId, string InputUid, string TurnId);
internal sealed record RemoteInputInspection(RemoteInputReceipt Receipt, SessionInputQueueState? DeliveryState, string? ErrorCode);

public sealed partial class SessionInputQueueStore
{
    internal async Task<RemoteInputReceipt> EnqueueRemoteInputAsync(SessionRuntimeGrant grant, SessionInputQueueSubmission submission,
        CancellationToken ct = default)
    {
        if (grant.Operation != SessionRuntimeOperation.SendInput || submission.OwnerUserId != grant.Scope.OwnerId
            || submission.MessageUid != grant.RequestId || submission.IdempotencyKey != grant.RequestId
            || submission.DeliveryPolicy != SessionInputDeliveryPolicy.AfterCurrent
            || submission.InferenceChange is not null || submission.BindingChange is not null)
            throw new SessionInputQueueStoreException("invalid_remote_input", "The submission does not match the signed input");

        var digest = Convert.ToHexString(SHA256.HashData(grant.Payload));
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT QueueItemId, OwnerId, AccessScope, PayloadDigest, RuntimeSessionId
                FROM RemoteSessionInputReceipts
                WHERE NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND Generation=$generation AND InputUid=$uid
                """;
            BindRemoteSessionKey(read, grant.Scope);
            Add(read, "$uid", grant.RequestId);
            await using var result = await read.ExecuteReaderAsync(ct);
            if (await result.ReadAsync(ct))
            {
                if (result.GetString(1) != grant.Scope.OwnerId || result.GetString(2) != grant.Scope.AccessScope
                    || result.GetString(3) != digest || result.GetString(4) != submission.SessionId)
                    throw new SessionInputQueueStoreException("idempotency_conflict", "The input UID was already used for a different signed input");
                return new(result.GetString(0), grant.RequestId, grant.RequestId);
            }
        }

        // The queue uses provenance scope to coalesce ordinary messages. A remote receipt names
        // one separately recoverable turn, so that input gets its own stable scope.
        var provenance = submission.Provenance with
        {
            Context = [.. submission.Provenance.Context, new("remote-input", grant.RequestId)],
        };
        var admission = await EnqueueCoreAsync(connection, transaction, submission with { Provenance = provenance }, ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO RemoteSessionInputReceipts
                (NodeId, RuntimeId, LogicalSessionId, Generation, InputUid, OwnerId, AccessScope, PayloadDigest, RuntimeSessionId, QueueItemId)
            VALUES ($node, $runtime, $logical, $generation, $uid, $owner, $access, $digest, $session, $queue)
            """;
        BindRemoteLifecycleScope(insert, grant.Scope);
        Add(insert, "$uid", grant.RequestId);
        Add(insert, "$digest", digest);
        Add(insert, "$session", submission.SessionId);
        Add(insert, "$queue", admission.Item.Id);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(admission.Item.Id, grant.RequestId, grant.RequestId);
    }

    internal async Task<RemoteInputInspection?> ReadRemoteInputAsync(SessionRuntimeScope scope, string inputUid,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt.QueueItemId, COALESCE(input.State, receipt.FinalState), COALESCE(input.ErrorCode, receipt.FinalErrorCode)
            FROM RemoteSessionInputReceipts receipt
            LEFT JOIN SessionQueuedInputs input ON input.Id=receipt.QueueItemId
            WHERE receipt.NodeId=$node AND receipt.RuntimeId=$runtime AND receipt.LogicalSessionId=$logical
                AND receipt.Generation=$generation AND receipt.InputUid=$uid AND receipt.OwnerId=$owner AND receipt.AccessScope=$access
            """;
        BindRemoteLifecycleScope(command, scope);
        Add(command, "$uid", inputUid);
        await using var result = await command.ExecuteReaderAsync(ct);
        if (!await result.ReadAsync(ct)) return null;
        var state = result.IsDBNull(1) ? (SessionInputQueueState?)null : Enum.Parse<SessionInputQueueState>(result.GetString(1));
        return new(new(result.GetString(0), inputUid, inputUid), state, result.IsDBNull(2) ? null : result.GetString(2));
    }
}
