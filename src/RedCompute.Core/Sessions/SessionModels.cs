namespace RedCompute.Core.Sessions;

public class ModelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Fast { get; init; }
}

public record SessionExecuteResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error);

public record SessionGenerateResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error);

public enum InterruptResult { Interrupted, NotActive, NotFound, Error }

/// <summary>
/// A client's reply to a structured question a session parked on (Claude Code's
/// AskUserQuestion). Correlated by <see cref="RequestId"/>, which the session emitted on
/// the "question" stream event — it is not a message, so it does not go through SendAnswer.
/// </summary>
public class SessionQuestionAnswer
{
    public required string RequestId { get; init; }
    /// <summary>Answers keyed by the exact question text. Takes precedence over <see cref="PositionalAnswers"/>.</summary>
    public Dictionary<string, string>? Answers { get; init; }
    /// <summary>Answers in question order — zipped against the parked question list server-side.</summary>
    public List<string>? PositionalAnswers { get; init; }
    /// <summary>Freeform text the user typed instead of picking an option.</summary>
    public string? Response { get; init; }
    /// <summary>Dismiss the question instead of answering it.</summary>
    public bool Decline { get; init; }
    public string? DeclineReason { get; init; }
}

public enum QuestionAnswerResult { Answered, Declined, SessionNotFound, RequestNotFound, Error }

public record ImageAttachment(string MediaType, string Base64);
