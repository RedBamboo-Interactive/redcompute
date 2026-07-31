namespace RedCompute.Plugin.Codex;

public class CodexMessageRecord
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

    /// <summary>
    /// Provider-neutral turn identity, minted once per assistant turn and stamped on every event in
    /// it. Without it the streamed transcript and the one rebuilt from this table group their parts
    /// differently, so a session visibly reshuffles on reload.
    /// </summary>
    public string? MessageUid { get; set; }

    public string? AttachmentsJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
