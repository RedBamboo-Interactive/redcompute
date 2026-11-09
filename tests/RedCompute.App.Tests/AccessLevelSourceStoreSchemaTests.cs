using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.App.Tests;

// accessLevelSource as an upgrade finds the plugin databases: rows written before the column read
// as null (policy), and an explicit source survives both an insert and an update.
[Collection("Plugin database path")]
public sealed class AccessLevelSourceStoreSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-access-source-schema", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;

    public AccessLevelSourceStoreSchemaTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Claude_store_adds_the_source_column_and_keeps_it()
    {
        var path = Path.Combine(_root, "claude.db");
        ClaudeDbContext.DbPath = path;
        using (var db = new ClaudeDbContext()) db.Initialize();
        // The 14:35 relaunch's row: a level resolved from policy, persisted before sources existed.
        new ClaudeSessionStore().SaveSession(new ClaudeSessionRecord
        {
            Id = "old", ProjectName = "T", ProjectPath = ".", Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
            AccessLevel = SessionAccessLevels.Ask,
        });
        Execute(path, "ALTER TABLE Sessions DROP COLUMN AccessLevelSource");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new ClaudeDbContext()) db.Initialize(); // Idempotent.
        var store = new ClaudeSessionStore();

        Assert.Equal(SessionAccessLevels.Ask, store.FindSession("old")!.AccessLevel);
        Assert.Null(store.FindSession("old")!.AccessLevelSource);

        var record = store.FindSession("old")!;
        record.AccessLevelSource = SessionAccessLevelSources.Explicit;
        store.SaveSession(record);
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "new", ProjectName = "T", ProjectPath = ".", Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
            AccessLevel = SessionAccessLevels.Full, AccessLevelSource = SessionAccessLevelSources.Policy,
        });

        Assert.Equal(SessionAccessLevelSources.Explicit, store.FindSession("old")!.AccessLevelSource);
        Assert.Equal(SessionAccessLevelSources.Policy, store.FindSession("new")!.AccessLevelSource);
        Assert.Equal(SessionAccessLevelSources.Explicit, ClaudeSessionService.AccessOf(store.FindSession("old")!).Source);
    }

    [Fact]
    public void Codex_store_adds_the_source_column_and_keeps_it()
    {
        var path = Path.Combine(_root, "codex.db");
        CodexDbContext.DbPath = path;
        using (var db = new CodexDbContext()) db.Initialize();
        Execute(path, "ALTER TABLE Sessions DROP COLUMN AccessLevelSource");
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        var store = new CodexSessionStore();
        store.SaveSession(new CodexSessionRecord
        {
            Id = "s", ProjectName = "T", ProjectPath = ".", Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
            AccessLevel = SessionAccessLevels.Ask,
        });
        Assert.Null(store.FindSession("s")!.AccessLevelSource);

        var record = store.FindSession("s")!;
        record.AccessLevelSource = SessionAccessLevelSources.Explicit;
        store.SaveSession(record);

        Assert.Equal(SessionAccessLevelSources.Explicit, store.FindSession("s")!.AccessLevelSource);
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

    public void Dispose()
    {
        ClaudeDbContext.DbPath = _claudePath;
        CodexDbContext.DbPath = _codexPath;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }
}
