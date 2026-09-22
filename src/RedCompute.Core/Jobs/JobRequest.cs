namespace RedCompute.Core.Jobs;

public class JobRequest
{
    public Guid? JobId { get; init; }
    public required string CapabilitySlug { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }
    public string? Provider { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Name { get; init; }
    public string? Rationale { get; init; }
    public JobProvenance? Provenance { get; init; }
    public DateTimeOffset? QueuedAt { get; init; }
    public DateTimeOffset? InvocationStartedAt { get; init; }
}
