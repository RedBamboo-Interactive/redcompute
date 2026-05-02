namespace RedCompute.Core.Jobs;

public class JobResult
{
    public bool Success { get; init; }
    public Stream? OutputStream { get; init; }
    public string? OutputFilePath { get; init; }
    public string? ResultJson { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ContentType { get; init; }
}
