using System.Security.Cryptography;
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

    private static void BindRemoteLifecycle(SqliteCommand command, SessionRuntimeGrant grant)
    {
        Add(command, "$node", grant.Scope.NodeId);
        Add(command, "$runtime", grant.Scope.RuntimeId);
        Add(command, "$logical", grant.Scope.LogicalSessionId);
        Add(command, "$generation", grant.Scope.Generation);
        Add(command, "$request", grant.RequestId);
        Add(command, "$operation", grant.Operation.ToString());
        Add(command, "$owner", grant.Scope.OwnerId);
        Add(command, "$access", grant.Scope.AccessScope);
        Add(command, "$digest", Convert.ToHexString(SHA256.HashData(grant.Payload)));
    }

    private static void CheckRemoteLifecycleOperation(SessionRuntimeOperation operation)
    {
        if (operation is not (SessionRuntimeOperation.Start or SessionRuntimeOperation.Resume or SessionRuntimeOperation.Park))
            throw new ArgumentOutOfRangeException(nameof(operation), "Expected a lifecycle operation.");
    }
}
