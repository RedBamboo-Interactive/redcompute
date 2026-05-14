using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;
using RedCompute.App.Services.Claude;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;

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

    public IReadOnlyList<EndpointDescriptor> GetAppEndpoints()
    {
        return new List<EndpointDescriptor>
        {
            new("GET", "/status", "Service status with uptime and capability states"),
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

    private static List<EndpointDescriptor> BuildEndpoints(string slug, CapabilityEntry entry)
    {
        var endpoints = new List<EndpointDescriptor>();
        if (entry.ActiveProvider is not IPluginProvider plugin) return endpoints;

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
}
