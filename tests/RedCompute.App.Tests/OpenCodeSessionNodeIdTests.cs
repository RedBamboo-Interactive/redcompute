using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, C13 (design 2.2/2.3): opencode.db <c>Sessions.NodeId</c> is a nullable column added without
/// touching a row, stamped once when a session row is created (only with a persisted node identity),
/// never changed by a later save, and carried as stored into the ai-session mirror snapshot, as
/// claude.db's and codex.db's (<see cref="ClaudeSessionNodeIdTests"/>, <see cref="CodexSessionNodeIdTests"/>).
/// </summary>
[Collection("Plugin database path")]
public sealed class OpenCodeSessionNodeIdTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-opencode-nodeid", Guid.NewGuid().ToString("N"));
    private readonly string _openCodePath = OpenCodeDbContext.DbPath;

    public OpenCodeSessionNodeIdTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        OpenCodeDbContext.DbPath = Path.Combine(_root, "opencode.db");
    }

    public void Dispose()
    {
        OpenCodeDbContext.DbPath = _openCodePath;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Adding_the_column_rewrites_no_row_of_a_large_old_table()
    {
        const int rows = 50_000;
        using (var db = new OpenCodeDbContext()) db.Initialize();
        Execute("ALTER TABLE Sessions DROP COLUMN NodeId");
        Assert.DoesNotContain("NodeId", Columns());

        // Old rows as an earlier build wrote them, with instructions large enough to fill pages.
        var instructions = new string('i', 2048);
        using (var connection = Open())
        {
            using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed,
                    Unattended, DeveloperInstructions)
                VALUES ($id, 'P', 'T:/p', 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 3, 0, 0, $instructions)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            insert.Parameters.AddWithValue("$instructions", instructions);
            for (var i = 0; i < rows; i++)
            {
                id.Value = $"old-{i:D6}";
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var bytes = new FileInfo(OpenCodeDbContext.DbPath).Length;
        var pagesBefore = Scalar("PRAGMA page_count");

        var clock = Stopwatch.StartNew();
        using (var db = new OpenCodeDbContext()) db.Initialize();
        clock.Stop();
        _output.WriteLine($"{rows} rows, {bytes / (1024 * 1024)} MB: Initialize (with Sessions.NodeId added) took {clock.ElapsedMilliseconds} ms");

        Assert.Contains("NodeId", Columns());
        Assert.Equal(pagesBefore, Scalar("PRAGMA page_count"));
        Assert.Equal((long)rows, Scalar("SELECT COUNT(*) FROM Sessions WHERE NodeId IS NULL"));

        // Idempotent: the next boot finds the column and changes nothing.
        using (var db = new OpenCodeDbContext()) db.Initialize();
        Assert.Equal(pagesBefore, Scalar("PRAGMA page_count"));
    }

    [Fact]
    public void A_row_is_stamped_once_at_insert_and_a_save_never_changes_it()
    {
        using (var db = new OpenCodeDbContext()) db.Initialize();

        new OpenCodeSessionStore("node_zero").SaveSession(Record("s1", "Idle"));
        Assert.Equal("node_zero", NodeOf("s1"));

        // A later save (every persist builds a fresh record without a node) keeps the row's node,
        // whichever node the saving store would stamp, and publishes it as stored.
        var update = Record("s1", "Stopped");
        new OpenCodeSessionStore("node_other").SaveSession(update);
        Assert.Equal("node_zero", NodeOf("s1"));
        Assert.Equal("node_zero", update.NodeId);
        Assert.Equal("node_zero", OpenCodeSessionStore.ToSnapshot(update).NodeId);
        Assert.Equal("Stopped", new OpenCodeSessionStore().FindSession("s1")!.Status);

        // No persisted identity: the row stays NULL (the legacy node), and stays NULL afterwards.
        new OpenCodeSessionStore(null).SaveSession(Record("s2", "Idle"));
        Assert.Null(NodeOf("s2"));
        var later = Record("s2", "Stopped");
        new OpenCodeSessionStore("node_zero").SaveSession(later);
        Assert.Null(NodeOf("s2"));
        Assert.Null(OpenCodeSessionStore.ToSnapshot(later).NodeId);
    }

    [Fact]
    public void A_loaded_row_carries_its_node_into_the_snapshot()
    {
        using (var db = new OpenCodeDbContext()) db.Initialize();
        var store = new OpenCodeSessionStore("node_zero");
        store.SaveSession(Record("s1", "Stopped"));
        var found = store.FindSession("s1")!;
        Assert.Equal("node_zero", found.NodeId);
        Assert.Equal("node_zero", OpenCodeSessionStore.ToSnapshot(found).NodeId);
    }

    /// <summary>RedCompute hands the provider its node registry; the provider's store stamps with it.</summary>
    [Theory]
    [InlineData(true, "node_now")]
    [InlineData(false, null)]
    public void The_provider_stamps_with_the_registry_stamp(bool persisted, string? expected)
    {
        var nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_legacy"), persisted, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);
        var provider = new OpenCodeProvider(new ProviderConfig { Type = "OpenCode" }, "ai-session",
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), (_, _) => { }, null, LocalExecutionHost.Shared, nodes);
        var store = (IOpenCodeSessionStore)typeof(OpenCodeProvider)
            .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;

        store.SaveSession(Record("p1", "Idle"));

        Assert.Equal(expected, NodeOf("p1"));
    }

    private static OpenCodeSessionRecord Record(string id, string status) => new()
    {
        Id = id, ProjectName = "P", ProjectPath = "T:/p", Status = status, StartedAt = DateTimeOffset.UtcNow,
    };

    private static string? NodeOf(string id)
        => Scalar($"SELECT NodeId FROM Sessions WHERE Id = '{id}'") is string node ? node : null;

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={OpenCodeDbContext.DbPath}");
        connection.Open();
        return connection;
    }

    private static List<string> Columns()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Sessions)";
        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static object? Scalar(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
