using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeCodeProvider : IPluginProvider, IPluginEventSource, IJobExtendedProvider, ISessionProvider,
    IProviderUsageSource, ICliVersionManagedProvider
{
    private readonly string _capabilitySlug;
    private readonly ClaudeSessionService _claude;
    private readonly IClaudeSessionStore _store;
    private readonly object _injectionGate = new();
    private readonly IJobTracker _jobTracker;
    private readonly NpmCliVersionManager _cliVersionManager;
    private readonly Action<string, Guid?> _log;

    public event Action<string, object>? PluginEvent;
    public event Action<string, UnifiedStreamEvent>? SessionStreamEvent;

    public string Name => "Claude Code";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Claude Code";
    public string ProviderType => "ClaudeCode";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    // ISessionProvider
    public string ProviderId => "claude-code";
    public string ProviderDisplayName => "Claude Code";
    public NpmCliVersionManager CliVersionManager => _cliVersionManager;
    internal const SessionCapabilities DeclaredCapabilities =
        SessionCapabilities.StatelessExecution | SessionCapabilities.PersistentSessions |
        SessionCapabilities.Resume | SessionCapabilities.Interrupt |
        SessionCapabilities.SendMessage | SessionCapabilities.PermissionMode |
        SessionCapabilities.ImageAttachments | SessionCapabilities.FileAttachments |
        SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate |
        SessionCapabilities.SupportsSteering | SessionCapabilities.SupportsBackgroundTaskStop;
    public SessionCapabilities Capabilities => DeclaredCapabilities;

    public string? LastStartError => _claude.LastStartError;

    public ClaudeCodeProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, IProviderQualityModeResolver qualityModes,
        Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new ClaudeDbContext()) { db.Initialize(); }
        _store = new ClaudeSessionStore();
        var claudeConfig = BuildConfig(config);
        _claude = new ClaudeSessionService(
            claudeConfig, jobTracker, _store, qualityModes,
            () => GetTitleQualityTier(config), log);
        _cliVersionManager = CreateCliVersionManager(claudeConfig);
        _claude.LaunchGate = _cliVersionManager;
        WireSessionEvents();
    }

    /// <summary>Test seam: binds the provider to an existing service and store.</summary>
    internal ClaudeCodeProvider(string capabilitySlug, ClaudeSessionService claude,
        IClaudeSessionStore store, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _claude = claude;
        _store = store;
        _jobTracker = jobTracker;
        _log = log;
        _cliVersionManager = new NpmCliVersionManager(
            "@anthropic-ai/claude-code", "Claude Code", () => null,
            GetCliSessionState, ParkIdleCliSessionsAsync);
        _claude.LaunchGate = _cliVersionManager;
        WireSessionEvents();
    }

    private void WireSessionEvents()
    {
        _claude.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _claude.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _claude.SessionEnded += (id, reason, stopReason) => PluginEvent?.Invoke("session.ended", new { id, reason, stopReason });
        _claude.StreamEvent += (sessionId, evt) =>
        {
            PluginEvent?.Invoke("session.stream", new { sessionId, @event = evt });
            SessionStreamEvent?.Invoke(sessionId, ToUnifiedEvent(evt));
        };
    }

    public Task<bool> InjectMessageAsync(string sessionId, string role, string content,
        string? attachmentsJson = null, string? messageUid = null)
    {
        lock (_injectionGate)
        {
            var session = _store.FindSession(sessionId);
            if (session == null) return Task.FromResult(false);
            var existing = messageUid is null
                ? null
                : _store.GetMessages(sessionId).FirstOrDefault(message =>
                    message.EventType == "text" && message.MessageUid == messageUid);
            if (existing is not null)
                return Task.FromResult(existing.Role == role
                    && existing.Content == content
                    && existing.AttachmentsJson == attachmentsJson);

            _store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = sessionId,
                Role = role,
                EventType = "text",
                Content = content,
                MessageUid = messageUid,
                Timestamp = DateTimeOffset.UtcNow,
                AttachmentsJson = attachmentsJson,
            });
            return Task.FromResult(true);
        }
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync(CancellationToken ct = default) => _claude.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public Task<ProviderUsageSnapshot?> GetProviderUsageAsync(bool forceRefresh = false, CancellationToken ct = default)
        => _claude.GetProviderUsageAsync(forceRefresh, ct);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void CancelJob(string jobKey) => _claude.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _claude.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    // --- ISessionProvider: Session Lifecycle ---

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential = false)
    {
        var info = await _claude.StartSessionAsync(projectPath, model, userId, userName, userAvatarUrl,
            effort, qualityTier, providerEntity, repositoryId, provenance, scratchDirectory,
            confidential);
        return info != null ? ToUnified(info) : null;
    }

    /// <summary>
    /// Delivers developer instructions as a real system-level instruction: the persistent CLI
    /// is launched with <c>--append-system-prompt</c>, and the text is persisted with the session
    /// so every later relaunch (resume, restart recovery, interrupt recovery) reapplies it.
    /// </summary>
    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
        string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl,
        string? apiKey, int? thinkingBudget, string? qualityTier, string? providerEntity,
        Guid? repositoryId, JobProvenance provenance, string? scratchDirectory,
        bool confidential, string? developerInstructions)
    {
        var info = await _claude.StartSessionAsync(projectPath, model, userId, userName, userAvatarUrl,
            effort, qualityTier, providerEntity, repositoryId, provenance, scratchDirectory,
            confidential, developerInstructions);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = await _claude.ResumeSessionAsync(sessionId);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
    {
        var info = await _claude.ResumeSessionAsync(sessionId, provenance);
        return info != null ? ToUnified(info) : null;
    }

    public Task StopSessionAsync(string sessionId) => _claude.StopSession(sessionId);
    public Task ForceKillAsync(string sessionId) => _claude.ForceKill(sessionId);
    public Task ForceKillAsync(string sessionId, string stopReason) => _claude.ForceKill(sessionId, stopReason);
    public void DismissSession(string sessionId) => _claude.DismissSession(sessionId);

    // --- ISessionProvider: Messaging ---

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null)
        => _claude.SendInput(sessionId, input, attachmentsJson, messageUid);

    public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson, string? messageUid, string? displayContent)
        => _claude.SendInput(sessionId, input, attachmentsJson, messageUid, displayContent);

    public Task<SessionInputDeliveryResult> TrySteerInputAsync(string sessionId,
        IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null,
        string? messageUid = null, string? displayContent = null)
        => _claude.TrySteerInputAsync(sessionId, input, attachmentsJson, messageUid, displayContent);

    public bool SendAnswer(string sessionId, string answer) => _claude.SendAnswer(sessionId, answer);

    public QuestionAnswerResult SubmitQuestionAnswer(string sessionId, SessionQuestionAnswer answer)
        => _claude.SubmitQuestionAnswer(sessionId, answer);

    // --- ISessionProvider: Interrupt ---

    public Core.Sessions.InterruptResult InterruptSession(string sessionId)
    {
        var result = _claude.InterruptSession(sessionId);
        return result switch
        {
            ClaudeSessionService.InterruptResult.Interrupted => Core.Sessions.InterruptResult.Interrupted,
            ClaudeSessionService.InterruptResult.NotActive => Core.Sessions.InterruptResult.NotActive,
            ClaudeSessionService.InterruptResult.NotFound => Core.Sessions.InterruptResult.NotFound,
            _ => Core.Sessions.InterruptResult.Error,
        };
    }

    public Task<BackgroundTaskStopResult> TryStopBackgroundTaskAsync(string sessionId, string taskId)
        => _claude.TryStopBackgroundTaskAsync(sessionId, taskId);

    public IReadOnlyList<BackgroundTaskInfo> GetBackgroundTasks(string sessionId)
        => _claude.GetBackgroundTasks(sessionId);

    // --- ISessionProvider: Configuration ---

    public bool SetPermissionMode(string sessionId, string mode) => _claude.SetPermissionMode(sessionId, mode);

    // --- ISessionProvider: Querying ---

    public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
        => _claude.GetSessions(limit, includeDismissed).Select(ToUnified).ToList();

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
    {
        var (info, history) = _claude.GetSession(sessionId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId)
    {
        var (info, history) = _claude.GetSessionByJobId(jobId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

    public Dictionary<Guid, Core.Sessions.SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var statuses = _claude.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => (Core.Sessions.SessionStatus)(int)kv.Value);
    }

    // --- ISessionProvider: Execution ---

    public async Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
        int timeout, CancellationToken ct, string? streamKey = null,
        Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null)
    {
        string? effort = null;
        int maxTurns = 1;
        string[]? tools = null;
        string[]? allowedTools = null;
        string[]? addDirs = null;
        string? container = null;

        if (providerParams != null)
        {
            if (providerParams.TryGetValue("effort", out var e) && e is string es) effort = es;
            if (providerParams.TryGetValue("maxTurns", out var mt) && mt is int mti) maxTurns = mti;
            if (providerParams.TryGetValue("tools", out var toolsValue) && toolsValue is string[] toolNames) tools = toolNames;
            if (providerParams.TryGetValue("allowedTools", out var at) && at is string[] ats) allowedTools = ats;
            if (providerParams.TryGetValue("addDirs", out var ad) && ad is string[] ads) addDirs = ads;
            if (providerParams.TryGetValue("container", out var c) && c is string cs) container = cs;
        }

        var result = await _claude.ExecuteAgentAsync(prompt, container, workingDir, model, effort,
            maxTurns, tools, allowedTools, addDirs, timeout, ct, streamKey, env);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Generate ---

    public async Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null)
    {
        var messages = JsonDocument.Parse(messagesJson).RootElement;
        var result = await _claude.ExecuteOneshotAsync(model, system, messages, maxTokens, ct, effort, timeout ?? 120);
        return new SessionGenerateResult(result.Success, result.Text, result.StreamOutput, result.Model,
            result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    public List<ModelInfo> GetAvailableModels() =>
    [
        new() { Id = "haiku", Name = "Haiku", Fast = true },
        new() { Id = "sonnet", Name = "Sonnet", Fast = false },
        new() { Id = "opus", Name = "Opus", Fast = false },
        new() { Id = "fable", Name = "Fable", Fast = false },
    ];

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _claude.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _claude.StopAllAsync();

    // --- Mapping Helpers ---

    private static UnifiedSessionInfo ToUnified(ClaudeSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "claude-code",
        ProviderEntity = s.ProviderEntity,
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        RepositoryId = s.RepositoryId,
        Status = (Core.Sessions.SessionStatus)(int)s.Status,
        StopReason = s.StopReason,
        StoppedAt = s.StoppedAt,
        StartedAt = s.StartedAt,
        Model = s.Model,
        ProviderSessionId = s.ClaudeSessionId,
        Title = s.Title,
        MessageCount = s.MessageCount,
        CostUsd = s.CostUsd,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        CachedInputTokens = (s.CacheReadInputTokens ?? 0) + (s.CacheCreationInputTokens ?? 0) > 0
            ? (s.CacheReadInputTokens ?? 0) + (s.CacheCreationInputTokens ?? 0) : null,
        ContextTokens = s.ContextTokens,
        ContextWindow = s.ContextWindow,
        Effort = s.Effort,
        QualityTier = s.QualityTier,
        JobId = s.JobId,
        PermissionMode = s.PermissionMode,
        Source = s.Source,
        UserId = s.UserId,
        UserName = s.UserName,
        UserAvatarUrl = s.UserAvatarUrl,
        SupportsSteering = true,
        BackgroundTasks = s.BackgroundTasks,
        ProviderMetadata = new()
        {
            ["cacheReadInputTokens"] = s.CacheReadInputTokens,
            ["cacheCreationInputTokens"] = s.CacheCreationInputTokens,
        }
    };

    private static UnifiedStreamEvent ToUnifiedEvent(ClaudeStreamEvent e) => new()
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
        ParentToolUseId = e.ParentToolUseId,
        TurnOrigin = e.TurnOrigin,
        RequestId = e.RequestId,
    };

    private static UnifiedMessageRecord ToUnifiedMessage(ClaudeMessageRecord m) => new()
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
        TurnOrigin = ClaudeSessionStore.ReadTurnOrigin(m.TurnOriginJson),
        Timestamp = m.Timestamp,
        AttachmentsJson = m.AttachmentsJson,
    };

    // --- Config ---

    private static ClaudeConfig BuildConfig(ProviderConfig config)
    {
        var claudePath = ProviderHelpers.GetExtra(config, "ClaudePath", "");
        return new ClaudeConfig
        {
            ClaudePath = string.IsNullOrEmpty(claudePath) ? null : claudePath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
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
        ["model"] = new() { Type = "string", Required = false, Description = "Optional concrete model override; quality tiers are preferred" },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];

    private NpmCliVersionManager CreateCliVersionManager(ClaudeConfig config)
        => new(
            "@anthropic-ai/claude-code",
            "Claude Code",
            () => ClaudeSessionService.ResolveClaudePath(
                config.ClaudePath,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("PATH")),
            GetCliSessionState,
            ParkIdleCliSessionsAsync);

    private CliProviderSessionState GetCliSessionState()
    {
        var sessions = _claude.GetSessions(100_000, includeDismissed: false);
        return new CliProviderSessionState(
            sessions.Count(session => session.Status is SessionStatus.Starting or SessionStatus.Active),
            sessions.Where(session => session.Status == SessionStatus.Idle && session.ProcessId is not null)
                .Select(session => session.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            sessions.Where(session => session.ProcessId is > 0)
                .Select(session => session.ProcessId!.Value)
                .Distinct()
                .ToArray());
    }

    private async Task ParkIdleCliSessionsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken ct)
    {
        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            await _claude.ForceKill(sessionId, SessionStopReasons.MaintenanceRestart);
        }
    }
}
