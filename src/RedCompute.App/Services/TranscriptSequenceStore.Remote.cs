using Microsoft.Data.Sqlite;

namespace RedCompute.App.Services;

internal sealed partial class TranscriptSequenceStore
{
    private const string RemoteBindingWhere = "SessionKey=$key AND NodeId=$node AND RuntimeId=$runtime AND Generation=$generation";

    internal RemoteTranscriptReservation ReserveRemote(string provider, string sessionId, RemoteTranscriptBinding binding,
        RemoteTranscriptRecord record)
    {
        Validate(binding, record);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var key = $"{provider}\n{sessionId}";
            if (ReadRemoteReservation(connection, transaction, key, binding, record) is { } pending)
                return pending;
            if (ReadRemotePersistedThrough(connection, transaction, key, binding) != record.Sequence - 1)
                throw new InvalidOperationException("The next remote event must follow the persisted prefix");

            var stamp = Advance(connection, transaction, provider, sessionId);
            using var insert = RemoteCommand(connection, transaction, key, binding, """
                INSERT INTO TranscriptRemoteRecords
                    (SessionKey, NodeId, RuntimeId, Generation, EventSequence, EventId, PayloadDigest, Epoch, CanonicalSequence)
                VALUES ($key,$node,$runtime,$generation,$seq,$event,$digest,$epoch,$canonical)
                """);
            insert.Parameters.AddWithValue("$seq", record.Sequence);
            insert.Parameters.AddWithValue("$event", record.EventId);
            insert.Parameters.AddWithValue("$digest", record.PayloadDigest);
            insert.Parameters.AddWithValue("$epoch", stamp.Epoch);
            insert.Parameters.AddWithValue("$canonical", stamp.Sequence);
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(stamp, false);
        }
    }

    internal bool CompleteRemote(string provider, string sessionId, RemoteTranscriptBinding binding,
        RemoteTranscriptRecord record, TranscriptStamp stamp)
    {
        Validate(binding, record);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var key = $"{provider}\n{sessionId}";
            var reservation = ReadRemoteReservation(connection, transaction, key, binding, record);
            if (reservation is null || reservation.Value.Stamp != stamp)
                throw new InvalidOperationException("The canonical transcript receipt does not match its reservation");
            if (reservation.Value.Persisted) return false;

            using var update = RemoteCommand(connection, transaction, key, binding,
                "UPDATE TranscriptRemoteRecords SET Persisted=1 WHERE " + RemoteBindingWhere + " AND EventSequence=$seq");
            update.Parameters.AddWithValue("$seq", record.Sequence);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The remote transcript reservation disappeared");
            transaction.Commit();
            return true;
        }
    }

    internal long RemotePersistedThrough(string provider, string sessionId, RemoteTranscriptBinding binding)
    {
        using var connection = Open();
        return ReadRemotePersistedThrough(connection, null, $"{provider}\n{sessionId}", binding);
    }

    private static RemoteTranscriptReservation? ReadRemoteReservation(SqliteConnection connection, SqliteTransaction transaction,
        string key, RemoteTranscriptBinding binding, RemoteTranscriptRecord record)
    {
        using var command = RemoteCommand(connection, transaction, key, binding,
            "SELECT EventSequence, EventId, PayloadDigest, Epoch, CanonicalSequence, Persisted FROM TranscriptRemoteRecords WHERE "
            + RemoteBindingWhere + " AND (EventSequence=$seq OR EventId=$event)");
        command.Parameters.AddWithValue("$seq", record.Sequence);
        command.Parameters.AddWithValue("$event", record.EventId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(0) != record.Sequence || reader.GetString(1) != record.EventId || reader.GetString(2) != record.PayloadDigest)
            throw new InvalidOperationException("A remote transcript event was replayed with different identity or payload");
        return new(new(reader.GetString(3), reader.GetInt64(4)), reader.GetBoolean(5));
    }

    private static long ReadRemotePersistedThrough(SqliteConnection connection, SqliteTransaction? transaction, string key,
        RemoteTranscriptBinding binding)
    {
        using var command = RemoteCommand(connection, transaction, key, binding,
            "SELECT COALESCE(MAX(EventSequence),0) FROM TranscriptRemoteRecords WHERE " + RemoteBindingWhere + " AND Persisted=1");
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand RemoteCommand(SqliteConnection connection, SqliteTransaction? transaction, string key,
        RemoteTranscriptBinding binding, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$node", binding.NodeId);
        command.Parameters.AddWithValue("$runtime", binding.RuntimeId);
        command.Parameters.AddWithValue("$generation", binding.Generation);
        return command;
    }

    private static void Validate(RemoteTranscriptBinding binding, RemoteTranscriptRecord record)
    {
        if (string.IsNullOrWhiteSpace(binding.NodeId) || string.IsNullOrWhiteSpace(binding.RuntimeId) || binding.Generation < 0
            || record.Sequence <= 0 || string.IsNullOrWhiteSpace(record.EventId) || record.EventId.Length > 200
            || record.PayloadDigest is not { Length: 64 } || !record.PayloadDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid remote transcript event");
    }
}
