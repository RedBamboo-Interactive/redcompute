namespace RedCompute.Core.Sessions;

public class ModelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Fast { get; init; }
}

/// <summary>
/// The isolation a provider actually applied to one stateless call, as reported in API responses
/// so callers can record it. Only a provider that removed and verified the tool surface for this
/// exact call may report <see cref="VerifiedToolFree"/>.
/// </summary>
public static class SessionIsolation
{
    /// <summary>No tool reached the model, by construction, and the runtime verified it for this call.</summary>
    public const string VerifiedToolFree = "verified-tool-free";
    /// <summary>A sandbox or tool restriction was applied, but an empty tool surface was not verified.</summary>
    public const string SandboxedBestEffort = "sandboxed-best-effort";
    /// <summary>Full agent tool access with no provider sandbox.</summary>
    public const string Unrestricted = "unrestricted";
    /// <summary>The provider did not report what it applied.</summary>
    public const string Unverified = "unverified";
}

/// <param name="Isolation">One of <see cref="SessionIsolation"/>; null means the provider did not report it.</param>
/// <param name="IsolationMechanism">Short machine-readable name of the mechanism that produced <paramref name="Isolation"/>.</param>
public record SessionExecuteResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error,
    string? Isolation = null, string? IsolationMechanism = null);

/// <param name="Isolation">One of <see cref="SessionIsolation"/>; null means the provider did not report it.</param>
/// <param name="IsolationMechanism">Short machine-readable name of the mechanism that produced <paramref name="Isolation"/>.</param>
/// <param name="NativeResponseSchema">True when the provider enforced the response schema with its native structured output.</param>
public record SessionGenerateResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error,
    string? Isolation = null, string? IsolationMechanism = null, bool NativeResponseSchema = false);

/// <summary>One stateless completion request as admitted by <c>/ai-session/generate</c>.</summary>
/// <param name="System">System prompt. When a schema is present it already carries the schema instruction.</param>
/// <param name="RequireToolFree">The caller requires <see cref="SessionIsolation.VerifiedToolFree"/> for this call.</param>
/// <param name="ResponseSchema">Optional parsed response schema. Providers may enforce it natively; RedCompute validates regardless.</param>
/// <param name="Images">Images attached to the final user message, already decoded and validated by RedCompute.</param>
public record SessionGenerateRequest(
    string? Model, string? System, string MessagesJson, int MaxTokens,
    string? Effort = null, int? Timeout = null, bool RequireToolFree = false,
    ResponseSchema? ResponseSchema = null, IReadOnlyList<GenerateImage>? Images = null);

/// <summary>
/// One generate image input after RedCompute decoded and validated it. <see cref="Bytes"/> are what a
/// provider forwards: always a fresh PNG re-encoded from the decoded pixels (first frame, EXIF
/// orientation applied, downscaled if needed to stay within the size limit), never the caller's
/// bytes. <see cref="Sha256"/> identifies those forwarded bytes; <see cref="SourceSha256"/> the
/// caller's original, for audit.
/// </summary>
public sealed record GenerateImage(string MediaType, byte[] Bytes, int Width, int Height, string Sha256,
    string? SourceSha256 = null)
{
    public string Base64 => Convert.ToBase64String(Bytes);
    public string DataUrl => $"data:{MediaType};base64,{Base64}";
}

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
