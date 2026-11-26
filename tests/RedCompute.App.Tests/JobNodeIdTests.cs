using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, C3 (design 2.2/2.3): <c>Jobs.NodeId</c> is a nullable column added without touching a
/// row, stamped on new jobs only when the node identity is persisted, and read as the legacy node
/// when NULL.
/// </summary>
public sealed class JobNodeIdTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-job-nodeid-tests", Guid.NewGuid().ToString("N"));

    public JobNodeIdTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Adding_the_column_rewrites_no_row_of_a_large_old_table()
    {
        const int rows = 200_000;
        var database = Path.Combine(_directory, "old.db");
        CreateOldSchema(database);

        // A few hundred MB of old rows, written straight into the old schema.
        var input = "{\"prompt\":\"" + new string('x', 1024) + "\"}";
        using (var connection = Open(database))
        {
            using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Jobs (Id, CapabilitySlug, ProviderName, Status, QueuedAt, InputJson,
                    ExternalExecution, AttemptCount, Confidential, KeepResident)
                VALUES ($id, 'image-gen', 'comfyui', 'Completed', $queued, $input, 0, 0, 0, 0)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var queued = insert.Parameters.Add("$queued", SqliteType.Text);
            insert.Parameters.AddWithValue("$input", input);
            var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            for (var i = 0; i < rows; i++)
            {
                id.Value = Guid.NewGuid().ToString().ToUpperInvariant();
                queued.Value = start.AddSeconds(i).ToString("O");
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var bytes = new FileInfo(database).Length;
        var pagesBefore = Scalar(database, "PRAGMA page_count");
        var pageSize = (int)Scalar(database, "PRAGMA page_size");
        Assert.True(bytes > 200L * 1024 * 1024, $"synthetic database is only {bytes} bytes");
        var hashesBefore = PageHashes(database, pageSize);

        TimeSpan? added;
        using (var db = new RedComputeDbContext(database))
        {
            db.MigrateSchema();
            added = db.NodeIdColumnAddedIn;
        }

        // No row was rewritten: only the header and the schema's pages differ, out of tens of thousands. The time is
        // logged, not asserted: one ALTER is a schema write and a journal sync, so a wall-clock bound measured the
        // disk's sync latency under load, not rows (2026-11-25: 111 ms against 100 ms in a full-suite run).
        SqliteConnection.ClearAllPools();
        var hashesAfter = PageHashes(database, pageSize);
        var changed = Enumerable.Range(0, hashesBefore.Length).Where(page => hashesBefore[page] != hashesAfter[page]).Select(page => page + 1).ToArray();
        _output.WriteLine($"{rows} rows, {bytes / (1024 * 1024)} MB, {hashesBefore.Length} pages: Jobs.NodeId added in "
            + $"{added?.TotalMilliseconds:0.##} ms, pages changed: {string.Join(", ", changed)}");
        Assert.NotNull(added);
        Assert.Equal(pagesBefore, Scalar(database, "PRAGMA page_count"));
        Assert.True(changed.Length <= 8, $"{changed.Length} of {hashesBefore.Length} pages changed: {string.Join(", ", changed.Take(20))}");
        Assert.Equal(rows, Scalar(database, "SELECT COUNT(*) FROM Jobs WHERE NodeId IS NULL"));

        // Idempotent: the next boot finds the column and changes nothing.
        using (var db = new RedComputeDbContext(database))
        {
            db.MigrateSchema();
            Assert.Null(db.NodeIdColumnAddedIn);
        }
        Assert.Equal(pagesBefore, Scalar(database, "PRAGMA page_count"));

        // EF reads an old row's missing value as null.
        using (var db = new RedComputeDbContext(database))
            Assert.Null(db.Jobs.OrderBy(job => job.QueuedAt).First().NodeId);
    }

    [Fact]
    public void A_new_job_carries_the_stamp_node_id_and_an_unstamped_one_stays_null()
    {
        var jobs = Jobs();
        jobs.StampNodeId = "node_persisted";
        var stamped = jobs.CreateJob(Submission());
        Assert.Equal("node_persisted", stamped.NodeId);
        Assert.Equal("node_persisted", jobs.GetJob(stamped.Id)!.NodeId);

        jobs.StampNodeId = null;
        var unstamped = jobs.CreateJob(Submission());
        Assert.Null(unstamped.NodeId);
        Assert.Null(jobs.GetJob(unstamped.Id)!.NodeId);

        // The listing projection keeps the column.
        var (listed, _) = jobs.GetJobs(null, null, null, 10, 0);
        Assert.Contains(listed, job => job.Id == stamped.Id && job.NodeId == "node_persisted");
        Assert.Contains(listed, job => job.Id == unstamped.Id && job.NodeId == null);
    }

    [Fact]
    public void A_restored_job_is_not_stamped()
    {
        var jobs = Jobs();
        jobs.StampNodeId = "node_persisted";
        var restored = jobs.RestoreJob(new JobRecord
        {
            CapabilitySlug = "image-gen",
            ProviderName = "comfyui",
            Status = JobStatus.Completed,
            CreationProvenance = Submission().Provenance,
        });
        Assert.Null(jobs.GetJob(restored.Id)!.NodeId);
    }

    [Fact]
    public void The_api_reads_null_as_the_legacy_node_and_keeps_a_stamped_id()
    {
        var nodes = Registry(persisted: true);
        var legacy = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "comfyui" };
        var stamped = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "comfyui", NodeId = "node_other" };

        Assert.Equal("node_legacy", JobApiResponse.Details(legacy, _directory, nodes)["nodeId"]);
        Assert.Equal("node_legacy", JobApiResponse.Progress(legacy, _directory, nodes)["nodeId"]);
        Assert.Equal("node_other", JobApiResponse.Details(stamped, _directory, nodes)["nodeId"]);
        Assert.Equal("node_legacy", JobApiResponse.NodeId(legacy, nodes));
    }

    [Theory]
    [InlineData(true, "node_now")]
    [InlineData(false, null)]
    public void Only_a_persisted_identity_is_stamped(bool persisted, string? expected)
    {
        INodeRegistry nodes = Registry(persisted);
        Assert.Equal(expected, nodes.StampNodeId);
        Assert.Equal("node_now", nodes.LocalNodeId);
    }

    private static NodeRegistry Registry(bool persisted) => new(
        new NodeIdentityResult(new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_legacy"),
            persisted, Created: false, persisted ? null : "unreadable"),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);

    private JobTrackingService Jobs()
    {
        var database = Path.Combine(_directory, $"jobs-{Guid.NewGuid():N}.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        return new JobTrackingService(() => new RedComputeDbContext(database));
    }

    private static JobSubmission Submission() => new("image-gen", "comfyui", "{}",
        JobProvenance.DirectRedCompute("POST", "/image-gen/generate", new JobBeneficiary("user", "owner"),
            Guid.NewGuid().ToString("N"), null));

    /// <summary>Today's schema without Jobs.NodeId: what a database written before C3 looks like.</summary>
    private static void CreateOldSchema(string database)
    {
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        // A pooled connection keeps the schema it parsed; one that saw NodeId would refuse to add it.
        SqliteConnection.ClearAllPools();
        using (var connection = Open(database))
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE Jobs DROP COLUMN NodeId";
            drop.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>A hash of each page of the database file, in page order.</summary>
    private static long[] PageHashes(string database, int pageSize)
    {
        using var file = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hashes = new long[file.Length / pageSize];
        var page = new byte[pageSize];
        for (var i = 0; i < hashes.Length; i++)
        {
            file.ReadExactly(page);
            hashes[i] = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(page));
        }
        return hashes;
    }

    private static long Scalar(string database, string sql)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
