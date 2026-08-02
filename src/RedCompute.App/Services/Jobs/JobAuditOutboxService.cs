using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RedBamboo.AppHost.Streams;
using RedCompute.App.Data;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Services.Jobs;

/// <summary>
/// Ships the transactional job outbox to RedLeaf. Delivery is at-least-once locally and
/// idempotent remotely. Messages are never abandoned; failures only move next-attempt time.
/// A bounded reconciliation sweep also repairs remote deletion or historical mirror gaps.
/// </summary>
public sealed class JobAuditOutboxService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);
    private readonly RedLeafStreamClient _client;
    private readonly Action<string, Guid?> _log;
    private readonly Func<RedComputeDbContext> _dbFactory;
    private DateTimeOffset _nextReconcile = DateTimeOffset.UtcNow;
    private int _reconcileOffset;

    public JobAuditOutboxService(RedLeafStreamClient client, Action<string, Guid?> log,
        Func<RedComputeDbContext>? dbFactory = null)
    {
        _client = client;
        _log = log;
        _dbFactory = dbFactory ?? (() => new RedComputeDbContext());
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var processed = await ProcessBatchAsync(ct);
            if (DateTimeOffset.UtcNow >= _nextReconcile)
            {
                await ReconcileBatchAsync(ct);
                _nextReconcile = DateTimeOffset.UtcNow + ReconcileInterval;
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
                    await _client.UpsertEntityAsync(JobSlug(message.JobId), "compute-job", name, payload, ct);
                }
                else
                {
                    var eventId = message.JobEventId ?? message.MessageId;
                    await _client.AppendForEntityAsync("compute-job-events", JobSlug(message.JobId), payload,
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

    internal async Task ReconcileBatchAsync(CancellationToken ct)
    {
        List<JobRecord> jobs;
        using (var db = _dbFactory())
        {
            jobs = await db.Jobs.AsNoTracking()
                .OrderBy(j => j.QueuedAt).ThenBy(j => j.Id)
                .Skip(_reconcileOffset).Take(500).ToListAsync(ct);
            if (jobs.Count == 0)
            {
                _reconcileOffset = 0;
                return;
            }
            _reconcileOffset += jobs.Count;
        }

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _client.EntityExistsAsync(JobSlug(job.Id), ct))
                await EnsureProjectionOutboxAsync(job, ct);

            IReadOnlyList<JobLifecycleEvent> events;
            using (var db = _dbFactory())
                events = await db.JobEvents.AsNoTracking().Where(e => e.JobId == job.Id).ToListAsync(ct);
            foreach (var evt in events)
                if (!await _client.RecordExistsAsync("compute-job-events", EventExternalId(evt.Id), ct))
                    await EnsureEventOutboxAsync(evt, ct);
        }
    }

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
