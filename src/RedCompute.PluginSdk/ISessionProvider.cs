using RedCompute.Core.Sessions;
using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public interface ISessionProvider
{
    string ProviderId { get; }
    string ProviderDisplayName { get; }
    SessionCapabilities Capabilities { get; }

    // Session lifecycle (PersistentSessions). A session cannot exist without the
    // immutable identity of the job it creates.
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort,
        string? endpointUrl, string? apiKey, int? thinkingBudget, string? qualityTier,
        string? providerEntity, Guid? repositoryId, JobProvenance provenance,
        string? scratchDirectory = null, bool confidential = false);

    /// <summary>
    /// Starts a persistent session with provider-native developer instructions. Providers must
    /// override this overload before accepting a non-empty instruction; silently demoting it to a
    /// user message would break the caller's security boundary.
    /// </summary>
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort,
        string? endpointUrl, string? apiKey, int? thinkingBudget, string? qualityTier,
        string? providerEntity, Guid? repositoryId, JobProvenance provenance,
        string? scratchDirectory, bool confidential, string? developerInstructions)
        => string.IsNullOrWhiteSpace(developerInstructions)
            ? StartSessionAsync(projectPath, model, userId, userName, userAvatarUrl, effort,
                endpointUrl, apiKey, thinkingBudget, qualityTier, providerEntity, repositoryId,
                provenance, scratchDirectory, confidential)
            : throw new NotSupportedException(
                $"Provider '{ProviderId}' does not support session developer instructions");
    Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId);
    Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
        => ResumeSessionAsync(sessionId);
    Task StopSessionAsync(string sessionId);
    Task ForceKillAsync(string sessionId);

    /// <summary>
    /// Force-stops a session for an infrastructure-owned reason. Providers should persist the
    /// supplied reason and keep a maintenance-restart job resumable instead of completing it as
    /// an ordinary user stop. The default preserves compatibility with older provider plugins.
    /// </summary>
    Task ForceKillAsync(string sessionId, string stopReason)
        => ForceKillAsync(sessionId);
    void DismissSession(string sessionId);

    /// <summary>
    /// Clears a dismissal so the session returns to default listings, without starting a
    /// process. Returns false when the session is unknown or the provider cannot restore;
    /// the default keeps older provider plugins compatible.
    /// </summary>
    bool RestoreSession(string sessionId) => false;

    // Messaging (SendMessage). messageUid is the provider-neutral message
    // identity minted by the caller (endpoint layer) — providers persist it
    // on the user message record verbatim.
    Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null);

    /// <summary>
    /// Sends provider-facing input while allowing the canonical transcript to retain a separate
    /// human-readable user turn. Existing providers keep the legacy behavior unless they opt in.
    /// </summary>
    Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson, string? messageUid, string? displayContent)
        => SendInputAsync(sessionId, input, attachmentsJson, messageUid);

    /// <summary>
    /// Provider-neutral, non-destructive input admission. Implementations must return
    /// <see cref="SessionInputDeliveryStatus.Busy"/> while a turn is active and must
    /// never interrupt that turn as a side effect of this call.
    /// </summary>
    async Task<SessionInputDeliveryResult> TrySendInputAsync(
        string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson = null, string? messageUid = null)
    {
        var (info, _) = GetSession(sessionId);
        if (info is null)
            return SessionInputDeliveryResult.Unavailable("session_not_found", $"Session '{sessionId}' was not found");
        if (info.Status is SessionStatus.Active or SessionStatus.Starting)
            return SessionInputDeliveryResult.Busy();
        if (info.Status is SessionStatus.Stopped or SessionStatus.Error)
            return NotWritable(sessionId, info);

        try
        {
            if (await SendInputAsync(sessionId, input, attachmentsJson, messageUid))
                return SessionInputDeliveryResult.Accepted();
            return AfterRefusedSend(sessionId);
        }
        catch (Exception ex)
        {
            return SessionInputDeliveryResult.Unavailable("provider_unavailable", ex.Message, retryable: true);
        }
    }

    /// <summary>
    /// Provider-neutral admission with distinct model-facing and transcript-facing text.
    /// </summary>
    async Task<SessionInputDeliveryResult> TrySendInputAsync(
        string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson, string? messageUid, string? displayContent)
    {
        var (info, _) = GetSession(sessionId);
        if (info is null)
            return SessionInputDeliveryResult.Unavailable("session_not_found", $"Session '{sessionId}' was not found");
        if (info.Status is SessionStatus.Active or SessionStatus.Starting)
            return SessionInputDeliveryResult.Busy();
        if (info.Status is SessionStatus.Stopped or SessionStatus.Error)
            return NotWritable(sessionId, info);

        try
        {
            if (await SendInputAsync(sessionId, input, attachmentsJson, messageUid, displayContent))
                return SessionInputDeliveryResult.Accepted();
            return AfterRefusedSend(sessionId);
        }
        catch (Exception ex)
        {
            return SessionInputDeliveryResult.Unavailable("provider_unavailable", ex.Message, retryable: true);
        }
    }

    /// <summary>
    /// Classifies a send the provider refused after admission passed. The session can lose its
    /// process between the status check and the write (a restart, a kill, an exit). That is not
    /// a rejection of the input: a restart-recoverable stop must stay retryable so the queue
    /// takes its resume path on the next attempt.
    /// </summary>
    private SessionInputDeliveryResult AfterRefusedSend(string sessionId)
    {
        var (after, _) = GetSession(sessionId);
        if (after?.Status is SessionStatus.Active or SessionStatus.Starting)
            return SessionInputDeliveryResult.Busy();
        if (after?.Status is SessionStatus.Stopped or SessionStatus.Error)
            return NotWritable(sessionId, after);
        return SessionInputDeliveryResult.Rejected(
            "provider_rejected", $"Provider '{ProviderId}' rejected the input");
    }

    private static SessionInputDeliveryResult NotWritable(string sessionId, UnifiedSessionInfo info)
        => info.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(info.StopReason)
            ? SessionInputDeliveryResult.Unavailable("session_restarting",
                $"Session '{sessionId}' lost its process and will resume", retryable: true)
            : SessionInputDeliveryResult.Unavailable("session_not_writable",
                $"Session '{sessionId}' is {info.Status.ToString().ToLowerInvariant()}");

    /// <summary>
    /// Injects input into the active provider turn without interrupting, stopping, or restarting it.
    /// Providers that do not implement same-turn input remain side-effect-free.
    /// </summary>
    Task<SessionInputDeliveryResult> TrySteerInputAsync(
        string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson = null, string? messageUid = null, string? displayContent = null)
        => Task.FromResult(SessionInputDeliveryResult.Unsupported());

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

    /// <summary>
    /// Questions the live session is parked on right now. Live control state, like background
    /// tasks: the API overlays it on session reads and it is never written to the transcript.
    /// </summary>
    IReadOnlyList<PendingSessionQuestion> GetPendingQuestions(string sessionId) => [];

    // Interrupt
    InterruptResult InterruptSession(string sessionId);

    /// <summary>
    /// Asks the provider to stop one running background task (subagent or background shell)
    /// without interrupting the session's main loop. Acceptance means the request reached the
    /// provider; the task's terminal state is reported by the provider's own lifecycle events.
    /// Providers without <see cref="SessionCapabilities.SupportsBackgroundTaskStop"/> stay
    /// side-effect-free.
    /// </summary>
    Task<BackgroundTaskStopResult> TryStopBackgroundTaskAsync(string sessionId, string taskId)
        => Task.FromResult(BackgroundTaskStopResult.Unsupported());

    /// <summary>
    /// The session's live background tasks, without reading history. Durable reads come from the
    /// suite mirror, which holds no live state, so the API overlays this on every session read.
    /// </summary>
    IReadOnlyList<BackgroundTaskInfo> GetBackgroundTasks(string sessionId) => [];

    // Configuration
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

    /// <summary>
    /// The request-object form used by <c>/ai-session/generate</c>. The default forwards to the
    /// positional overload, which neither honours <see cref="SessionGenerateRequest.RequireToolFree"/>
    /// nor reports isolation, so a provider claiming <see cref="SessionCapabilities.ToolFreeGenerate"/>
    /// must override this and report <see cref="SessionIsolation.VerifiedToolFree"/> per call.
    /// </summary>
    Task<SessionGenerateResult> GenerateAsync(SessionGenerateRequest request, CancellationToken ct)
        => GenerateAsync(request.Model, request.System, request.MessagesJson, request.MaxTokens, ct,
            request.Effort, request.Timeout);

    // Model discovery
    List<ModelInfo> GetAvailableModels();

    // Message injection (without triggering inference)
    Task<bool> InjectMessageAsync(string sessionId, string role, string content, string? attachmentsJson = null, string? messageUid = null)
        => Task.FromResult(false);

    // Events
    event Action<string, UnifiedStreamEvent>? SessionStreamEvent;
    void CancelExecution(string key);
    Task StopAllAsync();
    string? LastStartError { get; }

    /// <summary>
    /// Machine-readable reason for the last failed start/resume (for example
    /// <see cref="RedCompute.Core.Sessions.SessionStopReasons.ProjectPathMissing"/>); null when the failure has no typed reason.
    /// </summary>
    string? LastStartErrorCode => null;
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

/// <summary>
/// Optional contract for providers whose persistent sessions own resident processes that can be
/// released without ending the logical session or completing its compute job.
/// </summary>
public interface IIdleSessionParkingProvider
{
    string ProviderId { get; }

    /// <summary>Only live, provider-owned processes whose main session loop is currently idle.</summary>
    IReadOnlyList<UnifiedSessionInfo> GetResidentIdleSessions();

    /// <summary>
    /// Atomically rechecks provider-local eligibility and parks the process. A successful park
    /// persists a resumable stopped shell and publishes session.updated, never session.ended.
    /// </summary>
    Task<bool> TryParkIdleSessionAsync(string sessionId, string stopReason, CancellationToken ct = default);
}
