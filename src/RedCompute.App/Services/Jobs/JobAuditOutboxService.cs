using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RedBamboo.AppHost.Streams;
using RedCompute.App.Data;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Jobs;

/// <summary>
/// Ships the transactional job outbox to RedLeaf. Delivery is at-least-once locally and
/// idempotent remotely. Messages are never abandoned; failures only move next-attempt time.
/// <para>
/// Reconciliation repairs remote mirror gaps incrementally. A persisted keyset watermark
/// (QueuedAt, Id) walks the Jobs table once, checking each batch's compute-job entities and
/// lifecycle records with ids-only presence calls, and advances only after every check in the
/// batch succeeded. Once caught up it checks only jobs queued since, plus the non-terminal jobs
/// (whose entity RedLeaf may delete by creation time while they still run). Every projection change
/// is already its own outbox row, delivered by idempotent upsert, so a terminal job behind the
/// watermark needs no re-check; a full pass from the start runs at most once per
/// <see cref="FullPassInterval"/> to repair later deletions (a manual delete, a RedLeaf restore).
/// </para>
/// <para>
/// RedLeaf deletes compute-job entities whose creation is older than the type's
/// <c>retention_days</c>. Reconciliation never recreates a job whose last activity is past that
/// horizon, and the local purge removes such terminal jobs with their events and outbox rows, so
/// both sides keep the same window. No retention (null or &lt;= 0) means no horizon: no purge and
/// unlimited repair.
/// </para>
/// </summary>
public sealed class JobAuditOutboxService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    /// <summary>Reconcile cadence while the watermark trails the table.</summary>
    internal static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(1);
    /// <summary>Reconcile cadence once the watermark is caught up: new and live jobs only.</summary>
    internal static readonly TimeSpan SteadyInterval = TimeSpan.FromMinutes(10);
    /// <summary>Jobs younger than this are left to their own first delivery before being checked.</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FullPassInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan HorizonRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan HorizonRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstPurgeDelay = TimeSpan.FromMinutes(10);

    internal const int ReconcileBatchSize = 500;
    internal const int LiveBatchSize = 500;
    private const int PresenceChunk = 1000;
    internal const int PurgeBatchSize = 200;
    internal const int MaxPurgedJobsPerRun = 10_000;
    private const int OutboxPruneBatch = 5_000;
    private const int MaxPrunedOutboxPerRun = 200_000;

    internal const string ComputeJobType = "compute-job";
    internal const string EventStream = "compute-job-events";
    /// <summary>
    /// AI session jobs are never purged: a session stopped months ago resumes on its recorded job
    /// (StartInvocation on that id), and the session stores reference it.
    /// </summary>
    internal const string SessionCapability = "ai-session";

    internal const string WatermarkKey = "reconcile.watermark";
    internal const string PassStartedKey = "reconcile.pass-started-at";
    internal const string HorizonKey = "retention.compute-job";

    private static readonly JobStatus[] TerminalStatuses =
        [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Skipped, JobStatus.TimedOut];
    private static readonly JobStatus[] LiveStatuses = [JobStatus.Queued, JobStatus.Running];

    private readonly RedLeafStreamClient _client;
    private readonly Action<string, Guid?> _log;
    private readonly Func<RedComputeDbContext> _dbFactory;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _nextReconcile;
    private DateTimeOffset _nextPurge;
    private RetentionHorizon? _horizon;
    private DateTimeOffset _nextHorizonRefresh = DateTimeOffset.MinValue;

    public JobAuditOutboxService(RedLeafStreamClient client, Action<string, Guid?> log,
        Func<RedComputeDbContext>? dbFactory = null, Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        _log = log;
        _dbFactory = dbFactory ?? (() => new RedComputeDbContext());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextReconcile = _clock();
        _nextPurge = _clock() + FirstPurgeDelay;
    }

    /// <summary>Context-free: continuations never run on the caller's SynchronizationContext (see DetachedWork).</summary>
    public Task RunAsync(CancellationToken ct) => DetachedWork.Start(() => RunLoopAsync(ct));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var processed = await ProcessBatchAsync(ct);
            if (_clock() >= _nextReconcile)
            {
                var caughtUp = false;
                try { caughtUp = await ReconcileBatchAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log($"[job-audit] Reconciliation deferred: {ex.Message}", null); }
                _nextReconcile = _clock() + (caughtUp ? SteadyInterval : CatchUpInterval);
            }
            if (_clock() >= _nextPurge)
            {
                try
                {
                    var purge = await PurgeExpiredAsync(ct);
                    if (purge.Jobs > 0 || purge.AcknowledgedOutbox > 0)
                        _log($"[job-audit] Retention purge removed {purge.Jobs} job(s) past {purge.RetentionDays} days " +
                             $"with their events and outbox rows, and {purge.AcknowledgedOutbox} acknowledged outbox row(s)", null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log($"[job-audit] Retention purge deferred: {ex.Message}", null); }
                _nextPurge = _clock() + PurgeInterval;
            }
            if (processed == 0)
                await Task.Delay(IdleDelay, ct);
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        List<JobOutboxMessage> pending;
        using (var db = _dbFactory())
        {
            var now = DateTimeOffset.UtcNow;
            pending = await db.JobOutbox.AsNoTracking()
                .Where(m => m.AcknowledgedAt == null && m.NextAttemptAt <= now)
                .OrderBy(m => m.Id).Take(100).ToListAsync(ct);
        }

        foreach (var message in pending)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(message.PayloadJson);
                if (message.Kind == JobOutboxKind.JobProjection)
                {
                    var name = payload.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()!
                        : payload.TryGetProperty("capability", out var cap) ? cap.GetString() ?? "Compute Job" : "Compute Job";
                    await _client.UpsertEntityAsync(JobSlug(message.JobId), ComputeJobType, name, payload, ct);
                }
                else
                {
                    var eventId = message.JobEventId ?? message.MessageId;
                    await _client.AppendForEntityAsync(EventStream, JobSlug(message.JobId), payload,
                        EventExternalId(eventId), createdAt: message.CreatedAt, ct: ct);
                }
                await AcknowledgeAsync(message.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await RescheduleAsync(message, ex, ct);
            }
        }
        return pending.Count;
    }

    /// <summary>
    /// One reconciliation step: the next batch past the watermark plus the live jobs behind it.
    /// Returns true when the watermark has caught up with the table (steady state).
    /// Throws, without moving the watermark, when the horizon is unknown or any presence check fails.
    /// </summary>
    internal async Task<bool> ReconcileBatchAsync(CancellationToken ct)
    {
        var now = _clock();
        var horizon = await GetHorizonAsync(ct);
        var cutoff = horizon.Cutoff(now);
        var settle = now - SettleDelay;

        var watermark = await ReadWatermarkAsync(ct);
        if (watermark.IsStart && await ReadStateAsync(PassStartedKey, ct) is null)
            await WriteStateAsync(PassStartedKey, now.ToUniversalTime().ToString("O"), ct);
        var scanned = await NextScanKeysAsync(watermark, settle, ct);

        List<JobFacts> scanJobs, liveJobs;
        List<(Guid Id, Guid JobId, DateTimeOffset OccurredAt)> scanEvents;
        var latestEvent = new Dictionary<Guid, DateTimeOffset>();
        Dictionary<Guid, DateTimeOffset> latestPending;
        using (var db = _dbFactory())
        {
            var scanIds = scanned.Select(k => k.Id).ToArray();
            scanJobs = await LoadFactsAsync(db, scanIds, ct);
            var liveIds = await db.Jobs.AsNoTracking()
                .Where(j => LiveStatuses.Contains(j.Status) && j.QueuedAt < settle && !scanIds.Contains(j.Id))
                .OrderBy(j => j.QueuedAt).Select(j => j.Id).Take(LiveBatchSize).ToArrayAsync(ct);
            liveJobs = await LoadFactsAsync(db, liveIds, ct);

            scanEvents = (await db.JobEvents.AsNoTracking().Where(e => scanIds.Contains(e.JobId))
                    .Select(e => new { e.Id, e.JobId, e.OccurredAt }).ToListAsync(ct))
                .Select(e => (e.Id, e.JobId, e.OccurredAt)).ToList();
            foreach (var evt in scanEvents)
                if (!latestEvent.TryGetValue(evt.JobId, out var seen) || evt.OccurredAt > seen)
                    latestEvent[evt.JobId] = evt.OccurredAt;
            // A live job can carry thousands of events: read only its newest, through the
            // (JobId, OccurredAt) index.
            foreach (var id in liveIds)
            {
                var newest = await db.JobEvents.AsNoTracking().Where(e => e.JobId == id)
                    .OrderByDescending(e => e.OccurredAt).Select(e => e.OccurredAt).Take(1).ToListAsync(ct);
                if (newest.Count == 1) latestEvent[id] = newest[0];
            }
            latestPending = await LatestPendingOutboxAsync(db, [.. scanIds, .. liveIds], ct);
        }

        bool InHorizon(JobFacts job) => cutoff is not { } limit
            || LastActivity(job, latestEvent.GetValueOrDefault(job.Id), latestPending.GetValueOrDefault(job.Id)) >= limit;
        var checkedScan = scanJobs.Where(InHorizon).ToList();
        var checkedLive = liveJobs.Where(InHorizon).ToList();
        var skipped = scanJobs.Count - checkedScan.Count + liveJobs.Count - checkedLive.Count;

        // Every presence check runs before any repair or watermark move: a failure leaves the
        // batch exactly where it was, to be checked again on the next step.
        var entitySlugs = checkedScan.Concat(checkedLive).Select(j => JobSlug(j.Id)).Distinct().ToArray();
        var presentEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in entitySlugs.Chunk(PresenceChunk))
            presentEntities.UnionWith(await _client.ExistingEntitySlugsAsync(ComputeJobType, chunk, ct));
        var missingJobs = checkedScan.Concat(checkedLive).Where(j => !presentEntities.Contains(JobSlug(j.Id)))
            .Select(j => j.Id).Distinct().ToArray();

        var checkedScanIds = checkedScan.Select(j => j.Id).ToHashSet();
        var eventIds = scanEvents.Where(e => checkedScanIds.Contains(e.JobId)).Select(e => e.Id).ToArray();
        var missingEvents = new List<Guid>();
        foreach (var chunk in eventIds.Chunk(PresenceChunk))
        {
            var present = await _client.ExistingRecordIdsAsync(EventStream, chunk.Select(EventExternalId).ToArray(), ct);
            missingEvents.AddRange(chunk.Where(id => !present.Contains(EventExternalId(id))));
        }

        // Projections first, so each repaired event's parent is delivered before it.
        foreach (var jobId in missingJobs)
        {
            JobRecord? job;
            using (var db = _dbFactory())
                job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is not null) await EnsureProjectionOutboxAsync(job, ct);
        }
        foreach (var chunk in missingEvents.Chunk(PresenceChunk))
        {
            List<JobLifecycleEvent> events;
            using (var db = _dbFactory())
                events = await db.JobEvents.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct);
            foreach (var evt in events) await EnsureEventOutboxAsync(evt, ct);
        }
        if (missingJobs.Length > 0 || missingEvents.Count > 0)
            _log($"[job-audit] Reconciliation queued {missingJobs.Length} projection and {missingEvents.Count} event repair(s)" +
                 (skipped > 0 ? $"; {skipped} job(s) past the {horizon.Days}-day retention horizon left alone" : ""), null);

        if (scanned.Count > 0)
        {
            await WriteStateAsync(WatermarkKey, JsonSerializer.Serialize(
                new WatermarkState(scanned[^1].QueuedAt, scanned[^1].RawId)), ct);
            return scanned.Count < ReconcileBatchSize;
        }

        // Caught up. Start the next full pass once the current one is old enough.
        var passStarted = await ReadStateAsync(PassStartedKey, ct) is { } text
            && DateTimeOffset.TryParse(text, out var started) ? started : now;
        if (!watermark.IsStart && now - passStarted >= FullPassInterval)
        {
            await DeleteStateAsync(WatermarkKey, ct);
            await WriteStateAsync(PassStartedKey, now.ToUniversalTime().ToString("O"), ct);
            _log("[job-audit] Starting a full reconciliation pass", null);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes local terminal jobs past the RedLeaf retention horizon, with their events and outbox
    /// rows, in bounded transactions, and prunes acknowledged outbox rows older than the horizon.
    /// Never touches non-terminal jobs, AI session jobs, or a job with activity (including an
    /// unacknowledged outbox row) inside the horizon. No horizon, no purge.
    /// </summary>
    internal async Task<PurgeResult> PurgeExpiredAsync(CancellationToken ct)
    {
        var horizon = await GetHorizonAsync(ct);
        if (horizon.Cutoff(_clock()) is not { } cutoff) return new PurgeResult(horizon.Days, 0, 0);

        List<Guid> candidates;
        using (var db = _dbFactory())
            // A coarse prefilter: last activity is never earlier than QueuedAt or CompletedAt.
            candidates = await db.Jobs.AsNoTracking()
                .Where(j => TerminalStatuses.Contains(j.Status) && j.CapabilitySlug != SessionCapability
                    && j.QueuedAt < cutoff && (j.CompletedAt == null || j.CompletedAt < cutoff))
                .OrderBy(j => j.QueuedAt).Select(j => j.Id).ToListAsync(ct);

        var purged = 0;
        foreach (var chunk in candidates.Chunk(PurgeBatchSize))
        {
            if (purged >= MaxPurgedJobsPerRun) break;
            ct.ThrowIfCancellationRequested();
            purged += await PurgeChunkAsync(chunk, cutoff, ct);
        }

        var pruned = 0;
        while (pruned < MaxPrunedOutboxPerRun)
        {
            ct.ThrowIfCancellationRequested();
            using var db = _dbFactory();
            var ids = await db.JobOutbox.AsNoTracking()
                .Where(m => m.AcknowledgedAt != null && m.AcknowledgedAt < cutoff)
                .OrderBy(m => m.Id).Select(m => m.Id).Take(OutboxPruneBatch).ToListAsync(ct);
            if (ids.Count == 0) break;
            pruned += await db.JobOutbox.Where(m => ids.Contains(m.Id) && m.AcknowledgedAt != null).ExecuteDeleteAsync(ct);
        }
        return new PurgeResult(horizon.Days, purged, pruned);
    }

    private async Task<int> PurgeChunkAsync(Guid[] ids, DateTimeOffset cutoff, CancellationToken ct)
    {
        using var db = _dbFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Decided again inside the write transaction: a job resumed or touched since selection stays.
        var jobs = await LoadFactsAsync(db, ids, ct);
        var latestEvent = (await db.JobEvents.AsNoTracking().Where(e => ids.Contains(e.JobId))
                .Select(e => new { e.JobId, e.OccurredAt }).ToListAsync(ct))
            .GroupBy(e => e.JobId).ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAt));
        var latestPending = await LatestPendingOutboxAsync(db, ids, ct);
        var expired = jobs
            .Where(j => JobTrackingService.IsTerminal(j.Status) && j.CapabilitySlug != SessionCapability
                && LastActivity(j, latestEvent.GetValueOrDefault(j.Id), latestPending.GetValueOrDefault(j.Id)) < cutoff)
            .Select(j => j.Id).ToArray();
        if (expired.Length == 0) return 0;

        await db.JobEvents.Where(e => expired.Contains(e.JobId)).ExecuteDeleteAsync(ct);
        await db.JobOutbox.Where(m => expired.Contains(m.JobId)).ExecuteDeleteAsync(ct);
        var deleted = await db.Jobs.Where(j => expired.Contains(j.Id) && TerminalStatuses.Contains(j.Status))
            .ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>
    /// A job's last activity: the latest of its terminal time, start, newest lifecycle event and
    /// newest undelivered outbox row, else its queue time. Acknowledged outbox rows do not count:
    /// a past repair re-sending an old projection is not activity.
    /// </summary>
    internal static DateTimeOffset LastActivity(JobFacts job, DateTimeOffset? latestEvent, DateTimeOffset? latestPending)
    {
        var last = job.QueuedAt;
        foreach (var at in new[] { job.StartedAt, job.CompletedAt, latestEvent, latestPending })
            if (at is { } value && value > last) last = value;
        return last;
    }

    internal sealed record JobFacts(Guid Id, JobStatus Status, string CapabilitySlug,
        DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    internal sealed record PurgeResult(int? RetentionDays, int Jobs, int AcknowledgedOutbox);

    private static Task<List<JobFacts>> LoadFactsAsync(RedComputeDbContext db, Guid[] ids, CancellationToken ct)
        => ids.Length == 0
            ? Task.FromResult(new List<JobFacts>())
            : db.Jobs.AsNoTracking().Where(j => ids.Contains(j.Id))
                .Select(j => new JobFacts(j.Id, j.Status, j.CapabilitySlug, j.QueuedAt, j.StartedAt, j.CompletedAt))
                .ToListAsync(ct);

    private static async Task<Dictionary<Guid, DateTimeOffset>> LatestPendingOutboxAsync(
        RedComputeDbContext db, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.JobOutbox.AsNoTracking()
            .Where(m => m.AcknowledgedAt == null && ids.Contains(m.JobId))
            .Select(m => new { m.JobId, m.CreatedAt }).ToListAsync(ct);
        return rows.GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.Max(r => r.CreatedAt));
    }

    // ---- Retention horizon ----

    /// <summary>The compute-job retention RedLeaf applies, in days; null means none.</summary>
    internal sealed record RetentionHorizon(int? Days)
    {
        public DateTimeOffset? Cutoff(DateTimeOffset now) => Days is > 0 ? now.AddDays(-Days.Value) : null;
    }

    /// <summary>
    /// Cached, refreshed hourly, persisted so a boot with RedLeaf down still knows it. Unknown
    /// (never read) throws: repairing or purging against a guessed horizon is never safe.
    /// </summary>
    internal async Task<RetentionHorizon> GetHorizonAsync(CancellationToken ct)
    {
        _horizon ??= await ReadStateAsync(HorizonKey, ct) is { } persisted
            ? JsonSerializer.Deserialize<RetentionHorizon>(persisted)
            : null;
        var now = _clock();
        if (_horizon is not null && now < _nextHorizonRefresh) return _horizon;
        try
        {
            var days = await _client.EntityTypeRetentionDaysAsync(ComputeJobType, ct);
            if (_horizon?.Days != days)
                _log($"[job-audit] compute-job retention is {(days is > 0 ? $"{days} days" : "unset")}", null);
            _horizon = new RetentionHorizon(days);
            _nextHorizonRefresh = now + HorizonRefreshInterval;
            await WriteStateAsync(HorizonKey, JsonSerializer.Serialize(_horizon), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _nextHorizonRefresh = now + HorizonRetryInterval;
            if (_horizon is null)
                throw new InvalidOperationException($"compute-job retention is unknown until RedLeaf answers: {ex.Message}", ex);
        }
        return _horizon!;
    }

    // ---- Watermark ----

    internal sealed record WatermarkState(string QueuedAt, string Id)
    {
        public static readonly WatermarkState Start = new("", "");
        [JsonIgnore] public bool IsStart => QueuedAt.Length == 0;
    }

    private sealed record ScanKey(Guid Id, string QueuedAt, string RawId);

    private async Task<WatermarkState> ReadWatermarkAsync(CancellationToken ct)
        => await ReadStateAsync(WatermarkKey, ct) is { } json
            && JsonSerializer.Deserialize<WatermarkState>(json) is { } state
                ? state
                : WatermarkState.Start;

    /// <summary>
    /// Keyset over the stored text of (QueuedAt, Id), the order SQLite sorts them in, so the walk
    /// is exact whatever offset a timestamp was written with.
    /// </summary>
    private async Task<List<ScanKey>> NextScanKeysAsync(WatermarkState after, DateTimeOffset settle, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, QueuedAt FROM Jobs
            WHERE QueuedAt < $settle AND QueuedAt >= $queuedAt AND (QueuedAt > $queuedAt OR Id > $id)
            ORDER BY QueuedAt, Id
            LIMIT $limit
            """;
        Parameter(command, "$settle", settle.ToUniversalTime().ToString("O"));
        Parameter(command, "$queuedAt", after.QueuedAt);
        Parameter(command, "$id", after.Id);
        Parameter(command, "$limit", ReconcileBatchSize);
        var keys = new List<ScanKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rawId = reader.GetString(0);
            keys.Add(new ScanKey(Guid.Parse(rawId), reader.GetString(1), rawId));
        }
        return keys;
    }

    // ---- Keyed state (JobAuditState) ----

    private async Task<string?> ReadStateAsync(string key, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM JobAuditState WHERE Key = $key";
        Parameter(command, "$key", key);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task WriteStateAsync(string key, string value, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JobAuditState (Key, Value, UpdatedAt) VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt
            """;
        Parameter(command, "$key", key);
        Parameter(command, "$value", value);
        Parameter(command, "$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task DeleteStateAsync(string key, CancellationToken ct)
    {
        using var db = _dbFactory();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM JobAuditState WHERE Key = $key";
        Parameter(command, "$key", key);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // ---- Outbox rows ----

    private async Task EnsureProjectionOutboxAsync(JobRecord job, CancellationToken ct)
    {
        using var db = _dbFactory();
        var alreadyPending = await db.JobOutbox.AnyAsync(m => m.Kind == JobOutboxKind.JobProjection &&
            m.JobId == job.Id && m.AcknowledgedAt == null, ct);
        if (alreadyPending) return;
        db.JobOutbox.Add(new JobOutboxMessage
        {
            Kind = JobOutboxKind.JobProjection,
            JobId = job.Id,
            PayloadJson = JobTrackingService.SerializeProjection(job),
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureEventOutboxAsync(JobLifecycleEvent evt, CancellationToken ct)
    {
        using var db = _dbFactory();
        var alreadyPending = await db.JobOutbox.AnyAsync(m => m.Kind == JobOutboxKind.JobEvent &&
            m.JobEventId == evt.Id && m.AcknowledgedAt == null, ct);
        if (alreadyPending) return;
        db.JobOutbox.Add(new JobOutboxMessage
        {
            Kind = JobOutboxKind.JobEvent,
            JobId = evt.JobId,
            JobEventId = evt.Id,
            PayloadJson = JobTrackingService.SerializeEvent(evt),
            CreatedAt = evt.OccurredAt,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task AcknowledgeAsync(long id, CancellationToken ct)
    {
        using var db = _dbFactory();
        var row = await db.JobOutbox.FindAsync([id], ct);
        if (row == null) return;
        row.AcknowledgedAt = DateTimeOffset.UtcNow;
        row.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task RescheduleAsync(JobOutboxMessage message, Exception ex, CancellationToken ct)
    {
        using var db = _dbFactory();
        var row = await db.JobOutbox.FindAsync([message.Id], ct);
        if (row == null) return;
        row.Attempts++;
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8)));
        row.NextAttemptAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        row.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        await db.SaveChangesAsync(ct);
        if (row.Attempts == 1 || row.Attempts % 10 == 0)
            _log($"[job-audit] RedLeaf delivery retry {row.Attempts} for outbox {row.Id}: {row.LastError}", row.JobId);
    }

    internal static string JobSlug(Guid id) => $"compute-job-{id:N}";
    internal static string EventExternalId(Guid id) => $"compute-job-event:{id:N}";
}
