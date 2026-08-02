using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public interface IJobTracker
{
    JobRecord CreateJob(string capabilitySlug, string providerName, string inputJson,
        string? callerInfo = null, string? idempotencyKey = null,
        string? name = null, string? rationale = null,
        string? userId = null, string? userName = null, string? userAvatarUrl = null);
    void MarkRunning(Guid jobId);

    /// <summary>Create a job with required structured provenance.</summary>
    JobRecord CreateJob(JobSubmission submission)
        => CreateJob(submission.CapabilitySlug, submission.ProviderName, submission.InputJson,
            submission.CallerInfo, submission.IdempotencyKey, submission.Name, submission.Rationale,
            submission.Provenance.OnBehalfOf.Id,
            submission.Provenance.OnBehalfOf.NameSnapshot,
            submission.Provenance.OnBehalfOf.AvatarSnapshot);

    /// <summary>Start a distinct invocation of an existing job with its own audit identity.</summary>
    void StartInvocation(Guid jobId, JobProvenance provenance, JobEventKind kind = JobEventKind.Started)
        => MarkRunning(jobId);
    void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
        string? contentType = null, string? resultJson = null, double? costUsd = null);
    void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null);
    void MarkCancelled(Guid jobId);
    void MarkSkipped(Guid jobId, string reason, string? resultJson = null)
        => MarkCancelled(jobId);
    void MarkTimedOut(Guid jobId, string reason, string? resultJson = null)
        => MarkFailed(jobId, reason, resultJson: resultJson);
    void UpdateName(Guid jobId, string name);
    JobRecord? GetJob(Guid jobId) => null;

    IReadOnlyList<JobLifecycleEvent> GetJobEvents(Guid jobId) => [];

    /// <summary>
    /// Restore a job whose source record survived while the job row did not. Implementations must
    /// preserve the supplied identity and timestamps, and must return the existing row when the
    /// idempotency key has already been imported.
    /// </summary>
    JobRecord RestoreJob(JobRecord job) => throw new NotSupportedException("Historical job restoration is not supported by this tracker");
}
