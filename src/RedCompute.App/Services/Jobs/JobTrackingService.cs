using System.IO;
using RedCompute.App.Data;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Services.Jobs;

public class JobTrackingService
{
    private readonly object _lock = new();

    public event Action<JobRecord>? JobCreated;
    public event Action<JobRecord>? JobUpdated;

    public JobRecord CreateJob(string capabilitySlug, string providerName, string inputJson, string? callerInfo = null, string? idempotencyKey = null, string? name = null, string? rationale = null)
    {
        using var db = new RedComputeDbContext();

        if (idempotencyKey != null)
        {
            var existing = db.Jobs.FirstOrDefault(j => j.IdempotencyKey == idempotencyKey);
            if (existing != null)
                return existing;
        }

        var job = new JobRecord
        {
            CapabilitySlug = capabilitySlug,
            ProviderName = providerName,
            InputJson = inputJson,
            CallerInfo = callerInfo,
            IdempotencyKey = idempotencyKey,
            Name = name,
            Rationale = rationale,
            Status = JobStatus.Queued,
            QueuedAt = DateTimeOffset.UtcNow
        };

        db.Jobs.Add(job);
        db.SaveChanges();
        JobCreated?.Invoke(job);
        return job;
    }

    public void MarkRunning(Guid jobId)
    {
        using var db = new RedComputeDbContext();
        var job = db.Jobs.Find(jobId);
        if (job == null) return;

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        JobUpdated?.Invoke(job);
    }

    public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null, string? contentType = null, string? resultJson = null)
    {
        using var db = new RedComputeDbContext();
        var job = db.Jobs.Find(jobId);
        if (job == null) return;

        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Progress = 1.0;
        job.OutputLocation = outputLocation;
        job.OutputSizeBytes = outputSizeBytes;
        job.OutputContentType = contentType;
        job.ResultJson = resultJson;
        db.SaveChanges();
        JobUpdated?.Invoke(job);
    }

    public void UpdateProgress(Guid jobId, double progress)
    {
        using var db = new RedComputeDbContext();
        var job = db.Jobs.Find(jobId);
        if (job == null) return;

        job.Progress = Math.Clamp(progress, 0.0, 1.0);
        db.SaveChanges();
        JobUpdated?.Invoke(job);
    }

    public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null)
    {
        using var db = new RedComputeDbContext();
        var job = db.Jobs.Find(jobId);
        if (job == null) return;

        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = errorMessage;
        job.ErrorDetails = errorDetails;
        db.SaveChanges();
        JobUpdated?.Invoke(job);
    }

    public void MarkCancelled(Guid jobId)
    {
        using var db = new RedComputeDbContext();
        var job = db.Jobs.Find(jobId);
        if (job == null) return;

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        JobUpdated?.Invoke(job);
    }

    public List<JobRecord> GetJobs(string? capabilitySlug = null, JobStatus? status = null, int limit = 50, int offset = 0)
    {
        using var db = new RedComputeDbContext();
        IQueryable<JobRecord> query = db.Jobs.OrderByDescending(j => j.QueuedAt);

        if (capabilitySlug != null)
            query = query.Where(j => j.CapabilitySlug == capabilitySlug);
        if (status != null)
            query = query.Where(j => j.Status == status);

        return query.Skip(offset).Take(limit).ToList();
    }

    public JobRecord? GetJob(Guid id)
    {
        using var db = new RedComputeDbContext();
        return db.Jobs.Find(id);
    }

    public List<JobRecord> GetJobsSince(DateTimeOffset since)
    {
        using var db = new RedComputeDbContext();
        return db.Jobs
            .Where(j => j.QueuedAt >= since
                || (j.CompletedAt != null && j.CompletedAt >= since)
                || j.Status == JobStatus.Running
                || j.Status == JobStatus.Queued)
            .OrderBy(j => j.QueuedAt)
            .ToList();
    }

    public int CleanupOldJobs(int retentionDays)
    {
        using var db = new RedComputeDbContext();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var old = db.Jobs.Where(j => j.QueuedAt < cutoff).ToList();

        var outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "outputs");

        foreach (var job in old)
        {
            if (job.OutputLocation != null && File.Exists(job.OutputLocation))
            {
                try { File.Delete(job.OutputLocation); } catch { }
            }
            // Clean up extra clip files (Suno variations)
            for (var i = 1; i <= 4; i++)
            {
                var clipPath = Path.Combine(outputDir, $"{job.Id}_clip{i}.mp3");
                if (File.Exists(clipPath))
                    try { File.Delete(clipPath); } catch { }
            }
        }

        db.Jobs.RemoveRange(old);
        return db.SaveChanges();
    }
}
