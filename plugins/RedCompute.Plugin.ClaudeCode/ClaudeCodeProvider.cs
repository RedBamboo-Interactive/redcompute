using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeCodeProvider : IPluginProvider
{
    private readonly string _capabilitySlug;

    public string Name => "Claude Code";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Claude Code";
    public string ProviderType => "ClaudeCode";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    // The IStopAllAsync delegate allows the App layer to inject session shutdown
    private readonly Func<Task>? _stopAll;

    public ClaudeCodeProvider(ProviderConfig config, string capabilitySlug, Action<string> log, Func<Task>? stopAll = null)
    {
        _capabilitySlug = capabilitySlug;
        _stopAll = stopAll;
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync(CancellationToken ct = default) => _stopAll?.Invoke() ?? Task.CompletedTask;
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    // Claude session endpoints are complex (~15 routes) and stay in the App layer.
    // They are registered by the App's ClaudeSessionEndpoints, not here.
    // GetCustomEndpointManifests provides the discovery metadata.
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
