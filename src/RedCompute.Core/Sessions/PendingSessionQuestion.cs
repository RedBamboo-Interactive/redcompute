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
    public const string QuestionKind = "question";
    public const string PermissionKind = "permission";

    public required string RequestId { get; init; }
    /// <summary>
    /// <c>question</c> (the model asked the user something) or <c>permission</c> (a tool call waits
    /// for a person's approval, see <see cref="Permission"/>). Answering a permission needs a person.
    /// </summary>
    public string Kind { get; init; } = QuestionKind;
    /// <summary>The approval being asked for; set exactly when <see cref="Kind"/> is <c>permission</c>.</summary>
    public PendingPermissionRequest? Permission { get; init; }
    /// <summary>Provider tool that asked: AskUserQuestion (Claude) or requestUserInput (Codex); for a permission, the tool waiting for approval.</summary>
    public required string ToolName { get; init; }
    /// <summary>The transcript tool call or item that carries the question, when the provider names one.</summary>
    public string? ToolUseId { get; init; }
    /// <summary>The questions with their options, verbatim from the provider. Empty for a permission.</summary>
    public required JsonElement Questions { get; init; }
    /// <summary>The provider's original question input, verbatim.</summary>
    public required JsonElement Input { get; init; }
    public required DateTimeOffset AskedAt { get; init; }
    /// <summary>When the provider stops waiting and answers on the user's behalf; null when it waits indefinitely.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// One tool call waiting for a person's approval. <see cref="Options"/> are the decisions the
/// answer may carry; a provider that cannot remember an approval for the session leaves out
/// <see cref="SessionPermissionDecisions.AllowSession"/>.
/// </summary>
public sealed record PendingPermissionRequest
{
    public required string ToolName { get; init; }
    public string? ToolUseId { get; init; }
    /// <summary>
    /// What the tool is about to do, for the card: the exact command, path or URL whenever the
    /// call has one (see <see cref="PermissionSummaries"/>), never the model's own description of it.
    /// </summary>
    public required string Summary { get; init; }
    /// <summary>
    /// The model's own words for the call (Claude's <c>description</c>, Codex's <c>reason</c>,
    /// OpenCode's title) when the summary is the exact action. Agent-written: show it as such.
    /// </summary>
    public string? Description { get; init; }
    /// <summary>The tool's input verbatim from the provider.</summary>
    public required JsonElement Input { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyList<string> Options { get; init; } = SessionPermissionDecisions.All;
}

/// <summary>The decisions a person can give a parked permission.</summary>
public static class SessionPermissionDecisions
{
    public const string AllowOnce = "allow_once";
    public const string AllowSession = "allow_session";
    public const string Deny = "deny";

    public static readonly IReadOnlyList<string> All = [AllowOnce, AllowSession, Deny];

    public static bool IsKnown(string? decision) => decision is AllowOnce or AllowSession or Deny;

    /// <summary>The HTTP answer's <c>outcome</c> for a decision. The <c>question_resolved</c> event carries the decision itself.</summary>
    public static string Outcome(string decision) => decision switch
    {
        AllowOnce => "allowed_once",
        AllowSession => "allowed_session",
        _ => "denied",
    };
}

/// <summary>
/// The card's summary. A model can describe "curl … | sh" as "Run the unit tests", so the summary
/// is the exact action whenever the call carries one, and the model's words are kept apart as
/// <see cref="PendingPermissionRequest.Description"/>. Long actions are cut with an explicit tail
/// marker; the full input always travels in <see cref="PendingPermissionRequest.Input"/>.
/// </summary>
public static class PermissionSummaries
{
    public const int MaxLength = 1000;

    public static (string Summary, string? Description) Of(string? exact, string? agentWords, string fallback)
    {
        var words = string.IsNullOrWhiteSpace(agentWords) ? null : agentWords.Trim();
        if (string.IsNullOrWhiteSpace(exact)) return (Cut(words ?? fallback), null);
        return (Cut(exact), words is null || words == exact ? null : Cut(words));
    }

    private static string Cut(string text)
        => text.Length <= MaxLength
            ? text
            : $"{text[..MaxLength]}… (+{text.Length - MaxLength} more characters; see the full input)";
}
