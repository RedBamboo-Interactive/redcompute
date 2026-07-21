using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api.Endpoints;

// Tunnel and autostart settings are gone: RedCompute runs headless under the Leaf
// kernel, which owns both. What remains is what the compute plane actually owns —
// port, log level, and capability/provider configuration.
public static class SettingsEndpoints
{
    private static CapabilityRegistry _registry = null!;
    private static ProviderConfigService _providerConfig = null!;

    public static void Map(EndpointRegistry endpoints, ConfigManager configManager, CapabilityRegistry registry, ProviderConfigService providerConfig)
    {
        _registry = registry;
        _providerConfig = providerConfig;

        endpoints.MapGet("/settings", "Current settings including capability/provider config", () =>
        {
            var config = configManager.Config;
            var sanitized = new
            {
                config.ApiPort,
                config.LogLevel,
                configPath = GetConfigPath(),
                capabilities = config.Capabilities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        kvp.Value.ActiveProvider,
                        providers = kvp.Value.Providers.ToDictionary(
                            p => p.Key,
                            p => SanitizeProvider(p.Value)
                        )
                    }
                )
            };
            return Results.Ok(sanitized);
        });

        endpoints.MapPut("/settings/general", "Update general settings", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<GeneralSettingsUpdate>();
            if (body == null)
                return Results.BadRequest(new { error = "invalid_body", message = "Expected JSON body" });

            var config = configManager.Config;
            if (body.ApiPort.HasValue) config.ApiPort = body.ApiPort.Value;
            if (body.LogLevel != null) config.LogLevel = body.LogLevel;

            configManager.Save();
            return Results.Ok(new { message = "Settings updated", config.ApiPort, config.LogLevel });
        })
            .WithParam("apiPort", "integer", description: "HTTP port the service listens on (applies after restart)", location: ParamLocation.Body)
            .WithParam("logLevel", "string", description: "Log verbosity", location: ParamLocation.Body);

        endpoints.MapPut("/settings/capability/{slug}", "Update capability settings", async (HttpContext ctx, string slug) =>
        {
            var config = configManager.Config;
            if (!config.Capabilities.ContainsKey(slug))
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found in config" });

            var body = await ctx.Request.ReadFromJsonAsync<CapabilitySettingsUpdate>();
            if (body == null)
                return Results.BadRequest(new { error = "invalid_body", message = "Expected JSON body" });

            var cap = config.Capabilities[slug];
            if (body.ActiveProvider != null)
            {
                cap.ActiveProvider = body.ActiveProvider;
                var entry = _registry.Get(slug);
                if (entry != null)
                    entry.DefaultProviderName = body.ActiveProvider;
            }

            // ActiveProvider has no entity representation, so config.json is its only
            // durable store -- without this Save() the selection dies with the process.
            configManager.Save();
            _ = _providerConfig.RefreshAsync();
            return Results.Ok(new { message = $"Capability '{slug}' settings updated", slug, cap.ActiveProvider });
        })
            .WithParam("activeProvider", "string", description: "Provider name to make the capability's default", location: ParamLocation.Body);

        endpoints.MapGet("/settings/capability/{slug}/provider/{providerName}", "Current settings for one provider of a capability (secrets redacted). Accepts the config provider name or a provider entity slug", (string slug, string providerName) =>
        {
            var config = configManager.Config;
            if (!config.Capabilities.TryGetValue(slug, out var cap))
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });

            var resolved = _providerConfig.ResolveProviderName(cap, providerName);
            if (resolved == null || !cap.Providers.TryGetValue(resolved, out var provider))
                return Results.NotFound(new { error = "not_found", message = $"Provider '{providerName}' not found in capability '{slug}'" });

            return Results.Ok(new { slug, providerName = resolved, values = SanitizeProvider(provider) });
        });

        endpoints.MapPut("/settings/capability/{slug}/provider/{providerName}", "Update provider settings for a capability", async (HttpContext ctx, string slug, string providerName) =>
        {
            var config = configManager.Config;
            if (!config.Capabilities.TryGetValue(slug, out var cap))
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });

            // The kernel relays by provider ENTITY slug (tts-local), which need not
            // match the config provider name (local-wsl) -- resolve via entities.
            var resolvedName = _providerConfig.ResolveProviderName(cap, providerName);
            if (resolvedName == null || !cap.Providers.TryGetValue(resolvedName, out var provider))
                return Results.NotFound(new { error = "not_found", message = $"Provider '{providerName}' not found in capability '{slug}'" });
            providerName = resolvedName;

            var body = await ctx.Request.ReadFromJsonAsync<ProviderSettingsUpdate>();
            if (body == null)
                return Results.BadRequest(new { error = "invalid_body", message = "Expected JSON body" });

            if (body.WslDistro != null) provider.WslDistro = body.WslDistro;
            if (body.VenvPath != null) provider.VenvPath = body.VenvPath;
            if (body.ServerPath != null) provider.ServerPath = body.ServerPath;
            if (body.BackendPort.HasValue) provider.BackendPort = body.BackendPort.Value;
            if (body.Model != null) provider.Model = body.Model;
            if (body.VoicesBasePath != null) provider.VoicesBasePath = body.VoicesBasePath;
            if (body.HealthEndpoint != null) provider.HealthEndpoint = body.HealthEndpoint;
            if (body.StartupTimeoutSeconds.HasValue) provider.StartupTimeoutSeconds = body.StartupTimeoutSeconds.Value;
            if (body.ApiKey != null) provider.ApiKey = body.ApiKey;
            if (body.PodId != null) provider.PodId = body.PodId;
            if (body.GpuCount.HasValue) provider.GpuCount = body.GpuCount.Value;
            if (body.AutoStopOnExit.HasValue) provider.AutoStopOnExit = body.AutoStopOnExit.Value;

            if (body.Extra != null)
            {
                provider.Extra ??= new Dictionary<string, object?>();
                foreach (var kvp in body.Extra)
                    provider.Extra[kvp.Key] = kvp.Value;
            }

            // The settings UI writes through this endpoint (via the kernel proxy)
            // without touching entities, so config.json must keep persisting edits or
            // they evaporate on restart. The entity sync overlays on the next boot.
            configManager.Save();
            _ = _providerConfig.RefreshAsync();
            return Results.Ok(new { message = $"Provider '{providerName}' settings updated", slug, providerName, provider = SanitizeProvider(provider) });
        })
            .WithParam("wslDistro", "string", description: "WSL distribution to run the backend in", location: ParamLocation.Body)
            .WithParam("venvPath", "string", description: "Python virtualenv path", location: ParamLocation.Body)
            .WithParam("serverPath", "string", description: "Backend server path", location: ParamLocation.Body)
            .WithParam("backendPort", "integer", description: "Port the backend listens on", location: ParamLocation.Body)
            .WithParam("model", "string", description: "Default model for the provider", location: ParamLocation.Body)
            .WithParam("voicesBasePath", "string", description: "Base path for TTS voice files", location: ParamLocation.Body)
            .WithParam("healthEndpoint", "string", description: "Health-check endpoint path", location: ParamLocation.Body)
            .WithParam("startupTimeoutSeconds", "integer", description: "Seconds to wait for the backend to become healthy", location: ParamLocation.Body)
            .WithParam("apiKey", "string", description: "API key for cloud providers", location: ParamLocation.Body)
            .WithParam("podId", "string", description: "RunPod pod ID", location: ParamLocation.Body)
            .WithParam("gpuCount", "integer", description: "Number of GPUs to request (RunPod)", location: ParamLocation.Body)
            .WithParam("autoStopOnExit", "boolean", description: "Stop the pod when RedCompute exits (RunPod)", location: ParamLocation.Body)
            .WithParam("extra", "object", description: "Provider-specific key/value settings merged into the provider's Extra map", location: ParamLocation.Body);
    }

    private static object SanitizeProvider(ProviderConfig p)
    {
        return new
        {
            p.Type,
            p.WslDistro,
            p.VenvPath,
            p.ServerPath,
            p.BackendPort,
            p.Model,
            p.VoicesBasePath,
            p.HealthEndpoint,
            p.StartupTimeoutSeconds,
            apiKey = string.IsNullOrEmpty(p.ApiKey) ? null : "***",
            p.PodId,
            p.GpuCount,
            p.AutoStopOnExit,
            p.Extra
        };
    }

    private static string GetConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "config.json");
    }

    private class GeneralSettingsUpdate
    {
        public int? ApiPort { get; set; }
        public string? LogLevel { get; set; }
    }

    private class CapabilitySettingsUpdate
    {
        public string? ActiveProvider { get; set; }
    }

    private class ProviderSettingsUpdate
    {
        public string? WslDistro { get; set; }
        public string? VenvPath { get; set; }
        public string? ServerPath { get; set; }
        public int? BackendPort { get; set; }
        public string? Model { get; set; }
        public string? VoicesBasePath { get; set; }
        public string? HealthEndpoint { get; set; }
        public int? StartupTimeoutSeconds { get; set; }
        public string? ApiKey { get; set; }
        public string? PodId { get; set; }
        public int? GpuCount { get; set; }
        public bool? AutoStopOnExit { get; set; }
        public Dictionary<string, object?>? Extra { get; set; }
    }
}
