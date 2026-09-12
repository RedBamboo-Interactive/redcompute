namespace RedCompute.Core.Sessions;

/// <summary>
/// One keyset-addressed page of the canonical durable transcript.
/// Storage anchors remain opaque to callers through the cursor strings.
/// </summary>
public sealed class TranscriptPageResponse
{
    public required UnifiedSessionInfo Session { get; init; }
    public required IReadOnlyList<UnifiedMessageRecord> Messages { get; init; }
    public required TranscriptPageMetadata Page { get; init; }
}

public sealed class TranscriptPageMetadata
{
    public required string Epoch { get; init; }
    public required string Direction { get; init; }
    public string? OldestCursor { get; init; }
    public string? NewestCursor { get; init; }
    public bool HasEarlier { get; init; }
    public bool HasLater { get; init; }
    public long? FromSequence { get; init; }
    public long? ThroughSequence { get; init; }
    public bool BoundaryComplete { get; init; }
}
