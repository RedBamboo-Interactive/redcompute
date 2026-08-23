namespace RedCompute.Core.Sessions;

public sealed class TranscriptCursor
{
    public required string Epoch { get; init; }
    public long ThroughSequence { get; init; }
}
