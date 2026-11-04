using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

internal readonly record struct TranscriptStamp(string Epoch, long Sequence);

/// <summary>
/// What a reader needs to project durable records into the session's current epoch: the epoch, the
/// rotation point a snapshot may claim as covered, and the sequence ranges that were rolled back.
/// A rollback changes the epoch and never the sequence, so sequences stay ordered across epochs.
/// </summary>
internal sealed record TranscriptEpochView(
    string Epoch,
    long RotationSequence,
    IReadOnlyList<(long From, long Through)> RemovedRanges)
{
    public bool IsRemoved(long? sequence) =>
        sequence is { } value && RemovedRanges.Any(range => value >= range.From && value <= range.Through);
}

internal interface ITranscriptSequenceStore
{
    TranscriptStamp Next(string provider, string sessionId);

    /// <summary>Null until the session's first rollback: its epoch is then still the derived one.</summary>
    TranscriptEpochView? CurrentView(string provider, string sessionId) => null;

    /// <summary>
    /// Moves the session to the epoch owned by this rollback, once. Consumes one sequence for the
    /// rotation point and removes [<paramref name="removedFromSequence"/>, that point]. A repeat of
    /// the same rollback id returns the stored stamp and changes nothing.
    /// </summary>
    TranscriptStamp Rotate(string provider, string sessionId, string rollbackId, long? removedFromSequence) =>
        throw new NotSupportedException();
}

/// <summary>
/// Durable allocator for transcript transport order. One small SQLite update
/// happens before each event leaves the central transcript pipeline, so a
/// restart can skip sequence values but can never reuse them.
/// </summary>
internal sealed class TranscriptSequenceStore : ITranscriptSequenceStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public TranscriptSequenceStore(string? databasePath = null)
    {
        var usePooling = databasePath is null;
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "redcompute.db");
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = usePooling,
            DefaultTimeout = 5,
        }.ToString();
        EnsureSchema();
    }

    public TranscriptEpochView? CurrentView(string provider, string sessionId)
    {
        lock (_gate)
        {
            var key = $"{provider}\n{sessionId}";
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Epoch, RemovedFromSequence, RotationSequence FROM TranscriptRotations
                WHERE SessionKey = $key ORDER BY RotationSequence
                """;
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            string? epoch = null;
            long rotation = 0;
            var ranges = new List<(long From, long Through)>();
            while (reader.Read())
            {
                epoch = reader.GetString(0);
                rotation = reader.GetInt64(2);
                if (!reader.IsDBNull(1)) ranges.Add((reader.GetInt64(1), rotation));
            }
            return epoch is null ? null : new TranscriptEpochView(epoch, rotation, ranges);
        }
    }

    public TranscriptStamp Rotate(string provider, string sessionId, string rollbackId, long? removedFromSequence)
    {
        lock (_gate)
        {
            var key = $"{provider}\n{sessionId}";
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT Epoch, RotationSequence FROM TranscriptRotations
                    WHERE SessionKey = $key AND RollbackId = $rollback
                    """;
                existing.Parameters.AddWithValue("$key", key);
                existing.Parameters.AddWithValue("$rollback", rollbackId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                    return new TranscriptStamp(reader.GetString(0), reader.GetInt64(1));
            }

            var epoch = TranscriptOrdering.RotatedEpoch(provider, sessionId, rollbackId);
            long sequence;
            using (var advance = connection.CreateCommand())
            {
                advance.Transaction = transaction;
                // The sequence is never reset: it orders kept records and new ones in one line.
                advance.CommandText = """
                    INSERT INTO TranscriptCursors (SessionKey, Epoch, LastSequence)
                    VALUES ($key, $epoch, 1)
                    ON CONFLICT(SessionKey) DO UPDATE SET
                        Epoch = excluded.Epoch,
                        LastSequence = TranscriptCursors.LastSequence + 1
                    RETURNING LastSequence;
                    """;
                advance.Parameters.AddWithValue("$key", key);
                advance.Parameters.AddWithValue("$epoch", epoch);
                sequence = (long)advance.ExecuteScalar()!;
            }
            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO TranscriptRotations (SessionKey, RollbackId, Epoch, RemovedFromSequence, RotationSequence)
                    VALUES ($key, $rollback, $epoch, $from, $sequence);
                    """;
                record.Parameters.AddWithValue("$key", key);
                record.Parameters.AddWithValue("$rollback", rollbackId);
                record.Parameters.AddWithValue("$epoch", epoch);
                record.Parameters.AddWithValue("$from", (object?)removedFromSequence ?? DBNull.Value);
                record.Parameters.AddWithValue("$sequence", sequence);
                record.ExecuteNonQuery();
            }
            transaction.Commit();
            return new TranscriptStamp(epoch, sequence);
        }
    }

    public TranscriptStamp Next(string provider, string sessionId)
    {
        lock (_gate)
        {
            var key = $"{provider}\n{sessionId}";
            var epoch = TranscriptOrdering.Epoch(provider, sessionId);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TranscriptCursors (SessionKey, Epoch, LastSequence)
                VALUES ($key, $epoch, 1)
                ON CONFLICT(SessionKey) DO UPDATE SET
                    LastSequence = TranscriptCursors.LastSequence + 1
                RETURNING Epoch, LastSequence;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$epoch", epoch);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Transcript sequence allocation returned no row");
            return new TranscriptStamp(reader.GetString(0), reader.GetInt64(1));
        }
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TranscriptCursors (
                SessionKey TEXT PRIMARY KEY,
                Epoch TEXT NOT NULL,
                LastSequence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TranscriptRotations (
                SessionKey TEXT NOT NULL,
                RollbackId TEXT NOT NULL,
                Epoch TEXT NOT NULL,
                RemovedFromSequence INTEGER NULL,
                RotationSequence INTEGER NOT NULL,
                PRIMARY KEY (SessionKey, RollbackId)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

internal static class TranscriptOrdering
{
    public static string Epoch(string provider, string sessionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"v1\n{provider}\n{sessionId}"));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

    /// <summary>The epoch a rollback moves its session to, stable across a retried rollback.</summary>
    public static string RotatedEpoch(string provider, string sessionId, string rollbackId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"v2\n{provider}\n{sessionId}\n{rollbackId}"));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }

    /// <summary>
    /// A rolled-back session's snapshot: visible records are projected into the current epoch, and
    /// the rotation point is covered because no live event at or below it exists in that epoch.
    /// </summary>
    public static TranscriptCursor Cursor(
        TranscriptEpochView view,
        IReadOnlyList<UnifiedMessageRecord> history) => new()
    {
        Epoch = view.Epoch,
        ThroughSequence = Math.Max(view.RotationSequence, history
            .Where(message => message.Sequence.HasValue && message.Epoch == view.Epoch)
            .Select(message => message.Sequence!.Value)
            .DefaultIfEmpty(0)
            .Max()),
    };

    public static TranscriptCursor Cursor(
        string provider,
        string sessionId,
        IReadOnlyList<UnifiedMessageRecord> history)
    {
        var ordered = history.Where(message => message.Sequence.HasValue).ToList();
        var epoch = ordered.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Epoch))?.Epoch
            ?? Epoch(provider, sessionId);
        var through = ordered
            .Where(message => string.Equals(message.Epoch, epoch, StringComparison.Ordinal))
            .Select(message => message.Sequence!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return new TranscriptCursor { Epoch = epoch, ThroughSequence = through };
    }
}
