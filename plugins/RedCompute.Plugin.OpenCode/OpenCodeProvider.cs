using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeProvider : IPluginProvider, IPluginEventSource, IJobExtendedProvider, ISessionProvider,
    IIdleSessionParkingProvider, ICliVersionManagedProvider, IProviderWarmup, ISessionInferenceSettings
{
    private readonly string _capabilitySlug;
    private readonly OpenCodeSessionService _opencode;
    private readonly OpenCodeModelCatalog _models;
    private readonly OpenCodeSessionStore _store;
    private readonly object _injectionGate = new();
    private readonly IJobTracker _jobTracker;
    private readonly Action<string, Guid?> _log;

    public event Action<string, object>? PluginEvent;
    public event Action<string, UnifiedStreamEvent>? SessionStreamEvent;

    public string Name => "OpenCode";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Open Code";
    public string ProviderType => "OpenCode";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    // ISessionProvider
    public string ProviderId => "opencode";
    public string ProviderDisplayName => "Open Code";
    public NpmCliVersionManager CliVersionManager => _opencode.CliVersionManager;
    public SessionCapabilities Capabilities =>
        SessionCapabilities.StatelessExecution |
        SessionCapabilities.PersistentSessions |
        SessionCapabilities.Resume |
        SessionCapabilities.Interrupt |
        SessionCapabilities.SendMessage |
        SessionCapabilities.ImageAttachments |
        SessionCapabilities.FileAttachments |
        SessionCapabilities.PermissionMode;

    /// <summary>ask: request_permission parks as a permission card. full: allow_once, as before. No plan or auto.</summary>
    public IReadOnlyList<string> SupportedAccessLevels { get; } = [SessionAccessLevels.Ask, SessionAccessLevels.Full];
    public SessionAccessState? GetAccessState(string sessionId) => _opencode.GetAccessState(sessionId);
    public bool SetAccessState(string sessionId, SessionAccessState state) => _opencode.SetAccessState(sessionId, state);
    public IReadOnlyList<PendingSessionQuestion> GetPendingQuestions(string sessionId) => _opencode.GetPendingQuestions(sessionId);
    public QuestionAnswerResult SubmitQuestionAnswer(string sessionId, SessionQuestionAnswer answer)
        => _opencode.SubmitQuestionAnswer(sessionId, answer);

    public bool RequiresIdleForInferenceChanges => true;
    public bool RequiresResidentForInferenceChanges => true;
    public Task<SessionInferenceOptions> GetInferenceOptionsAsync(string sessionId, CancellationToken ct)
        => Task.FromResult(_opencode.GetInferenceOptions(sessionId));
    public Task<bool> TrySetInferenceSettingsAsync(string sessionId, string? model, string? effort, CancellationToken ct)
        => _opencode.TrySetInferenceSettingsAsync(sessionId, model, effort, ct);

    public string? LastStartError => _opencode.LastStartError;
    public string? LastStartErrorCode => _opencode.LastStartErrorCode;

    public OpenCodeProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, Action<string, Guid?> log,
        IAcceleratorAdmissionCoordinator? acceleratorAdmission = null, IExecutionHost? host = null,
        INodeRegistry? nodes = null, Func<string, ProviderConfig?>? resolveSharedProfile = null,
        ISessionScratchAllocator? scratchAllocator = null)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new OpenCodeDbContext())
        {
            db.Initialize();
            // Once: a later instance (a dedicated provider, a reload) finds the column and logs nothing.
            if (db.NodeIdColumnAddedIn is { } added)
                log($"[OpenCode] Added Sessions.NodeId to opencode.db in {added.TotalMilliseconds:0.#} ms", null);
            if (db.PlacementColumnAddedIn is { } placementAdded)
                log($"[OpenCode] Added Sessions.Placement to opencode.db in {placementAdded.TotalMilliseconds:0.#} ms", null);
        }
        _store = new OpenCodeSessionStore(nodes?.StampNodeId);
        var openCodeConfig = BuildConfig(config);
        _opencode = new OpenCodeSessionService(openCodeConfig, jobTracker, _store, log,
            acceleratorAdmission: acceleratorAdmission, host: host, resolveSharedProfile: resolveSharedProfile,
            scratchAllocator: scratchAllocator);
        _models = new OpenCodeModelCatalog(_opencode.ResolveOpenCodePath, log, new ProcessCliCommandRunner(host));
        WireEvents();
    }

    /// <summary>Test seam: a provider over an existing service and store, as the Claude and Codex providers have.</summary>
    internal OpenCodeProvider(string capabilitySlug, OpenCodeSessionService opencode, OpenCodeSessionStore store,
        IJobTracker jobTracker, Action<string, Guid?> log, OpenCodeModelCatalog? models = null)
    {
        _capabilitySlug = capabilitySlug;
        _opencode = opencode;
        _models = models ?? new OpenCodeModelCatalog(() => null, log); // tests never spawn the real CLI
        _store = store;
        _jobTracker = jobTracker;
        _log = log;
        WireEvents();
    }

    private void WireEvents()
    {
        _opencode.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _opencode.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _opencode.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _opencode.StreamEvent += (sessionId, evt) =>
        {
            PluginEvent?.Invoke("session.stream", new { sessionId, @event = evt });
            SessionStreamEvent?.Invoke(sessionId, ToUnifiedEvent(evt));
        };
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>
    /// Fills the model list from <c>opencode models</c>. The host warms every provider, not only the default one
    /// that it starts: OpenCode is usually selectable beside the default, and its list is served from this cache.
    /// The CLI is resolved and spawned on the thread pool, never on the caller.
    /// </summary>
    public Task WarmAsync(CancellationToken ct = default) => Task.Run(() => _models.PrimeAsync(ct), ct);
    public Task StopAsync(CancellationToken ct = default) => _opencode.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void CancelJob(string jobKey) => _opencode.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _opencode.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // --- ISessionProvider: Session Lifecycle ---

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential = false)
    {
        var info = await _opencode.StartSession(projectPath, model, userId, userName,
            userAvatarUrl, endpointUrl, apiKey, effort, thinkingBudget, qualityTier, providerEntity,
            repositoryId, provenance, scratchDirectory, confidential);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential, string? developerInstructions)
    {
        var info = await _opencode.StartSession(projectPath, model, userId, userName,
            userAvatarUrl, endpointUrl, apiKey, effort, thinkingBudget, qualityTier, providerEntity,
            repositoryId, provenance, scratchDirectory, confidential, developerInstructions);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = await _opencode.ResumeSession(sessionId);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
    {
        var info = await _opencode.ResumeSession(sessionId, provenance);
        return info != null ? ToUnified(info) : null;
    }

    public Task StopSessionAsync(string sessionId) => _opencode.StopSession(sessionId);

    public Task ForceKillAsync(string sessionId)
    {
        _opencode.ForceKill(sessionId);
        return Task.CompletedTask;
    }

    public Task ForceKillAsync(string sessionId, string stopReason)
    {
        _opencode.ForceKill(sessionId, stopReason);
        return Task.CompletedTask;
    }

    public IReadOnlyList<UnifiedSessionInfo> GetResidentIdleSessions()
        => _opencode.GetResidentIdleSessions().Select(ToUnified).ToArray();

    public Task<bool> TryParkIdleSessionAsync(
        string sessionId, string stopReason, CancellationToken ct = default)
        => _opencode.TryParkIdleSessionAsync(sessionId, stopReason, ct);

    public void DismissSession(string sessionId) => _opencode.DismissSession(sessionId);

    public bool RestoreSession(string sessionId) => _opencode.RestoreSession(sessionId);

    // --- ISessionProvider: Messaging ---

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null)
        => _opencode.SendInput(sessionId, input, attachmentsJson, messageUid);

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson, string? messageUid, string? displayContent)
        => _opencode.SendInput(sessionId, input, attachmentsJson, messageUid, displayContent);

    public Task<bool> InjectMessageAsync(string sessionId, string role, string content,
        string? attachmentsJson = null, string? messageUid = null)
    {
        lock (_injectionGate)
        {
            var (info, history) = _opencode.GetSession(sessionId);
            if (info is null) return Task.FromResult(false);
            var existing = messageUid is null
                ? null
                : history.FirstOrDefault(message =>
                    message.EventType == "text" && message.MessageUid == messageUid);
            if (existing is not null)
                return Task.FromResult(existing.Role == role
                    && existing.Content == content
                    && existing.AttachmentsJson == attachmentsJson);

            _store.AddMessage(new OpenCodeMessageRecord
            {
                SessionId = sessionId,
                Role = role,
                EventType = "text",
                Content = content,
                MessageUid = messageUid,
                AttachmentsJson = attachmentsJson,
                ProviderPartId = messageUid is null ? null : $"injected:{messageUid}",
                Timestamp = DateTimeOffset.UtcNow,
            });
            return Task.FromResult(true);
        }
    }

    public bool SendAnswer(string sessionId, string answer)
        => _opencode.SendAnswer(sessionId, answer);

    // --- ISessionProvider: Interrupt ---

    public InterruptResult InterruptSession(string sessionId)
        => _opencode.InterruptSession(sessionId);

    // --- ISessionProvider: Configuration ---

    /// <summary>Legacy entry point: a mode maps to an access level; RedCompute's routes apply the rules.</summary>
    public bool SetPermissionMode(string sessionId, string mode)
        => SessionAccessLevels.TryFromPermissionMode(mode, out var level)
            && GetAccessState(sessionId) is { } current
            && SetAccessState(sessionId, (current with { AccessLevel = SessionAccessResolver.ClampToSupported(level, SupportedAccessLevels) }).AsExplicit());

    // --- ISessionProvider: Generate (not supported) ---

    public Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null)
        => throw new NotSupportedException("OpenCode does not support direct LLM completion");

    // --- ISessionProvider: Querying ---

    public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
        => _opencode.GetSessions(limit, includeDismissed).Select(ToUnified).ToList();

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
    {
        var (info, history) = _opencode.GetSession(sessionId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public UnifiedSessionInfo? GetSessionInfo(string sessionId)
        => _opencode.GetSessionInfo(sessionId) is { } info ? ToUnified(info) : null;

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId)
    {
        var (info, history) = _opencode.GetSessionByJobId(jobId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var statuses = _opencode.GetSessionStatusesByJobIds(jobIds);
        var result = new Dictionary<Guid, SessionStatus>();
        foreach (var (jobId, statusStr) in statuses)
        {
            if (Enum.TryParse<SessionStatus>(statusStr, out var status))
                result[jobId] = status;
        }
        return result;
    }

    // --- ISessionProvider: Execution ---

    public async Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
        int timeout, CancellationToken ct, string? streamKey = null,
        Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null)
    {
        string? container = null;
        if (providerParams?.TryGetValue("container", out var c) == true && c is string cs)
            container = cs;

        env = OneshotEnvironment(env,
            providerParams?.TryGetValue("accessLevel", out var level) == true ? level as string : null);
        var providerEntity = providerParams?.GetValueOrDefault("providerEntity") as string;
        var effort = providerParams?.GetValueOrDefault("effort") as string;
        var result = await _opencode.ExecuteAsync(
            prompt, container, workingDir, model, timeout, ct, streamKey, env, providerEntity, effort);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    /// <summary>
    /// The environment a one-shot really runs with at its resolved level (RedCompute sets
    /// <c>accessLevel</c>; a body cannot). Below Full, and with no level, the deny override is
    /// written last, over any value the caller's <c>env</c> carried.
    /// </summary>
    internal static Dictionary<string, string>? OneshotEnvironment(Dictionary<string, string>? env, string? accessLevel)
    {
        if (accessLevel == SessionAccessLevels.Full) return env;
        var result = env is null ? new Dictionary<string, string>() : new Dictionary<string, string>(env);
        result[OpenCodeSessionService.PermissionEnvironment] = OpenCodeSessionService.OneshotPermissionOverride;
        return result;
    }

    /// <summary>What this machine's OpenCode reports (<c>opencode models</c>), from a ten-minute cache; empty until known.</summary>
    public List<ModelInfo> GetAvailableModels() => [.. _models.Cached];

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _opencode.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _opencode.StopAllAsync();

    // --- Mapping Helpers ---

    private static UnifiedSessionInfo ToUnified(OpenCodeSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "opencode",
        NativeDeveloperInstructions = s.DeveloperInstructions,
        ProviderEntity = s.ProviderEntity,
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        NodeId = s.NodeId,
        Placement = SessionPlacementScope.Parse(s.Placement),
        RepositoryId = s.RepositoryId,
        Status = Enum.TryParse<SessionStatus>(s.Status, out var st)
            ? st : SessionStatus.Stopped,
        StopReason = s.StopReason,
        StoppedAt = s.StoppedAt,
        StartedAt = s.StartedAt,
        Model = s.Model,
        ProviderSessionId = s.OpenCodeSessionId,
        Title = s.Title,
        MessageCount = s.MessageCount,
        CostUsd = s.CostUsd,
        InputTokens = s.InputTokens,
        PromptCache = s.PromptCache,
        OutputTokens = s.OutputTokens,
        ContextTokens = s.InputTokens,
        ContextWindow = s.ContextWindow,
        Effort = s.Effort,
        QualityTier = s.QualityTier,
        JobId = s.JobId,
        Source = s.Source,
        UserId = s.UserId,
        UserName = s.UserName,
        UserAvatarUrl = s.UserAvatarUrl,
        AccessLevel = s.AccessLevel,
        MaxAccessLevel = s.MaxAccessLevel,
        AccessLevelBeforePlan = s.AccessLevelBeforePlan,
        Unattended = s.Unattended,
        AccessLevelSource = s.Access.Source,
        SupportedAccessLevels = [SessionAccessLevels.Ask, SessionAccessLevels.Full],
    };

    private static UnifiedStreamEvent ToUnifiedEvent(OpenCodeStreamEvent e) => new()
    {
        Type = e.Type,
        Content = e.Content,
        ToolName = e.ToolName,
        ToolInput = e.ToolInput,
        ToolResult = e.ToolResult,
        IsPartial = e.IsPartial,
        MessageId = e.MessageId,
        MessageUid = e.MessageUid,
        RequestId = e.RequestId,
        Kind = e.Kind,
        Permission = e.Permission,
        Decision = e.Decision,
        Attachments = e.Attachments?.Select(a => new UnifiedAttachment
        {
            Type = a.Type,
            MimeType = a.MimeType,
            Data = a.Data,
            Url = a.Url
        }).ToList(),
    };

    private static UnifiedMessageRecord ToUnifiedMessage(OpenCodeMessageRecord m) => new()
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
        MessageUid = m.MessageUid,
        ParentToolUseId = m.ParentToolUseId,
        Timestamp = m.Timestamp,
        AttachmentsJson = m.AttachmentsJson,
    };

    // --- Config ---

    private static OpenCodeConfig BuildConfig(ProviderConfig config)
    {
        var openCodePath = ProviderHelpers.GetExtra(config, "OpenCodePath", "");
        var openCodeDataHome = ProviderHelpers.GetExtra(config, "OpenCodeDataHome", "");
        return new OpenCodeConfig
        {
            OpenCodePath = string.IsNullOrEmpty(openCodePath) ? null : openCodePath,
            OpenCodeDataHome = string.IsNullOrEmpty(openCodeDataHome) ? null : openCodeDataHome,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
            AcceleratorId = ProviderHelpers.GetExtra(config, "AcceleratorId", "cuda:0"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new() { Type = "string", Required = false, Description = "Optional concrete model override; quality tiers are preferred" },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];
}
