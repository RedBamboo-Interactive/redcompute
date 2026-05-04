using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void Map(WebApplication app, ConfigManager configManager)
    {
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

    private class GeneralSettingsUpdate
    {
        public int? ApiPort { get; set; }
        public int? JobRetentionDays { get; set; }
        public string? LogLevel { get; set; }
        public bool? AutoStartWithWindows { get; set; }
    }

    private class CapabilitySettingsUpdate
    {
        public bool? Enabled { get; set; }
        public string? ActiveProvider { get; set; }
    }
}
