using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RedCompute.App.Data;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Jobs;

public class JobTrackingService : IJobTracker
{
    private static readonly JsonSerializerOptions JsonOptions = JobProvenance.JsonOptions;
    private readonly object _lock = new();
    private readonly Func<RedComputeDbContext> _dbFactory;
    private readonly Func<DateTimeOffset> _clock;

    public JobTrackingService(Func<RedComputeDbContext>? dbFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _dbFactory = dbFactory ?? (() => new RedComputeDbContext());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<JobRecord>? JobCreated;
    public event Action<JobRecord>? JobUpdated;
    public event Action<JobLifecycleEvent>? JobEventAppended;

    /// <summary>
    /// Compatibility boundary for older in-process providers. It records an honest audit gap
    /// instead of inventing a Local User. New HTTP and SDK paths use the structured overload.
    /// </summary>
    public JobRecord CreateJob(string capabilitySlug, string providerName, string inputJson,
        string? callerInfo = null, string? idempotencyKey = null, string? name = null,
        string? rationale = null, string? userId = null, string? userName = null,
        string? userAvatarUrl = null)
    {
        var hasRealUser = !string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "local-user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase);
        var provenance = new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redcompute",
                new JobAppReference("legacy-in-process", "redcompute-provider", null, callerInfo ?? providerName),
                new JobEntrypoint("sdk", "IJobTracker.CreateJob")),
            new JobActor("app", callerInfo ?? providerName, Id: "redcompute-provider"),
            hasRealUser
                ? new JobBeneficiary("user", userId, userName, userAvatarUrl)
                : new JobBeneficiary("system", Reason: "Legacy in-process submission did not provide a beneficiary"),
            [], new JobTrace(), JobProvenanceAssurance.Unknown, DateTimeOffset.UtcNow);

        return CreateJob(new JobSubmission(capabilitySlug, providerName, inputJson, provenance,
            callerInfo, idempotencyKey, name, rationale));
    }

    public JobRecord CreateJob(JobSubmission submission)
    {
        submission.Provenance.ValidateForNewJob();
        var scope = string.IsNullOrWhiteSpace(submission.IdempotencyScope)
            ? ComputeIdempotencyScope(submission.CapabilitySlug, submission.Provenance)
            : ComputeExplicitIdempotencyScope(submission.IdempotencyScope);
        var fingerprint = ComputeIdempotencyFingerprint(submission);

        JobRecord result = null!;
        var created = false;
        JobLifecycleEvent? createdEvent = null;
        JobLifecycleEvent? reuseEvent = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();

            if (!string.IsNullOrWhiteSpace(submission.IdempotencyKey))
            {
                var candidates = db.Jobs
                    .Where(j => j.IdempotencyKey == submission.IdempotencyKey && j.IdempotencyScope == scope)
                    .OrderBy(j => j.QueuedAt)
                    .ToList();
                var exact = candidates.FirstOrDefault(j => j.IdempotencyFingerprint == fingerprint);
                if (exact != null)
                {
                    exact.IsIdempotencyReuse = true;
                    reuseEvent = AppendEvent(db, exact, JobEventKind.Reused, submission.Provenance,
                        new { idempotencyKey = submission.IdempotencyKey });
                    db.SaveChanges();
                    tx.Commit();
                    result = exact;
                }
                else if (candidates.Count > 0)
                    throw new IdempotencyConflictException(submission.IdempotencyKey!, candidates[0].Id);
            }

            if (reuseEvent == null)
            {
                result = new JobRecord
                {
                    CapabilitySlug = submission.CapabilitySlug,
                    ProviderName = submission.ProviderName,
                    InputJson = submission.InputJson,
                    CallerInfo = submission.CallerInfo,
                    IdempotencyKey = submission.IdempotencyKey,
                    IdempotencyScope = scope,
                    IdempotencyFingerprint = fingerprint,
                    Name = submission.Name,
                    Rationale = submission.Rationale,
                    UserId = submission.Provenance.OnBehalfOf.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
                        ? submission.Provenance.OnBehalfOf.Id : null,
                    UserName = submission.Provenance.OnBehalfOf.NameSnapshot,
                    UserAvatarUrl = submission.Provenance.OnBehalfOf.AvatarSnapshot,
                    CreationProvenance = submission.Provenance,
                    ExternalExecution = submission.ExternalExecution,
                    ParentJobId = Guid.TryParse(submission.Provenance.Trace.ParentJobId, out var parentJobId)
                        ? parentJobId : null,
                    Status = JobStatus.Queued,
                    QueuedAt = _clock(),
                };

                db.Jobs.Add(result);
                createdEvent = AppendEvent(db, result, JobEventKind.Created, submission.Provenance);
                EnqueueProjection(db, result);
                db.SaveChanges();
                tx.Commit();
                created = true;
            }
        }

        if (created)
        {
            JobCreated?.Invoke(result);
            JobEventAppended?.Invoke(createdEvent!);
        }
        else if (reuseEvent != null)
        {
            JobEventAppended?.Invoke(reuseEvent);
        }
        return result;
    }

    public JobRecord RestoreJob(JobRecord job)
    {
        JobLifecycleEvent? createdEvent = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();

            job.CreationProvenance ??= BackfilledUnknown(job);
            job.IdempotencyScope ??= ComputeIdempotencyScope(job.CapabilitySlug, job.CreationProvenance);
            if (job.IdempotencyKey != null)
            {
                var existing = db.Jobs.FirstOrDefault(j => j.IdempotencyKey == job.IdempotencyKey &&
                    j.IdempotencyScope == job.IdempotencyScope &&
                    (job.IdempotencyFingerprint == null || j.IdempotencyFingerprint == job.IdempotencyFingerprint));
                if (existing != null) return existing;
            }
            if (db.Jobs.Find(job.Id) is { } existingById) return existingById;

            job.IdempotencyFingerprint ??= ComputeIdempotencyFingerprint(new JobSubmission(
                job.CapabilitySlug, job.ProviderName, job.InputJson, job.CreationProvenance,
                job.CallerInfo, job.IdempotencyKey, job.Name, job.Rationale,
                ExternalExecution: job.ExternalExecution));
            db.Jobs.Add(job);
            createdEvent = AppendEvent(db, job, JobEventKind.Created, job.CreationProvenance,
                new { restored = true, assurance = "unknown" });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
        }
        JobCreated?.Invoke(job);
        JobEventAppended?.Invoke(createdEvent!);
        return job;
    }

    /// <summary>
    /// Import one exact legacy execution record as a terminal external Compute job.
    /// The supplied deterministic id and idempotency key make the migration replayable;
    /// original timestamps remain the lifecycle timestamps instead of pretending the
    /// work happened when the importer ran.
    /// </summary>
    public JobRecord BackfillExternalJob(JobRecord job, DateTimeOffset? scheduledFor,
        string source)
    {
        if (!job.ExternalExecution)
            throw new ArgumentException("A historical external job must set ExternalExecution", nameof(job));
        if (!IsTerminal(job.Status))
            throw new ArgumentException("A historical external job must already be terminal", nameof(job));
        if (job.CreationProvenance is null)
            throw new ArgumentException("Historical jobs require provenance", nameof(job));
        if (job.CreationProvenance.Assurance is not
            (JobProvenanceAssurance.BackfilledExact or JobProvenanceAssurance.BackfilledInferred))
            throw new ArgumentException("Historical imports require backfilled provenance assurance", nameof(job));
        job.CreationProvenance.ValidateForNewJob();

        job.IdempotencyKey ??= $"external-history:{job.Id:N}";
        job.IdempotencyScope = ComputeIdempotencyScope(job.CapabilitySlug, job.CreationProvenance);
        job.IdempotencyFingerprint = ComputeIdempotencyFingerprint(new JobSubmission(
            job.CapabilitySlug, job.ProviderName, job.InputJson, job.CreationProvenance,
            job.CallerInfo, job.IdempotencyKey, job.Name, job.Rationale,
            ExternalExecution: true));
        job.StartedAt ??= job.QueuedAt;
        job.CompletedAt ??= job.StartedAt;
        job.Progress = job.Status == JobStatus.Completed ? 1 : job.Progress;
        job.AttemptCount = Math.Max(1, job.AttemptCount);
        job.LeaseOwner = null;
        job.LeaseTokenHash = null;
        job.LeaseExpiresAt = null;
        job.ParentJobId ??= Guid.TryParse(job.CreationProvenance.Trace.ParentJobId, out var parentJobId)
            ? parentJobId : null;
        job.UserId = job.CreationProvenance.OnBehalfOf.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? job.CreationProvenance.OnBehalfOf.Id : null;
        job.UserName = job.CreationProvenance.OnBehalfOf.NameSnapshot;
        job.UserAvatarUrl = job.CreationProvenance.OnBehalfOf.AvatarSnapshot;

        var events = new List<JobLifecycleEvent>();
        var created = false;
        JobRecord result;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            if (db.Jobs.Find(job.Id) is { } existingById)
            {
                if (!string.Equals(existingById.IdempotencyFingerprint, job.IdempotencyFingerprint,
                        StringComparison.Ordinal))
                    throw new IdempotencyConflictException(job.IdempotencyKey, existingById.Id);
                existingById.IsIdempotencyReuse = true;
                return existingById;
            }

            var existing = db.Jobs.FirstOrDefault(candidate =>
                candidate.IdempotencyKey == job.IdempotencyKey
                && candidate.IdempotencyScope == job.IdempotencyScope);
            if (existing is not null)
            {
                if (!string.Equals(existing.IdempotencyFingerprint, job.IdempotencyFingerprint,
                        StringComparison.Ordinal))
                    throw new IdempotencyConflictException(job.IdempotencyKey, existing.Id);
                existing.IsIdempotencyReuse = true;
                return existing;
            }

            db.Jobs.Add(job);
            events.Add(AppendEvent(db, job, JobEventKind.Created, job.CreationProvenance,
                new { restored = true, source }, job.QueuedAt));
            events.Add(AppendEvent(db, job, JobEventKind.Scheduled, job.CreationProvenance,
                new { scheduledFor = scheduledFor ?? job.QueuedAt, restored = true, source },
                scheduledFor ?? job.QueuedAt));
            events.Add(AppendEvent(db, job, JobEventKind.Started, job.CreationProvenance,
                new { restored = true, source }, job.StartedAt));
            var terminalKind = job.Status switch
            {
                JobStatus.Completed => JobEventKind.Completed,
                JobStatus.Failed => JobEventKind.Failed,
                JobStatus.Cancelled => JobEventKind.Cancelled,
                JobStatus.Skipped => JobEventKind.Skipped,
                JobStatus.TimedOut => JobEventKind.TimedOut,
                _ => throw new InvalidOperationException($"Unsupported historical status {job.Status}"),
            };
            events.Add(AppendEvent(db, job, terminalKind, job.CreationProvenance,
                new { restored = true, source, error = job.ErrorMessage }, job.CompletedAt));
            events.Add(AppendEvent(db, job, JobEventKind.Backfilled, job.CreationProvenance,
                new { source, originalQueuedAt = job.QueuedAt, originalCompletedAt = job.CompletedAt },
                _clock()));
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            result = job;
            created = true;
        }

        if (created)
        {
            JobCreated?.Invoke(result);
            foreach (var evt in events) JobEventAppended?.Invoke(evt);
        }
        return result;
    }

    /// <summary>
    /// Conservatively annotates pre-contract rows. Exact session/job and user links are
    /// retained as context/snapshots, but legacy Source/CallerInfo strings never become
    /// verified actors. Rows with unresolved roles remain explicit audit gaps.
    /// </summary>
    public int BackfillLegacyJobs(IEnumerable<UnifiedSessionInfo> sessions)
    {
        var byJob = sessions.Where(s => s.JobId.HasValue)
            .GroupBy(s => s.JobId!.Value).ToDictionary(g => g.Key, g => g.First());
        var changed = 0;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            foreach (var job in db.Jobs.Where(j => j.CreationProvenanceJson == null).ToList())
            {
                byJob.TryGetValue(job.Id, out var session);
                var provenance = session == null ? BackfilledUnknown(job) : BackfilledFromSession(job, session);
                job.CreationProvenance = provenance;
                if (session != null && (string.IsNullOrWhiteSpace(job.UserId) || job.UserId == "local-user"))
                    job.UserId = session.UserId;
                job.UserName ??= session?.UserName;
                job.UserAvatarUrl ??= session?.UserAvatarUrl;
                job.IdempotencyFingerprint ??= ComputeIdempotencyFingerprint(new JobSubmission(
                    job.CapabilitySlug, job.ProviderName, job.InputJson, provenance,
                    job.CallerInfo, job.IdempotencyKey, job.Name, job.Rationale,
                    ExternalExecution: job.ExternalExecution));
                job.IdempotencyScope ??= $"legacy:{job.Id:N}";
                if (!db.JobEvents.Any(e => e.JobId == job.Id))
                    AppendEvent(db, job, JobEventKind.Created, provenance, new
                    {
                        migrated = true,
                        sessionLink = session == null ? "unknown" : "exact",
                        actor = "unknown",
                    });
                EnqueueProjection(db, job);
                changed++;
            }
            db.SaveChanges();
            tx.Commit();
        }
        return changed;
    }

    public void MarkRunning(Guid jobId)
    {
        var job = GetJob(jobId);
        if (job == null) return;
        var provenance = job.CreationProvenance ?? BackfilledUnknown(job);
        StartInvocation(jobId, provenance, job.StartedAt.HasValue ? JobEventKind.Resumed : JobEventKind.Started);
    }

    public void StartInvocation(Guid jobId, JobProvenance provenance, JobEventKind kind = JobEventKind.Started)
    {
        if (kind is not (JobEventKind.Started or JobEventKind.Resumed or JobEventKind.Retried or JobEventKind.Rerun))
            throw new ArgumentOutOfRangeException(nameof(kind), "Invocation kind must start work");
        provenance.ValidateForNewJob();

        Mutate(jobId, job =>
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.CompletedAt = null;
            job.Progress = 0;
            job.ErrorMessage = null;
            job.ErrorDetails = null;
        }, kind, provenance);
    }

    public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
        string? contentType = null, string? resultJson = null, double? costUsd = null)
        => Mutate(jobId, job =>
        {
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Progress = 1.0;
            job.OutputLocation = outputLocation;
            job.OutputSizeBytes = outputSizeBytes;
            job.OutputContentType = contentType;
            job.ResultJson = resultJson;
            job.CostUsd = costUsd;
        }, JobEventKind.Completed, data: new { outputLocation, outputSizeBytes, contentType, costUsd });

    public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null)
        => Mutate(jobId, job =>
        {
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ErrorMessage = errorMessage;
            job.ErrorDetails = errorDetails;
            if (resultJson != null) job.ResultJson = resultJson;
        }, JobEventKind.Failed, data: new { errorMessage, errorDetails });

    public void MarkCancelled(Guid jobId)
        => Mutate(jobId, job =>
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }, JobEventKind.Cancelled);

    public void MarkSkipped(Guid jobId, string reason, string? resultJson = null)
        => Mutate(jobId, job =>
        {
            job.Status = JobStatus.Skipped;
            job.CompletedAt = _clock();
            job.ErrorMessage = null;
            job.ErrorDetails = null;
            job.ResultJson = resultJson;
            job.LeaseOwner = null;
            job.LeaseTokenHash = null;
            job.LeaseExpiresAt = null;
        }, JobEventKind.Skipped, data: new { reason });

    public void MarkTimedOut(Guid jobId, string reason, string? resultJson = null)
        => Mutate(jobId, job =>
        {
            job.Status = JobStatus.TimedOut;
            job.CompletedAt = _clock();
            job.ErrorMessage = reason;
            job.ResultJson = resultJson;
            job.LeaseOwner = null;
            job.LeaseTokenHash = null;
            job.LeaseExpiresAt = null;
        }, JobEventKind.TimedOut, data: new { reason });

    public ExternalJobLease ClaimExternalJob(Guid jobId, string workerId,
        TimeSpan leaseDuration, JobProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("workerId is required", nameof(workerId));
        provenance.ValidateForNewJob();
        leaseDuration = ClampLease(leaseDuration);

        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        ExternalJobLease lease;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = db.Jobs.Find(jobId)
                ?? throw new KeyNotFoundException($"Job {jobId} not found");
            if (!job.ExternalExecution)
                throw new JobLeaseConflictException(jobId, "Only externally executed jobs can be leased");
            if (IsTerminal(job.Status))
                throw new JobLeaseConflictException(jobId, $"Job is already {job.Status}");

            var now = _clock();
            if (job.Status == JobStatus.Running && job.LeaseExpiresAt > now)
                throw new JobLeaseConflictException(jobId,
                    $"Job is leased by '{job.LeaseOwner ?? "another worker"}' until {job.LeaseExpiresAt:O}");

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expiresAt = now.Add(leaseDuration);
            var kind = job.AttemptCount == 0 ? JobEventKind.Claimed : JobEventKind.Retried;

            job.Status = JobStatus.Running;
            job.StartedAt ??= now;
            job.CompletedAt = null;
            job.Progress ??= 0;
            job.ErrorMessage = null;
            job.ErrorDetails = null;
            job.LeaseOwner = workerId.Trim();
            job.LeaseTokenHash = HashLeaseToken(token);
            job.LeaseExpiresAt = expiresAt;
            job.AttemptCount++;

            evt = AppendEvent(db, job, kind, provenance, new
            {
                workerId = job.LeaseOwner,
                leaseExpiresAt = expiresAt,
                attempt = job.AttemptCount,
            });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
            lease = new ExternalJobLease(job.Id, token, job.LeaseOwner, expiresAt, job.AttemptCount);
        }

        JobUpdated?.Invoke(updated);
        JobEventAppended?.Invoke(evt!);
        return lease;
    }

    public ExternalJobLease RenewExternalLease(Guid jobId, string leaseToken,
        TimeSpan leaseDuration, JobProvenance? provenance = null)
    {
        leaseDuration = ClampLease(leaseDuration);
        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        ExternalJobLease renewed;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = RequireExternalLease(db, jobId, leaseToken);
            var expiresAt = _clock().Add(leaseDuration);
            job.LeaseExpiresAt = expiresAt;
            evt = AppendEvent(db, job, JobEventKind.LeaseRenewed, provenance,
                new { workerId = job.LeaseOwner, leaseExpiresAt = expiresAt, attempt = job.AttemptCount });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
            renewed = new ExternalJobLease(job.Id, leaseToken, job.LeaseOwner!, expiresAt, job.AttemptCount);
        }
        JobUpdated?.Invoke(updated);
        JobEventAppended?.Invoke(evt!);
        return renewed;
    }

    public void ReportExternalProgress(Guid jobId, string leaseToken, double progress,
        string? message = null, object? data = null)
    {
        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = RequireExternalLease(db, jobId, leaseToken);
            job.Progress = Math.Clamp(progress, 0, 1);
            evt = AppendEvent(db, job, JobEventKind.Progress, data: new
            {
                progress = job.Progress,
                message,
                detail = data,
            });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
        }
        JobUpdated?.Invoke(updated);
        JobEventAppended?.Invoke(evt!);
    }

    public void ReportExternalRetry(Guid jobId, string leaseToken, int retryNumber,
        TimeSpan delay, string? error = null)
    {
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = RequireExternalLease(db, jobId, leaseToken);
            evt = AppendEvent(db, job, JobEventKind.Retried, data: new
            {
                retryNumber,
                delaySeconds = Math.Max(0, delay.TotalSeconds),
                error,
                workerId = job.LeaseOwner,
                leaseAttempt = job.AttemptCount,
            });
            db.SaveChanges();
            tx.Commit();
        }
        JobEventAppended?.Invoke(evt!);
    }

    public void LinkExternalChild(Guid jobId, string leaseToken, Guid childJobId,
        string relation = "child")
    {
        JobLifecycleEvent? evt = null;
        JobRecord? childUpdated = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = RequireExternalLease(db, jobId, leaseToken);
            var child = db.Jobs.Find(childJobId)
                ?? throw new KeyNotFoundException($"Child job {childJobId} not found");
            if (child.Id == job.Id)
                throw new JobLeaseValidationException(job.Id, "A job cannot be linked as its own child");
            var ownsChild = !string.Equals(relation, "reference",
                StringComparison.OrdinalIgnoreCase);
            if (ownsChild)
            {
                if (child.ParentJobId is { } existingParent && existingParent != job.Id)
                    throw new JobLeaseValidationException(job.Id,
                        $"Child job {child.Id} already belongs to parent {existingParent}");
                child.ParentJobId = job.Id;
                EnqueueProjection(db, child);
            }
            evt = AppendEvent(db, job, JobEventKind.ChildLinked, data: new
            {
                childJobId,
                childCapability = child.CapabilitySlug,
                relation,
                ownsChild,
            });
            db.SaveChanges();
            tx.Commit();
            childUpdated = ownsChild ? child : null;
        }
        if (childUpdated is not null) JobUpdated?.Invoke(childUpdated);
        JobEventAppended?.Invoke(evt!);
    }

    public void AppendJobEvent(Guid jobId, JobEventKind kind, object? data = null,
        JobProvenance? provenance = null)
    {
        if (kind is JobEventKind.Created or JobEventKind.Completed or JobEventKind.Failed
            or JobEventKind.Cancelled or JobEventKind.Skipped or JobEventKind.TimedOut)
            throw new ArgumentOutOfRangeException(nameof(kind), "Use the lifecycle transition method for terminal and creation events");
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = db.Jobs.Find(jobId)
                ?? throw new KeyNotFoundException($"Job {jobId} not found");
            evt = AppendEvent(db, job, kind, provenance, data);
            db.SaveChanges();
            tx.Commit();
        }
        JobEventAppended?.Invoke(evt!);
    }

    public void CompleteExternalJob(Guid jobId, string leaseToken,
        string? resultJson = null, string? outputLocation = null,
        long? outputSizeBytes = null, string? contentType = null, double? costUsd = null)
        => FinishExternalJob(jobId, leaseToken, JobStatus.Completed, JobEventKind.Completed,
            resultJson, outputLocation, outputSizeBytes, contentType, costUsd: costUsd);

    public void FailExternalJob(Guid jobId, string leaseToken, string error,
        string? errorDetails = null, string? resultJson = null)
        => FinishExternalJob(jobId, leaseToken, JobStatus.Failed, JobEventKind.Failed,
            resultJson, error: error, errorDetails: errorDetails);

    public void SkipExternalJob(Guid jobId, string leaseToken, string reason,
        string? resultJson = null)
        => FinishExternalJob(jobId, leaseToken, JobStatus.Skipped, JobEventKind.Skipped,
            resultJson, reason: reason);

    public void TimeoutExternalJob(Guid jobId, string leaseToken, string reason,
        string? resultJson = null)
        => FinishExternalJob(jobId, leaseToken, JobStatus.TimedOut, JobEventKind.TimedOut,
            resultJson, error: reason, reason: reason);

    public void CancelExternalJob(Guid jobId, string leaseToken, string? reason = null)
        => FinishExternalJob(jobId, leaseToken, JobStatus.Cancelled, JobEventKind.Cancelled,
            reason: reason);

    public void SetJobCost(Guid jobId, double costUsd)
        => MutateProjection(jobId, job => job.CostUsd = costUsd);

    public void UpdateProgress(Guid jobId, double progress)
    {
        JobRecord? updated = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            var job = db.Jobs.Find(jobId);
            if (job == null) return;
            job.Progress = Math.Clamp(progress, 0.0, 1.0);
            db.SaveChanges();
            updated = job;
        }
        JobUpdated?.Invoke(updated);
    }

    public void UpdateName(Guid jobId, string name)
        => MutateProjection(jobId, job => job.Name = name);

    public int RecoverOrphanedJobs()
    {
        var changed = new List<(JobRecord Job, JobLifecycleEvent Event)>();
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            // Externally executed jobs are protected by durable leases. A RedCompute
            // restart must not manufacture a failure while the RedLeaf worker is still
            // running, and a suite restart must leave the same attempt reclaimable once
            // its lease expires.
            var orphaned = db.Jobs.Where(j => !j.ExternalExecution &&
                (j.Status == JobStatus.Running || j.Status == JobStatus.Queued)).ToList();
            foreach (var job in orphaned)
            {
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.ErrorMessage = "Interrupted by application restart";
                var evt = AppendEvent(db, job, JobEventKind.Failed, data: new { reason = "application-restart" });
                EnqueueProjection(db, job);
                changed.Add((job, evt));
            }
            db.SaveChanges();
            tx.Commit();
        }
        foreach (var (job, evt) in changed)
        {
            JobUpdated?.Invoke(job);
            JobEventAppended?.Invoke(evt);
        }
        return changed.Count;
    }

    public (List<JobRecord> Items, int TotalCount) GetJobs(
        string? capabilitySlug = null, JobStatus? status = null, string? caller = null,
        string? search = null, int limit = 50, int offset = 0,
        string? originApp = null, string? originApi = null, string? actor = null,
        string? beneficiary = null, string? assurance = null, bool? complete = null,
        Guid? parentJobId = null, string? contextKind = null, string? contextId = null,
        bool? externalExecution = null, string? executionId = null)
    {
        using var db = _dbFactory();
        IQueryable<JobRecord> query = db.Jobs.OrderByDescending(j => j.QueuedAt);

        if (capabilitySlug != null) query = query.Where(j => j.CapabilitySlug == capabilitySlug);
        if (status != null) query = query.Where(j => j.Status == status);
        if (caller != null) query = query.Where(j => j.CallerInfo == caller);
        if (parentJobId != null) query = query.Where(j => j.ParentJobId == parentJobId);
        if (externalExecution != null) query = query.Where(j => j.ExternalExecution == externalExecution);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(j =>
                (j.Name != null && j.Name.Contains(search)) || j.ProviderName.Contains(search) ||
                (j.CallerInfo != null && j.CallerInfo.Contains(search)) ||
                (j.CreationProvenanceJson != null && j.CreationProvenanceJson.Contains(search)) ||
                j.CapabilitySlug.Contains(search));

        // Provenance filters operate on the typed contract. Substring matching JSON can
        // confuse origin app IDs with actor/beneficiary IDs and is not an audit boundary.
        IEnumerable<JobRecord> filtered = query.ToList();
        if (originApp != null) filtered = filtered.Where(j => Eq(j.CreationProvenance?.Origin.App.Id, originApp));
        if (originApi != null) filtered = filtered.Where(j => Eq(j.CreationProvenance?.Origin.Entrypoint.Route, originApi));
        if (actor != null) filtered = filtered.Where(j =>
        {
            var a = j.CreationProvenance?.Actor;
            return Eq(a?.EntityId, actor) || Eq(a?.Id, actor) || Eq(a?.NameSnapshot, actor);
        });
        if (beneficiary != null) filtered = filtered.Where(j =>
        {
            var b = j.CreationProvenance?.OnBehalfOf;
            return Eq(b?.Id, beneficiary) || Eq(b?.NameSnapshot, beneficiary) ||
                (b?.Kind == "system" && Eq($"system: {b.Reason}", beneficiary));
        });
        if (assurance != null) filtered = filtered.Where(j =>
            Eq(AssuranceWireValue(j.CreationProvenance?.Assurance), assurance));
        if (complete != null) filtered = filtered.Where(j => IsAuditComplete(j.CreationProvenance) == complete);
        if (contextKind != null || contextId != null) filtered = filtered.Where(j =>
            j.CreationProvenance?.Context.Any(c =>
                (contextKind == null || Eq(c.Kind, contextKind)) &&
                (contextId == null || Eq(c.Id, contextId) || Eq(c.EntityId, contextId))) == true);
        if (executionId != null) filtered = filtered.Where(j =>
            j.CreationProvenance?.Context.Any(c =>
                Eq(c.Kind, "execution") && Eq(c.Id, executionId)) == true);

        var materialized = filtered.ToList();
        return (materialized.Skip(offset).Take(limit).ToList(), materialized.Count);
    }

    public JobRecord? GetJob(Guid id)
    {
        using var db = _dbFactory();
        return db.Jobs.Find(id);
    }

    public IReadOnlyList<JobLifecycleEvent> GetJobEvents(Guid jobId)
    {
        using var db = _dbFactory();
        return db.JobEvents.AsNoTracking().Where(e => e.JobId == jobId)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToList();
    }

    /// <summary>
    /// Conservatively enriches a pre-contract job from durable RedLeaf evidence. Verified or
    /// asserted creation provenance is never rewritten. The original Created event stays
    /// immutable; the migration is recorded as its own append-only Backfilled event.
    /// </summary>
    public bool ApplyBackfillEvidence(Guid jobId, JobProvenance evidence,
        IReadOnlyDictionary<string, string>? fieldAssurance = null, string? ruleVersion = null,
        string? source = null)
    {
        if (evidence.SchemaVersion != JobProvenance.CurrentSchemaVersion ||
            evidence.Assurance is not (JobProvenanceAssurance.BackfilledExact
                or JobProvenanceAssurance.BackfilledInferred or JobProvenanceAssurance.Unknown))
            throw new JobProvenanceValidationException("Backfill evidence requires schema version 1 and a backfilled/unknown assurance");

        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = db.Jobs.Find(jobId);
            if (job == null) return false;
            var current = job.CreationProvenance ?? BackfilledUnknown(job);
            if (current.Assurance is JobProvenanceAssurance.Verified or JobProvenanceAssurance.Asserted)
                return false;

            var contexts = current.Context.Concat(evidence.Context)
                .GroupBy(c => $"{c.Kind}:{c.Id}:{c.EntityId}:{c.Route}", StringComparer.Ordinal)
                .Select(g => g.First()).ToArray();
            var mayUpgradeLegacy = current.Assurance == JobProvenanceAssurance.Unknown;
            var origin = mayUpgradeLegacy && !IsUnknown(evidence.Origin.App.Id)
                ? evidence.Origin : current.Origin;
            var actor = mayUpgradeLegacy && !IsUnknown(evidence.Actor.Kind)
                ? evidence.Actor : current.Actor;
            var beneficiary = mayUpgradeLegacy && IsUnverifiedLegacyBeneficiary(current.OnBehalfOf) &&
                !IsUnknown(evidence.OnBehalfOf.Kind)
                ? evidence.OnBehalfOf : current.OnBehalfOf;
            var assurance = evidence.Assurance == JobProvenanceAssurance.Unknown
                ? current.Assurance : evidence.Assurance;
            var enriched = current with
            {
                Origin = origin,
                Actor = actor,
                OnBehalfOf = beneficiary,
                Context = contexts,
                Assurance = assurance,
            };
            if (enriched.ToJson() == current.ToJson()) return false;

            job.CreationProvenance = enriched;
            job.UserId = enriched.OnBehalfOf.Kind == "user" ? enriched.OnBehalfOf.Id : null;
            job.UserName = enriched.OnBehalfOf.NameSnapshot;
            job.UserAvatarUrl = enriched.OnBehalfOf.AvatarSnapshot;
            evt = AppendEvent(db, job, JobEventKind.Backfilled, enriched,
                new { source, ruleVersion, fieldAssurance });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
        }
        JobUpdated?.Invoke(updated!);
        JobEventAppended?.Invoke(evt!);
        return true;
    }

    public static bool IsAuditComplete(JobProvenance? p)
    {
        if (p == null || p.Assurance == JobProvenanceAssurance.Unknown) return false;
        if (string.IsNullOrWhiteSpace(p.Origin.Service) || IsUnknown(p.Origin.Service)
            || string.IsNullOrWhiteSpace(p.Origin.App.Id)
            || string.IsNullOrWhiteSpace(p.Origin.App.NameSnapshot)
            || IsUnknown(p.Origin.App.Id) || string.IsNullOrWhiteSpace(p.Origin.Entrypoint.Route)
            || IsUnknown(p.Origin.Entrypoint.Route)) return false;
        if (string.IsNullOrWhiteSpace(p.Actor.Kind) || string.IsNullOrWhiteSpace(p.Actor.NameSnapshot)
            || p.Actor.Kind == "unknown"
            || (string.IsNullOrWhiteSpace(p.Actor.EntityId) && string.IsNullOrWhiteSpace(p.Actor.Id))) return false;
        return p.OnBehalfOf.Kind switch
        {
            "user" => !string.IsNullOrWhiteSpace(p.OnBehalfOf.Id) && p.OnBehalfOf.Id != "local-user",
            "system" => !string.IsNullOrWhiteSpace(p.OnBehalfOf.Reason),
            _ => false,
        };
    }

    private static bool Eq(string? left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static string AssuranceWireValue(JobProvenanceAssurance? assurance) => assurance switch
    {
        JobProvenanceAssurance.Verified => "verified",
        JobProvenanceAssurance.Asserted => "asserted",
        JobProvenanceAssurance.BackfilledExact => "backfilled-exact",
        JobProvenanceAssurance.BackfilledInferred => "backfilled-inferred",
        _ => "unknown",
    };

    private static bool IsUnknown(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Contains("unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnverifiedLegacyBeneficiary(JobBeneficiary beneficiary)
        => IsUnknown(beneficiary.Kind) ||
           (beneficiary.Kind.Equals("system", StringComparison.OrdinalIgnoreCase) &&
            beneficiary.Reason?.Contains("legacy", StringComparison.OrdinalIgnoreCase) == true);

    public List<JobRecord> GetJobsSince(DateTimeOffset since)
    {
        using var db = _dbFactory();
        return db.Jobs.Where(j => j.QueuedAt >= since ||
                (j.CompletedAt != null && j.CompletedAt >= since) ||
                j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
            .OrderBy(j => j.QueuedAt).ToList();
    }

    public int CleanupOldJobs(int retentionDays)
    {
        using var db = _dbFactory();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var old = db.Jobs.Where(j => j.QueuedAt < cutoff).ToList();
        var ids = old.Select(j => j.Id).ToList();
        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute", "outputs");
        foreach (var job in old)
        {
            if (job.OutputLocation != null && File.Exists(job.OutputLocation))
                try { File.Delete(job.OutputLocation); } catch { }
            for (var i = 1; i <= 4; i++)
            {
                var clipPath = Path.Combine(outputDir, $"{job.Id}_clip{i}.mp3");
                if (File.Exists(clipPath)) try { File.Delete(clipPath); } catch { }
            }
        }
        db.JobEvents.RemoveRange(db.JobEvents.Where(e => ids.Contains(e.JobId)));
        db.JobOutbox.RemoveRange(db.JobOutbox.Where(e => ids.Contains(e.JobId)));
        db.Jobs.RemoveRange(old);
        return db.SaveChanges();
    }

    private void FinishExternalJob(Guid jobId, string leaseToken,
        JobStatus status, JobEventKind kind, string? resultJson = null,
        string? outputLocation = null, long? outputSizeBytes = null,
        string? contentType = null, string? error = null, string? errorDetails = null,
        string? reason = null, double? costUsd = null)
    {
        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = RequireExternalLease(db, jobId, leaseToken);
            job.Status = status;
            job.CompletedAt = _clock();
            job.Progress = status == JobStatus.Completed ? 1 : job.Progress;
            job.ResultJson = resultJson;
            job.OutputLocation = outputLocation;
            job.OutputSizeBytes = outputSizeBytes;
            job.OutputContentType = contentType;
            job.ErrorMessage = error;
            job.ErrorDetails = errorDetails;
            job.CostUsd = costUsd;
            job.LeaseOwner = null;
            job.LeaseTokenHash = null;
            job.LeaseExpiresAt = null;
            evt = AppendEvent(db, job, kind, data: new
            {
                reason,
                error,
                errorDetails,
                outputLocation,
                outputSizeBytes,
                contentType,
                costUsd,
            });
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
        }
        JobUpdated?.Invoke(updated);
        JobEventAppended?.Invoke(evt!);
    }

    private JobRecord RequireExternalLease(
        RedComputeDbContext db, Guid jobId, string leaseToken)
    {
        var job = db.Jobs.Find(jobId)
            ?? throw new KeyNotFoundException($"Job {jobId} not found");
        if (!job.ExternalExecution)
            throw new JobLeaseValidationException(jobId, "Job is not externally executed");
        if (job.Status != JobStatus.Running)
            throw new JobLeaseValidationException(jobId, $"Job is {job.Status}, not running");
        if (string.IsNullOrWhiteSpace(leaseToken) || string.IsNullOrWhiteSpace(job.LeaseTokenHash) ||
            !LeaseTokenMatches(leaseToken, job.LeaseTokenHash))
            throw new JobLeaseValidationException(jobId, "Lease token is invalid or has been superseded");
        if (job.LeaseExpiresAt is null || job.LeaseExpiresAt <= _clock())
            throw new JobLeaseValidationException(jobId,
                $"Lease expired at {job.LeaseExpiresAt:O}; reclaim the same attempt before writing");
        return job;
    }

    private static string HashLeaseToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool LeaseTokenMatches(string token, string expectedHash)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { return false; }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static TimeSpan ClampLease(TimeSpan requested)
    {
        if (requested < TimeSpan.FromSeconds(15)) return TimeSpan.FromSeconds(15);
        if (requested > TimeSpan.FromMinutes(30)) return TimeSpan.FromMinutes(30);
        return requested;
    }

    public static bool IsTerminal(JobStatus status)
        => status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled
            or JobStatus.Skipped or JobStatus.TimedOut;

    private void Mutate(Guid jobId, Action<JobRecord> apply, JobEventKind kind,
        JobProvenance? provenance = null, object? data = null)
    {
        JobRecord? updated = null;
        JobLifecycleEvent? evt = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = db.Jobs.Find(jobId);
            if (job == null) return;
            apply(job);
            evt = AppendEvent(db, job, kind, provenance, data);
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
        }
        JobUpdated?.Invoke(updated);
        JobEventAppended?.Invoke(evt!);
    }

    private void MutateProjection(Guid jobId, Action<JobRecord> apply)
    {
        JobRecord? updated = null;
        lock (_lock)
        {
            using var db = _dbFactory();
            using var tx = db.Database.BeginTransaction();
            var job = db.Jobs.Find(jobId);
            if (job == null) return;
            apply(job);
            EnqueueProjection(db, job);
            db.SaveChanges();
            tx.Commit();
            updated = job;
        }
        JobUpdated?.Invoke(updated);
    }

    private static JobLifecycleEvent AppendEvent(RedComputeDbContext db, JobRecord job,
        JobEventKind kind, JobProvenance? provenance = null, object? data = null,
        DateTimeOffset? occurredAt = null)
    {
        // A lifecycle transition without a more specific caller identity still belongs to
        // the immutable execution that created the job. Explicit event provenance wins for
        // worker claims, resumes, reruns, and backfill evidence.
        provenance ??= job.CreationProvenance;
        var evt = new JobLifecycleEvent
        {
            JobId = job.Id,
            Kind = kind,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            Provenance = provenance,
            DataJson = JsonSerializer.Serialize(data ?? new { status = job.Status.ToString() }, JsonOptions),
        };
        db.JobEvents.Add(evt);
        db.JobOutbox.Add(new JobOutboxMessage
        {
            Kind = JobOutboxKind.JobEvent,
            JobId = job.Id,
            JobEventId = evt.Id,
            PayloadJson = SerializeEvent(evt),
        });
        return evt;
    }

    private static void EnqueueProjection(RedComputeDbContext db, JobRecord job)
        => db.JobOutbox.Add(new JobOutboxMessage
        {
            Kind = JobOutboxKind.JobProjection,
            JobId = job.Id,
            PayloadJson = SerializeProjection(job),
        });

    internal static string SerializeProjection(JobRecord job) => JsonSerializer.Serialize(new
    {
        jobId = job.Id,
        capability = job.CapabilitySlug,
        provider = job.ProviderName,
        status = job.Status.ToString(),
        queuedAt = job.QueuedAt,
        startedAt = job.StartedAt,
        completedAt = job.CompletedAt,
        inputJson = job.InputJson,
        outputLocation = job.OutputLocation,
        outputSizeBytes = job.OutputSizeBytes,
        outputContentType = job.OutputContentType,
        resultJson = job.ResultJson,
        errorMessage = job.ErrorMessage,
        callerInfo = job.CallerInfo,
        rationale = job.Rationale,
        costUsd = job.CostUsd,
        durationMs = job.DurationMs,
        userId = job.UserId,
        userName = job.UserName,
        userAvatarUrl = job.UserAvatarUrl,
        externalExecution = job.ExternalExecution,
        parentJobId = job.ParentJobId,
        leaseOwner = job.LeaseOwner,
        leaseExpiresAt = job.LeaseExpiresAt,
        attemptCount = job.AttemptCount,
        creationProvenance = job.CreationProvenance,
        recordingService = "redcompute",
    }, JsonOptions);

    internal static string SerializeEvent(JobLifecycleEvent evt) => JsonSerializer.Serialize(new
    {
        eventId = evt.Id,
        jobId = evt.JobId,
        kind = $"job.{evt.Kind.ToString().ToLowerInvariant()}",
        occurredAt = evt.OccurredAt,
        provenance = evt.Provenance,
        data = JsonSerializer.Deserialize<JsonElement>(evt.DataJson),
        recordingService = "redcompute",
    }, JsonOptions);

    private static string ComputeIdempotencyFingerprint(JobSubmission submission)
    {
        var p = submission.Provenance;
        var material = string.Join('\n', submission.CapabilitySlug, submission.ProviderName, submission.InputJson,
            submission.ExternalExecution,
            p.Origin.Service, p.Origin.App.Kind, p.Origin.App.Id, p.Origin.App.EntityId,
            p.Origin.Entrypoint.Kind, p.Origin.Entrypoint.Method, p.Origin.Entrypoint.Route,
            p.Actor.Kind, p.Actor.EntityId, p.Actor.Id,
            p.OnBehalfOf.Kind, p.OnBehalfOf.Id, p.OnBehalfOf.Reason, SemanticContext(p));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string ComputeIdempotencyScope(string capability, JobProvenance p)
    {
        var material = string.Join("\n", capability, p.Origin.Service, p.Origin.App.Kind,
            p.Origin.App.Id, p.Origin.Entrypoint.Kind, p.Origin.Entrypoint.Method, p.Origin.Entrypoint.Route,
            p.Actor.Kind, p.Actor.EntityId, p.Actor.Id,
            p.OnBehalfOf.Kind, p.OnBehalfOf.Id, p.OnBehalfOf.Reason, SemanticContext(p));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string ComputeExplicitIdempotencyScope(string scope)
    {
        var material = $"external\n{scope}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string SemanticContext(JobProvenance provenance)
        => string.Join('|', provenance.Context
            .Select(c => $"{c.Kind}:{c.Id}:{c.EntityId}:{c.Route}")
            .OrderBy(value => value, StringComparer.Ordinal));

    private static JobProvenance BackfilledUnknown(JobRecord job) => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute",
            new JobAppReference("legacy", "unknown", null, job.CallerInfo ?? "Unknown legacy origin"),
            new JobEntrypoint("legacy", "unknown")),
        new JobActor("unknown", job.CallerInfo ?? "Unknown legacy actor", Id: "unknown"),
        !string.IsNullOrWhiteSpace(job.UserId) && !string.Equals(job.UserId, "local-user", StringComparison.OrdinalIgnoreCase)
            ? new JobBeneficiary("user", job.UserId, job.UserName, job.UserAvatarUrl)
            : new JobBeneficiary("system", Reason: "Legacy record has no verifiable beneficiary"),
        [], new JobTrace(), JobProvenanceAssurance.Unknown, job.QueuedAt);

    private static JobProvenance BackfilledFromSession(JobRecord job, UnifiedSessionInfo session)
    {
        var source = string.IsNullOrWhiteSpace(session.Source) ? "unknown" : session.Source.Trim();
        var realUser = !string.IsNullOrWhiteSpace(session.UserId)
            && session.UserId is not ("local-user" or "system");
        var context = new List<JobContextReference>
        {
            new("session", session.Id, NameSnapshot: session.Title ?? session.ProjectName),
        };
        if (!string.IsNullOrWhiteSpace(session.ProviderSessionId))
            context.Add(new("provider-session", session.ProviderSessionId));
        return new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redcompute",
                new JobAppReference("legacy-source", source.ToLowerInvariant(), null,
                    source == "unknown" ? "Unknown legacy origin" : source),
                new JobEntrypoint("legacy", "session-store/job-link")),
            new JobActor("unknown", "Unknown legacy actor", Id: "unknown"),
            realUser
                ? new JobBeneficiary("user", session.UserId, session.UserName, session.UserAvatarUrl)
                : new JobBeneficiary("system", Reason: "Legacy session has no verifiable beneficiary"),
            context, new JobTrace(), JobProvenanceAssurance.Unknown, job.QueuedAt);
    }

}
