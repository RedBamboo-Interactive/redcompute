namespace RedCompute.Core.Jobs;

public class JobRequest
{
    public required string CapabilitySlug { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }
    public string? CallerInfo { get; init; }
    public string? IdempotencyKey { get; init; }
}
