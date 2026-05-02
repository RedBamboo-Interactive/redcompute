namespace RedCompute.Core.Jobs;

public class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string CapabilitySlug { get; set; }
    public required string ProviderName { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string InputJson { get; set; } = "{}";
    public string? OutputLocation { get; set; }
    public long? OutputSizeBytes { get; set; }
    public string? OutputContentType { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
    public string? CallerInfo { get; set; }
    public string? IdempotencyKey { get; set; }

    public long? DurationMs => CompletedAt.HasValue && StartedAt.HasValue
        ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
        : null;
}
