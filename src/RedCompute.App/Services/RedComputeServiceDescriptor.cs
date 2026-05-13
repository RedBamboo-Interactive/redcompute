using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;
using RedCompute.App.Services.Claude;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public class RedComputeServiceDescriptor : IServiceDescriptor
{
    private readonly RedComputeConfig _config;
    private readonly CapabilityRegistry _registry;
    private readonly ClaudeSessionService _claude;
    private readonly LogService? _logService;

    public RedComputeServiceDescriptor(RedComputeConfig config, CapabilityRegistry registry, ClaudeSessionService claude, LogService? logService = null)
    {
        _config = config;
        _registry = registry;
        _claude = claude;
        _logService = logService;
    }

    public string ServiceName => "RedCompute";
    public string Version => "0.2.0";
    public string Description => "AI-native inference abstraction layer — TTS, STT, image gen, music gen, AI sessions";
    public string ApiBase => $"http://localhost:{_config.ApiPort}";

    public async Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync()
    {
        var caps = new List<CapabilityDescriptor>();
        foreach (var (slug, entry) in _registry.Capabilities)
        {
            var status = entry.ActiveProvider != null
                ? (await entry.ActiveProvider.GetStatusAsync()).ToString()
                : "Stopped";
            caps.Add(new CapabilityDescriptor(slug, entry.Definition.DisplayName, status));
        }
        if (_logService is not null)
            caps.Add(LogEndpoints.GetLogCapabilityDescriptor(_logService));
        return caps;
    }

    public IReadOnlyList<EndpointDescriptor> GetAppEndpoints()
    {
        return new List<EndpointDescriptor>
        {
            new("GET", "/status", "Service status with uptime and capability states"),
            new("GET", "/discover", "Detailed service manifest with capability parameters (legacy format)"),
            new("GET", "/openapi.json", "OpenAPI 3.1 spec (legacy detailed version)"),
            new("GET", "/jobs", "List jobs with optional filters"),
            new("GET", "/jobs/{id}", "Get job details"),
            new("POST", "/jobs/{id}/rerun", "Rerun a completed job"),
            new("GET", "/hardware", "GPU/CPU hardware metrics"),
            new("GET", "/settings", "Current settings including tunnel config"),
            new("GET", "/claude/sessions", "List Claude Code sessions"),
        };
    }

    public Task<object?> GetHealthExtrasAsync()
    {
        return Task.FromResult<object?>(new
        {
            capabilities = _registry.Capabilities.Count,
        });
    }
}
