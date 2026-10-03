using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class JobAuditReconciliationTests
{
    [Fact]
    public async Task Historical_events_use_bounded_batches_and_only_missing_events_are_requeued()
    {
        await using var f = await Fixture.Create(2050);
        var missing = f.EventIds[100];
        f.Present.TryRemove(JobAuditOutboxService.EventExternalId(missing), out _);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        Assert.Equal(3, f.PresenceRequests.Count);
        Assert.Equal(2050, f.PresenceRequests.Sum(ids => ids.Length));
        Assert.All(f.PresenceRequests, ids => Assert.InRange(ids.Length, 1, 1000));
        using (var db = f.Db())
        {
            var repair = Assert.Single(db.JobOutbox);
            Assert.Equal(missing, repair.JobEventId);
            Assert.Equal(JobOutboxKind.JobEvent, repair.Kind);
        }
        // Existing pending repair is deduplicated when the next full sweep wraps.
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        using (var db = f.Db()) Assert.Single(db.JobOutbox);
        Assert.Equal(1, await f.Worker.ProcessBatchAsync(CancellationToken.None));
        Assert.True(f.Present.ContainsKey(JobAuditOutboxService.EventExternalId(missing)));
        using (var db = f.Db()) Assert.NotNull(Assert.Single(db.JobOutbox).AcknowledgedAt);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        using (var db = f.Db()) Assert.Single(db.JobOutbox);
        Assert.Equal(0, f.LegacyRecordQueries);
    }

    [Fact]
    public async Task Failed_presence_check_never_creates_false_repairs_and_retries_same_jobs()
    {
        await using var f = await Fixture.Create(20);
        f.FailPresence = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using (var db = f.Db()) Assert.Empty(db.JobOutbox);
        f.FailPresence = false;
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        Assert.Equal(2, f.PresenceRequests.Count);
        using (var db = f.Db()) Assert.Empty(db.JobOutbox);
    }

    [Fact]
    public async Task Invalid_success_response_is_not_interpreted_as_missing_history()
    {
        await using var f = await Fixture.Create(4);
        f.MalformedPresence = true;
        await Assert.ThrowsAsync<JsonException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using var db = f.Db();
        Assert.Empty(db.JobOutbox);
    }

    [Fact]
    public async Task Reconciliation_failure_does_not_stop_outbox_delivery()
    {
        await using var f = await Fixture.Create(1);
        f.FailPresence = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = f.Worker.RunAsync(cancellation.Token);
        try
        {
            await f.ReconciliationFailure.Task.WaitAsync(cancellation.Token);
            using (var db = f.Db())
            {
                var evt = Assert.Single(db.JobEvents);
                db.JobOutbox.Add(new JobOutboxMessage
                {
                    Kind = JobOutboxKind.JobEvent, JobId = evt.JobId, JobEventId = evt.Id,
                    PayloadJson = JobTrackingService.SerializeEvent(evt), CreatedAt = evt.OccurredAt,
                });
                await db.SaveChangesAsync(cancellation.Token);
            }
            while (true)
            {
                using var db = f.Db();
                if (Assert.Single(db.JobOutbox).AcknowledgedAt != null) break;
                await Task.Delay(20, cancellation.Token);
            }
            Assert.False(running.IsCompleted);
            Assert.Equal(0, f.LegacyRecordQueries);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-audit-batch-" + Guid.NewGuid());
        private readonly WebApplication _server;
        private RedLeafStreamClient _client = null!;
        public JobAuditOutboxService Worker { get; private set; } = null!;
        public ConcurrentDictionary<string, byte> Present { get; } = new(StringComparer.Ordinal);
        public List<string[]> PresenceRequests { get; } = [];
        public List<Guid> EventIds { get; } = [];
        public TaskCompletionSource ReconciliationFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailPresence;
        public bool MalformedPresence;
        public int LegacyRecordQueries;
        private Fixture(WebApplication server) => _server = server;
        public RedComputeDbContext Db() => new(Path.Combine(_directory, "jobs.db"));
        public static async Task<Fixture> Create(int count)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var server = builder.Build();
            var f = new Fixture(server);
            Directory.CreateDirectory(f._directory);
            using (var db = f.Db())
            {
                db.Database.EnsureCreated();
                db.MigrateSchema();
                var job = new JobRecord { Id = Guid.NewGuid(), CapabilitySlug = "tts", ProviderName = "Local", InputJson = "{}", QueuedAt = DateTimeOffset.UtcNow };
                db.Jobs.Add(job);
                for (var i = 0; i < count; i++)
                {
                    var evt = new JobLifecycleEvent { JobId = job.Id, Kind = JobEventKind.Progress, DataJson = "{}" };
                    db.JobEvents.Add(evt);
                    f.EventIds.Add(evt.Id);
                    f.Present[JobAuditOutboxService.EventExternalId(evt.Id)] = 0;
                }
                db.SaveChanges();
            }
            server.MapGet("/api/entities/{slug}", () => Results.Ok(new { id = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
            server.MapGet("/api/streams/{stream}/records", () => { f.LegacyRecordQueries++; return Results.Ok(new { count = 1 }); });
            server.MapPost("/api/streams/{stream}/records/exists", async (HttpContext ctx) =>
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var ids = body.RootElement.GetProperty("external_ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
                f.PresenceRequests.Add(ids);
                if (f.FailPresence) return Results.StatusCode(503);
                if (f.MalformedPresence) return Results.Ok(new { unexpected = true });
                return Results.Ok(new { external_ids = ids.Where(id => f.Present.ContainsKey(id)).ToArray() });
            });
            server.MapPost("/api/streams/{stream}/records", async (HttpContext ctx) =>
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var record in body.RootElement.GetProperty("records").EnumerateArray())
                    f.Present[record.GetProperty("external_id").GetString()!] = 0;
                return Results.Ok(new { created = 1 });
            });
            server.Urls.Add("http://127.0.0.1:0");
            await server.StartAsync();
            f._client = new RedLeafStreamClient(server.Urls.Single(), "RedComputeTests", new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            f.Worker = new JobAuditOutboxService(f._client, (message, _) =>
            {
                if (message.Contains("Reconciliation deferred")) f.ReconciliationFailure.TrySetResult();
            }, f.Db);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            using (var db = Db())
                SqliteConnection.ClearPool((SqliteConnection)db.Database.GetDbConnection());
            // SQLite test files are ordinary test scratch; no production path is touched.
            Directory.Delete(_directory, recursive: true);
        }
    }
}
