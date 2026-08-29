namespace RedCompute.Core.Jobs;

public class JobResult
{
    public bool Success { get; init; }
    public Stream? OutputStream { get; init; }
    public string? OutputFilePath { get; init; }
    public string? ResultJson { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ContentType { get; init; }
    /// <summary>Stable logical selector for the primary stream in addition to primary/clip-0.</summary>
    public string? PrimaryOutputName { get; init; }
    /// <summary>Suggested user-facing filename for the primary stream.</summary>
    public string? PrimaryFileName { get; init; }

    /// <summary>
    /// Additional output streams beyond the primary one (for example variations,
    /// cover art, or stems). Each is persisted beside the primary output and served
    /// by stable artifact name. Legacy unnamed outputs also retain clip-N aliases.
    /// </summary>
    public IReadOnlyList<JobOutputPart>? ExtraOutputs { get; init; }
}

/// <summary>An additional named output stream produced by a job.</summary>
public class JobOutputPart
{
    /// <summary>Filename suffix appended to the job id (e.g. "_clip1").</summary>
    public required string Suffix { get; init; }
    /// <summary>
    /// Stable logical selector exposed as ?artifact={name}. When omitted, generic
    /// capability jobs retain the legacy clip-1, clip-2, ... selectors.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>Suggested user-facing file name recorded in the artifact manifest.</summary>
    public string? FileName { get; init; }
    public required Stream Data { get; init; }
    public string? ContentType { get; init; }
}
