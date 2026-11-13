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
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Historical_events_use_bounded_batches_and_only_missing_events_are_requeued()
    {
        await using var f = await Fixture.Create();
        var job = f.AddJob(Now.AddHours(-1), events: 2050);
        var missing = f.EventIds(job)[100];
        f.PresentRecords.TryRemove(JobAuditOutboxService.EventExternalId(missing), out _);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        Assert.Equal(0, f.LegacyEntityReads);
        Assert.Equal(3, f.RecordPresenceRequests.Count);
        Assert.Equal(2050, f.RecordPresenceRequests.Sum(ids => ids.Length));
        Assert.All(f.RecordPresenceRequests, ids => Assert.InRange(ids.Length, 1, 1000));
        using (var db = f.Db())
        {
            var repair = Assert.Single(db.JobOutbox);
            Assert.Equal(missing, repair.JobEventId);
            Assert.Equal(JobOutboxKind.JobEvent, repair.Kind);
        }
        Assert.Equal(1, await f.Worker.ProcessBatchAsync(CancellationToken.None));
        Assert.True(f.PresentRecords.ContainsKey(JobAuditOutboxService.EventExternalId(missing)));
        using (var db = f.Db()) Assert.NotNull(Assert.Single(db.JobOutbox).AcknowledgedAt);
        // Behind the watermark the job's history is not walked again.
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        Assert.Equal(3, f.RecordPresenceRequests.Count);
        using (var db = f.Db()) Assert.Single(db.JobOutbox);
        Assert.Equal(0, f.LegacyRecordQueries);
    }

    [Fact]
    public async Task Entity_presence_is_one_ids_only_batch_and_only_missing_projections_are_requeued()
    {
        await using var f = await Fixture.Create();
        var jobs = Enumerable.Range(0, 300).Select(i => f.AddJob(Now.AddDays(-2).AddSeconds(i))).ToArray();
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(jobs[7]), out _);
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(jobs[250]), out _);

        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));

        var request = Assert.Single(f.EntityPresenceRequests);
        Assert.Equal("compute-job", request.Type);
        Assert.Equal(300, request.Slugs.Length);
        using (var db = f.Db())
            Assert.Equal(new[] { jobs[7], jobs[250] }.Order(), db.JobOutbox.Select(m => m.JobId).ToList().Order());
        Assert.Equal(0, f.LegacyEntityReads);
        // Delivery recreates both, and the next step (caught up, nothing live) sends nothing.
        Assert.Equal(2, await f.Worker.ProcessBatchAsync(CancellationToken.None));
        Assert.True(f.PresentEntities.ContainsKey(JobAuditOutboxService.JobSlug(jobs[7])));
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Single(f.EntityPresenceRequests);
    }

    [Fact]
    public async Task Watermark_advances_and_steady_state_checks_only_new_and_live_jobs()
    {
        await using var f = await Fixture.Create();
        var old = Enumerable.Range(0, 1200).Select(i => f.AddJob(Now.AddDays(-3).AddSeconds(i))).ToArray();
        var live = f.AddJob(Now.AddDays(-2), status: JobStatus.Running, capability: "ai-session");

        Assert.False(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(f.Watermark(old[499]), f.ReadWatermark());
        Assert.False(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(f.Watermark(live), f.ReadWatermark());
        var everyJob = f.EntityPresenceRequests.SelectMany(r => r.Slugs).Distinct().ToHashSet();
        Assert.Equal(1201, everyJob.Count);

        // Steady state: only the live job, then a job queued since, are checked.
        f.EntityPresenceRequests.Clear();
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal([JobAuditOutboxService.JobSlug(live)], Assert.Single(f.EntityPresenceRequests).Slugs);
        var fresh = f.AddJob(Now.AddMinutes(-10));
        var settling = f.AddJob(Now.AddMinutes(-1));
        f.EntityPresenceRequests.Clear();
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(new[] { JobAuditOutboxService.JobSlug(fresh), JobAuditOutboxService.JobSlug(live) }.Order(),
            Assert.Single(f.EntityPresenceRequests).Slugs.Order());
        Assert.Equal(f.Watermark(fresh), f.ReadWatermark());
        Assert.DoesNotContain(JobAuditOutboxService.JobSlug(settling), f.EntityPresenceRequests.SelectMany(r => r.Slugs));
    }

    [Fact]
    public async Task Restart_resumes_from_the_persisted_watermark()
    {
        await using var f = await Fixture.Create();
        var jobs = Enumerable.Range(0, 700).Select(i => f.AddJob(Now.AddDays(-3).AddSeconds(i))).ToArray();
        Assert.False(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        var first = f.EntityPresenceRequests.SelectMany(r => r.Slugs).ToHashSet();
        Assert.Equal(500, first.Count);

        f.Restart();
        f.EntityPresenceRequests.Clear();
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        var second = f.EntityPresenceRequests.SelectMany(r => r.Slugs).ToHashSet();
        Assert.Equal(jobs.Skip(500).Select(JobAuditOutboxService.JobSlug).ToHashSet(), second);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public async Task Failed_presence_check_never_creates_false_repairs_and_retries_same_jobs()
    {
        await using var f = await Fixture.Create();
        var jobs = Enumerable.Range(0, 600).Select(i => f.AddJob(Now.AddDays(-3).AddSeconds(i), events: 1)).ToArray();
        Assert.False(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        var afterFirst = f.ReadWatermark();
        foreach (var job in jobs.Skip(500)) f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(job), out _);

        f.FailEntityPresence = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        f.FailEntityPresence = false;
        f.FailRecordPresence = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using (var db = f.Db()) Assert.Empty(db.JobOutbox);
        Assert.Equal(afterFirst, f.ReadWatermark());

        f.FailRecordPresence = false;
        f.EntityPresenceRequests.Clear();
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Equal(jobs.Skip(500).Select(JobAuditOutboxService.JobSlug).ToHashSet(),
            f.EntityPresenceRequests.SelectMany(r => r.Slugs).ToHashSet());
        using (var db = f.Db())
            Assert.Equal(jobs.Skip(500).ToHashSet(), db.JobOutbox.Select(m => m.JobId).ToHashSet());
        Assert.Equal(f.Watermark(jobs[^1]), f.ReadWatermark());
    }

    [Theory]
    [InlineData("malformed-records")]
    [InlineData("malformed-entities")]
    [InlineData("unrequested-entity")]
    public async Task Invalid_success_response_is_not_interpreted_as_missing_history(string mode)
    {
        await using var f = await Fixture.Create();
        var job = f.AddJob(Now.AddHours(-1), events: 4);
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(job), out _);
        f.MalformedRecordPresence = mode == "malformed-records";
        f.MalformedEntityPresence = mode == "malformed-entities";
        f.UnrequestedEntity = mode == "unrequested-entity";
        await Assert.ThrowsAsync<JsonException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using var db = f.Db();
        Assert.Empty(db.JobOutbox);
        Assert.Null(f.ReadWatermark());
    }

    [Fact]
    public async Task Retention_expired_jobs_are_not_recreated()
    {
        await using var f = await Fixture.Create(retentionDays: 90);
        var expired = f.AddJob(Now.AddDays(-121), status: JobStatus.Completed, completedAt: Now.AddDays(-120), events: 2);
        var recent = f.AddJob(Now.AddDays(-11), status: JobStatus.Completed, completedAt: Now.AddDays(-10), events: 2);
        var touchedLately = f.AddJob(Now.AddDays(-200), status: JobStatus.Completed, completedAt: Now.AddDays(-199),
            eventsAt: Now.AddDays(-5));
        var idleSession = f.AddJob(Now.AddDays(-200), status: JobStatus.Running, capability: "ai-session",
            eventsAt: Now.AddDays(-100));
        var activeSession = f.AddJob(Now.AddDays(-150), status: JobStatus.Running, capability: "ai-session",
            eventsAt: Now.AddHours(-2));
        foreach (var job in new[] { expired, recent, touchedLately, idleSession, activeSession })
            f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(job), out _);
        foreach (var id in f.EventIds(expired).Concat(f.EventIds(recent)))
            f.PresentRecords.TryRemove(JobAuditOutboxService.EventExternalId(id), out _);

        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        // Run again in steady state: the idle live session is still left alone.
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));

        using var db = f.Db();
        var projections = db.JobOutbox.Where(m => m.Kind == JobOutboxKind.JobProjection).Select(m => m.JobId).ToHashSet();
        Assert.Equal(new[] { recent, touchedLately, activeSession }.ToHashSet(), projections);
        var events = db.JobOutbox.Where(m => m.Kind == JobOutboxKind.JobEvent).Select(m => m.JobId).ToList();
        Assert.Equal([recent, recent], events);
        // Past the horizon nothing is even asked about.
        Assert.DoesNotContain(JobAuditOutboxService.JobSlug(expired), f.EntityPresenceRequests.SelectMany(r => r.Slugs));
        Assert.DoesNotContain(JobAuditOutboxService.JobSlug(idleSession), f.EntityPresenceRequests.SelectMany(r => r.Slugs));
    }

    [Fact]
    public async Task Null_retention_keeps_repairing_old_gaps()
    {
        await using var f = await Fixture.Create(retentionDays: null);
        var ancient = f.AddJob(Now.AddDays(-400), status: JobStatus.Completed, completedAt: Now.AddDays(-400), events: 1);
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(ancient), out _);
        f.PresentRecords.TryRemove(JobAuditOutboxService.EventExternalId(f.EventIds(ancient)[0]), out _);
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        using (var db = f.Db())
        {
            Assert.Equal(2, db.JobOutbox.Count(m => m.JobId == ancient));
            // Projection before event, so the event finds its parent on delivery.
            Assert.Equal(JobOutboxKind.JobProjection, db.JobOutbox.OrderBy(m => m.Id).First().Kind);
        }
        var purge = await f.Worker.PurgeExpiredAsync(CancellationToken.None);
        Assert.Equal(0, purge.Jobs);
        using (var db = f.Db()) Assert.Single(db.Jobs);
    }

    [Fact]
    public async Task Unknown_retention_defers_reconciliation_and_a_persisted_horizon_survives_restart()
    {
        await using var f = await Fixture.Create(retentionDays: 90);
        var expired = f.AddJob(Now.AddDays(-121), status: JobStatus.Completed, completedAt: Now.AddDays(-120));
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(expired), out _);
        f.FailRetention = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Worker.ReconcileBatchAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Worker.PurgeExpiredAsync(CancellationToken.None));
        Assert.Empty(f.EntityPresenceRequests);

        f.FailRetention = false;
        await f.Worker.ReconcileBatchAsync(CancellationToken.None);
        f.Restart();
        f.FailRetention = true;
        f.EntityPresenceRequests.Clear();
        // The persisted 90 days still apply after a boot with RedLeaf's entity types unreadable.
        var purge = await f.Worker.PurgeExpiredAsync(CancellationToken.None);
        Assert.Equal(1, purge.Jobs);
        using var db = f.Db();
        Assert.Empty(db.JobOutbox);
    }

    [Fact]
    public async Task Purge_removes_expired_terminal_jobs_with_their_events_and_outbox_rows()
    {
        await using var f = await Fixture.Create(retentionDays: 90);
        var expired = f.AddJob(Now.AddDays(-121), status: JobStatus.Completed, completedAt: Now.AddDays(-120),
            eventsAt: Now.AddDays(-120), events: 3);
        f.AddOutbox(expired, created: Now.AddDays(-120), acknowledged: Now.AddDays(-120));
        f.AddOutbox(expired, created: Now.AddDays(-100), acknowledged: null);
        var failedLongAgo = f.AddJob(Now.AddDays(-300), status: JobStatus.Failed, completedAt: null, events: 0);
        var inHorizon = f.AddJob(Now.AddDays(-30), status: JobStatus.Completed, completedAt: Now.AddDays(-29), events: 2);
        var oldAck = f.AddOutbox(inHorizon, created: Now.AddDays(-100), acknowledged: Now.AddDays(-95));
        var newAck = f.AddOutbox(inHorizon, created: Now.AddDays(-29), acknowledged: Now.AddDays(-29));
        var running = f.AddJob(Now.AddDays(-200), status: JobStatus.Running, eventsAt: Now.AddDays(-200), events: 1);
        var queued = f.AddJob(Now.AddDays(-200), status: JobStatus.Queued);
        var session = f.AddJob(Now.AddDays(-200), status: JobStatus.Completed, capability: "ai-session",
            completedAt: Now.AddDays(-199), events: 1);
        var pendingDelivery = f.AddJob(Now.AddDays(-150), status: JobStatus.Completed, completedAt: Now.AddDays(-150));
        f.AddOutbox(pendingDelivery, created: Now.AddDays(-1), acknowledged: null);

        var purge = await f.Worker.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(2, purge.Jobs);
        Assert.Equal(1, purge.AcknowledgedOutbox);
        using var db = f.Db();
        Assert.Equal(new[] { inHorizon, running, queued, session, pendingDelivery }.ToHashSet(),
            db.Jobs.Select(j => j.Id).ToHashSet());
        Assert.False(db.JobEvents.Any(e => e.JobId == expired || e.JobId == failedLongAgo));
        Assert.False(db.JobOutbox.Any(m => m.JobId == expired));
        Assert.Equal(2, db.JobEvents.Count(e => e.JobId == inHorizon));
        Assert.Equal(1, db.JobEvents.Count(e => e.JobId == running));
        Assert.Equal(1, db.JobEvents.Count(e => e.JobId == session));
        Assert.False(db.JobOutbox.Any(m => m.Id == oldAck));
        Assert.True(db.JobOutbox.Any(m => m.Id == newAck));
        Assert.True(db.JobOutbox.Any(m => m.JobId == pendingDelivery && m.AcknowledgedAt == null));
    }

    [Fact]
    public async Task Purge_works_in_bounded_batches()
    {
        await using var f = await Fixture.Create(retentionDays: 30);
        var count = JobAuditOutboxService.PurgeBatchSize * 2 + 17;
        for (var i = 0; i < count; i++)
            f.AddJob(Now.AddDays(-60).AddSeconds(i), status: JobStatus.Completed, completedAt: Now.AddDays(-59), events: 1);
        var purge = await f.Worker.PurgeExpiredAsync(CancellationToken.None);
        Assert.Equal(count, purge.Jobs);
        using var db = f.Db();
        Assert.Empty(db.Jobs);
        Assert.Empty(db.JobEvents);
    }

    [Fact]
    public async Task A_full_pass_repairs_later_deletions_at_most_once_per_interval()
    {
        await using var f = await Fixture.Create();
        var jobs = Enumerable.Range(0, 10).Select(i => f.AddJob(Now.AddDays(-3).AddSeconds(i),
            status: JobStatus.Completed, completedAt: Now.AddDays(-3))).ToArray();
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        f.PresentEntities.TryRemove(JobAuditOutboxService.JobSlug(jobs[3]), out _);

        f.Clock = Now.AddDays(6);
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using (var db = f.Db()) Assert.Empty(db.JobOutbox);

        f.Clock = Now + JobAuditOutboxService.FullPassInterval + TimeSpan.FromHours(1);
        Assert.False(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.Null(f.ReadWatermark());
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        using (var db = f.Db()) Assert.Equal(jobs[3], Assert.Single(db.JobOutbox).JobId);
        // The next pass is a full interval away again.
        Assert.True(await f.Worker.ReconcileBatchAsync(CancellationToken.None));
        Assert.NotNull(f.ReadWatermark());
    }

    [Fact]
    public async Task Reconciliation_failure_does_not_stop_outbox_delivery()
    {
        await using var f = await Fixture.Create();
        var job = f.AddJob(Now.AddHours(-1), events: 1);
        f.FailEntityPresence = true;
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

    private sealed record PresenceRequest(string Type, string[] Slugs);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-audit-batch-" + Guid.NewGuid());
        private readonly WebApplication _server;
        private readonly Dictionary<Guid, List<Guid>> _events = [];
        private RedLeafStreamClient _client = null!;
        public JobAuditOutboxService Worker { get; private set; } = null!;
        public ConcurrentDictionary<string, byte> PresentRecords { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte> PresentEntities { get; } = new(StringComparer.Ordinal);
        public List<string[]> RecordPresenceRequests { get; } = [];
        public List<PresenceRequest> EntityPresenceRequests { get; } = [];
        public TaskCompletionSource ReconciliationFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Clock = Now;
        public int? RetentionDays;
        public bool FailRecordPresence, FailEntityPresence, FailRetention;
        public bool MalformedRecordPresence, MalformedEntityPresence, UnrequestedEntity;
        public int LegacyRecordQueries, LegacyEntityReads;
        private Fixture(WebApplication server) => _server = server;
        public RedComputeDbContext Db() => new(Path.Combine(_directory, "jobs.db"));
        public List<Guid> EventIds(Guid job) => _events[job];

        public static async Task<Fixture> Create(int? retentionDays = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var server = builder.Build();
            var f = new Fixture(server) { RetentionDays = retentionDays };
            Directory.CreateDirectory(f._directory);
            using (var db = f.Db())
            {
                db.Database.EnsureCreated();
                db.MigrateSchema();
            }
            // Single entity reads stay available for parent-id resolution on delivery; the
            // reconciler itself must never use them.
            server.MapGet("/api/entities/{slug}", (string slug) =>
            {
                if (slug.StartsWith("compute-job-", StringComparison.Ordinal) && !slug.Contains("event"))
                    Interlocked.Increment(ref f.LegacyEntityReads);
                return Results.Ok(new { id = Guid.Parse("11111111-1111-1111-1111-111111111111") });
            });
            server.MapGet("/api/entities", (HttpContext ctx) =>
            {
                if (f.FailRetention) return Results.StatusCode(503);
                Assert.Equal("entity-type", ctx.Request.Query["type"].ToString());
                var data = f.RetentionDays is { } days ? $"{{\"retention_days\":{days}}}" : "{}";
                return Results.Ok(new { items = new[] { new { slug = ctx.Request.Query["slug"].ToString(), data } }, total = 1 });
            });
            server.MapGet("/api/entity-types/{slug}", () => Results.Ok(new { type = new { slug = "compute-job" } }));
            server.MapPut("/api/entities/by-slug/{slug}", (string slug) =>
            {
                f.PresentEntities[slug] = 0;
                return Results.Ok(new { id = Guid.NewGuid() });
            });
            server.MapPost("/api/entities/exists", async (HttpContext ctx) =>
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var type = body.RootElement.GetProperty("type").GetString()!;
                var slugs = body.RootElement.GetProperty("slugs").EnumerateArray().Select(s => s.GetString()!).ToArray();
                lock (f.EntityPresenceRequests) f.EntityPresenceRequests.Add(new PresenceRequest(type, slugs));
                if (f.FailEntityPresence) return Results.StatusCode(503);
                if (f.MalformedEntityPresence) return Results.Ok(new { unexpected = true });
                var present = slugs.Where(s => f.PresentEntities.ContainsKey(s));
                if (f.UnrequestedEntity) present = present.Append("compute-job-unrequested");
                return Results.Ok(new { slugs = present.ToArray() });
            });
            server.MapGet("/api/streams/{stream}/records", () => { f.LegacyRecordQueries++; return Results.Ok(new { count = 1 }); });
            server.MapPost("/api/streams/{stream}/records/exists", async (HttpContext ctx) =>
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                var ids = body.RootElement.GetProperty("external_ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
                lock (f.RecordPresenceRequests) f.RecordPresenceRequests.Add(ids);
                if (f.FailRecordPresence) return Results.StatusCode(503);
                if (f.MalformedRecordPresence) return Results.Ok(new { unexpected = true });
                return Results.Ok(new { external_ids = ids.Where(id => f.PresentRecords.ContainsKey(id)).ToArray() });
            });
            server.MapPost("/api/streams/{stream}/records", async (HttpContext ctx) =>
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var record in body.RootElement.GetProperty("records").EnumerateArray())
                    f.PresentRecords[record.GetProperty("external_id").GetString()!] = 0;
                return Results.Ok(new { created = 1 });
            });
            server.Urls.Add("http://127.0.0.1:0");
            await server.StartAsync();
            f._client = new RedLeafStreamClient(server.Urls.Single(), "RedComputeTests", new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            f.Restart();
            return f;
        }

        /// <summary>A fresh worker on the same database, as after a RedCompute restart.</summary>
        public void Restart()
            => Worker = new JobAuditOutboxService(_client, (message, _) =>
            {
                if (message.Contains("Reconciliation deferred")) ReconciliationFailure.TrySetResult();
            }, Db, () => Clock);

        /// <summary>A job (terminal by default) mirrored to RedLeaf, entity and events present unless a test removes them.</summary>
        public Guid AddJob(DateTimeOffset queuedAt, JobStatus status = JobStatus.Completed, string capability = "tts",
            DateTimeOffset? completedAt = null, int events = 0, DateTimeOffset? eventsAt = null)
        {
            using var db = Db();
            var job = new JobRecord
            {
                Id = Guid.NewGuid(), CapabilitySlug = capability, ProviderName = "Local", InputJson = "{}",
                QueuedAt = queuedAt, Status = status, CompletedAt = completedAt,
            };
            db.Jobs.Add(job);
            var ids = new List<Guid>();
            if (eventsAt is not null && events == 0) events = 1;
            for (var i = 0; i < events; i++)
            {
                var evt = new JobLifecycleEvent
                {
                    JobId = job.Id, Kind = JobEventKind.Progress, DataJson = "{}",
                    OccurredAt = eventsAt ?? queuedAt,
                };
                db.JobEvents.Add(evt);
                ids.Add(evt.Id);
                PresentRecords[JobAuditOutboxService.EventExternalId(evt.Id)] = 0;
            }
            db.SaveChanges();
            _events[job.Id] = ids;
            PresentEntities[JobAuditOutboxService.JobSlug(job.Id)] = 0;
            return job.Id;
        }

        public long AddOutbox(Guid jobId, DateTimeOffset created, DateTimeOffset? acknowledged)
        {
            using var db = Db();
            var row = new JobOutboxMessage
            {
                Kind = JobOutboxKind.JobProjection, JobId = jobId, PayloadJson = "{}",
                CreatedAt = created, NextAttemptAt = acknowledged is null ? DateTimeOffset.MaxValue : created,
                AcknowledgedAt = acknowledged,
            };
            db.JobOutbox.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        public string? ReadWatermark()
        {
            using var db = Db();
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM JobAuditState WHERE Key = 'reconcile.watermark'";
            return command.ExecuteScalar() as string;
        }

        /// <summary>The watermark persisted after <paramref name="jobId"/> was the last job checked.</summary>
        public string Watermark(Guid jobId)
        {
            using var db = Db();
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, QueuedAt FROM Jobs WHERE upper(Id) = upper($id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = jobId.ToString();
            command.Parameters.Add(parameter);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return JsonSerializer.Serialize(new { QueuedAt = reader.GetString(1), Id = reader.GetString(0) });
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
