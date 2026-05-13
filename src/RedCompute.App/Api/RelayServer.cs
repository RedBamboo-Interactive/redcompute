using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Extensions;
using RedBamboo.AppHost.Logging;
using RedBamboo.AppHost.RemoteAccess;
using RedBamboo.AppHost.Tunnel;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api;

public class RelayServer
{
    private WebApplication? _app;
    private readonly RedComputeConfig _config;
    private readonly CapabilityRegistry _registry;
    private readonly JobTrackingService _jobTracker;
    private readonly LoggingService _logger;
    private readonly ConfigManager _configManager;
    private readonly CloudflareTunnelService _tunnelService;
    private readonly ClaudeSessionService _claudeService;
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly Action<string, Guid?> _log;

    public RelayServer(RedComputeConfig config, CapabilityRegistry registry, JobTrackingService jobTracker,
        LoggingService logger, ConfigManager configManager, CloudflareTunnelService tunnelService,
        ClaudeSessionService claudeService, HardwareMonitorService hardwareMonitor, Action<string, Guid?> log)
    {
        _config = config;
        _registry = registry;
        _jobTracker = jobTracker;
        _logger = logger;
        _configManager = configManager;
        _tunnelService = tunnelService;
        _claudeService = claudeService;
        _hardwareMonitor = hardwareMonitor;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{_config.ApiPort}");
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });

        _app = builder.Build();

        _app.UseCors();
        _app.UseWebSockets();
        _app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-RedCompute-Version"] = "0.2.0";
            await next();
        });
        _app.UseAppHostAuth(new BearerAuthOptions
        {
            GetAccessToken = () => _config.Tunnel.AccessToken,
            CookieName = "redcompute_token",
            BypassPaths = ["/ping", "/api/remote/status"],
        });

        // Prefer web/dist in the repo root for dev (live Vite rebuilds), fall back to wwwroot for production
        var repoWebDist = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "dist");
        var webRoot = Directory.Exists(repoWebDist) ? Path.GetFullPath(repoWebDist)
            : Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot)
            });
        }

        GlobalEndpoints.Initialize();
        GlobalEndpoints.Map(_app, _registry, _jobTracker, _logger, _claudeService);
        DiscoverEndpoints.Map(_app, _config, _registry, _claudeService);
        OpenApiEndpoints.Map(_app, _config, _registry);
        GenericCapabilityEndpoints.Map(_app, _registry, _jobTracker, _log);
        WebSocketEndpoints.Map(_app, _registry, _jobTracker, _logger, _tunnelService, _claudeService, _hardwareMonitor);
        HardwareEndpoints.Map(_app, _hardwareMonitor);
        ClaudeSessionEndpoints.Map(_app, _claudeService, _jobTracker, _log);
        var descriptor = new RedComputeServiceDescriptor(_config, _registry, _claudeService, App.LogService);
        _app.MapAppHostEndpoints(descriptor, _tunnelService, "RedCompute", () => new RedBamboo.AppHost.Tunnel.TunnelConfig
        {
            Enabled = _config.Tunnel.Enabled,
            TunnelToken = _config.Tunnel.TunnelToken,
            Hostname = _config.Tunnel.Hostname,
            CloudflaredPath = _config.Tunnel.CloudflaredPath,
            AccessToken = _config.Tunnel.AccessToken,
        }, App.LogService);
        SettingsEndpoints.Map(_app, _configManager, _tunnelService, _registry);

        if (Directory.Exists(webRoot))
        {
            _app.MapFallback(async ctx =>
            {
                // Don't serve index.html for API-like paths or known capability slugs
                var path = ctx.Request.Path.Value ?? "";
                var firstSegment = path.TrimStart('/').Split('/').FirstOrDefault() ?? "";
                if (_registry.Get(firstSegment) != null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "not_found", message = $"No endpoint at '{path}'" });
                    return;
                }

                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
            });
        }

        _log($"[Relay] Starting on port {_config.ApiPort}", null);
        await _app.StartAsync(ct);
        _log($"[Relay] Listening at http://localhost:{_config.ApiPort}", null);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            _log("[Relay] Shutting down", null);
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
