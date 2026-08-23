using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

internal readonly record struct TranscriptStamp(string Epoch, long Sequence);

internal interface ITranscriptSequenceStore
{
    TranscriptStamp Next(string provider, string sessionId);
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
