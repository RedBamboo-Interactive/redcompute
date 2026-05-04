using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api.Endpoints;

public static class SettingsEndpoints
{
    private static CloudflareTunnelService _tunnelService = null!;

    public static void Map(WebApplication app, ConfigManager configManager, CloudflareTunnelService tunnelService)
    {
        _tunnelService = tunnelService;

        app.MapGet("/settings", () =>
        {
            var config = configManager.Config;
            var sanitized = new
            {
                config.ApiPort,
                config.LogLevel,
                config.JobRetentionDays,
                config.AutoStartWithWindows,
                configPath = GetConfigPath(),
                tunnel = new
                {
                    config.Tunnel.Enabled,
                    config.Tunnel.AccessToken,
                    tunnelToken = string.IsNullOrEmpty(config.Tunnel.TunnelToken) ? (string?)null : "***",
                    config.Tunnel.Hostname,
                    config.Tunnel.CloudflaredPath,
                    status = tunnelService.Status.ToString(),
                    error = tunnelService.ErrorMessage
                },
                capabilities = config.Capabilities.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        kvp.Value.Enabled,
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

        app.MapPut("/settings/general", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<GeneralSettingsUpdate>();
            if (body == null)
                return Results.BadRequest(new { error = "invalid_body", message = "Expected JSON body" });

            var config = configManager.Config;
            if (body.ApiPort.HasValue) config.ApiPort = body.ApiPort.Value;
            if (body.JobRetentionDays.HasValue) config.JobRetentionDays = body.JobRetentionDays.Value;
            if (body.LogLevel != null) config.LogLevel = body.LogLevel;
            if (body.AutoStartWithWindows.HasValue) config.AutoStartWithWindows = body.AutoStartWithWindows.Value;

            if (body.TunnelAccessToken != null) config.Tunnel.AccessToken = body.TunnelAccessToken;
            if (body.TunnelToken != null) config.Tunnel.TunnelToken = body.TunnelToken;
            if (body.TunnelHostname != null) config.Tunnel.Hostname = body.TunnelHostname;
            if (body.TunnelCloudflaredPath != null) config.Tunnel.CloudflaredPath = body.TunnelCloudflaredPath;

            if (body.TunnelEnabled.HasValue)
                await ApplyTunnelToggle(config, body.TunnelEnabled.Value, tunnelService);

            configManager.Save();
            return Results.Ok(new { message = "Settings updated", config.ApiPort, config.LogLevel, config.JobRetentionDays, config.AutoStartWithWindows });
        });

        app.MapPut("/settings/capability/{slug}", async (HttpContext ctx, string slug) =>
        {
            var config = configManager.Config;
            if (!config.Capabilities.ContainsKey(slug))
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found in config" });

            var body = await ctx.Request.ReadFromJsonAsync<CapabilitySettingsUpdate>();
            if (body == null)
                return Results.BadRequest(new { error = "invalid_body", message = "Expected JSON body" });

            var cap = config.Capabilities[slug];
            if (body.Enabled.HasValue) cap.Enabled = body.Enabled.Value;
            if (body.ActiveProvider != null) cap.ActiveProvider = body.ActiveProvider;

            configManager.Save();
            return Results.Ok(new { message = $"Capability '{slug}' settings updated", slug, cap.Enabled, cap.ActiveProvider });
        });
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

    private static async Task ApplyTunnelToggle(RedComputeConfig config, bool enabled, CloudflareTunnelService tunnelService)
    {
        if (enabled && !config.Tunnel.Enabled)
        {
            if (string.IsNullOrEmpty(config.Tunnel.AccessToken))
                config.Tunnel.AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            config.Tunnel.Enabled = true;
            await tunnelService.StartAsync(config.ApiPort, config.Tunnel);
        }
        else if (!enabled && config.Tunnel.Enabled)
        {
            config.Tunnel.Enabled = false;
            await tunnelService.StopAsync();
        }
    }

    private class GeneralSettingsUpdate
    {
        public int? ApiPort { get; set; }
        public int? JobRetentionDays { get; set; }
        public string? LogLevel { get; set; }
        public bool? AutoStartWithWindows { get; set; }
        public bool? TunnelEnabled { get; set; }
        public string? TunnelAccessToken { get; set; }
        public string? TunnelToken { get; set; }
        public string? TunnelHostname { get; set; }
        public string? TunnelCloudflaredPath { get; set; }
    }

    private class CapabilitySettingsUpdate
    {
        public bool? Enabled { get; set; }
        public string? ActiveProvider { get; set; }
    }
}
