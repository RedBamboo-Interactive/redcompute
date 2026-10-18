namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public object? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public bool IsPartial { get; init; }
    // Settable: streamed text/thinking deltas receive their block identity after parsing.
    public string? MessageId { get; set; }
    /// <summary>
    /// Assistant-text phase, <c>commentary</c> or <c>final_answer</c>, using Codex's vocabulary.
    /// Set only once a text block's role is known; streamed deltas leave it null (undetermined).
    /// </summary>
    public string? Phase { get; init; }
    // Provider-neutral message uid, stamped in ReadStdout after parsing
    // (the parsers are static and don't know the session) — hence settable,
    // unlike the init-only parse-time properties.
    public string? MessageUid { get; set; }
    public string? PermissionMode { get; init; }
    // Correlation id for the CLI control_request this event reports ("question" /
    // "question_resolved"). Clients echo it back when submitting an answer. Null elsewhere.
    public string? RequestId { get; init; }

    // Parse-time stream coordinates for text/thinking accumulation. Internal, so never serialized onto
    // the plugin event or the unified stream.
    internal int? BlockIndex { get; init; }
    internal string? ParentToolUseId { get; init; }
}
