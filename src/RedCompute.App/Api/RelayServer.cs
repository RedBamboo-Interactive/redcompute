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
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;
using RedCompute.PluginSdk;

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
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly DockerContainerService _docker;
    private SessionCallbackRegistry _callbacks;
    private readonly Action<string, Guid?> _log;

    public RelayServer(RedComputeConfig config, CapabilityRegistry registry, JobTrackingService jobTracker,
        LoggingService logger, ConfigManager configManager, CloudflareTunnelService tunnelService,
        HardwareMonitorService hardwareMonitor, Action<string, Guid?> log)
    {
        _config = config;
        _registry = registry;
        _jobTracker = jobTracker;
        _logger = logger;
        _configManager = configManager;
        _tunnelService = tunnelService;
        _hardwareMonitor = hardwareMonitor;
        _docker = new DockerContainerService(log);
        _callbacks = new SessionCallbackRegistry(log);  // re-created with auth factory after Build()
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
        builder.Services.AddAppHostWebSocket();
        builder.Services.AddAppHostTelemetry(opts => opts.AppName = "RedCompute");

        var redSuiteDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var googleAuth = SigningKeyPersistence.LoadGoogleOAuth(redSuiteDir);
        builder.Services.AddAppHostAuth(new AuthOptions
        {
            Jwt = new JwtOptions { SigningKey = signingKey },
            Google = googleAuth,
            Mode = googleAuth != null ? AuthMode.Required : AuthMode.LocalDefault,
        });

        _app = builder.Build();

        _app.UseAppHostForwardedHeaders();
        _app.UseAppHostTelemetry();
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
            FallThroughOnFailure = googleAuth != null,
        });
        _app.UseAppHostJwtAuth();

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

        var authFactory = _app.Services.GetRequiredService<AuthenticatedHttpClientFactory>();
        _callbacks = new SessionCallbackRegistry(_log, authFactory);

        _app.UseUserDetection();

        var registry = _app.CreateEndpointRegistry();
        registry.MapAuthEndpoints();

        GlobalEndpoints.Initialize();
        GlobalEndpoints.Map(registry, _registry, _jobTracker, _logger);
        HardwareEndpoints.Map(registry, _hardwareMonitor);
        SettingsEndpoints.Map(registry, _configManager, _tunnelService, _registry);

        SuiteTelemetryEndpoints.Map(registry);
        UnifiedSessionEndpoints.Map(_app, _registry, _jobTracker, _log, _docker, _callbacks);
        GenericCapabilityEndpoints.Map(_app, _registry, _jobTracker, _log, _hardwareMonitor, _config);

        var broadcaster = _app.Services.GetRequiredService<WebSocketBroadcaster>();
        RegisterWsEvents(broadcaster);

        var descriptor = new RedComputeServiceDescriptor(_config, _registry, App.LogService, registry);
        _app.MapAppHostEndpoints(descriptor, _tunnelService, "RedCompute", () => new RedBamboo.AppHost.Tunnel.TunnelConfig
        {
            Enabled = _config.Tunnel.Enabled,
            TunnelToken = _config.Tunnel.TunnelToken,
            Hostname = _config.Tunnel.Hostname,
            CloudflaredPath = _config.Tunnel.CloudflaredPath,
            AccessToken = _config.Tunnel.AccessToken,
        }, App.LogService);

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

    private void RegisterWsEvents(WebSocketBroadcaster broadcaster)
    {
        broadcaster.RegisterEvent(new WsEventSchema("job.created",
            "Fired when a new job is queued", "JobRecord",
            ["id", "capabilitySlug", "providerName", "status", "queuedAt", "inputJson", "callerInfo", "name", "rationale"]));
        broadcaster.RegisterEvent(new WsEventSchema("job.updated",
            "Fired when a job's status, progress, or output changes", "JobRecord",
            ["id", "capabilitySlug", "status", "progress", "startedAt", "completedAt", "errorMessage", "outputSizeBytes", "durationMs"]));
        broadcaster.RegisterEvent(new WsEventSchema("capability.status",
            "Fired when a capability's backend status changes (polled every 5s)",
            Fields: ["slug", "displayName", "status", "sleeping", "provider"]));
        broadcaster.RegisterEvent(new WsEventSchema("tunnel.status",
            "Fired when the Cloudflare tunnel status changes",
            Fields: ["status", "hostname", "error"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.created",
            "Fired when a new AI session is started", "UnifiedSessionInfo",
            ["id", "provider", "projectName", "projectPath", "status", "startedAt", "model", "providerSessionId", "title", "messageCount", "permissionMode"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.updated",
            "Fired when a session's status, tokens, cost, or title changes", "UnifiedSessionInfo",
            ["id", "provider", "projectName", "status", "model", "title", "messageCount", "costUsd", "inputTokens", "outputTokens"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.ended",
            "Fired when a session stops or errors out",
            Fields: ["id", "reason"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.stream",
            "Fired for each streaming event from an active session (text, tool calls, thinking, errors)",
            Fields: ["sessionId", "event"]));
        broadcaster.RegisterEvent(new WsEventSchema("hardware.snapshot",
            "Fired every 2 seconds with live system hardware metrics",
            Fields: ["timestamp", "cpu", "ram", "gpus"]));

        _jobTracker.JobCreated += job => broadcaster.Broadcast("job.created", job);
        _jobTracker.JobUpdated += job => broadcaster.Broadcast("job.updated", job);
        _tunnelService.StatusChanged += (status, error) => broadcaster.Broadcast("tunnel.status", new
        {
            status = status.ToString(),
            hostname = App.ConfigManager.Config.Tunnel.Hostname,
            error
        });

        foreach (var source in _registry.FindProviders<IPluginEventSource>())
        {
            source.PluginEvent += (type, data) => broadcaster.Broadcast(type, data);
            source.PluginEvent += _callbacks.OnSessionEvent;
        }

        foreach (var sp in _registry.FindProviders<ISessionProvider>())
        {
            var providerId = sp.ProviderId;
            sp.SessionStreamEvent += (sessionId, evt) =>
                broadcaster.Broadcast("ai-session.stream", new { provider = providerId, sessionId, @event = evt });
        }

        _hardwareMonitor.SnapshotUpdated += snapshot => broadcaster.Broadcast("hardware.snapshot", snapshot);

        _ = PollCapabilityStatus(broadcaster);
    }

    private async Task PollCapabilityStatus(WebSocketBroadcaster broadcaster)
    {
        var lastStatuses = new Dictionary<string, string>();
        while (true)
        {
            await Task.Delay(5000);
            foreach (var (slug, entry) in _registry.Capabilities)
            {
                try
                {
                    var defaultStatus = entry.ActiveProvider != null
                        ? (await entry.ActiveProvider.GetStatusAsync()).ToString()
                        : "Stopped";

                    var provStatuses = new List<object>();
                    foreach (var (name, prov) in entry.Providers)
                    {
                        var ps = (await prov.GetStatusAsync()).ToString();
                        provStatuses.Add(new { name, status = ps });
                    }

                    var key = $"{slug}:{defaultStatus}:{entry.IsSleeping}:{entry.IsManuallyDisabled}:{string.Join(",", provStatuses.Select(p => p.ToString()))}";
                    if (lastStatuses.TryGetValue(slug, out var prev) && prev == key)
                        continue;

                    lastStatuses[slug] = key;
                    broadcaster.Broadcast("capability.status", new
                    {
                        slug,
                        displayName = entry.Definition.DisplayName,
                        status = defaultStatus,
                        sleeping = entry.IsSleeping,
                        disabled = entry.IsManuallyDisabled,
                        provider = entry.ActiveProvider?.Name,
                        defaultProvider = entry.DefaultProviderName,
                        providers = provStatuses
                    });
                }
                catch { }
            }
        }
    }

    public async Task StopAsync()
    {
        await _docker.StopAllAsync();

        if (_app != null)
        {
            _log("[Relay] Shutting down", null);
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
