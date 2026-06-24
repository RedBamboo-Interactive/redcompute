namespace RedCompute.Plugin.OpenCode;

public class OpenCodeMessageRecord
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
    public DateTimeOffset Timestamp { get; set; }
    public string? AttachmentsJson { get; set; }
}
