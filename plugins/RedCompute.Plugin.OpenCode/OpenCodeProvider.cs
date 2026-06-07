using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider, ISessionProvider
{
    private readonly string _capabilitySlug;
    private readonly OpenCodeSessionService _opencode;
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
    public SessionCapabilities Capabilities =>
        SessionCapabilities.StatelessExecution |
        SessionCapabilities.PersistentSessions |
        SessionCapabilities.Resume |
        SessionCapabilities.Interrupt |
        SessionCapabilities.SendMessage |
        SessionCapabilities.ConfigUpdate |
        SessionCapabilities.ImageAttachments |
        SessionCapabilities.ProjectDiscovery;

    public string? LastStartError => _opencode.LastStartError;

    public OpenCodeProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new OpenCodeDbContext()) { db.Initialize(); }
        var store = new OpenCodeSessionStore();
        var openCodeConfig = BuildConfig(config);
        _opencode = new OpenCodeSessionService(openCodeConfig, jobTracker, store, log);

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
    public Task StopAsync(CancellationToken ct = default) => _opencode.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void MapCustomEndpoints(WebApplication app)
    {
        OpenCodeSessionEndpoints.Map(app, _opencode, _jobTracker, _log);
    }

    public void CancelJob(string jobKey) => _opencode.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _opencode.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // --- ISessionProvider: Session Lifecycle ---

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null)
    {
        var info = await _opencode.StartSession(projectPath, callerInfo, model, userId, userName, userAvatarUrl);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = await _opencode.ResumeSession(sessionId);
        return info != null ? ToUnified(info) : null;
    }

    public Task StopSessionAsync(string sessionId) => _opencode.StopSession(sessionId);

    public Task ForceKillAsync(string sessionId)
    {
        _opencode.ForceKill(sessionId);
        return Task.CompletedTask;
    }

    public void DismissSession(string sessionId) => _opencode.DismissSession(sessionId);

    // --- ISessionProvider: Messaging ---

    public Task<bool> SendMessageAsync(string sessionId, string content, ImageAttachment[]? images = null)
        => _opencode.SendMessage(sessionId, content, images);

    public bool SendAnswer(string sessionId, string answer)
        => _opencode.SendAnswer(sessionId, answer);

    // --- ISessionProvider: Interrupt ---

    public InterruptResult InterruptSession(string sessionId)
        => _opencode.InterruptSession(sessionId);

    // --- ISessionProvider: Configuration ---

    public async Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort)
    {
        var info = await _opencode.UpdateSessionConfig(sessionId, model, effort);
        return info != null ? ToUnified(info) : null;
    }

    public bool SetPermissionMode(string sessionId, string mode)
        => false;

    // --- ISessionProvider: Generate (not supported) ---

    public Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null)
        => throw new NotSupportedException("OpenCode does not support direct LLM completion");

    // --- ISessionProvider: Querying ---

    public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
        => _opencode.GetSessions(limit, includeDismissed).Select(ToUnified).ToList();

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
    {
        var (info, history) = _opencode.GetSession(sessionId);
        return (info != null ? ToUnified(info) : null, history.Select(ToUnifiedMessage).ToList());
    }

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

        var result = await _opencode.ExecuteAsync(prompt, container, workingDir, model, timeout, ct, streamKey, env);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Discovery ---

    public List<SessionProjectInfo> ListProjects()
        => _opencode.ListProjects().Select(p => new SessionProjectInfo
        {
            Name = p.Name, Path = p.Path, HasClaudeMd = p.HasClaudeMd,
        }).ToList();

    public List<ModelInfo> GetAvailableModels() =>
    [
        new() { Id = "anthropic/claude-sonnet-4-20250514", Name = "Claude Sonnet 4", Fast = false },
        new() { Id = "anthropic/claude-opus-4-20250514", Name = "Claude Opus 4", Fast = false },
        new() { Id = "openai/gpt-4o", Name = "GPT-4o", Fast = true },
        new() { Id = "google/gemini-2.5-pro", Name = "Gemini 2.5 Pro", Fast = false },
    ];

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _opencode.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _opencode.StopAllAsync();

    // --- Mapping Helpers ---

    private static UnifiedSessionInfo ToUnified(OpenCodeSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "opencode",
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        Status = Enum.TryParse<SessionStatus>(s.Status, out var st)
            ? st : SessionStatus.Stopped,
        StartedAt = s.StartedAt,
        Model = s.Model,
        ProviderSessionId = s.OpenCodeSessionId,
        Title = s.Title,
        MessageCount = s.MessageCount,
        CostUsd = s.CostUsd,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        Effort = s.Effort,
        JobId = s.JobId,
        Source = s.Source,
        UserId = s.UserId,
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
        Timestamp = m.Timestamp,
    };

    // --- Config ---

    private static OpenCodeConfig BuildConfig(ProviderConfig config)
    {
        var openCodePath = ProviderHelpers.GetExtra(config, "OpenCodePath", "");
        return new OpenCodeConfig
        {
            ProjectsRoot = ProviderHelpers.GetExtra(config, "ProjectsRoot", @"T:\Projects"),
            OpenCodePath = string.IsNullOrEmpty(openCodePath) ? null : openCodePath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
            DefaultModel = ProviderHelpers.GetExtra(config, "DefaultModel", "sonnet"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new() { Type = "string", Required = false, Default = "sonnet", Description = "Model to use (supports 75+ providers)" },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];
}
