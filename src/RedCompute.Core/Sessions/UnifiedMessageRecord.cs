namespace RedCompute.Core.Sessions;

public class UnifiedMessageRecord
{
    public long Id { get; set; }
    public required string SessionId { get; set; }
    public required string Role { get; set; }
    public required string EventType { get; set; }
    public string? Content { get; set; }
    public string? ToolName { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolResult { get; set; }
    public TranscriptPayloadRef? PayloadRef { get; set; }
    public string? MessageId { get; set; }
    // Optional provider-authored assistant-message phase (for example,
    // commentary or final_answer). Null on legacy and unclassified records.
    public string? Phase { get; set; }
    // Provider-neutral message uid (see UnifiedStreamEvent.MessageUid).
    // Null on records persisted before the uid rollout.
    public string? MessageUid { get; set; }
    public string? Epoch { get; set; }
    public long? Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    // Durable RedLeaf record creation time. This is distinct from the provider
    // event timestamp and is the safe cross-stream pagination/alignment bound.
    public DateTimeOffset? RecordCreatedAt { get; set; }
    public string? AttachmentsJson { get; set; }
}
