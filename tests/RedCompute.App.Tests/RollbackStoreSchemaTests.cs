using Microsoft.Data.Sqlite;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.App.Tests;

// The plugin databases as an upgrade finds them: rows from before rollback existed, then the columns
// arrive, then a rollback marks rows that must leave every read and stay on disk.
[Collection("Plugin database path")]
public sealed class RollbackStoreSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-rollback-schema", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;

    public RollbackStoreSchemaTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Claude_store_upgrades_and_marks_without_deleting()
    {
        var path = Path.Combine(_root, "claude.db");
        ClaudeDbContext.DbPath = path;
        using (var db = new ClaudeDbContext()) db.Initialize();
        Execute(path, "ALTER TABLE Messages DROP COLUMN RolledBack", "ALTER TABLE Messages DROP COLUMN PromptUuid",
            "ALTER TABLE Sessions DROP COLUMN ForkTargetJson");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new ClaudeDbContext()) db.Initialize(); // Idempotent.
        var store = new ClaudeSessionStore();
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "s", ProjectName = "T", ProjectPath = ".", Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        foreach (var (uid, role) in new[] { ("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant") })
            store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = "s", Role = role, EventType = "text", Content = uid, MessageUid = uid,
                PromptUuid = role == "user" ? "P" + uid : null, Timestamp = DateTimeOffset.UtcNow,
            });

        Assert.Null(store.GetForkTarget("s"));
        store.SetForkTarget("s", "{\"RollbackId\":\"r\"}");
        Assert.Equal("{\"RollbackId\":\"r\"}", store.GetForkTarget("s"));
        Assert.Equal("Pu2", store.GetMessages("s").Single(m => m.MessageUid == "u2").PromptUuid);

        store.MarkMessagesRolledBack("s", "u2");

        Assert.Equal(["u1", "a1"], store.GetMessages("s").Select(m => m.MessageUid));
        Assert.Equal(4L, Count(path, "SELECT COUNT(*) FROM Messages"));
        Assert.Equal(2L, Count(path, "SELECT COUNT(*) FROM Messages WHERE RolledBack = 1"));

        store.SetForkTarget("s", null);
        Assert.Null(store.GetForkTarget("s"));
    }

    [Fact]
    public void Codex_store_upgrades_maps_turns_and_marks_without_deleting()
    {
        var path = Path.Combine(_root, "codex.db");
        CodexDbContext.DbPath = path;
        using (var db = new CodexDbContext()) db.Initialize();
        Execute(path, "ALTER TABLE Messages DROP COLUMN RolledBack", "ALTER TABLE Messages DROP COLUMN ProviderTurnId",
            "ALTER TABLE Sessions DROP COLUMN HistoryMode");
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        var store = new CodexSessionStore();
        store.SaveSession(new CodexSessionRecord
        {
            Id = "s", ProjectName = "T", ProjectPath = ".", Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
            HistoryMode = "paginated",
        });
        foreach (var (uid, role) in new[] { ("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant") })
            store.AddMessage(new CodexMessageRecord
            {
                SessionId = "s", Role = role, EventType = "text", Content = uid, MessageUid = uid,
                Timestamp = DateTimeOffset.UtcNow,
            });

        Assert.Equal("paginated", store.FindSession("s")!.HistoryMode);
        Assert.Null(store.GetUserTurnId("s", "u2"));
        store.SetUserTurnId("s", "u2", "turn-2");
        Assert.Equal("turn-2", store.GetUserTurnId("s", "u2"));

        store.MarkMessagesRolledBack("s", "u2");

        Assert.Equal(["u1", "a1"], store.GetMessages("s").Select(m => m.MessageUid));
        Assert.Equal(4L, Count(path, "SELECT COUNT(*) FROM Messages"));
        // A repeated rollback must still find the turn behind the mark.
        Assert.Equal("turn-2", store.GetUserTurnId("s", "u2"));
    }

    private static void Execute(string path, params string[] statements)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private static long Count(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        ClaudeDbContext.DbPath = _claudePath;
        CodexDbContext.DbPath = _codexPath;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }
}
