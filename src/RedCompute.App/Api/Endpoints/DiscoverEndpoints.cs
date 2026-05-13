using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class DiscoverEndpoints
{
    private static ClaudeSessionService? _claudeService;

    public static void Map(WebApplication app, RedComputeConfig config, CapabilityRegistry registry, ClaudeSessionService? claudeService = null)
    {
        _claudeService = claudeService;
        MapRoutes(app, config, registry);
    }

    private static void MapRoutes(WebApplication app, RedComputeConfig config, CapabilityRegistry registry)
    {
        app.MapGet("/discover", async () =>
        {
            var capabilities = new List<CapabilityManifest>();

            foreach (var (slug, entry) in registry.Capabilities)
            {
                BackendStatus defaultStatus;
                try
                {
                    defaultStatus = entry.ActiveProvider != null
                        ? await entry.ActiveProvider.GetStatusAsync()
                        : BackendStatus.Stopped;
                }
                catch { defaultStatus = BackendStatus.Error; }

                var endpoints = GetEndpointsForCapability(slug, entry);

                var providerManifests = new List<ProviderManifest>();
                foreach (var (name, prov) in entry.Providers)
                {
                    BackendStatus pStatus;
                    try { pStatus = await prov.GetStatusAsync(); }
                    catch { pStatus = BackendStatus.Error; }
                    var provType = entry.Config.Providers.TryGetValue(name, out var pc) ? pc.Type : "Unknown";
                    providerManifests.Add(new ProviderManifest
                    {
                        Name = name,
                        Type = provType,
                        Status = pStatus.ToString()
                    });
                }

                capabilities.Add(new CapabilityManifest
                {
                    Slug = slug,
                    Type = slug,
                    DisplayName = entry.Definition.DisplayName,
                    Status = defaultStatus.ToString(),
                    Provider = entry.ActiveProvider?.Name,
                    DefaultProvider = entry.DefaultProviderName,
                    Providers = providerManifests.Count > 0 ? providerManifests : null,
                    Sleeping = entry.IsSleeping,
                    Disabled = entry.IsManuallyDisabled,
                    Endpoints = endpoints
                });
            }

            return Results.Ok(new
            {
                service = "RedCompute",
                version = "0.2.0",
                apiBase = $"http://localhost:{config.ApiPort}",
                capabilities,
                management = new
                {
                    endpoints = new object[]
                    {
                        new { method = "GET", path = "/status", description = "Service status with capabilities and uptime" },
                        new { method = "GET", path = "/discover", description = "Full API discovery manifest with all endpoints" },
                        new { method = "GET", path = "/openapi.json", description = "OpenAPI 3.1 specification" },
                        new { method = "GET", path = "/jobs", description = "List jobs with filtering and pagination. Query: capability (slug), status (Queued|Running|Completed|Failed|Cancelled), caller (exact callerInfo match), search (case-insensitive across name, provider, caller, capability), limit (default 50), offset. Returns { items: JobRecord[], total: int }." },
                        new { method = "GET", path = "/jobs/{id}", description = "Get a specific job by ID" },
                        new { method = "DELETE", path = "/jobs/{id}", description = "Cancel a job" },
                        new { method = "DELETE", path = "/jobs/cleanup", description = "Delete old jobs and logs (query: olderThanDays, default 30)" },
                        new { method = "GET", path = "/jobs/{id}/logs", description = "Get logs for a specific job" },
                        new { method = "GET", path = "/activity", description = "Job activity histogram (query: window in hours)" },
                        new { method = "GET", path = "/logs", description = "Query logs (tag, search, since, until, jobId, level, limit, offset)" },
                        new { method = "GET", path = "/logs/tags", description = "List log tags with counts" },
                        new { method = "GET", path = "/logs/summary", description = "Log summary statistics" },
                        new { method = "POST", path = "/control/start/{slug}", description = "Start the default provider's backend" },
                        new { method = "POST", path = "/control/stop/{slug}", description = "Stop the default provider's backend" },
                        new { method = "POST", path = "/control/start/{slug}/{provider}", description = "Start a specific provider's backend" },
                        new { method = "POST", path = "/control/stop/{slug}/{provider}", description = "Stop a specific provider's backend" },
                        new { method = "POST", path = "/control/sleep/{slug}", description = "Put a capability to sleep (stop after idle)" },
                        new { method = "POST", path = "/control/wake/{slug}", description = "Wake a sleeping capability" },
                        new { method = "GET", path = "/settings", description = "Current service configuration (API keys masked)" },
                        new { method = "PUT", path = "/settings/general", description = "Update general settings: apiPort, logLevel, autoStartWithWindows, tunnel*" },
                        new { method = "PUT", path = "/settings/capability/{slug}", description = "Update capability config: enabled, activeProvider" },
                        new { method = "PUT", path = "/settings/capability/{slug}/provider/{name}", description = "Update provider settings: model, ports, paths, API keys, extras" },
                        new { method = "GET", path = "/tunnel/status", description = "Cloudflare tunnel status, hostname, and error" },
                        new { method = "POST", path = "/tunnel/start", description = "Start the Cloudflare tunnel" },
                        new { method = "POST", path = "/tunnel/stop", description = "Stop the Cloudflare tunnel" },
                        new { method = "GET", path = "/ws/schema", description = "WebSocket event schema — discover event types and data shapes" },
                        new { method = "GET", path = "/hardware", description = "Live hardware metrics: GPU utilization, VRAM, power, temperature, CPU, RAM, per-process GPU memory. Polled every 2s via WebSocket hardware.snapshot events." }
                    },
                    websocket = new
                    {
                        url = $"ws://localhost:{config.ApiPort}/ws",
                        description = "Real-time event stream. Events: job.created, job.updated, log.entry, capability.status, hardware.snapshot, claude.session.created, claude.session.updated, claude.session.ended, claude.stream",
                        schemaEndpoint = "/ws/schema"
                    },
                    dashboard = new
                    {
                        url = $"http://localhost:{config.ApiPort}/",
                        description = "Web dashboard UI (served from the same port as the API)"
                    }
                }
            });
        });
    }

    private static List<EndpointManifest> GetEndpointsForCapability(string slug, CapabilityEntry entry)
    {
        var result = new List<EndpointManifest>();

        // Build generate endpoint from active provider's schema
        var activeProvider = entry.ActiveProvider;
        if (activeProvider is IPluginProvider plugin)
        {
            var generateParams = new Dictionary<string, ParameterSchema>(plugin.InputParameters);

            // Add provider parameter if multiple providers
            if (entry.Providers.Count > 1)
            {
                generateParams["provider"] = new ParameterSchema
                {
                    Type = "string",
                    Required = false,
                    Default = entry.DefaultProviderName,
                    Enum = entry.Providers.Keys.ToList(),
                    Description = "Provider to use for this request"
                };
            }

            result.Add(new EndpointManifest
            {
                Method = "POST",
                Path = $"/{slug}/generate",
                Description = $"Generate via {plugin.DisplayName}",
                Parameters = generateParams.Count > 0 ? generateParams : null,
                Returns = plugin.OutputSchema
            });

            // Add standard job endpoints
            if (plugin.SupportsProgress)
            {
                result.Add(new EndpointManifest
                {
                    Method = "GET",
                    Path = $"/{slug}/jobs/{{id}}/progress",
                    Description = "Get real-time progress of a job",
                    Returns = new ReturnSchema { ContentType = "application/json", Streaming = false }
                });
            }

            result.Add(new EndpointManifest
            {
                Method = "GET",
                Path = $"/{slug}/jobs/{{id}}/output",
                Description = "Download the output for a completed job",
                Returns = plugin.OutputSchema
            });

            // Add provider's custom endpoint manifests
            var customManifests = plugin.GetCustomEndpointManifests();
            result.AddRange(customManifests);
        }

        return result;
    }
}
