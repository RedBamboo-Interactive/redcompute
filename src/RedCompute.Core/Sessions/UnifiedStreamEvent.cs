namespace RedCompute.Core.Sessions;

public class UnifiedStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public object? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public TranscriptPayloadRef? PayloadRef { get; init; }
    public bool IsPartial { get; init; }
    public string? MessageId { get; init; }
    // Provider-neutral message identity minted by RedCompute (not the
    // provider's msg_/toolu_ id). All events of one assistant turn share it;
    // the persisted records carry the same value, so a streamed block and its
    // reloaded counterpart resolve to the same id. Null on legacy events.
    public string? MessageUid { get; init; }
    // Correlation id for a provider control request the client must answer out-of-band
    // (Claude Code's "question" / "question_resolved" events). Echoed back to
    // POST /ai-session/sessions/{id}/question. Null on every other event.
    public string? RequestId { get; init; }
    public List<UnifiedAttachment>? Attachments { get; init; }
}

public class UnifiedAttachment
{
    public required string Type { get; init; }
    public string? MimeType { get; init; }
    public string? Data { get; init; }
    public string? Url { get; init; }
}
