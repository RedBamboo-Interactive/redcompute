using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Storage;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class JobDatabaseSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly ComputeStorage _storage;

    public JobDatabaseSnapshotTests()
    {
        Directory.CreateDirectory(_root);
        _storage = new ComputeStorage(_root, Path.Combine(_root, "suite"), isRelocated: true);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private void CreateSourceDatabase(int rows)
    {
        using var db = new SqliteConnection(ConnectionString(_storage.Database));
        db.Open();
        Exec(db, "PRAGMA journal_mode=WAL");
        Exec(db, "CREATE TABLE Jobs (Id INTEGER PRIMARY KEY, Payload BLOB NOT NULL)");
        using var tx = db.BeginTransaction();
        for (var i = 0; i < rows; i++)
            Exec(db, $"INSERT INTO Jobs (Payload) VALUES (randomblob(2000))");
        tx.Commit();
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using var db = new SqliteConnection(ConnectionString(path));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Jobs";
        return (long)command.ExecuteScalar()!;
    }

    private string[] BackupFolderFiles()
        => Directory.Exists(Path.Combine(_root, "backups"))
            ? Directory.GetFiles(Path.Combine(_root, "backups")).Select(Path.GetFileName).Order().ToArray()!
            : [];

    [Fact]
    public async Task Snapshot_under_concurrent_writes_is_consistent_and_lands_under_the_storage_root()
    {
        CreateSourceDatabase(20_000);
        var service = new JobDatabaseSnapshotService(_storage);
        long before = Count(_storage.Database);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            using var db = new SqliteConnection(ConnectionString(_storage.Database));
            db.Open();
            Exec(db, "PRAGMA busy_timeout=10000");
            while (!stop.IsCancellationRequested)
                Exec(db, "INSERT INTO Jobs (Payload) VALUES (randomblob(2000))");
        });
        while (Count(_storage.Database) == before) await Task.Delay(5); // writer is running

        var result = await service.TryCreateAsync();
        var after = Count(_storage.Database);
        stop.Cancel();
        await writer;

        Assert.NotNull(result);
        Assert.Equal("ok", result.QuickCheck);
        Assert.Equal(Path.Combine(_root, "backups", "redcompute-snapshot.db"), result.Path);
        Assert.Equal(Path.GetFullPath(result.Path), Path.GetFullPath(service.SnapshotPath));
        var snapshotRows = Count(result.Path);
        Assert.InRange(snapshotRows, before, after);
        Assert.Equal(new FileInfo(result.Path).Length, result.SizeBytes);
        Assert.Equal(result.PageCount * result.PageSize, result.SizeBytes);
        using (var stream = File.OpenRead(result.Path))
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), result.Sha256);
        // One file, nothing else: no temp copy, no WAL/SHM beside the snapshot.
        Assert.Equal(["redcompute-snapshot.db"], BackupFolderFiles());
    }

    [Fact]
    public async Task A_second_snapshot_while_one_runs_is_refused_until_it_finishes()
    {
        CreateSourceDatabase(100);
        var service = new JobDatabaseSnapshotService(_storage);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.AfterCopy = _ => { reached.Set(); release.Wait(TimeSpan.FromSeconds(30)); };

        var first = service.TryCreateAsync();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(service.IsRunning);
        Assert.Null(await service.TryCreateAsync());
        Assert.Null(service.TryDelete()); // would delete under the writer

        release.Set();
        Assert.NotNull(await first);
        Assert.False(service.IsRunning);
        service.AfterCopy = null;
        Assert.NotNull(await service.TryCreateAsync());
    }

    [Fact]
    public async Task A_failed_snapshot_leaves_no_temp_file_and_keeps_the_previous_snapshot()
    {
        CreateSourceDatabase(100);
        var service = new JobDatabaseSnapshotService(_storage);
        var good = await service.TryCreateAsync();
        Assert.NotNull(good);
        var goodBytes = File.ReadAllBytes(good.Path);

        service.AfterCopy = temp =>
        {
            Assert.Contains(".tmp-", temp); // it really is writing under a temporary name
            throw new IOException("disk went away");
        };
        var ex = await Assert.ThrowsAsync<JobDatabaseSnapshotException>(service.TryCreateAsync);
        Assert.Contains("disk went away", ex.Message);

        Assert.Equal(["redcompute-snapshot.db"], BackupFolderFiles());
        Assert.Equal(goodBytes, File.ReadAllBytes(good.Path));
        Assert.False(service.IsRunning); // the gate is released: the next run may start
    }

    [Fact]
    public async Task A_temp_file_left_by_a_killed_run_is_cleared_by_the_next_snapshot()
    {
        CreateSourceDatabase(10);
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        File.WriteAllText(Path.Combine(_root, "backups", "redcompute-snapshot.db.tmp-dead"), "partial");

        var service = new JobDatabaseSnapshotService(_storage);
        Assert.NotNull(await service.TryCreateAsync());
        Assert.Equal(["redcompute-snapshot.db"], BackupFolderFiles());
    }

    [Fact]
    public async Task Missing_database_fails_without_creating_anything_and_never_creates_the_source()
    {
        var service = new JobDatabaseSnapshotService(_storage);
        await Assert.ThrowsAsync<JobDatabaseSnapshotException>(service.TryCreateAsync);
        Assert.False(File.Exists(_storage.Database));
        Assert.Empty(BackupFolderFiles());
    }

    [Fact]
    public async Task Delete_removes_the_snapshot_and_reports_whether_there_was_one()
    {
        CreateSourceDatabase(10);
        var service = new JobDatabaseSnapshotService(_storage);
        Assert.False(service.TryDelete());
        await service.TryCreateAsync();
        Assert.True(service.TryDelete());
        Assert.Empty(BackupFolderFiles());
    }

    // ---- the endpoints ----

    private static ClaimsPrincipal Kernel(params Claim[] extra)
        => new(new ClaimsIdentity(
            [new Claim("client_id", "redleaf"), new Claim("compute_provenance", "true"), .. extra], "signed"));

    private async Task<(int Status, JsonDocument? Body)> Call(string method, ClaimsPrincipal? user, JobDatabaseSnapshotService service)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var registry = new EndpointRegistry(app);
        BackupEndpoints.Map(registry, service);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/backups/job-database"
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        if (user is not null) context.User = user;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        await route.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var body = context.Response.Body.Length == 0 ? null : await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("ordinary-user")]
    [InlineData("other-client")]
    [InlineData("execution-token")]
    public async Task Callers_other_than_the_kernel_service_are_refused_and_nothing_is_written(string caller)
    {
        CreateSourceDatabase(10);
        ClaimsPrincipal? user = caller switch
        {
            "anonymous" => null,
            "ordinary-user" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "laurent")], "signed")),
            "other-client" => new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("client_id", "nova"), new Claim("compute_provenance", "true")], "signed")),
            _ => Kernel(new Claim("token_use", "execution")),
        };
        var service = new JobDatabaseSnapshotService(_storage);

        foreach (var method in new[] { "POST", "DELETE" })
        {
            var (status, _) = await Call(method, user, service);
            Assert.Equal(403, status);
        }
        Assert.Empty(BackupFolderFiles());
    }

    [Fact]
    public async Task The_kernel_service_gets_the_snapshot_and_can_delete_it()
    {
        CreateSourceDatabase(50);
        var service = new JobDatabaseSnapshotService(_storage);

        var (status, body) = await Call("POST", Kernel(), service);
        Assert.Equal(200, status);
        var root = body!.RootElement;
        Assert.Equal(service.SnapshotPath, root.GetProperty("path").GetString());
        Assert.Equal("ok", root.GetProperty("quickCheck").GetString());
        Assert.Equal(64, root.GetProperty("sha256").GetString()!.Length);
        Assert.True(root.GetProperty("pageCount").GetInt64() > 0);
        Assert.True(root.GetProperty("durationSeconds").GetDouble() >= 0);

        var (deleteStatus, deleteBody) = await Call("DELETE", Kernel(), service);
        Assert.Equal(200, deleteStatus);
        Assert.True(deleteBody!.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Empty(BackupFolderFiles());
    }

    [Fact]
    public async Task The_endpoint_answers_409_while_a_snapshot_runs()
    {
        CreateSourceDatabase(50);
        var service = new JobDatabaseSnapshotService(_storage);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.AfterCopy = _ => { reached.Set(); release.Wait(TimeSpan.FromSeconds(30)); };
        var running = service.TryCreateAsync();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));

        Assert.Equal(409, (await Call("POST", Kernel(), service)).Status);
        Assert.Equal(409, (await Call("DELETE", Kernel(), service)).Status);

        release.Set();
        await running;
    }

    [Fact]
    public async Task A_failed_snapshot_is_a_500_with_a_message()
    {
        var service = new JobDatabaseSnapshotService(_storage); // no database
        var (status, body) = await Call("POST", Kernel(), service);
        Assert.Equal(500, status);
        Assert.Equal("snapshot_failed", body!.RootElement.GetProperty("error").GetString());
        Assert.Empty(BackupFolderFiles());
    }

    [Fact]
    public void The_snapshot_path_follows_a_relocated_data_root()
    {
        var other = Path.Combine(_root, "elsewhere");
        var storage = ComputeStorage.FromEnvironment(
            name => name == ComputeStorage.DataRootVariable ? other : null, _ => _root);
        Assert.Equal(Path.Combine(other, "backups", "redcompute-snapshot.db"),
            new JobDatabaseSnapshotService(storage).SnapshotPath);
    }
}
