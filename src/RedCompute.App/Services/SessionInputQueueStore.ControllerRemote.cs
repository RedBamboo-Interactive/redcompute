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

    private sealed record ControllerRemoteStart(SessionRuntimeScope Scope, RemoteSessionPlacement Placement,
        RemoteSessionPlacementPreview Preview);
}
