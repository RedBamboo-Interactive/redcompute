using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeCodeProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider
{
    private readonly string _capabilitySlug;
    private readonly ClaudeSessionService _claude;
    private readonly IJobTracker _jobTracker;
    private readonly Action<string, Guid?> _log;

    public event Action<string, object>? PluginEvent;

    public string Name => "Claude Code";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Claude Code";
    public string ProviderType => "ClaudeCode";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    public ClaudeCodeProvider(ProviderConfig config, string capabilitySlug,
        IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _capabilitySlug = capabilitySlug;
        _log = log;
        _jobTracker = jobTracker;

        using (var db = new ClaudeDbContext()) { db.Initialize(); }
        var store = new ClaudeSessionStore();
        var claudeConfig = BuildConfig(config);
        _claude = new ClaudeSessionService(claudeConfig, jobTracker, store, log);

        _claude.SessionCreated += session => PluginEvent?.Invoke("claude.session.created", session);
        _claude.SessionUpdated += session => PluginEvent?.Invoke("claude.session.updated", session);
        _claude.SessionEnded += (id, reason) => PluginEvent?.Invoke("claude.session.ended", new { id, reason });
        _claude.StreamEvent += (sessionId, evt) => PluginEvent?.Invoke("claude.stream", new { sessionId, @event = evt });
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

    private static ClaudeConfig BuildConfig(ProviderConfig config)
    {
        var claudePath = ProviderHelpers.GetExtra(config, "ClaudePath", "");
        return new ClaudeConfig
        {
            ProjectsRoot = ProviderHelpers.GetExtra(config, "ProjectsRoot", @"T:\Projects"),
            ClaudePath = string.IsNullOrEmpty(claudePath) ? null : claudePath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "5"), out var ms) ? ms : 5,
            Model = config.Model,
            DefaultOneshotModel = ProviderHelpers.GetExtra(config, "DefaultOneshotModel", "haiku"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["mode"] = new() { Type = "string", Required = false, Default = "session", Enum = ["session", "oneshot"], Description = "'session' starts a persistent coding session; 'oneshot' runs a single stateless prompt" },
        ["project"] = new() { Type = "string", Required = false, Description = "(session mode) Project name to start the session in" },
        ["prompt"] = new() { Type = "string", Required = false, Description = "(session mode) Initial message / (oneshot) messages required" },
        ["model"] = new() { Type = "string", Required = false, Default = "haiku", Description = "(oneshot mode) Model alias" },
        ["system"] = new() { Type = "string", Required = false, Description = "(oneshot mode) System prompt" },
        ["messages"] = new() { Type = "array", Required = false, Description = "(oneshot mode) Array of {role, content} message objects" },
        ["maxTokens"] = new() { Type = "integer", Required = false, Default = 1024, Min = 1, Max = 8192, Description = "(oneshot mode) Maximum tokens to generate" }
    };

    public ReturnSchema OutputSchema => new() { ContentType = "application/json", Streaming = true };

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => new List<EndpointManifest>
    {
        new() { Method = "POST", Path = "/ai-session/execute", Description = "Execute a prompt with full agent capabilities", Parameters = new() { ["prompt"] = new() { Type = "string", Required = true, Description = "Full prompt text" }, ["model"] = new() { Type = "string", Required = false, Default = "sonnet", Enum = ["haiku", "sonnet", "opus"] }, ["maxTurns"] = new() { Type = "integer", Required = false, Default = 1 } }, Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/ai-session/models", Description = "List available LLM models", Returns = new() { ContentType = "application/json", Streaming = false } },
        new() { Method = "GET", Path = "/claude/projects", Description = "List available projects" },
        new() { Method = "POST", Path = "/claude/sessions", Description = "Start a new session by project path", Parameters = new() { ["projectPath"] = new() { Type = "string", Required = true } } },
        new() { Method = "GET", Path = "/claude/sessions", Description = "List all active and recent AI sessions" },
        new() { Method = "GET", Path = "/claude/sessions/{id}", Description = "Get session details and message history" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/message", Description = "Send a message to an active session" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/answer", Description = "Answer a pending question" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/interrupt", Description = "Interrupt the current operation" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/stop", Description = "Stop a session gracefully" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/resume", Description = "Resume a stopped session" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/dismiss", Description = "Mark a session as dismissed" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/config", Description = "Update session model and effort" },
        new() { Method = "POST", Path = "/claude/sessions/{id}/permission-mode", Description = "Set permission mode" },
        new() { Method = "DELETE", Path = "/claude/sessions/{id}", Description = "Force-kill a session" }
    };
}
