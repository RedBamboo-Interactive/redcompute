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
    // Correlation id for the CLI control_request this event reports ("question" /
    // "question_resolved"). Clients echo it back when submitting an answer. Null elsewhere.
    public string? RequestId { get; init; }
}
