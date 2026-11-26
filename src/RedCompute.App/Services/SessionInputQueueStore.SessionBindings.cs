using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.App.Services.SessionBindings;

namespace RedCompute.App.Services;

public sealed partial class SessionInputQueueStore
{
    internal event Action<SessionBindingSnapshot>? BindingCommitted;

    internal SessionBindingSnapshot[] ReadBindingSnapshots()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, ActiveGeneration, ThreadsJson FROM SessionThreadBindings";
        using var reader = command.ExecuteReader();
        var result = new List<SessionBindingSnapshot>();
        while (reader.Read()) result.Add(MapBindingSnapshot(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        return result.ToArray();
    }

    internal async Task<SessionBindingSnapshot?> ReadBindingSnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await ReadBindingSnapshotAsync(connection, null, sessionId, ct);
    }

    private static async Task GuardNodeMoveAdmissionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, QueuedBindingChange change, CancellationToken ct)
    {
        var snapshot = await ReadBindingSnapshotAsync(connection, transaction, sessionId, ct);
        if (change.ExpectedGeneration != (snapshot?.Active.Generation ?? 0))
            throw new SessionInputQueueStoreException("binding_changed", "The active node generation changed");
        if (change.ReplacesHeldBinding && snapshot?.Active.Native.Remote is null)
            throw new SessionInputQueueStoreException("binding_changed", "The session has no held remote binding");

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM SessionQueuedInputs WHERE SessionId=$session AND State IN ('Pending','Delivering','Failed')
                    AND ($redirect=0 OR json_extract(BindingChangeJson,'$.changesNode')=1
                        OR (State='Delivering' AND (COALESCE(LeaseOwner,'')!=$remote OR RemoteSendMayHaveStarted=1)))
            ) OR EXISTS(SELECT 1 FROM SessionTurns WHERE SessionId=$session AND EndedAt IS NULL)
            """;
        Add(check, "$session", sessionId);
        Add(check, "$redirect", change.ReplacesHeldBinding ? 1 : 0);
        Add(check, "$remote", ControllerRemoteLease);
        if (await check.ExecuteScalarAsync(ct) is 1L)
            throw new SessionInputQueueStoreException("input_queued", "An earlier input or turn must settle before moving the session");
    }

    private static async Task<bool> ExpectsRemoteInputAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM SessionThreadBindings WHERE SessionId=$session
                    AND json_extract(ThreadsJson,'$[#-1].native.remote.scope.nodeId') IS NOT NULL
            ) OR EXISTS(
                SELECT 1 FROM SessionQueuedInputs WHERE SessionId=$session AND State='Pending'
                    AND json_extract(BindingChangeJson,'$.changesNode')=1
                    AND json_extract(BindingChangeJson,'$.destination.runtime.runtimeId') IS NOT NULL
            )
            """;
        Add(command, "$session", sessionId);
        return await command.ExecuteScalarAsync(ct) is 1L;
    }

    internal async Task<IReadOnlyList<(string SessionId, SessionBinding Thread)>> ReadPendingNodeRetirementsAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT binding.SessionId, thread.value FROM SessionThreadBindings binding, json_each(binding.ThreadsJson) thread
            WHERE json_extract(thread.value,'$.retirementMoveId') IS NOT NULL
            """;
        var pending = new List<(string, SessionBinding)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (JsonSerializer.Deserialize<SessionBinding>(reader.GetString(1), JsonOptions) is { } thread)
                pending.Add((reader.GetString(0), thread));
        return pending;
    }

    internal async Task ConfirmNodeRetirementAsync(string sessionId, SessionBinding retired, CancellationToken ct)
    {
        await using var connection = Open();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await ReadBindingSnapshotAsync(connection, transaction, sessionId, ct);
        if (snapshot is null || snapshot.Active.Generation <= retired.Generation || !snapshot.Threads.Contains(retired)) return;
        var threads = snapshot.Threads.Select(thread => thread == retired ? thread with { RetirementMoveId = null } : thread);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE SessionThreadBindings SET ThreadsJson=$threads WHERE SessionId=$session";
        Add(update, "$threads", JsonSerializer.Serialize(threads, JsonOptions));
        Add(update, "$session", sessionId);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task<SessionInputQueueItem?> ReadPendingHeldBindingReplacementAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM SessionQueuedInputs WHERE SessionId=$id AND State='Pending'
                AND json_extract(BindingChangeJson,'$.replacesHeldBinding')=1 ORDER BY Sequence LIMIT 1
            """;
        Add(command, "$id", sessionId);
        return (await ReadItemsAsync(command, ct)).FirstOrDefault();
    }

    internal async Task<bool> IsBindingChangeReadyAsync(SessionInputQueueItem item, CancellationToken ct)
    {
        await using var connection = Open();
        return await IsBindingChangeReadyAsync(connection, null, item, ct);
    }

    // The provider runs outside this transaction. Its idempotent prepared identity and
    // final promotion are recorded against the exact durable queue row.
    internal async Task<bool> TryAdvanceBindingMoveAsync(SessionInputQueueItem observed, NativeSessionBinding initial,
        SessionBindingMove next, CancellationToken ct)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await GetCoreAsync(connection, transaction, observed.SessionId, observed.Id, observed.OwnerUserId, ct);
        if (current?.BindingChange is not { } request || current.BindingChange != observed.BindingChange
            || current.BindingMove != observed.BindingMove
            || !await IsBindingChangeReadyAsync(connection, transaction, current, ct))
            return false;
        if (next.Id != current.Id || next.SourceGeneration != request.ExpectedGeneration || next.Destination != request.Destination
            || !AllowedAdvance(current.BindingMove, next, request.ChangesNode))
            throw new InvalidOperationException("Binding transition does not match the admitted queue operation");

        var bindings = await ReadBindingSnapshotAsync(connection, transaction, current.SessionId, ct);
        if (bindings is null)
        {
            bindings = SessionBindingSnapshot.Initial(current.SessionId, initial);
            using var seed = connection.CreateCommand();
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO SessionThreadBindings (SessionId, ActiveGeneration, ThreadsJson) VALUES ($id, 0, $threads)";
            Add(seed, "$id", current.SessionId);
            Add(seed, "$threads", JsonSerializer.Serialize(bindings.Threads, JsonOptions));
            await seed.ExecuteNonQueryAsync(ct);
        }

        if (next.State != BindingMoveState.Rejected && bindings.Active.Generation != request.ExpectedGeneration)
            return false;
        string? receipt = null;
        SessionBindingSnapshot? published = null;
        if (next.State == BindingMoveState.Committed)
        {
            var active = bindings.Active;
            var native = next.Prepared!;
            var newBinding = new SessionBinding(active.Generation + 1, native, next.HistoryThrough);
            if (next.CommittedGeneration != newBinding.Generation || newBinding.HistoryThrough < active.HistoryThrough)
                throw new InvalidOperationException("Binding commit does not advance the active generation and history");
            var retired = active with
            {
                HistoryThrough = newBinding.HistoryThrough,
                RetirementMoveId = request.ReplacesHeldBinding ? current.Id : null,
            };
            var threads = bindings.Threads.Take(bindings.Threads.Count - 1).Append(retired).Append(newBinding).ToArray();
            using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = """
                UPDATE SessionThreadBindings SET ActiveGeneration=$generation, ThreadsJson=$threads
                WHERE SessionId=$id AND ActiveGeneration=$source
                """;
            Add(promote, "$id", current.SessionId);
            Add(promote, "$source", active.Generation);
            Add(promote, "$generation", newBinding.Generation);
            Add(promote, "$threads", JsonSerializer.Serialize(threads, JsonOptions));
            if (await promote.ExecuteNonQueryAsync(ct) != 1) return false;
            if (request.ReplacesHeldBinding)
            {
                await using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = """
                    UPDATE SessionQueuedInputs
                    SET State='Pending', LeaseOwner=NULL, LeaseExpiresAt=NULL, NextAttemptAt=NULL,
                        ErrorCode=NULL, ErrorMessage=NULL, ErrorRetryable=0
                    WHERE SessionId=$session AND State='Delivering' AND LeaseOwner=$remote AND RemoteSendMayHaveStarted=0
                    """;
                Add(release, "$session", current.SessionId);
                Add(release, "$remote", ControllerRemoteLease);
                await release.ExecuteNonQueryAsync(ct);
            }
            receipt = JsonSerializer.Serialize(new { applied = newBinding }, JsonOptions);
            published = new(current.SessionId, threads);
        }

        var terminal = next.State is BindingMoveState.Committed or BindingMoveState.Rejected;
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE SessionQueuedInputs
            SET BindingMoveJson=$move, State=$state, UpdatedAt=$now, CompletedAt=$completed,
                MetadataJson=$metadata, ErrorCode=$error, ErrorMessage=$error, ErrorRetryable=0,
                AttemptCount=AttemptCount+$attempt, NextAttemptAt=NULL
            WHERE Id=$id AND State='Pending' AND BindingChangeJson IS NOT NULL
            """;
        Add(update, "$id", current.Id);
        Add(update, "$move", JsonSerializer.Serialize(next, JsonOptions));
        Add(update, "$state", next.State switch
        {
            BindingMoveState.Committed => "Delivered",
            BindingMoveState.Rejected when request.ChangesNode => "Cancelled",
            BindingMoveState.Rejected => "Failed",
            _ => "Pending",
        });
        Add(update, "$now", now);
        AddNullable(update, "$completed", next.State == BindingMoveState.Committed
            || next.State == BindingMoveState.Rejected && request.ChangesNode ? now : null);
        AddNullable(update, "$metadata", receipt);
        AddNullable(update, "$error", next.Error);
        Add(update, "$attempt", terminal ? 1 : 0);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return false;

        await transaction.CommitAsync(ct);
        if (published is not null) BindingCommitted?.Invoke(published);
        return true;
    }

    private static bool AllowedAdvance(SessionBindingMove? current, SessionBindingMove next, bool changesNode)
        => (current?.State, next.State) switch
        {
            (null, BindingMoveState.Pending) => next.Prepared is null,
            (null or BindingMoveState.Pending or BindingMoveState.Prepared or BindingMoveState.SealPending,
                BindingMoveState.Rejected) => next.Error is not null,
            (BindingMoveState.Pending, BindingMoveState.Prepared) => next.Prepared is not null
                && next.Prepared.Target == next.Destination && current!.HistoryThrough == next.HistoryThrough,
            (BindingMoveState.Prepared, BindingMoveState.SealPending) when changesNode => SamePreparation(current!, next),
            (BindingMoveState.Prepared, BindingMoveState.Committed) when !changesNode => SamePreparation(current!, next),
            (BindingMoveState.SealPending, BindingMoveState.Committed) when changesNode => SamePreparation(current!, next),
            _ => false,
        };

    private static bool SamePreparation(SessionBindingMove current, SessionBindingMove next)
        => current.Prepared is not null && current.Prepared == next.Prepared && current.HistoryThrough == next.HistoryThrough;

    private static async Task<bool> IsBindingChangeReadyAsync(SqliteConnection connection, SqliteTransaction? transaction,
        SessionInputQueueItem item, CancellationToken ct)
    {
        if (item.BindingChange is null || item.State != SessionInputQueueState.Pending
            || IsDeferred(item.NextAttemptAt, DateTimeOffset.UtcNow)) return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM SessionQueuedInputs entry
                WHERE entry.Id=$item AND entry.SessionId=$id AND entry.State='Pending'
                    AND (
                        ($redirect=0 AND NOT EXISTS (
                            SELECT 1 FROM SessionQueuedInputs earlier
                            WHERE earlier.SessionId=$id AND earlier.Sequence < entry.Sequence
                                AND earlier.State IN ('Pending','Delivering','Failed')))
                        OR ($redirect=1 AND NOT EXISTS (
                            SELECT 1 FROM SessionQueuedInputs issued
                            WHERE issued.SessionId=$id AND issued.State='Delivering'
                                AND (COALESCE(issued.LeaseOwner,'')!=$remote OR issued.RemoteSendMayHaveStarted=1))
                            AND EXISTS (
                                SELECT 1 FROM SessionThreadBindings binding
                                WHERE binding.SessionId=$id AND binding.ActiveGeneration=$generation
                                    AND json_extract(binding.ThreadsJson,'$[#-1].native.remote.scope.nodeId') IS NOT NULL))
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM SessionTurns WHERE SessionId=$id AND EndedAt IS NULL))
            """;
        Add(command, "$item", item.Id);
        Add(command, "$id", item.SessionId);
        Add(command, "$redirect", item.BindingChange.ReplacesHeldBinding ? 1 : 0);
        Add(command, "$generation", item.BindingChange.ExpectedGeneration);
        Add(command, "$remote", ControllerRemoteLease);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static async Task<SessionBindingSnapshot?> ReadBindingSnapshotAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string sessionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ActiveGeneration, ThreadsJson FROM SessionThreadBindings WHERE SessionId=$id";
        Add(command, "$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapBindingSnapshot(sessionId, reader.GetInt64(0), reader.GetString(1));
    }

    private static SessionBindingSnapshot MapBindingSnapshot(string sessionId, long generation, string json)
    {
        var threads = JsonSerializer.Deserialize<SessionBinding[]>(json, JsonOptions);
        if (threads is not { Length: > 0 } || threads[^1].Generation != generation)
            throw new InvalidDataException($"Binding history does not contain the active generation for session {sessionId}");
        return new(sessionId, threads);
    }
}
