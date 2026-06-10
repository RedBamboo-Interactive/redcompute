namespace RedCompute.Core.Jobs;

public class JobResult
{
    public bool Success { get; init; }
    public Stream? OutputStream { get; init; }
    public string? OutputFilePath { get; init; }
    public string? ResultJson { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ContentType { get; init; }

    /// <summary>
    /// Additional output streams beyond the primary one (e.g. music variations).
    /// Each is saved alongside the main output as {jobId}{Suffix}{ext} and served
    /// via GET /{slug}/jobs/{id}/output?clip=N.
    /// </summary>
    public IReadOnlyList<JobOutputPart>? ExtraOutputs { get; init; }
}

/// <summary>An additional named output stream produced by a job.</summary>
public class JobOutputPart
{
    /// <summary>Filename suffix appended to the job id (e.g. "_clip1").</summary>
    public required string Suffix { get; init; }
    public required Stream Data { get; init; }
    public string? ContentType { get; init; }
}
