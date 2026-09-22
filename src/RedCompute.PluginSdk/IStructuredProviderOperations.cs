using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

/// <summary>Optional detailed, machine-readable provider status.</summary>
public interface IProviderStatusDetails
{
    Task<object> GetStatusDetailsAsync(CancellationToken ct = default);
}

/// <summary>
/// Durable identity for one admitted, bounded provider validation job.
/// </summary>
public sealed record ProviderValidationContext(
    Guid JobId,
    DateTimeOffset QueuedAt,
    DateTimeOffset InvocationStartedAt,
    JobProvenance Provenance);

/// <summary>
/// Optional bounded, action-free provider self-test. The endpoint owns the durable job;
/// the provider must use this context for every inference performed by the validation.
/// </summary>
public interface IProviderSelfValidator
{
    Task<ProviderValidationResult> ValidateProviderAsync(
        ProviderValidationContext context,
        CancellationToken ct = default);
}

public sealed record ProviderValidationResult(
    bool Success,
    string? ResultJson = null,
    string? ErrorCode = null,
    string? Message = null,
    int StatusCode = 200);

public static class ProviderValidationContract
{
    public static object ResponseSchema { get; } = new Dictionary<string, object?>
    {
        ["oneOf"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "jobId", "validation" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["jobId"] = new { type = "string", format = "uuid" },
                    ["validation"] = new
                    {
                        type = "object",
                        description = "The persisted provider validation result.",
                    },
                },
            },
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "jobId", "error", "message" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["jobId"] = new { type = "string", format = "uuid" },
                    ["error"] = new { type = "string" },
                    ["message"] = new { type = "string" },
                    ["validation"] = new
                    {
                        type = "object",
                        description = "Typed provider failure details when available.",
                    },
                },
            },
        },
    };
}
