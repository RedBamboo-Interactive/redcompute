using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.App.Services.Nodes;
using Microsoft.Data.Sqlite;
using RedCompute.NodeLink;

namespace RedCompute.App.Services;

internal enum RemoteLifecycleState
{
    Reserved,
    Pending,
    Completed,
    Conflict,
}

internal sealed record RemoteLifecycleReceipt(int Status, byte[] Body);
internal sealed record RemoteLifecycleReservation(RemoteLifecycleState State, RemoteLifecycleReceipt? Receipt = null);

public sealed partial class SessionInputQueueStore
{
    private const string MatchRemoteLifecycle = """
        NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND Generation=$generation
        AND RequestId=$request AND Operation=$operation AND OwnerId=$owner AND AccessScope=$access AND PayloadDigest=$digest
        """;

    /// <summary>Reserves an authorized lifecycle command before invoking the local provider.</summary>
    internal async Task<RemoteLifecycleReservation> ReserveRemoteLifecycleAsync(SessionRuntimeGrant grant,
        CancellationToken ct = default)
    {
        CheckRemoteLifecycleOperation(grant.Operation);
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO RemoteSessionLifecycleCommands
                (NodeId, RuntimeId, LogicalSessionId, Generation, RequestId, Operation, OwnerId, AccessScope, PayloadDigest)
            VALUES ($node, $runtime, $logical, $generation, $request, $operation, $owner, $access, $digest)
            ON CONFLICT DO NOTHING
            """;
        BindRemoteLifecycle(insert, grant);
        if (await insert.ExecuteNonQueryAsync(ct) == 1)
        {
            await transaction.CommitAsync(ct);
            return new(RemoteLifecycleState.Reserved);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT Status, Body FROM RemoteSessionLifecycleCommands WHERE " + MatchRemoteLifecycle;
        BindRemoteLifecycle(read, grant);
        await using var result = await read.ExecuteReaderAsync(ct);
        if (!await result.ReadAsync(ct))
            return new(RemoteLifecycleState.Conflict);
        if (result.IsDBNull(0))
            return new(RemoteLifecycleState.Pending);
        return new(RemoteLifecycleState.Completed, new(result.GetInt32(0), result.GetFieldValue<byte[]>(1)));
    }

    /// <summary>Publishes the exact result once. A recovery attempt cannot replace a committed result.</summary>
    internal async Task<bool> CompleteRemoteLifecycleAsync(SessionRuntimeGrant grant, RemoteLifecycleReceipt receipt,
        CancellationToken ct = default)
    {
        CheckRemoteLifecycleOperation(grant.Operation);
        if (receipt.Status is < 200 or > 599 || receipt.Body.Length > SessionRuntimeProtocol.MaxExchangeBodyBytes)
            throw new ArgumentException("Invalid remote session lifecycle receipt.", nameof(receipt));

        await using var connection = Open();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE RemoteSessionLifecycleCommands SET Status=$status, Body=$body WHERE Status IS NULL AND
            """ + " " + MatchRemoteLifecycle;
        BindRemoteLifecycle(update, grant);
        Add(update, "$status", receipt.Status);
        Add(update, "$body", receipt.Body);
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }

    internal RemoteSessionRuntimeBinding? FindRemoteRuntimeBinding(string runtimeId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active.NodeId, active.RuntimeId, active.LogicalSessionId, active.Generation,
                   active.OwnerId, active.AccessScope, active.Body
            FROM RemoteSessionLifecycleCommands active
            WHERE active.Operation IN ('Start','ActivateMove') AND active.Status BETWEEN 200 AND 299
              AND json_extract(CAST(active.Body AS TEXT),'$.runtimeSessionId')=$runtime
              AND NOT EXISTS (
                  SELECT 1 FROM RemoteSessionLifecycleCommands retirement
                  WHERE retirement.NodeId=active.NodeId AND retirement.RuntimeId=active.RuntimeId
                    AND retirement.LogicalSessionId=active.LogicalSessionId AND retirement.OwnerId=active.OwnerId
                    AND retirement.Status BETWEEN 200 AND 299
                    AND ((retirement.Generation=active.Generation AND retirement.AccessScope=active.AccessScope
                          AND retirement.Operation='SealMoveSource')
                         OR (retirement.Generation>active.Generation AND retirement.Operation='ActivateMove'))
              )
            """;
        Add(command, "$runtime", runtimeId);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var thread = JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(row.GetFieldValue<byte[]>(6), JsonOptions);
        if (thread is null) return null;
        var scope = new SessionRuntimeScope(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetString(4),
            row.GetString(5));
        return row.Read() ? null : new(scope, thread);
    }

    internal async Task<RemoteLifecycleReceipt?> ReadRemoteLifecycleStartAsync(SessionRuntimeScope scope, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT Status, Body FROM RemoteSessionLifecycleCommands
            WHERE NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND Generation=$generation
              AND OwnerId=$owner AND AccessScope=$access
              AND Operation IN ('Start','ActivateMove') AND Status BETWEEN 200 AND 299
            """;
        BindRemoteLifecycleScope(read, scope);
        await using var result = await read.ExecuteReaderAsync(ct);
        return await result.ReadAsync(ct) ? new(result.GetInt32(0), result.GetFieldValue<byte[]>(1)) : null;
    }

    internal async Task<RemoteLifecycleReceipt?> ReadRemoteMoveReceiptAsync(SessionRuntimeScope scope,
        SessionRuntimeOperation operation, string moveId, CancellationToken ct)
    {
        await using var connection = Open();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT Status, Body FROM RemoteSessionLifecycleCommands
            WHERE NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND Generation=$generation
              AND OwnerId=$owner AND AccessScope=$access AND Operation=$operation
              AND RequestId=$request AND Status BETWEEN 200 AND 299
            """;
        BindRemoteLifecycleScope(read, scope);
        Add(read, "$operation", operation.ToString());
        Add(read, "$request", SessionRuntimeProtocol.MoveRequestId(operation, moveId));
        await using var result = await read.ExecuteReaderAsync(ct);
        return await result.ReadAsync(ct) ? new(result.GetInt32(0), result.GetFieldValue<byte[]>(1)) : null;
    }

    internal async Task<bool> IsRemoteRuntimeGenerationRetiredAsync(SessionRuntimeScope scope, CancellationToken ct)
    {
        await using var connection = Open();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM RemoteSessionLifecycleCommands
                WHERE NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND OwnerId=$owner
                  AND Status BETWEEN 200 AND 299
                  AND ((Generation=$generation AND AccessScope=$access AND Operation='SealMoveSource')
                       OR (Generation>$generation AND Operation='ActivateMove'))
            )
            """;
        BindRemoteLifecycleScope(read, scope);
        return await read.ExecuteScalarAsync(ct) is 1L;
    }

    private static void BindRemoteLifecycle(SqliteCommand command, SessionRuntimeGrant grant)
    {
        BindRemoteLifecycleScope(command, grant.Scope);
        Add(command, "$request", grant.RequestId);
        Add(command, "$operation", grant.Operation.ToString());
        Add(command, "$digest", Convert.ToHexString(SHA256.HashData(grant.Payload)));
    }

    private static void BindRemoteLifecycleScope(SqliteCommand command, SessionRuntimeScope scope)
    {
        BindRemoteSessionKey(command, scope);
        Add(command, "$owner", scope.OwnerId);
        Add(command, "$access", scope.AccessScope);
    }

    private static void BindRemoteSessionKey(SqliteCommand command, SessionRuntimeScope scope)
    {
        Add(command, "$node", scope.NodeId);
        Add(command, "$runtime", scope.RuntimeId);
        Add(command, "$logical", scope.LogicalSessionId);
        Add(command, "$generation", scope.Generation);
    }

    private static void CheckRemoteLifecycleOperation(SessionRuntimeOperation operation)
    {
        if (operation is not (SessionRuntimeOperation.Start or SessionRuntimeOperation.Resume or SessionRuntimeOperation.Park)
            && !SessionRuntimeProtocol.IsMoveOperation(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), "Expected a lifecycle operation.");
    }
}
