using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace RedCompute.App.Services.SessionBindings;

internal sealed record BindingTarget(string ProviderId, string ProviderInstanceId, string NodeId, string Model, string? Effort);

internal sealed record NativeSessionBinding(string RuntimeSessionId, string NativeThreadId, BindingTarget Target);

internal sealed record SessionBinding(long Generation, NativeSessionBinding Native, long HistoryThrough);

internal enum BindingMoveState { Pending, Prepared, Committed, Rejected }

internal sealed record SessionBindingMove(string Id, long SourceGeneration, BindingTarget Destination, long HistoryThrough,
    BindingMoveState State, NativeSessionBinding? Prepared = null, long? CommittedGeneration = null, string? Error = null);

internal sealed record SessionBindingSnapshot(string LogicalId, long Revision, IReadOnlyList<SessionBinding> Threads,
    IReadOnlyList<SessionBindingMove> Moves)
{
    [JsonIgnore]
    public SessionBinding Active => Threads[^1];
}

/// <summary>One atomic current pointer and binding history per logical session, without transcript content or credentials.</summary>
internal sealed class SessionBindingJournal
{
    private readonly string _connectionString;

    public SessionBindingJournal(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionBindingJournal (
                LogicalId TEXT PRIMARY KEY NOT NULL,
                Revision INTEGER NOT NULL,
                Document TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    public SessionBindingSnapshot OpenOrAttach(string logicalId, NativeSessionBinding initial)
    {
        var existing = Read(logicalId);
        if (existing is not null) return existing;
        var first = new SessionBindingSnapshot(logicalId, 0, [new(0, initial, 0)], []);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO SessionBindingJournal (LogicalId, Revision, Document) VALUES ($id, 0, $document)";
        command.Parameters.AddWithValue("$id", logicalId);
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(first));
        return command.ExecuteNonQuery() == 1 ? first : Read(logicalId)!;
    }

    public SessionBindingSnapshot? Read(string logicalId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Document FROM SessionBindingJournal WHERE LogicalId = $id";
        command.Parameters.AddWithValue("$id", logicalId);
        return command.ExecuteScalar() is string document
            ? JsonSerializer.Deserialize<SessionBindingSnapshot>(document)
                ?? throw new InvalidDataException($"Binding journal could not read session {logicalId}")
            : null;
    }

    public bool TrySave(SessionBindingSnapshot previous, SessionBindingSnapshot next)
    {
        if (previous.LogicalId != next.LogicalId || next.Revision != previous.Revision + 1)
            throw new ArgumentException("A binding write must advance one revision of the same session.", nameof(next));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE SessionBindingJournal SET Revision = $next, Document = $document
            WHERE LogicalId = $id AND Revision = $previous
            """;
        command.Parameters.AddWithValue("$id", next.LogicalId);
        command.Parameters.AddWithValue("$previous", previous.Revision);
        command.Parameters.AddWithValue("$next", next.Revision);
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(next));
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }
}
