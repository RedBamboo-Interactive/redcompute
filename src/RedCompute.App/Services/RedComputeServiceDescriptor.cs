using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

public class RedComputeServiceDescriptor : RegistryServiceDescriptor
{
    private readonly RedComputeConfig _config;
    private readonly CapabilityRegistry _registry;
    private readonly LogService? _logService;

    public RedComputeServiceDescriptor(RedComputeConfig config, CapabilityRegistry registry, LogService? logService, EndpointRegistry endpointRegistry)
        : base(endpointRegistry)
    {
        _config = config;
        _registry = registry;
        _logService = logService;
    }

    public override string ServiceName => "RedCompute";
    public override string Version => "0.2.0";
    public override string Description => "AI-native inference abstraction layer — TTS, STT, image gen, music gen, AI sessions";
    public override string ApiBase => $"http://localhost:{_config.ApiPort}";

    public override async Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync()
    {
        var caps = new List<CapabilityDescriptor>();
        foreach (var (slug, entry) in _registry.Capabilities)
        {
            string status;
            try
            {
                status = entry.ActiveProvider != null
                    ? (await entry.ActiveProvider.GetStatusAsync()).ToString()
                    : "Stopped";
            }
            catch { status = "Error"; }

            var endpoints = BuildEndpoints(slug, entry);

            caps.Add(new CapabilityDescriptor(
                slug,
                entry.Definition.DisplayName,
                status,
                Description: entry.Definition.Description,
                Endpoints: endpoints.Count > 0 ? endpoints : null));
        }
        if (_logService is not null)
            caps.Add(LogEndpoints.GetLogCapabilityDescriptor(_logService));
        return caps;
    }

    public override Task<object?> GetHealthExtrasAsync()
    {
        return Task.FromResult<object?>(new
        {
            capabilities = _registry.Capabilities.Count,
        });
    }

    private List<EndpointDescriptor> BuildEndpoints(string slug, CapabilityEntry entry)
    {
        var endpoints = new List<EndpointDescriptor>();
        if (entry.ActiveProvider is not IPluginProvider plugin) return endpoints;

        if (slug == "ai-session")
        {
            BuildUnifiedSessionEndpoints(endpoints, entry);
        }
        else
        {
            var generateParams = plugin.InputParameters
                .Select(kv => new ParameterDescriptor(
                    kv.Key, kv.Value.Type, kv.Value.Required,
                    kv.Value.Description, kv.Value.Default, kv.Value.Enum))
                .ToList();

            if (entry.Providers.Count > 1)
            {
                generateParams.Add(new ParameterDescriptor(
                    "provider", "string", false,
                    "Provider to use for this request",
                    entry.DefaultProviderName,
                    entry.Providers.Keys.ToList()));
            }

            endpoints.Add(new EndpointDescriptor(
                "POST", $"/{slug}/generate",
                $"Generate via {plugin.DisplayName}",
                generateParams.Count > 0 ? generateParams : null));

            endpoints.Add(new EndpointDescriptor(
                "GET", $"/{slug}/jobs/{{id}}/output",
                "Download the output for a completed job"));

            if (plugin.SupportsProgress)
            {
                endpoints.Add(new EndpointDescriptor(
                    "GET", $"/{slug}/jobs/{{id}}/progress",
                    "Get real-time progress of a job"));
            }
        }

        foreach (var custom in plugin.GetCustomEndpointManifests())
        {
            var customParams = custom.Parameters?
                .Select(kv => new ParameterDescriptor(
                    kv.Key, kv.Value.Type, kv.Value.Required,
                    kv.Value.Description, kv.Value.Default, kv.Value.Enum))
                .ToList();

            endpoints.Add(new EndpointDescriptor(
                custom.Method, custom.Path, custom.Description,
                customParams is { Count: > 0 } ? customParams : null));
        }

        return endpoints;
    }

    private void BuildUnifiedSessionEndpoints(List<EndpointDescriptor> endpoints, CapabilityEntry entry)
    {
        var providerNames = entry.Providers.Keys.ToList();
        var providerParam = new ParameterDescriptor("provider", "string", false,
            "Provider to use (e.g. claude-code, codex, opencode). Defaults to active provider.",
            entry.DefaultProviderName, providerNames);

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/providers",
            "List all registered session providers with their capabilities and models"));

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/models",
            "List available models across all providers",
            [new ParameterDescriptor("provider", "string", false, "Filter by provider")]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/generate",
            "Start a session by project name, or run a oneshot LLM completion",
            [
                new("mode", "string", false, "'session' (default) starts a persistent session; 'oneshot' runs a stateless LLM completion", "session", ["session", "oneshot"]),
                new("project", "string", false, "(session mode) Project name"),
                new("prompt", "string", false, "(session mode) Initial message to send"),
                new("messages", "array", false, "(oneshot mode) Array of {role, content} message objects"),
                new("model", "string", false, "Model to use"),
                new("system", "string", false, "(oneshot mode) System prompt"),
                new("maxTokens", "integer", false, "(oneshot mode) Maximum tokens to generate", 1024),
                providerParam,
            ]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/execute",
            "Execute a prompt via any session provider",
            [
                new("prompt", "string", true, "Prompt text"),
                new("model", "string", false, "Model to use"),
                new("workingDir", "string", false, "Working directory"),
                new("timeout", "integer", false, "Timeout in seconds (1-1800)", 600),
                providerParam,
            ]));

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/projects",
            "List available projects across all providers",
            [new ParameterDescriptor("provider", "string", false, "Filter by provider")]));

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/sessions",
            "List sessions across all providers",
            [
                new("limit", "integer", false, "Max sessions to return", 20),
                new("provider", "string", false, "Filter by provider"),
            ]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions",
            "Start a new persistent session",
            [
                new("projectPath", "string", true, "Path to the project directory"),
                providerParam,
            ]));

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/sessions/{id}",
            "Get session details and message history"));

        endpoints.Add(new EndpointDescriptor("GET", "/ai-session/sessions/by-job/{jobId}",
            "Get session by job ID"));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/message",
            "Send a message to an active session",
            [new ParameterDescriptor("content", "string", true, "Message content")]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/answer",
            "Answer a pending question",
            [new ParameterDescriptor("answer", "string", true, "Answer text")]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/interrupt",
            "Interrupt the current operation"));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/resume",
            "Resume a stopped session"));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/stop",
            "Stop a session gracefully"));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/dismiss",
            "Mark a session as dismissed"));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/config",
            "Update session model and effort",
            [
                new("model", "string", false, "Model to switch to"),
                new("effort", "string", false, "Reasoning effort level"),
            ]));

        endpoints.Add(new EndpointDescriptor("POST", "/ai-session/sessions/{id}/permission-mode",
            "Set permission mode",
            [new ParameterDescriptor("mode", "string", true, "Permission mode")]));

        endpoints.Add(new EndpointDescriptor("DELETE", "/ai-session/sessions/{id}",
            "Force-kill a session"));
    }
}
