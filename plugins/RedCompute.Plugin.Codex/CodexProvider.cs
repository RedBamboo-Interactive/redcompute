using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider,
    ISessionProvider, IIdleSessionParkingProvider, IImageAttachmentSupportProvider, IProviderUsageSource,
    ICliVersionManagedProvider
{
    private readonly string _capabilitySlug;
    private readonly CodexSessionService _codex;
    private readonly string _defaultSandbox;
    private readonly CodexInteractiveService _interactive;
    private readonly CodexSessionStore _store;
    private readonly object _injectionGate = new();
    private readonly CodexModelCatalog _models;
    private readonly CodexAccountUsageService _accountUsage;
    private readonly NpmCliVersionManager _cliVersionManager;
    private readonly IJobTracker _jobTracker;
    private readonly Action<string, Guid?> _log;
    private string? _lastStartError;

    public event Action<string, object>? PluginEvent;
    public event Action<string, UnifiedStreamEvent>? SessionStreamEvent;

    public string Name => "Codex";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Codex";
    public string ProviderType => "Codex";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    // ISessionProvider
    public string ProviderId => "codex";
    public string ProviderDisplayName => "Codex";
    public NpmCliVersionManager CliVersionManager => _cliVersionManager;
    internal const SessionCapabilities DeclaredCapabilities =
        SessionCapabilities.StatelessExecution
        | SessionCapabilities.Generate
        // Only for requests that require it: CodexToolFreeRelay strips every tool at the model
        // boundary and verifies the call before text is returned.
        | SessionCapabilities.ToolFreeGenerate
        // Generate images: injected at the relay on tool-free calls, --image files otherwise.
        | SessionCapabilities.GenerateVision
        | SessionCapabilities.PersistentSessions
        | SessionCapabilities.Resume
        | SessionCapabilities.Interrupt
        | SessionCapabilities.SendMessage
        | SessionCapabilities.SupportsSteering
        | SessionCapabilities.ImageAttachments
        | SessionCapabilities.FileAttachments
        | SessionCapabilities.ImageOutputs;
    public SessionCapabilities Capabilities => DeclaredCapabilities;
    // Not claimed: PermissionMode (approvals are always auto-accepted, there is nothing to switch).

    public string? LastStartError => Volatile.Read(ref _lastStartError);

    public CodexProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, IProviderQualityModeResolver qualityModes,
        Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new CodexDbContext()) { db.Initialize(); }
        _store = new CodexSessionStore();
        var codexConfig = BuildConfig(config);
        _defaultSandbox = codexConfig.SandboxMode;
        _codex = new CodexSessionService(codexConfig, jobTracker, _store, log);
        _models = new CodexModelCatalog(codexConfig, log);
        _accountUsage = new CodexAccountUsageService(codexConfig, log);
        _cliVersionManager = new NpmCliVersionManager(
            "@openai/codex",
            "Codex",
            () => CodexCliLocator.Resolve(codexConfig.CodexPath),
            GetCliSessionState,
            ParkIdleCliSessionsAsync);
        _interactive = new CodexInteractiveService(
            codexConfig, _store, _models, _codex, jobTracker, _accountUsage, qualityModes,
            () => GetTitleQualityTier(config), log);
        _codex.LaunchGate = _cliVersionManager;
        _interactive.LaunchGate = _cliVersionManager;
        _models.LaunchGate = _cliVersionManager;
        _accountUsage.LaunchGate = _cliVersionManager;

        _codex.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _codex.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _codex.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _codex.StreamEvent += (sessionId, evt) => Broadcast(sessionId, evt);

        _interactive.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _interactive.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _interactive.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _interactive.StreamEvent += Broadcast;
    }

    /// <summary>
    /// Test seam: the provider contract over an existing interactive service, without opening the
    /// real Codex store or CLI catalog (as ClaudeCodeProvider's equivalent does). Only the
    /// interactive session surface is usable through it.
    /// </summary>
    internal CodexProvider(string capabilitySlug, CodexInteractiveService interactive, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;
        _interactive = interactive;
        _codex = null!;
        _defaultSandbox = "workspace-write";
        _store = null!;
        _models = null!;
        _accountUsage = null!;
        _cliVersionManager = null!;
        _interactive.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _interactive.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _interactive.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _interactive.StreamEvent += Broadcast;
    }

    private void Broadcast(string sessionId, CodexStreamEvent evt)
    {
        PluginEvent?.Invoke("session.stream", new { sessionId, @event = evt });
        SessionStreamEvent?.Invoke(sessionId, ToUnifiedEvent(evt));
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        var restored = _interactive.ReconcileMissingJobs();
        if (restored > 0)
            _log($"[Codex] Restored compute jobs for {restored} interactive sessions", null);

        // Construction happens before RelayServer installs the RedLeaf mirror hooks. Replay the
        // recovered local state now so sessions orphaned by a restart do not remain Active in the
        // suite-wide read model (and keep Nova's stop button latched forever).
        _interactive.RepublishStoredSessions();

        // Warm the model catalog so the synchronous GetAvailableModels() has something to serve.
        // A failure here is not fatal: the CLI may not be logged in yet, and /codex/models will
        // report that clearly on demand.
        await _models.PrimeAsync(ct);
        return true;
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _interactive.StopAllAsync();
        await _codex.StopAllAsync();
    }
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public Task<ProviderUsageSnapshot?> GetProviderUsageAsync(bool forceRefresh = false, CancellationToken ct = default)
        => _accountUsage.GetAsync(forceRefresh, ct);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public async ValueTask DisposeAsync()
    {
        await _interactive.DisposeAsync();
        await _codex.DisposeAsync();
    }

    public void MapCustomEndpoints(WebApplication app)
    {
        CodexSessionEndpoints.Map(app, _codex, _models, _jobTracker, _log);
    }

    public void CancelJob(string jobKey) => _codex.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _codex.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // --- ISessionProvider: Interactive sessions ---

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential = false)
    {
        if (!await ValidateRequestedModelAsync(model)) return null;
        var info = await _interactive.StartSessionAsync(projectPath, model, userId, userName,
            userAvatarUrl, effort, qualityTier, providerEntity, repositoryId, provenance,
            scratchDirectory, confidential, executionProfile: SessionExecutionProfile.Current);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential, string? developerInstructions)
    {
        if (!await ValidateRequestedModelAsync(model)) return null;
        var info = await _interactive.StartSessionAsync(projectPath, model, userId, userName,
            userAvatarUrl, effort, qualityTier, providerEntity, repositoryId, provenance,
            scratchDirectory, confidential, developerInstructions, SessionExecutionProfile.Current);
        return info != null ? ToUnified(info) : null;
    }

    private async Task<bool> ValidateRequestedModelAsync(string? model)
    {
        Volatile.Write(ref _lastStartError, null);
        if (string.IsNullOrWhiteSpace(model)
            || await _models.IsValidModelAsync(model))
            return true;

        var error = $"Model '{model}' is not available in the active Codex catalog";
        Volatile.Write(ref _lastStartError, error);
        _log($"[Codex] {error}", null);
        return false;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = await _interactive.ResumeSessionAsync(sessionId);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
    {
        var info = await _interactive.ResumeSessionAsync(sessionId, provenance);
        return info != null ? ToUnified(info) : null;
    }

    public Task StopSessionAsync(string sessionId) => _interactive.StopSessionAsync(sessionId);

    public Task ForceKillAsync(string sessionId)
    {
        // Stateless executions are keyed by job id in the other service; interactive ones by
        // session id here. Both are safe to attempt — only one will match.
        _codex.CancelExecution(sessionId);
        return _interactive.ForceKillAsync(sessionId);
    }

    public Task ForceKillAsync(string sessionId, string stopReason)
    {
        _codex.CancelExecution(sessionId);
        return _interactive.ForceKillAsync(sessionId, stopReason);
    }

    public IReadOnlyList<UnifiedSessionInfo> GetResidentIdleSessions()
        => _interactive.GetResidentIdleSessions().Select(ToUnified).ToArray();

    public Task<bool> TryParkIdleSessionAsync(
        string sessionId, string stopReason, CancellationToken ct = default)
        => _interactive.TryParkIdleSessionAsync(sessionId, stopReason, ct);

    public void DismissSession(string sessionId) => _codex.DismissSession(sessionId);

    public bool RestoreSession(string sessionId) => _codex.RestoreSession(sessionId);

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null)
        => _interactive.SendInputAsync(sessionId, input, attachmentsJson, messageUid);

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson, string? messageUid, string? displayContent)
        => _interactive.SendInputAsync(sessionId, input, attachmentsJson, messageUid, displayContent);

    public Task<SessionInputDeliveryResult> TrySteerInputAsync(
        string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson = null, string? messageUid = null, string? displayContent = null)
        => _interactive.TrySteerInputAsync(
            sessionId, input, attachmentsJson, messageUid, displayContent);

    public Task<bool> InjectMessageAsync(string sessionId, string role, string content,
        string? attachmentsJson = null, string? messageUid = null)
    {
        lock (_injectionGate)
        {
            var (info, history) = _interactive.GetSession(sessionId);
            if (info is null) return Task.FromResult(false);
            var existing = messageUid is null
                ? null
                : history.FirstOrDefault(message =>
                    message.EventType == "text" && message.MessageUid == messageUid);
            if (existing is not null)
                return Task.FromResult(existing.Role == role
                    && existing.Content == content
                    && existing.AttachmentsJson == attachmentsJson);

            _store.AddMessage(new CodexMessageRecord
            {
                SessionId = sessionId,
                Role = role,
                EventType = "text",
                Content = content,
                MessageUid = messageUid,
                AttachmentsJson = attachmentsJson,
                Timestamp = DateTimeOffset.UtcNow,
            });
            return Task.FromResult(true);
        }
    }

    public ImageAttachmentSupport GetImageAttachmentSupport(string sessionId)
    {
        var (info, _) = _interactive.GetSession(sessionId);
        return info is null
            ? new(false, $"Session '{sessionId}' was not found")
            : SessionExecutionProfile.IsConversationOnly(info.ExecutionProfile)
                ? new(false, "Conversation-only sessions accept text input only")
            : CodexInteractiveService.GetImageAttachmentSupport(info.Model, _models.Cached);
    }

    /// <summary>
    /// Free-text answer to a parked question. Codex keys answers by question id, so a bare string
    /// can only be applied when exactly one question is outstanding — which is the case that
    /// actually occurs. Structured multi-question replies go through SubmitQuestionAnswer.
    /// </summary>
    public bool SendAnswer(string sessionId, string answer)
        => _interactive.SubmitSingleAnswer(sessionId, answer);

    public IReadOnlyList<Core.Sessions.PendingSessionQuestion> GetPendingQuestions(string sessionId)
        => _interactive.GetPendingQuestions(sessionId);

    public Core.Sessions.QuestionAnswerResult SubmitQuestionAnswer(string sessionId, Core.Sessions.SessionQuestionAnswer answer)
        => !_interactive.SubmitQuestionAnswer(sessionId, answer) ? Core.Sessions.QuestionAnswerResult.RequestNotFound
            : answer.Decline ? Core.Sessions.QuestionAnswerResult.Declined
            : Core.Sessions.QuestionAnswerResult.Answered;

    // The service reports the real outcome now. Collapsing it to a bool here was half the reason
    // stop looked broken: "no such session", "no thread" and "nothing running" all arrived as
    // NotActive, and everything else — including a request the app-server rejected — as success.
    public Core.Sessions.InterruptResult InterruptSession(string sessionId)
        => _interactive.InterruptSession(sessionId);

    /// <summary>
    /// Not supported, and deliberately not faked. Sessions run with command and file approvals
    /// auto-accepted, matching Claude Code's --permission-mode bypassPermissions; there are no
    /// other modes to switch between, so returning true would be a lie the UI acts on.
    /// </summary>
    public bool SetPermissionMode(string sessionId, string mode) => false;

    public Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null)
        => GenerateAsync(new SessionGenerateRequest(model, system, messagesJson, maxTokens, effort, timeout), ct);

    /// <summary>
    /// Without <see cref="SessionGenerateRequest.RequireToolFree"/> this is the long-standing
    /// read-only-sandbox exec. With it, the call goes through the tool-free relay and reports
    /// verified-tool-free only when that call was verified. A schema with a StrictJson form is also
    /// enforced natively with --output-schema (Codex sends it as a strict json_schema format).
    /// </summary>
    public async Task<SessionGenerateResult> GenerateAsync(SessionGenerateRequest request, CancellationToken ct)
    {
        string prompt;
        try
        {
            prompt = BuildGeneratePrompt(request.System, request.MessagesJson, request.MaxTokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new SessionGenerateResult(false, null, null, request.Model, 0, 0, null,
                $"Invalid completion messages: {ex.Message}");
        }

        var timeout = request.Timeout ?? 120;
        var result = request.RequireToolFree
            ? await _codex.ExecuteToolFreeGenerateAsync(prompt, request.Model, timeout, ct, request.Effort,
                request.ResponseSchema, images: request.Images)
            : await _codex.ExecuteGenerateAsync(prompt, request.Model, timeout, ct, request.Effort,
                request.ResponseSchema, request.Images);
        return new SessionGenerateResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error,
            result.Isolation, result.IsolationMechanism, result.NativeResponseSchema);
    }

    private static string BuildGeneratePrompt(string? system, string messagesJson, int maxTokens)
    {
        using var doc = JsonDocument.Parse(messagesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("messages must be an array");

        var prompt = new System.Text.StringBuilder();
        prompt.AppendLine("Act as a stateless text completion. Return only the requested response.");
        prompt.AppendLine("Do not inspect files, run commands, call tools, or explain this wrapper.");
        prompt.AppendLine($"Keep the response within approximately {Math.Clamp(maxTokens, 1, 8192)} tokens.");
        if (!string.IsNullOrWhiteSpace(system))
            prompt.AppendLine($"\nSYSTEM:\n{system}");

        foreach (var message in doc.RootElement.EnumerateArray())
        {
            var role = message.TryGetProperty("role", out var r) ? r.GetString() : "user";
            var content = message.TryGetProperty("content", out var c)
                ? c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()
                : "";
            prompt.AppendLine($"\n{role?.ToUpperInvariant() ?? "USER"}:\n{content}");
        }

        prompt.AppendLine("\nRESPONSE:");
        return prompt.ToString();
    }

    // --- ISessionProvider: Querying ---

    /// <summary>
    /// Both services read the same store, so this would double-count every row. The interactive
    /// view is the authoritative one — it overlays live in-memory state on the same records — so
    /// take it and fill in only the ids it does not already have.
    /// </summary>
    // Both services read the same Sessions table, so reads go through the interactive one
    // unconditionally: it overlays live in-memory state on those rows and maps every column,
    // whereas the stateless mapper predates interactive sessions and silently drops ThreadId,
    // ContextWindow, Effort and Source — which reads as "the field is empty", not "wrong reader".
    public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
        => _interactive.GetSessions(limit, includeDismissed).Select(ToUnified).ToList();

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
    {
        var (info, history) = _interactive.GetSession(sessionId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId)
    {
        var (info, history) = _codex.GetSessionByJobId(jobId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public Dictionary<Guid, Core.Sessions.SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var statuses = _codex.GetSessionStatusesByJobIds(jobIds);
        var result = new Dictionary<Guid, Core.Sessions.SessionStatus>();
        foreach (var (jobId, statusStr) in statuses)
        {
            if (UnifiedStatus(statusStr) is { } status)
                result[jobId] = status;
        }
        return result;
    }

    // --- ISessionProvider: Execution ---

    public async Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
        int timeout, CancellationToken ct, string? streamKey = null,
        Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null)
    {
        string? sandbox = null;
        string? container = null;
        string? effort = null;
        var networkAccess = false;
        if (providerParams != null)
        {
            if (providerParams.TryGetValue("sandbox", out var sb) && sb is string sbs)
                sandbox = sbs;
            if (providerParams.TryGetValue("container", out var c) && c is string cs)
                container = cs;
            if (providerParams.TryGetValue("effort", out var e) && e is string es)
                effort = es;
            if (providerParams.TryGetValue("networkAccess", out var na) && na is bool enabled)
                networkAccess = enabled;
        }

        var result = await _codex.ExecuteExecAsync(prompt, container, workingDir, model, sandbox,
            timeout, ct, streamKey, env, networkAccess, effort);
        var (isolation, mechanism) = ExecuteIsolation(sandbox ?? _defaultSandbox);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error,
            isolation, mechanism);
    }

    /// <summary>
    /// Execute keeps Codex's full tool surface, so the sandbox is the only boundary: never verified
    /// tool-free, and unrestricted when the sandbox itself is turned off.
    /// </summary>
    internal static (string Isolation, string Mechanism) ExecuteIsolation(string sandbox)
        => sandbox == "danger-full-access"
            ? (SessionIsolation.Unrestricted, $"codex-sandbox:{sandbox}")
            : (SessionIsolation.SandboxedBestEffort, $"codex-sandbox:{sandbox}");

    public List<ModelInfo> GetAvailableModels()
    {
        var cached = _models.Cached;

        // This is a synchronous interface method serving a UI dropdown, so it cannot block on a
        // process spawn. If the startup prime lost its race (or failed while the CLI was logged
        // out), kick a refresh off in the background so the next call is populated rather than
        // leaving an empty model list forever.
        if (cached.Count == 0)
            _ = _models.PrimeAsync();

        return cached
            .Where(m => !m.Hidden)
            .Select(m => new ModelInfo { Id = m.Id, Name = m.DisplayName, Fast = m.Fast })
            .ToList();
    }

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _codex.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _codex.StopAllAsync();

    // --- Mapping Helpers ---

    /// <summary>
    /// Codex's internal "Waiting" means the turn is parked on a question: still the running turn,
    /// exactly as Claude reports a parked AskUserQuestion. It is not a stop, and must not end
    /// completion subscriptions or make the session unwritable.
    /// </summary>
    internal static Core.Sessions.SessionStatus? UnifiedStatus(string? status)
        => status == "Waiting" ? Core.Sessions.SessionStatus.Active
            : Enum.TryParse<Core.Sessions.SessionStatus>(status, out var parsed) ? parsed : null;

    private static UnifiedSessionInfo ToUnified(CodexSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "codex",
        ProviderEntity = s.ProviderEntity,
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        RepositoryId = s.RepositoryId,
        Status = UnifiedStatus(s.Status) ?? Core.Sessions.SessionStatus.Stopped,
        StartedAt = s.StartedAt,
        Model = s.Model,
        Title = s.Title,
        MessageCount = s.MessageCount,
        CostUsd = s.CostUsd,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        CachedInputTokens = s.CachedInputTokens,
        ContextTokens = s.ContextTokens,
        JobId = s.JobId,
        Effort = s.Effort,
        QualityTier = s.QualityTier,
        Source = s.Source,
        ExecutionProfile = s.ExecutionProfile,
        UserId = s.UserId,
        UserName = s.UserName,
        UserAvatarUrl = s.UserAvatarUrl,
        ContextWindow = s.ContextWindow,
        StopReason = s.StopReason,
        StoppedAt = s.StoppedAt,
        // The Codex thread id, which is what makes a session resumable — and resumable from the
        // Codex CLI and desktop app too, since every surface shares the same thread store.
        ProviderSessionId = s.ThreadId,
        ProviderMetadata = new() { ["executionProfile"] = s.ExecutionProfile },
        SupportsSteering = true,
    };

    private static UnifiedStreamEvent ToUnifiedEvent(CodexStreamEvent e) => new()
    {
        Type = e.Type,
        Content = e.Content,
        ToolName = e.ToolName,
        ToolInput = e.ToolInput,
        ToolResult = e.ToolResult,
        IsPartial = e.IsPartial,
        MessageId = e.MessageId,
        Phase = e.Phase,
        MessageUid = e.MessageUid,
        RequestId = e.RequestId,
        Attachments = e.Attachments?.Select(a => new UnifiedAttachment
        {
            Type = a.Type,
            MimeType = a.MimeType,
            Data = a.Data,
            Url = a.Url,
        }).ToList(),
    };

    private static UnifiedMessageRecord ToUnifiedMessage(CodexMessageRecord m) => new()
    {
        Id = m.Id,
        SessionId = m.SessionId,
        Role = m.Role,
        EventType = m.EventType,
        Content = m.Content,
        ToolName = m.ToolName,
        ToolInput = m.ToolInput,
        ToolResult = m.ToolResult,
        MessageId = m.MessageId,
        Phase = m.Phase,
        MessageUid = m.MessageUid,
        ParentToolUseId = m.ParentToolUseId,
        Timestamp = m.Timestamp,
    };

    // --- Config ---

    private static CodexConfig BuildConfig(ProviderConfig config)
    {
        var codexPath = ProviderHelpers.GetExtra(config, "CodexPath", "");
        return new CodexConfig
        {
            CodexPath = string.IsNullOrEmpty(codexPath) ? null : codexPath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
            SandboxMode = ProviderHelpers.GetExtra(config, "SandboxMode", "workspace-write"),
        };
    }

    internal static string? GetTitleQualityTier(ProviderConfig config)
    {
        var value = ProviderHelpers.GetExtra(config, "TitleQualityTier", "fast").Trim();
        return string.IsNullOrWhiteSpace(value)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new()
        {
            Type = "string",
            Required = false,
            Default = _models.Cached.FirstOrDefault(m => m.IsDefault)?.Id,
            Enum = _models.Cached.Where(m => !m.Hidden).Select(m => m.Id).ToList(),
            Description = "Model to use"
        },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["networkAccess"] = new() { Type = "boolean", Required = false, Default = false, Description = "Enable command network access for workspace-write executions" },
        ["sandbox"] = new() { Type = "string", Required = false, Default = "workspace-write", Enum = ["read-only", "workspace-write", "danger-full-access"], Description = "Sandbox mode" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => new List<EndpointManifest>
    {
        new() { Method = "POST", Path = "/codex/execute", Description = "Execute a prompt with Codex agent", Parameters = new() { ["prompt"] = new() { Type = "string", Required = true }, ["model"] = new() { Type = "string", Required = false } }, Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/codex/models", Description = "List available Codex models", Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/codex/sessions", Description = "List recent Codex sessions" },
        new() { Method = "GET", Path = "/codex/sessions/{id}", Description = "Get session details and message history" },
        new() { Method = "GET", Path = "/codex/sessions/by-job/{jobId}", Description = "Get the session associated with a job ID" },
        new() { Method = "POST", Path = "/codex/sessions/{id}/stop", Description = "Stop a running execution" },
        new() { Method = "POST", Path = "/codex/sessions/{id}/dismiss", Description = "Dismiss a session" },
        new() { Method = "DELETE", Path = "/codex/sessions/{id}", Description = "Force-kill an execution" }
    };

    private CliProviderSessionState GetCliSessionState()
    {
        var legacy = _codex.GetSessions(100_000, includeDismissed: false);
        var interactive = _interactive.GetSessions(100_000, includeDismissed: false);
        var blocking = legacy.Select(session => (session.Id, session.Status))
            .Concat(interactive.Select(session => (session.Id, session.Status)))
            .Where(session => session.Status is "Starting" or "Active")
            .Select(session => session.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var idle = interactive
            .Where(session => session.Status == "Idle" && session.ProcessId is not null)
            .Select(session => session.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownedProcesses = legacy.Select(session => session.ProcessId)
            .Concat(interactive.Select(session => session.ProcessId))
            .Where(processId => processId is > 0)
            .Select(processId => processId!.Value)
            .Distinct()
            .ToArray();
        return new CliProviderSessionState(blocking, idle, ownedProcesses);
    }

    private async Task ParkIdleCliSessionsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken ct)
    {
        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            await _interactive.ForceKillAsync(sessionId, SessionStopReasons.MaintenanceRestart);
        }
    }
}
