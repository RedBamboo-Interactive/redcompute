using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.App.Services.SessionBindings;

namespace RedCompute.App.Services;

public sealed partial class SessionInputQueueStore
{
    internal async Task<SessionBindingSnapshot?> ReadBindingSnapshotAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await ReadBindingSnapshotAsync(connection, null, sessionId, ct);
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
            || !AllowedAdvance(current.BindingMove, next))
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
        if (next.State == BindingMoveState.Committed)
        {
            var active = bindings.Active;
            var native = next.Prepared!;
            var newBinding = new SessionBinding(active.Generation + 1, native, next.HistoryThrough);
            if (next.CommittedGeneration != newBinding.Generation || newBinding.HistoryThrough < active.HistoryThrough)
                throw new InvalidOperationException("Binding commit does not advance the active generation and history");
            var threads = bindings.Threads.Take(bindings.Threads.Count - 1)
                .Append(active with { HistoryThrough = newBinding.HistoryThrough }).Append(newBinding).ToArray();
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
            receipt = JsonSerializer.Serialize(new { applied = newBinding }, JsonOptions);
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
            BindingMoveState.Rejected => "Failed",
            _ => "Pending",
        });
        Add(update, "$now", now);
        AddNullable(update, "$completed", next.State == BindingMoveState.Committed ? now : null);
        AddNullable(update, "$metadata", receipt);
        AddNullable(update, "$error", next.Error);
        Add(update, "$attempt", terminal ? 1 : 0);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return false;

        await transaction.CommitAsync(ct);
        return true;
    }

    private static bool AllowedAdvance(SessionBindingMove? current, SessionBindingMove next)
        => (current?.State, next.State) switch
        {
            (null, BindingMoveState.Pending) => next.Prepared is null,
            (null or BindingMoveState.Pending or BindingMoveState.Prepared, BindingMoveState.Rejected) => next.Error is not null,
            (BindingMoveState.Pending, BindingMoveState.Prepared) => next.Prepared is not null
                && next.Prepared.Target == next.Destination && current!.HistoryThrough == next.HistoryThrough,
            (BindingMoveState.Prepared, BindingMoveState.Committed) => current!.Prepared is not null
                && current.Prepared == next.Prepared && current.HistoryThrough == next.HistoryThrough,
            _ => false,
        };

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
                    AND NOT EXISTS (
                        SELECT 1 FROM SessionQueuedInputs earlier
                        WHERE earlier.SessionId=$id AND earlier.Sequence < entry.Sequence
                            AND earlier.State IN ('Pending','Delivering','Failed'))
                    AND NOT EXISTS (
                        SELECT 1 FROM SessionTurns WHERE SessionId=$id AND EndedAt IS NULL))
            """;
        Add(command, "$item", item.Id);
        Add(command, "$id", item.SessionId);
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
        var threads = JsonSerializer.Deserialize<SessionBinding[]>(reader.GetString(1), JsonOptions);
        if (threads is not { Length: > 0 } || threads[^1].Generation != reader.GetInt64(0))
            throw new InvalidDataException($"Binding history does not contain the active generation for session {sessionId}");
        return new(sessionId, threads);
    }
}
