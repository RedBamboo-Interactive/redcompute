using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeCodeProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider, ISessionProvider
{
    private readonly string _capabilitySlug;
    private readonly ClaudeSessionService _claude;
    private readonly ClaudeSessionStore _store;
    private readonly IJobTracker _jobTracker;
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
    public SessionCapabilities Capabilities =>
        SessionCapabilities.StatelessExecution | SessionCapabilities.PersistentSessions |
        SessionCapabilities.Resume | SessionCapabilities.Interrupt |
        SessionCapabilities.SendMessage | SessionCapabilities.PermissionMode |
        SessionCapabilities.ConfigUpdate | SessionCapabilities.ImageAttachments |
        SessionCapabilities.ProjectDiscovery | SessionCapabilities.Generate;

    public string? LastStartError => _claude.LastStartError;

    public ClaudeCodeProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new ClaudeDbContext()) { db.Initialize(); }
        _store = new ClaudeSessionStore();
        var claudeConfig = BuildConfig(config);
        _claude = new ClaudeSessionService(claudeConfig, jobTracker, _store, log);

        _claude.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _claude.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _claude.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _claude.StreamEvent += (sessionId, evt) =>
        {
            PluginEvent?.Invoke("session.stream", new { sessionId, @event = evt });
            SessionStreamEvent?.Invoke(sessionId, ToUnifiedEvent(evt));
        };
    }

    public Task<bool> InjectMessageAsync(string sessionId, string role, string content)
    {
        var session = _store.FindSession(sessionId);
        if (session == null) return Task.FromResult(false);

        _store.AddMessage(new ClaudeMessageRecord
        {
            SessionId = sessionId,
            Role = role,
            EventType = "text",
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
        });
        return Task.FromResult(true);
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync(CancellationToken ct = default) => _claude.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void MapCustomEndpoints(WebApplication app)
    {
        ClaudeSessionEndpoints.Map(app, _claude, _jobTracker, _log);
    }

    public void CancelJob(string jobKey) => _claude.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _claude.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    // --- ISessionProvider: Session Lifecycle ---

    public Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null)
    {
        var info = _claude.StartSession(projectPath, callerInfo, model, userId, userName, userAvatarUrl);
        return Task.FromResult(info != null ? ToUnified(info) : null);
    }

    public Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = _claude.ResumeSession(sessionId);
        return Task.FromResult(info != null ? ToUnified(info) : null);
    }

    public Task StopSessionAsync(string sessionId) => _claude.StopSession(sessionId);
    public Task ForceKillAsync(string sessionId) => _claude.ForceKill(sessionId);
    public void DismissSession(string sessionId) => _claude.DismissSession(sessionId);

    // --- ISessionProvider: Messaging ---

    public Task<bool> SendMessageAsync(string sessionId, string content, Core.Sessions.ImageAttachment[]? images = null)
    {
        var claudeImages = images?.Select(i => new ClaudeCode.ImageAttachment(i.MediaType, i.Base64)).ToArray();
        return _claude.SendMessage(sessionId, content, claudeImages);
    }

    public bool SendAnswer(string sessionId, string answer) => _claude.SendAnswer(sessionId, answer);

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

    // --- ISessionProvider: Configuration ---

    public async Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort)
    {
        var info = await _claude.UpdateSessionConfig(sessionId, model, effort);
        return info != null ? ToUnified(info) : null;
    }

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
        string[]? allowedTools = null;
        string[]? addDirs = null;
        string? container = null;

        if (providerParams != null)
        {
            if (providerParams.TryGetValue("effort", out var e) && e is string es) effort = es;
            if (providerParams.TryGetValue("maxTurns", out var mt) && mt is int mti) maxTurns = mti;
            if (providerParams.TryGetValue("allowedTools", out var at) && at is string[] ats) allowedTools = ats;
            if (providerParams.TryGetValue("addDirs", out var ad) && ad is string[] ads) addDirs = ads;
            if (providerParams.TryGetValue("container", out var c) && c is string cs) container = cs;
        }

        var result = await _claude.ExecuteAgentAsync(prompt, container, workingDir, model, effort,
            maxTurns, allowedTools, addDirs, timeout, ct, streamKey, env);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Generate ---

    public async Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct)
    {
        var messages = JsonDocument.Parse(messagesJson).RootElement;
        var result = await _claude.ExecuteOneshotAsync(model, system, messages, maxTokens, ct);
        return new SessionGenerateResult(result.Success, result.Text, result.Model,
            result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Discovery ---

    public List<SessionProjectInfo> ListProjects()
        => _claude.ListProjects().Select(p => new SessionProjectInfo
        {
            Name = p.Name, Path = p.Path, HasClaudeMd = p.HasClaudeMd, HasIcon = p.HasIcon
        }).ToList();

    public List<ModelInfo> GetAvailableModels() =>
    [
        new() { Id = "haiku", Name = "Haiku", Fast = true },
        new() { Id = "sonnet", Name = "Sonnet", Fast = false },
        new() { Id = "opus", Name = "Opus", Fast = false },
    ];

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _claude.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _claude.StopAllAsync();

    // --- Mapping Helpers ---

    private static UnifiedSessionInfo ToUnified(ClaudeSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "claude-code",
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        Status = (Core.Sessions.SessionStatus)(int)s.Status,
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
        JobId = s.JobId,
        PermissionMode = s.PermissionMode,
        Source = s.Source,
        UserId = s.UserId,
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
        Timestamp = m.Timestamp,
    };

    // --- Config ---

    private static ClaudeConfig BuildConfig(ProviderConfig config)
    {
        var claudePath = ProviderHelpers.GetExtra(config, "ClaudePath", "");
        return new ClaudeConfig
        {
            ProjectsRoot = ProviderHelpers.GetExtra(config, "ProjectsRoot", @"T:\Projects"),
            ClaudePath = string.IsNullOrEmpty(claudePath) ? null : claudePath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
            DefaultOneshotModel = ProviderHelpers.GetExtra(config, "DefaultOneshotModel", "haiku"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new() { Type = "string", Required = false, Default = "sonnet", Enum = ["haiku", "sonnet", "opus"], Description = "Model to use" },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];
}
