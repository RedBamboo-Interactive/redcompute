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
    public override string? IconClass => "fa-solid fa-microchip";
    public override string? IconColor => "#E55B5B";

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
        caps.Add(RedBamboo.AppHost.Telemetry.TelemetryEndpoints.GetTelemetryCapabilityDescriptor());
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
        var plugins = entry.Providers.Values.OfType<IPluginProvider>().ToList();
        var schemaPlugin = entry.ActiveProvider as IPluginProvider ?? plugins.FirstOrDefault();

        // The unified /ai-session/* surface is registered in the EndpointRegistry and
        // surfaces via app_endpoints; per-capability entries only carry the generic job
        // endpoints plus provider-specific custom endpoints.
        if (slug != "ai-session")
        {
            var generateParams = schemaPlugin?.InputParameters
                .Select(kv => new ParameterDescriptor(
                    kv.Key, kv.Value.Type, kv.Value.Required,
                    kv.Value.Description, kv.Value.Default, kv.Value.Enum))
                .ToList() ?? [];

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
                $"Generate via {schemaPlugin?.DisplayName ?? entry.Definition.DisplayName}",
                generateParams.Count > 0 ? generateParams : null));

            endpoints.Add(new EndpointDescriptor(
                "GET", $"/{slug}/jobs/{{id}}/output",
                "Download the output for a completed job"));

            if (plugins.Any(p => p.SupportsProgress))
            {
                endpoints.Add(new EndpointDescriptor(
                    "GET", $"/{slug}/jobs/{{id}}/progress",
                    "Get real-time progress of a job"));
            }
        }

        // Custom endpoints from ALL registered providers, not just the active one
        var seen = new HashSet<string>();
        foreach (var plugin in plugins)
        {
            foreach (var custom in plugin.GetCustomEndpointManifests())
            {
                if (!seen.Add($"{custom.Method} {custom.Path}")) continue;

                var customParams = custom.Parameters?
                    .Select(kv => new ParameterDescriptor(
                        kv.Key, kv.Value.Type, kv.Value.Required,
                        kv.Value.Description, kv.Value.Default, kv.Value.Enum))
                    .ToList();

                endpoints.Add(new EndpointDescriptor(
                    custom.Method, custom.Path, custom.Description,
                    customParams is { Count: > 0 } ? customParams : null));
            }
        }

        return endpoints;
    }
}
