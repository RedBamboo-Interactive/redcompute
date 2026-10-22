namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeMessageRecord
{
    public long Id { get; set; }
    public required string SessionId { get; set; }
    public required string Role { get; set; }
    public required string EventType { get; set; }
    public string? Content { get; set; }
    public string? ToolName { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolResult { get; set; }
    public string? MessageId { get; set; }
    public string? MessageUid { get; set; }
    // commentary | final_answer for assistant text; null for every other record and for history
    // written before Claude recorded phase.
    public string? Phase { get; set; }
    // Launching tool call of the background work (subagent) this record belongs to; null for the
    // main loop. Durable `task` records carry the tool call that launched the task.
    public string? ParentToolUseId { get; set; }
    // Serialized TurnOrigin for the first record of a turn the CLI started by itself.
    public string? TurnOriginJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? AttachmentsJson { get; set; }
}
