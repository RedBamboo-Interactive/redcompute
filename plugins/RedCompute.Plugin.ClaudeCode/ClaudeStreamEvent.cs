using RedCompute.Core.Sessions;

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
    // The launching tool call when the CLI attributes this line to background work (a subagent's
    // `parent_tool_use_id`). Null on main-loop events.
    public string? ParentToolUseId { get; init; }
    // Stamped on the first event (and the closing status) of a turn the CLI started by itself
    // after a background task finished.
    public TurnOrigin? TurnOrigin { get; set; }

    // Parse-time stream coordinates for text/thinking accumulation. Internal, so never serialized onto
    // the plugin event or the unified stream.
    internal int? BlockIndex { get; init; }
}
