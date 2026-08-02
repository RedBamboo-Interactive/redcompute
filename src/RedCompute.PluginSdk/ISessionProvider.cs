using RedCompute.Core.Sessions;
using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public interface ISessionProvider
{
    string ProviderId { get; }
    string ProviderDisplayName { get; }
    SessionCapabilities Capabilities { get; }

    // Session lifecycle (PersistentSessions)
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null, string? effort = null);

    // Extended start with provider-level endpoint override (default delegates to 7-param above).
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null, string? effort = null, string? endpointUrl = null, string? apiKey = null, int? thinkingBudget = null, string? qualityTier = null, string? providerEntity = null)
        => StartSessionAsync(projectPath, callerInfo, model, userId, userName, userAvatarUrl, effort);
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        JobProvenance provenance)
        => StartSessionAsync(projectPath, callerInfo, model, userId, userName, userAvatarUrl, effort,
            endpointUrl, apiKey, thinkingBudget, qualityTier, providerEntity);
    Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId);
    Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
        => ResumeSessionAsync(sessionId);
    Task StopSessionAsync(string sessionId);
    Task ForceKillAsync(string sessionId);
    void DismissSession(string sessionId);

    // Messaging (SendMessage). messageUid is the provider-neutral message
    // identity minted by the caller (endpoint layer) — providers persist it
    // on the user message record verbatim.
    Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null);

    // Provider API compatibility for callers that still construct {content, images}.
    // The public HTTP endpoint stages legacy images before it reaches providers;
    // this default exists for binary/source compatibility during the migration.
    Task<bool> SendMessageAsync(string sessionId, string content, ImageAttachment[]? images = null, string? attachmentsJson = null, string? messageUid = null)
    {
        var input = new List<SessionInputPart>();
        if (!string.IsNullOrWhiteSpace(content)) input.Add(SessionInputPart.TextPart(content));
        if (images is not null) input.AddRange(images.Select(SessionInputPart.LegacyImagePart));
        return SendInputAsync(sessionId, input, attachmentsJson, messageUid);
    }
    bool SendAnswer(string sessionId, string answer);

    // Reply to a structured question the session parked on (a provider control request the
    // session is blocked awaiting, not a conversation turn — hence the request-id
    // correlation rather than SendAnswer). Providers that never park default to "no such
    // request", which is what the endpoint reports for an unknown request id anyway.
    QuestionAnswerResult SubmitQuestionAnswer(string sessionId, SessionQuestionAnswer answer)
        => QuestionAnswerResult.RequestNotFound;

    // Interrupt
    InterruptResult InterruptSession(string sessionId);

    // Configuration (ConfigUpdate / PermissionMode)
    Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort, int? thinkingBudget = null, string? qualityTier = null);
    bool SetPermissionMode(string sessionId, string mode);

    // Querying
    List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false);
    (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId);
    (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId);
    Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);

    // Stateless execution
    Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
        int timeout, CancellationToken ct, string? streamKey = null,
        Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null);

    // LLM completion (Generate)
    Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null);

    // Discovery
    List<SessionProjectInfo> ListProjects();
    List<ModelInfo> GetAvailableModels();

    // Message injection (without triggering inference)
    Task<bool> InjectMessageAsync(string sessionId, string role, string content, string? attachmentsJson = null, string? messageUid = null)
        => Task.FromResult(false);

    // Events
    event Action<string, UnifiedStreamEvent>? SessionStreamEvent;
    void CancelExecution(string key);
    Task StopAllAsync();
    string? LastStartError { get; }
}

/// <summary>
/// Optional per-session refinement for providers whose image support varies by selected model.
/// Provider-level <see cref="SessionCapabilities.ImageAttachments"/> remains the coarse feature
/// declaration; this hook lets the HTTP boundary reject a known-incompatible model before delivery.
/// </summary>
public interface IImageAttachmentSupportProvider
{
    ImageAttachmentSupport GetImageAttachmentSupport(string sessionId);
}

public sealed record ImageAttachmentSupport(bool Supported, string? Reason = null);
