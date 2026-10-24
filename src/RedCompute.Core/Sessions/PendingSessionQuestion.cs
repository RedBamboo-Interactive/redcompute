using System.Text.Json;

namespace RedCompute.Core.Sessions;

/// <summary>
/// A structured question a live session is parked on (Claude Code's AskUserQuestion, Codex's
/// requestUserInput). Control state, not conversation: it is never a transcript record, and it
/// disappears when the question is answered, declined, cancelled, expires or the session ends.
/// Answer it with POST /ai-session/sessions/{id}/question using <see cref="RequestId"/>.
/// </summary>
public sealed record PendingSessionQuestion
{
    public required string RequestId { get; init; }
    /// <summary>Provider tool that asked: AskUserQuestion (Claude) or requestUserInput (Codex).</summary>
    public required string ToolName { get; init; }
    /// <summary>The transcript tool call or item that carries the question, when the provider names one.</summary>
    public string? ToolUseId { get; init; }
    /// <summary>The questions with their options, verbatim from the provider.</summary>
    public required JsonElement Questions { get; init; }
    /// <summary>The provider's original question input, verbatim.</summary>
    public required JsonElement Input { get; init; }
    public required DateTimeOffset AskedAt { get; init; }
    /// <summary>When the provider stops waiting and answers on the user's behalf; null when it waits indefinitely.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
