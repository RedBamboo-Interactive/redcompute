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
    private readonly CodexInteractiveService _interactive;
    private readonly CodexModelCatalog _models;
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
        SessionCapabilities.StatelessExecution
        | SessionCapabilities.Generate
        | SessionCapabilities.ProjectDiscovery
        | SessionCapabilities.PersistentSessions
        | SessionCapabilities.Resume
        | SessionCapabilities.Interrupt
        | SessionCapabilities.SendMessage
        | SessionCapabilities.ConfigUpdate;
    // Not claimed: PermissionMode (approvals are always auto-accepted, there is nothing to switch),
    // ImageAttachments (sending them through the CLI is not proven yet).

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
        _models = new CodexModelCatalog(codexConfig, log);
        _interactive = new CodexInteractiveService(codexConfig, store, _models, _codex, log);

        _codex.SessionCreated += session => PluginEvent?.Invoke("session.created", ToUnified(session));
        _codex.SessionUpdated += session => PluginEvent?.Invoke("session.updated", ToUnified(session));
        _codex.SessionEnded += (id, reason) => PluginEvent?.Invoke("session.ended", new { id, reason });
        _codex.StreamEvent += (sessionId, evt) => Broadcast(sessionId, evt);

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
        // Warm the model catalog so the synchronous GetAvailableModels() has something to serve.
        // A failure here is not fatal: the CLI may not be logged in yet, and /codex/models will
        // report that clearly on demand.
        await _models.PrimeAsync(ct);
        return true;
    }
    public Task StopAsync(CancellationToken ct = default) => _codex.StopAllAsync();
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    public async Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null, string? effort = null)
    {
        var info = await _interactive.StartSessionAsync(projectPath, callerInfo, model, userId, userName, userAvatarUrl, effort);
        return info != null ? ToUnified(info) : null;
    }

    public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        var info = await _interactive.ResumeSessionAsync(sessionId);
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

    public void DismissSession(string sessionId) => _codex.DismissSession(sessionId);

    public Task<bool> SendMessageAsync(string sessionId, string content, Core.Sessions.ImageAttachment[]? images = null, string? attachmentsJson = null, string? messageUid = null)
        => _interactive.SendMessageAsync(sessionId, content, attachmentsJson, messageUid);

    /// <summary>
    /// Free-text answer to a parked question. Codex keys answers by question id, so a bare string
    /// can only be applied when exactly one question is outstanding — which is the case that
    /// actually occurs. Structured multi-question replies go through SubmitQuestionAnswer.
    /// </summary>
    public bool SendAnswer(string sessionId, string answer)
        => _interactive.SubmitSingleAnswer(sessionId, answer);

    public Core.Sessions.QuestionAnswerResult SubmitQuestionAnswer(string sessionId, Core.Sessions.SessionQuestionAnswer answer)
        => _interactive.SubmitQuestionAnswer(sessionId, answer)
            ? Core.Sessions.QuestionAnswerResult.Answered
            : Core.Sessions.QuestionAnswerResult.RequestNotFound;

    // The service reports the real outcome now. Collapsing it to a bool here was half the reason
    // stop looked broken: "no such session", "no thread" and "nothing running" all arrived as
    // NotActive, and everything else — including a request the app-server rejected — as success.
    public Core.Sessions.InterruptResult InterruptSession(string sessionId)
        => _interactive.InterruptSession(sessionId);

    public async Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort, int? thinkingBudget = null)
    {
        // thinkingBudget has no Codex equivalent — reasoning depth is the `effort` ladder
        // (low → ultra), which is already carried by the effort parameter.
        var info = await _interactive.UpdateSessionConfigAsync(sessionId, model, effort);
        return info != null ? ToUnified(info) : null;
    }

    /// <summary>
    /// Not supported, and deliberately not faked. Sessions run with command and file approvals
    /// auto-accepted, matching Claude Code's --permission-mode bypassPermissions; there are no
    /// other modes to switch between, so returning true would be a lie the UI acts on.
    /// </summary>
    public bool SetPermissionMode(string sessionId, string mode) => false;

    public async Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null)
    {
        string prompt;
        try
        {
            prompt = BuildGeneratePrompt(system, messagesJson, maxTokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new SessionGenerateResult(false, null, null, model, 0, 0, null,
                $"Invalid completion messages: {ex.Message}");
        }

        var result = await _codex.ExecuteExecAsync(prompt, null, null, model, "read-only",
            timeout ?? 120, ct, effort: effort);
        return new SessionGenerateResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
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
        string? effort = null;
        IReadOnlyCollection<string>? networkDomains = null;
        if (providerParams != null)
        {
            if (providerParams.TryGetValue("sandbox", out var sb) && sb is string sbs)
                sandbox = sbs;
            if (providerParams.TryGetValue("container", out var c) && c is string cs)
                container = cs;
            if (providerParams.TryGetValue("effort", out var e) && e is string es)
                effort = es;
            if (providerParams.TryGetValue("networkDomains", out var nd))
                networkDomains = nd switch
                {
                    string[] values => values,
                    IEnumerable<string> values => values.ToArray(),
                    _ => null,
                };
        }

        var result = await _codex.ExecuteExecAsync(prompt, container, workingDir, model, sandbox,
            timeout, ct, streamKey, env, networkDomains, effort);
        return new SessionExecuteResult(result.Success, result.Text, result.StreamOutput,
            result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
    }

    // --- ISessionProvider: Discovery ---

    public List<SessionProjectInfo> ListProjects()
        => _codex.ListProjects().Select(p => new SessionProjectInfo
        {
            Name = p.Name, Path = p.Path, HasClaudeMd = p.HasClaudeMd,
        }).ToList();

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
        Effort = s.Effort,
        Source = s.Source,
        UserId = s.UserId,
        ContextWindow = s.ContextWindow,
        // The Codex thread id, which is what makes a session resumable — and resumable from the
        // Codex CLI and desktop app too, since every surface shares the same thread store.
        ProviderSessionId = s.ThreadId,
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
        MessageUid = e.MessageUid,
        RequestId = e.RequestId,
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
            DefaultExecModel = ProviderHelpers.GetExtra(config, "DefaultExecModel", "") is { Length: > 0 } dem ? dem : null,
            TitleModel = ProviderHelpers.GetExtra(config, "TitleModel", "") is { Length: > 0 } tm ? tm : null,
            SandboxMode = ProviderHelpers.GetExtra(config, "SandboxMode", "workspace-write"),
        };
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
        ["networkDomains"] = new() { Type = "array", Required = false, Description = "Exact network domains allowed for workspace-write executions" },
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
