using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public interface IJobTracker
{
    JobRecord CreateJob(string capabilitySlug, string providerName, string inputJson,
        string? callerInfo = null, string? idempotencyKey = null,
        string? name = null, string? rationale = null);
    void MarkRunning(Guid jobId);
    void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
        string? contentType = null, string? resultJson = null);
    void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null);
    void MarkCancelled(Guid jobId);
}
