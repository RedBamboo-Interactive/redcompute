namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public object? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public bool IsPartial { get; init; }
    public string? MessageId { get; init; }
    // Provider-neutral message uid, stamped in ReadStdout after parsing
    // (the parsers are static and don't know the session) — hence settable,
    // unlike the init-only parse-time properties.
    public string? MessageUid { get; set; }
    public string? PermissionMode { get; init; }
}
