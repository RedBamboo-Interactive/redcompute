using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Configuration;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.App.Tests;

/// <summary>
/// Placement by reference (design 2.2.7), the session side: each provider database gains a nullable
/// <c>Sessions.Placement</c> column the node-zero way (schema-only ALTER, timed and logged once), a row
/// takes the start's placement at insert and keeps it through every later save, and the mirror snapshot
/// carries it as stored. Phase 1's <c>NodeId</c> tests pin the same rules for the node.
/// </summary>
[Collection("Plugin database path")]
public abstract class SessionPlacementColumnTests : IDisposable
{
    protected const string First = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}""";
    protected const string Other = """{"nodeId":"local","repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""";

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly string _original;

    protected SessionPlacementColumnTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"redcompute-{Name}-placement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _original = DbPath;
        DbPath = Path.Combine(_root, Name + ".db");
    }

    public void Dispose()
    {
        DbPath = _original;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>claude, codex or opencode.</summary>
    protected abstract string Name { get; }
    protected abstract string Tag { get; }
    protected abstract string DbPath { get; set; }

    /// <summary>Runs the context's Initialize and returns how long the Placement add took, if it added it.</summary>
    protected abstract TimeSpan? Initialize();

    /// <summary>An insert of an old row, with <c>$id</c> and <c>$instructions</c> parameters.</summary>
    protected abstract string OldRowInsert { get; }

    protected abstract void BootProvider(Action<string> log);

    /// <summary>Saves a row with that placement through the provider's store; returns (record after save, snapshot).</summary>
    protected abstract (string? Record, string? Snapshot) Save(string id, string status, string? placement);

    [Fact]
    public void Adding_the_column_rewrites_no_row_of_a_large_old_table()
    {
        const int rows = 50_000;
        Initialize();
        Execute("ALTER TABLE Sessions DROP COLUMN Placement");
        Assert.DoesNotContain("Placement", Columns());

        var instructions = new string('i', 2048);
        using (var connection = Open())
        {
            using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = OldRowInsert;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            insert.Parameters.AddWithValue("$instructions", instructions);
            for (var i = 0; i < rows; i++)
            {
                id.Value = $"old-{i:D6}";
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var bytes = new FileInfo(DbPath).Length;
        var pagesBefore = Scalar("PRAGMA page_count");

        var clock = Stopwatch.StartNew();
        var added = Initialize();
        clock.Stop();
        _output.WriteLine($"{Name}.db, {rows} rows, {bytes / (1024 * 1024)} MB: Initialize (with Sessions.Placement added) took {clock.ElapsedMilliseconds} ms, the ALTER {added?.TotalMilliseconds:0.#} ms");
        Assert.NotNull(added);

        Assert.Contains("Placement", Columns());
        Assert.Equal(pagesBefore, Scalar("PRAGMA page_count"));
        Assert.Equal((long)rows, Scalar("SELECT COUNT(*) FROM Sessions WHERE Placement IS NULL"));

        Assert.Null(Initialize());
        Assert.Equal(pagesBefore, Scalar("PRAGMA page_count"));
    }

    [Fact]
    public void The_provider_logs_the_column_add_once()
    {
        Initialize();
        Execute("ALTER TABLE Sessions DROP COLUMN Placement");
        var logs = new List<string>();

        for (var boot = 0; boot < 2; boot++) BootProvider(logs.Add);

        var add = Assert.Single(logs, line => line.Contains("Sessions.Placement"));
        Assert.Matches($@"^\[{Tag}\] Added Sessions\.Placement to {Name}\.db in \d+(\.\d)? ms$", add);
        Assert.Contains("Placement", Columns());
    }

    [Fact]
    public void A_row_keeps_the_placement_it_was_inserted_with()
    {
        Initialize();

        Assert.Equal((First, First), Save("s1", "Idle", First));
        Assert.Equal(First, PlacementOf("s1"));

        // A later save carries whatever the info holds; the row and its snapshot keep the insert's.
        Assert.Equal((First, First), Save("s1", "Stopped", Other));
        Assert.Equal((First, First), Save("s1", "Stopped", null));
        Assert.Equal(First, PlacementOf("s1"));

        // A session started with path fields only stays without one.
        Assert.Equal((null, null), Save("s2", "Idle", null));
        Assert.Equal((null, null), Save("s2", "Stopped", Other));
        Assert.Null(PlacementOf("s2"));
    }

    private string? PlacementOf(string id)
        => Scalar($"SELECT Placement FROM Sessions WHERE Id = '{id}'") is string placement ? placement : null;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private List<string> Columns()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Sessions)";
        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed class ClaudeSessionPlacementColumnTests(ITestOutputHelper output) : SessionPlacementColumnTests(output)
{
    protected override string Name => "claude";
    protected override string Tag => "Claude";
    protected override string DbPath { get => ClaudeDbContext.DbPath; set => ClaudeDbContext.DbPath = value; }

    protected override TimeSpan? Initialize()
    {
        using var db = new ClaudeDbContext();
        db.Initialize();
        return db.PlacementColumnAddedIn;
    }

    protected override string OldRowInsert => """
        INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed,
            Unattended, DeliveredAsOneMessage, DeveloperInstructions)
        VALUES ($id, 'P', 'T:/p', 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 3, 0, 0, 0, $instructions)
        """;

    protected override void BootProvider(Action<string> log)
        => _ = new ClaudeCodeProvider(new ProviderConfig { Type = "ClaudeCode" }, "ai-session",
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), new ClaudeDeveloperInstructionsTests.NullQualityModes(),
            (message, _) => log(message), LocalExecutionHost.Shared, null);

    protected override (string?, string?) Save(string id, string status, string? placement)
    {
        var record = new ClaudeSessionRecord
        {
            Id = id, ProjectName = "P", ProjectPath = "T:/p", Status = status, StartedAt = DateTimeOffset.UtcNow,
            Placement = placement,
        };
        new ClaudeSessionStore(null, "node_zero").SaveSession(record);
        return (record.Placement, ClaudeSessionStore.ToSnapshot(record).Placement);
    }
}

public sealed class CodexSessionPlacementColumnTests(ITestOutputHelper output) : SessionPlacementColumnTests(output)
{
    protected override string Name => "codex";
    protected override string Tag => "Codex";
    protected override string DbPath { get => CodexDbContext.DbPath; set => CodexDbContext.DbPath = value; }

    protected override TimeSpan? Initialize()
    {
        using var db = new CodexDbContext();
        db.Initialize();
        return db.PlacementColumnAddedIn;
    }

    protected override string OldRowInsert => """
        INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed,
            ExecutionProfile, Unattended, DeveloperInstructions)
        VALUES ($id, 'P', 'T:/p', 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 3, 0, 'default', 0, $instructions)
        """;

    protected override void BootProvider(Action<string> log)
        => _ = new CodexProvider(new ProviderConfig { Type = "Codex" }, "ai-session",
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), new ClaudeDeveloperInstructionsTests.NullQualityModes(),
            (message, _) => log(message), LocalExecutionHost.Shared, null);

    protected override (string?, string?) Save(string id, string status, string? placement)
    {
        var record = new CodexSessionRecord
        {
            Id = id, ProjectName = "P", ProjectPath = "T:/p", Status = status, StartedAt = DateTimeOffset.UtcNow,
            Placement = placement,
        };
        new CodexSessionStore("node_zero").SaveSession(record);
        return (record.Placement, CodexSessionStore.ToSnapshot(record).Placement);
    }
}

public sealed class OpenCodeSessionPlacementColumnTests(ITestOutputHelper output) : SessionPlacementColumnTests(output)
{
    protected override string Name => "opencode";
    protected override string Tag => "OpenCode";
    protected override string DbPath { get => OpenCodeDbContext.DbPath; set => OpenCodeDbContext.DbPath = value; }

    protected override TimeSpan? Initialize()
    {
        using var db = new OpenCodeDbContext();
        db.Initialize();
        return db.PlacementColumnAddedIn;
    }

    protected override string OldRowInsert => """
        INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed,
            Unattended, DeveloperInstructions)
        VALUES ($id, 'P', 'T:/p', 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 3, 0, 0, $instructions)
        """;

    protected override void BootProvider(Action<string> log)
        => _ = new OpenCodeProvider(new ProviderConfig { Type = "OpenCode" }, "ai-session",
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), (message, _) => log(message), null,
            LocalExecutionHost.Shared, null);

    protected override (string?, string?) Save(string id, string status, string? placement)
    {
        var record = new OpenCodeSessionRecord
        {
            Id = id, ProjectName = "P", ProjectPath = "T:/p", Status = status, StartedAt = DateTimeOffset.UtcNow,
            Placement = placement,
        };
        new OpenCodeSessionStore("node_zero").SaveSession(record);
        return (record.Placement, OpenCodeSessionStore.ToSnapshot(record).Placement);
    }
}
