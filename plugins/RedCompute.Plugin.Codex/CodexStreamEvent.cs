namespace RedCompute.Plugin.Codex;

public class CodexStreamEvent
{
    public required string Type { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public object? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public bool IsPartial { get; init; }
    public string? MessageId { get; init; }

    /// <summary>
    /// Provider-neutral turn uid. Settable rather than init-only because the mapper builds events
    /// without knowing the session's current turn — it is stamped on the way out.
    /// </summary>
    public string? MessageUid { get; set; }

    /// <summary>
    /// Correlation id for a parked question, echoed back to
    /// POST /ai-session/sessions/{id}/question. Set only on question / question_resolved.
    /// </summary>
    public string? RequestId { get; init; }
}
