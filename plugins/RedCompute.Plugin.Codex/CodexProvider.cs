using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider, ISessionProvider
{
    private readonly string _capabilitySlug;
    private readonly CodexSessionService _codex;
    private readonly IJobTracker _jobTracker;
    private readonly Action<string, Guid?> _log;

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
    public SessionCapabilities Capabilities =>
        SessionCapabilities.StatelessExecution | SessionCapabilities.ProjectDiscovery;

    public string? LastStartError => null;

    public CodexProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new CodexDbContext()) { db.Initialize(); }
        var store = new CodexSessionStore();
        var codexConfig = BuildConfig(config);
        _codex = new CodexSessionService(codexConfig, jobTracker, store, log);

        _codex.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _codex.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _codex.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _codex.StreamEvent += (sessionId, evt) =>
        {
            PluginEvent?.Invoke("session.stream", new { sessionId, @event = evt });
            SessionStreamEvent?.Invoke(sessionId, ToUnifiedEvent(evt));
        };
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync(CancellationToken ct = default) => _codex.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void MapCustomEndpoints(WebApplication app)
    {
        CodexSessionEndpoints.Map(app, _codex, _jobTracker, _log);
    }

    public void CancelJob(string jobKey) => _codex.CancelExecution(jobKey);

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
    {
        var statuses = _codex.GetSessionStatusesByJobIds(jobIds);
        return statuses.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // --- ISessionProvider: Not supported (stateless only) ---

    public Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null, string? effort = null)
        => throw new NotSupportedException("Codex does not support persistent sessions");

    public Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
        => throw new NotSupportedException("Codex does not support session resume");

    public Task StopSessionAsync(string sessionId)
        => throw new NotSupportedException("Codex does not support persistent sessions");

    public Task ForceKillAsync(string sessionId)
    {
        _codex.CancelExecution(sessionId);
        return Task.CompletedTask;
    }

    public void DismissSession(string sessionId) => _codex.DismissSession(sessionId);

    public Task<bool> SendMessageAsync(string sessionId, string content, Core.Sessions.ImageAttachment[]? images = null)
        => throw new NotSupportedException("Codex does not support interactive messaging");

    public bool SendAnswer(string sessionId, string answer)
        => throw new NotSupportedException("Codex does not support interactive messaging");

    public Core.Sessions.InterruptResult InterruptSession(string sessionId)
        => throw new NotSupportedException("Codex does not support session interrupts");

    public Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort)
        => throw new NotSupportedException("Codex does not support config updates");

    public bool SetPermissionMode(string sessionId, string mode)
        => throw new NotSupportedException("Codex does not support permission modes");

    public Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null)
        => throw new NotSupportedException("Codex does not support LLM completion");

    // --- ISessionProvider: Querying ---

    public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
        => _codex.GetSessions(limit, includeDismissed).Select(ToUnified).ToList();

    public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
    {
        var (info, history) = _codex.GetSession(sessionId);
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
            if (Enum.TryParse<Core.Sessions.SessionStatus>(statusStr, out var status))
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
        if (providerParams != null)
        {
            if (providerParams.TryGetValue("sandbox", out var sb) && sb is string sbs)
                sandbox = sbs;
            if (providerParams.TryGetValue("container", out var c) && c is string cs)
                container = cs;
        }

        var result = await _codex.ExecuteExecAsync(prompt, container, workingDir, model, sandbox, timeout, ct, streamKey, env);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Discovery ---

    public List<SessionProjectInfo> ListProjects()
        => _codex.ListProjects().Select(p => new SessionProjectInfo
        {
            Name = p.Name, Path = p.Path, HasClaudeMd = p.HasClaudeMd,
        }).ToList();

    public List<ModelInfo> GetAvailableModels() =>
        CodexSessionEndpoints.ModelCatalog
            .Select(m => new ModelInfo { Id = m.Id, Name = m.Name, Fast = m.Fast })
            .ToList();

    // --- ISessionProvider: Process Management ---

    void ISessionProvider.CancelExecution(string key) => _codex.CancelExecution(key);
    Task ISessionProvider.StopAllAsync() => _codex.StopAllAsync();

    // --- Mapping Helpers ---

    private static UnifiedSessionInfo ToUnified(CodexSessionInfo s) => new()
    {
        Id = s.Id,
        Provider = "codex",
        ProjectName = s.ProjectName,
        ProjectPath = s.ProjectPath,
        Status = Enum.TryParse<Core.Sessions.SessionStatus>(s.Status, out var st)
            ? st : Core.Sessions.SessionStatus.Stopped,
        StartedAt = s.StartedAt,
        Model = s.Model,
        Title = s.Title,
        MessageCount = s.MessageCount,
        CostUsd = s.CostUsd,
        InputTokens = s.InputTokens,
        OutputTokens = s.OutputTokens,
        CachedInputTokens = s.CachedInputTokens,
        JobId = s.JobId,
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
        Timestamp = m.Timestamp,
    };

    // --- Config ---

    private static CodexConfig BuildConfig(ProviderConfig config)
    {
        var codexPath = ProviderHelpers.GetExtra(config, "CodexPath", "");
        return new CodexConfig
        {
            ProjectsRoot = ProviderHelpers.GetExtra(config, "ProjectsRoot", @"T:\Projects"),
            CodexPath = string.IsNullOrEmpty(codexPath) ? null : codexPath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "99"), out var ms) ? ms : 99,
            Model = config.Model,
            DefaultExecModel = ProviderHelpers.GetExtra(config, "DefaultExecModel", "codex-mini-latest"),
            SandboxMode = ProviderHelpers.GetExtra(config, "SandboxMode", "workspace-write"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new() { Type = "string", Required = false, Default = "codex-mini-latest", Enum = CodexSessionEndpoints.ModelCatalog.Select(m => m.Id).ToList(), Description = "Model to use" },
        ["workingDir"] = new() { Type = "string", Required = false, Description = "Working directory for the agent" },
        ["sandbox"] = new() { Type = "string", Required = false, Default = "workspace-write", Enum = ["read-only", "workspace-write", "danger-full-access"], Description = "Sandbox mode" },
        ["timeout"] = new() { Type = "integer", Required = false, Default = 600, Min = 1, Max = 1800, Description = "Timeout in seconds" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => new List<EndpointManifest>
    {
        new() { Method = "POST", Path = "/codex/execute", Description = "Execute a prompt with Codex agent", Parameters = new() { ["prompt"] = new() { Type = "string", Required = true }, ["model"] = new() { Type = "string", Required = false, Default = "codex-mini-latest" } }, Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/codex/models", Description = "List available Codex models", Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/codex/projects", Description = "List available projects" },
        new() { Method = "GET", Path = "/codex/sessions", Description = "List recent Codex sessions" },
        new() { Method = "GET", Path = "/codex/sessions/{id}", Description = "Get session details and message history" },
        new() { Method = "GET", Path = "/codex/sessions/by-job/{jobId}", Description = "Get the session associated with a job ID" },
        new() { Method = "POST", Path = "/codex/sessions/{id}/stop", Description = "Stop a running execution" },
        new() { Method = "POST", Path = "/codex/sessions/{id}/dismiss", Description = "Dismiss a session" },
        new() { Method = "DELETE", Path = "/codex/sessions/{id}", Description = "Force-kill an execution" }
    };
}
