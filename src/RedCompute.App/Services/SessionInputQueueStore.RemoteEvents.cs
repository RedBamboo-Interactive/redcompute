using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RedCompute.NodeLink;

namespace RedCompute.App.Services;

internal sealed record RemoteSessionEvent(string EventId, string? TurnId, string? InputUid, byte[] Body);
internal sealed record RemoteEventAppend(long Sequence);
internal sealed record SequencedRemoteSessionEvent(long Sequence, RemoteSessionEvent Event);
internal sealed record RemoteSessionEventPage(long AcknowledgedThrough, long LastSequence, bool Gap,
    IReadOnlyList<SequencedRemoteSessionEvent> Events);

public sealed partial class SessionInputQueueStore
{
    internal const int MaxRemoteEventBytes = 256 * 1024;
    internal const int MaxRemoteUnacknowledgedEventBytes = 4 * 1024 * 1024;
    private const int MaxRemoteReplayPageBytes = 1024 * 1024;
    private const int MaxRemoteUnacknowledgedEvents = 4096;
    private const string RemoteEventBinding = "NodeId=$node AND RuntimeId=$runtime AND LogicalSessionId=$logical AND Generation=$generation";

    internal async Task<RemoteEventAppend> AppendRemoteEventAsync(SessionRuntimeScope scope, RemoteSessionEvent item,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || item.EventId.Length > 200
            || item.TurnId?.Length > 200 || item.InputUid?.Length > 200 || item.Body.Length is <= 0 or > MaxRemoteEventBytes)
            throw new SessionInputQueueStoreException("invalid_remote_event", "The remote event exceeds its bounds");

        var digest = Convert.ToHexString(SHA256.HashData(item.Body));
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var position = await ReadRemoteEventPositionAsync(connection, transaction, scope, ct);
        if (position is null)
            throw new SessionInputQueueStoreException("remote_event_scope_mismatch", "The remote event binding belongs to another scope");

        await using (var command = RemoteEventCommand(connection, transaction, scope,
            "SELECT Sequence, TurnId, InputUid, PayloadDigest FROM RemoteSessionEvents WHERE " + RemoteEventBinding + " AND EventId=$event"))
        {
            Add(command, "$event", item.EventId);
            await using var row = await command.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                if (ReadNullableString(row, 1) != item.TurnId || ReadNullableString(row, 2) != item.InputUid || row.GetString(3) != digest)
                    throw new SessionInputQueueStoreException("remote_event_conflict", "The event ID was already used for a different record");
                return new(row.GetInt64(0));
            }
        }

        await using (var count = RemoteEventCommand(connection, transaction, scope,
            "SELECT COUNT(*), COALESCE(SUM(length(Body)),0) FROM RemoteSessionEvents WHERE " + RemoteEventBinding + " AND Sequence>$ack"))
        {
            Add(count, "$ack", position.Value.Ack);
            await using var totals = await count.ExecuteReaderAsync(ct);
            await totals.ReadAsync(ct);
            if (totals.GetInt64(0) >= MaxRemoteUnacknowledgedEvents
                || totals.GetInt64(1) + item.Body.Length > MaxRemoteUnacknowledgedEventBytes)
                throw new SessionInputQueueStoreException("remote_event_backpressure", "The unacknowledged remote event spool is full");
        }

        var seq = checked(position.Value.Last + 1);
        await using var insert = RemoteEventCommand(connection, transaction, scope, """
            INSERT INTO RemoteSessionEvents
                (NodeId, RuntimeId, LogicalSessionId, Generation, OwnerId, AccessScope, Sequence, EventId, TurnId, InputUid, PayloadDigest, Body)
            VALUES ($node,$runtime,$logical,$generation,$owner,$access,$seq,$event,$turn,$input,$digest,$body)
            """);
        Add(insert, "$owner", scope.OwnerId);
        Add(insert, "$access", scope.AccessScope);
        Add(insert, "$seq", seq);
        Add(insert, "$event", item.EventId);
        AddNullable(insert, "$turn", item.TurnId);
        AddNullable(insert, "$input", item.InputUid);
        Add(insert, "$digest", digest);
        Add(insert, "$body", item.Body);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(seq);
    }

    internal async Task<RemoteSessionEventPage> ReadRemoteEventsAsync(SessionRuntimeScope scope, long after, int limit,
        CancellationToken ct = default)
    {
        if (after < 0 || limit is < 1 or > 128) throw new ArgumentOutOfRangeException(after < 0 ? nameof(after) : nameof(limit));
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var position = await ReadRemoteEventPositionAsync(connection, transaction, scope, ct);
        if (position is null) return new(0, 0, after != 0, []);
        var (ack, last) = position.Value;
        if (after < ack || after > last) return new(ack, last, true, []);

        await using var command = RemoteEventCommand(connection, transaction, scope,
            "SELECT Sequence, EventId, TurnId, InputUid, Body FROM RemoteSessionEvents WHERE " + RemoteEventBinding
            + " AND Sequence>$after ORDER BY Sequence LIMIT $limit");
        Add(command, "$after", after);
        Add(command, "$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<SequencedRemoteSessionEvent>();
        var bytes = 0;
        while (await reader.ReadAsync(ct))
        {
            var body = reader.GetFieldValue<byte[]>(4);
            bytes += body.Length;
            if (bytes > MaxRemoteReplayPageBytes) break;
            events.Add(new(reader.GetInt64(0), new(reader.GetString(1), ReadNullableString(reader, 2), ReadNullableString(reader, 3), body)));
        }
        return new(ack, last, false, events);
    }

    internal async Task<long?> AcknowledgeRemoteEventsAsync(SessionRuntimeScope scope, long through,
        CancellationToken ct = default)
    {
        if (through < 0) return null;
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var position = await ReadRemoteEventPositionAsync(connection, transaction, scope, ct);
        if (position is null || through > position.Value.Last) return null;
        if (through <= position.Value.Ack) return position.Value.Ack;
        await using var command = RemoteEventCommand(connection, transaction, scope,
            "UPDATE RemoteSessionEvents SET Body=NULL WHERE " + RemoteEventBinding + " AND Sequence>$ack AND Sequence<=$through AND Body IS NOT NULL");
        Add(command, "$ack", position.Value.Ack);
        Add(command, "$through", through);
        if (await command.ExecuteNonQueryAsync(ct) != through - position.Value.Ack) return null;
        await transaction.CommitAsync(ct);
        return through;
    }

    private static async Task<(long Ack, long Last)?> ReadRemoteEventPositionAsync(SqliteConnection connection,
        SqliteTransaction transaction, SessionRuntimeScope scope, CancellationToken ct)
    {
        long last;
        await using (var command = RemoteEventCommand(connection, transaction, scope,
            "SELECT OwnerId, AccessScope, Sequence FROM RemoteSessionEvents WHERE " + RemoteEventBinding + " ORDER BY Sequence DESC LIMIT 1"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return (0, 0);
            if (reader.GetString(0) != scope.OwnerId || reader.GetString(1) != scope.AccessScope) return null;
            last = reader.GetInt64(2);
        }

        await using var ack = RemoteEventCommand(connection, transaction, scope,
            "SELECT COALESCE(MAX(Sequence),0) FROM RemoteSessionEvents WHERE " + RemoteEventBinding + " AND Body IS NULL");
        return (Convert.ToInt64(await ack.ExecuteScalarAsync(ct)), last);
    }

    private static SqliteCommand RemoteEventCommand(SqliteConnection connection, SqliteTransaction transaction,
        SessionRuntimeScope scope, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        BindRemoteSessionKey(command, scope);
        return command;
    }

    private static string? ReadNullableString(SqliteDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);
}
