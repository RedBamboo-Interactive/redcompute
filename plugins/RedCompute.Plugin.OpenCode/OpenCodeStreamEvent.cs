namespace RedCompute.Plugin.OpenCode;

public class OpenCodeStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public object? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public bool IsPartial { get; init; }
    public string? MessageId { get; init; }
    // Provider-neutral message uid, stamped in EmitAndStore (settable — the
    // event constructors don't know the session's current turn).
    public string? MessageUid { get; set; }
    public List<OpenCodeAttachment>? Attachments { get; init; }
}

public class OpenCodeAttachment
{
    public required string Type { get; init; }
    public string? MimeType { get; init; }
    public string? Data { get; init; }
    public string? Url { get; init; }
}
