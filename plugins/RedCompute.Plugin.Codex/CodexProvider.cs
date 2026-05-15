using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexProvider : IPluginProvider, ICustomEndpointProvider, IPluginEventSource, IJobExtendedProvider
{
    private readonly string _capabilitySlug;
    private readonly CodexSessionService _codex;
    private readonly IJobTracker _jobTracker;
    private readonly Action<string, Guid?> _log;

    public event Action<string, object>? PluginEvent;

    public string Name => "Codex";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "Codex";
    public string ProviderType => "Codex";
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

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

        _codex.SessionCreated += session => PluginEvent?.Invoke("codex.session.created", session);
        _codex.SessionUpdated += session => PluginEvent?.Invoke("codex.session.updated", session);
        _codex.SessionEnded += (id, reason) => PluginEvent?.Invoke("codex.session.ended", new { id, reason });
        _codex.StreamEvent += (sessionId, evt) => PluginEvent?.Invoke("codex.stream", new { sessionId, @event = evt });
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

    private static CodexConfig BuildConfig(ProviderConfig config)
    {
        var codexPath = ProviderHelpers.GetExtra(config, "CodexPath", "");
        return new CodexConfig
        {
            ProjectsRoot = ProviderHelpers.GetExtra(config, "ProjectsRoot", @"T:\Projects"),
            CodexPath = string.IsNullOrEmpty(codexPath) ? null : codexPath,
            MaxSessions = int.TryParse(ProviderHelpers.GetExtra(config, "MaxSessions", "5"), out var ms) ? ms : 5,
            Model = config.Model,
            DefaultExecModel = ProviderHelpers.GetExtra(config, "DefaultExecModel", "codex-mini-latest"),
            SandboxMode = ProviderHelpers.GetExtra(config, "SandboxMode", "workspace-write"),
        };
    }

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new() { Type = "string", Required = true, Description = "Prompt text for agent execution" },
        ["model"] = new() { Type = "string", Required = false, Default = "codex-mini-latest", Enum = ["codex-mini-latest", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini"], Description = "Model to use" },
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
        new() { Method = "POST", Path = "/codex/sessions/{id}/stop", Description = "Stop a running execution" },
        new() { Method = "POST", Path = "/codex/sessions/{id}/dismiss", Description = "Dismiss a session" },
        new() { Method = "DELETE", Path = "/codex/sessions/{id}", Description = "Force-kill an execution" }
    };
}
