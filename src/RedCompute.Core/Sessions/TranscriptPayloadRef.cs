namespace RedCompute.Core.Sessions;

/// <summary>
/// Lightweight reference to transcript bytes stored on a RedLeaf record.
/// Provider-neutral by design: tool output is the first payload kind, not a
/// provider-specific transport special case.
/// </summary>
public sealed class TranscriptPayloadRef
{
    public long RecordId { get; init; }
    public string Kind { get; init; } = "tool-output";
    public long Length { get; init; }
    public string ContentType { get; init; } = "text/plain; charset=utf-8";
    public string Encoding { get; init; } = "utf-8";
    public string Sha256 { get; init; } = "";
    public bool Available { get; init; } = true;
}
