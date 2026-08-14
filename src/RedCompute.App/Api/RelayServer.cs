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
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.Telemetry;
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
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly DockerContainerService _docker;
    private readonly QualityModeService _qualityModes;
    private readonly ProviderConfigService _providerConfig;
    private readonly InputAttachmentStore _inputAttachments;
    private SessionCallbackRegistry _callbacks;
    private readonly Action<string, Guid?> _log;
    private RedLeafStreamClient? _streamClient;
    private Action<RedBamboo.AppHost.Logging.LogEntry>? _logForwarder;
    private Action<TelemetryEntry>? _telemetryForwarder;
    private CancellationTokenSource? _jobAuditCts;
    private Task? _jobAuditTask;

    public RelayServer(RedComputeConfig config, CapabilityRegistry registry, JobTrackingService jobTracker,
        LoggingService logger, ConfigManager configManager,
        HardwareMonitorService hardwareMonitor, ProviderConfigService providerConfig, Action<string, Guid?> log)
    {
        _config = config;
        _registry = registry;
        _jobTracker = jobTracker;
        _logger = logger;
        _configManager = configManager;
        _hardwareMonitor = hardwareMonitor;
        _docker = new DockerContainerService(log);
        _providerConfig = providerConfig;
        _inputAttachments = new InputAttachmentStore(config);
        _qualityModes = new QualityModeService(config, log, _providerConfig);
        _callbacks = new SessionCallbackRegistry(log);  // re-created with auth factory after Build()
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Loopback only: the outside world reaches Compute through the Leaf kernel's
        // /compute/* proxy (one origin, one tunnel). Nothing dials this port directly.
        builder.WebHost.UseUrls($"http://127.0.0.1:{_config.ApiPort}");
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
            ctx.Response.Headers["X-RedCompute-Version"] = RedComputeServiceDescriptor.AppVersion;
            await next();
        });
        _app.UseAppHostAuth(new BearerAuthOptions
        {
            // No tunnel access token anymore — the kernel owns the tunnel. Loopback
            // callers (the kernel proxy) pass in LocalDefault mode; JWTs signed with the
            // shared RedSuite key still validate below. Auth stays ON.
            GetAccessToken = () => null,
            CookieName = "redcompute_token",
            BypassPaths = ["/ping", "/api/remote/status"],
            FallThroughOnFailure = googleAuth != null,
        });
        _app.UseAppHostJwtAuth();

        // No static frontend: the dashboard is the compute-dashboard Leaf plugin, served
        // by the kernel and talking through the /compute/* proxy.

        var authFactory = _app.Services.GetRequiredService<AuthenticatedHttpClientFactory>();
        _callbacks = new SessionCallbackRegistry(_log, authFactory);

        _app.UseUserDetection();

        // Ship job logs and API telemetry to RedLeaf record streams. Local
        // SQLite stays the read path (dual-write) until reads move over too.
        _streamClient = new RedLeafStreamClient(
            _config.RedLeafUrl, "redcompute",
            new JwtService(new JwtOptions { SigningKey = signingKey }),
            App.LogService);
        _streamClient.DefineStream(new StreamDefinition(
            "compute-logs", "Compute Logs",
            "Structured RedCompute job and app logs", RetentionDays: 30, ParentType: "compute-job"));
        _streamClient.DefineStream(new StreamDefinition(
            "api-telemetry", "API Telemetry",
            "HTTP request/response stats from Red Suite apps", RetentionDays: 30));

        _logForwarder = entry => _streamClient.Enqueue("compute-logs", new
        {
            tag = entry.Tag,
            category = entry.Category,
            message = entry.Message,
            full_message = entry.FullMessage,
            tag_color = entry.TagColor,
            is_error = entry.Level >= RedBamboo.AppHost.Logging.LogLevel.Error,
            job_id = entry.JobId,
        }, entityId: entry.JobId is { } jid && Guid.TryParse(jid, out var jobGuid) ? jobGuid : null);
        App.LogService.OnLogEntry += _logForwarder;

        var telemetry = _app.Services.GetRequiredService<TelemetryService>();
        _telemetryForwarder = e => _streamClient.Enqueue("api-telemetry", new
        {
            app = "redcompute",
            method = e.Method,
            path = e.Path,
            route_pattern = e.RoutePattern,
            status_code = e.StatusCode,
            duration_ms = Math.Round(e.DurationMs, 2),
            response_size = e.ResponseSize,
            error = e.Error,
        });
        telemetry.OnEntry += _telemetryForwarder;

        // Mirror AI sessions (claude-code / opencode / codex) to RedLeaf:
        // sessions as ai-session entities (versioning off — token counts and
        // status churn constantly), messages as session-messages records.
        _streamClient.DefineEntityType(new EntityTypeDefinition(
            "ai-session", "AI Session",
            "AI coding/inference session (claude-code, opencode, codex)",
            Icon: "ph ph-terminal-window", Color: "pink", Versioning: false,
            Fields:
            [
                // Field names slugify to the snake_case data keys below.
                new { name = "Provider", fieldType = "string", description = "Session provider plugin (claude-code, opencode, codex)" },
                new { name = "Provider Entity", fieldType = "entity_ref", description = "Provider entity selected for this session", constraints = "{\"target_type\":\"provider\"}" },
                new { name = "Status", fieldType = "string", description = "Lifecycle status (Starting, Active, Idle, Stopped...)" },
                new { name = "Project Name", fieldType = "string" },
                new { name = "Project Path", fieldType = "string", description = "Machine-local path to the project directory" },
                new { name = "Model", fieldType = "string" },
                new { name = "Message Count", fieldType = "number" },
                new { name = "Cost USD", fieldType = "float" },
                new { name = "Started At", fieldType = "date" },
                new { name = "Stop Reason", fieldType = "string" },
                new { name = "Effort", fieldType = "string" },
                new { name = "Quality Tier", fieldType = "entity_ref", description = "Quality-tier entity selected for this session", constraints = "{\"target_type\":\"quality-tier\"}" },
                new { name = "Job ID", fieldType = "string", description = "RedCompute job this session belongs to" },
                new { name = "Dismissed", fieldType = "boolean" },
                new { name = "Source", fieldType = "string", description = "Origin app (e.g. Nova)" },
                new { name = "User ID", fieldType = "string", description = "Owner of the session" },
                new { name = "External Session ID", fieldType = "string", description = "Provider-side session identifier" },
                new { name = "Input Tokens", fieldType = "number" },
                new { name = "Output Tokens", fieldType = "number" },
                new { name = "Session ID", fieldType = "string", description = "Provider-local session key" },
                new { name = "App", fieldType = "string", description = "Owning Red Suite app" },
            ]));
        _streamClient.DefineStream(new StreamDefinition(
            "session-messages", "Session Messages",
            "Messages and tool events from AI sessions", RetentionDays: 180, ParentType: "ai-session"));
        var broadcaster = _app.Services.GetRequiredService<WebSocketBroadcaster>();
        var transcriptPipeline = new SessionTranscriptPipeline(_streamClient, broadcaster, _log);

        SuiteMirror.SessionUpserted = snap => _streamClient.UpsertEntity(
            SessionEntitySlug(snap.Provider, snap.Id),
            "ai-session",
            snap.Title ?? $"{snap.ProjectName} ({snap.Provider})",
            new
            {
                provider = snap.Provider,
                provider_entity = snap.ProviderEntity,
                session_id = snap.Id,
                project_name = snap.ProjectName,
                project_path = snap.ProjectPath,
                status = snap.Status,
                stop_reason = snap.StopReason,
                started_at = snap.StartedAt.ToString("O"),
                model = snap.Model,
                external_session_id = snap.ExternalSessionId,
                message_count = snap.MessageCount,
                cost_usd = snap.CostUsd,
                input_tokens = snap.InputTokens,
                output_tokens = snap.OutputTokens,
                cache_read_input_tokens = snap.CacheReadInputTokens,
                cache_creation_input_tokens = snap.CacheCreationInputTokens,
                context_tokens = snap.ContextTokens,
                context_window = snap.ContextWindow,
                effort = snap.Effort,
                quality_tier = snap.QualityTier,
                job_id = snap.JobId,
                dismissed = snap.Dismissed,
                source = snap.Source,
                user_id = snap.UserId,
                app = "redcompute",
            });

        SuiteMirror.MessagesAdded = transcriptPipeline.MirrorMessages;

        // Jobs mirror as compute-job entities — the parent type compute-logs
        // records link to. Progress is deliberately excluded (churns per tick;
        // live progress stays on the WebSocket).
        _streamClient.DefineEntityType(new EntityTypeDefinition(
            "compute-job", "Compute Job",
            "RedCompute job lifecycle record",
            Icon: "ph-fill ph-cpu", Color: "pink", Versioning: false,
            Fields:
            [
                new { name = "Capability", fieldType = "string", description = "tts, stt, music-gen, image-gen, ai-session…" },
                new { name = "Provider", fieldType = "string" },
                new { name = "Status", fieldType = "string" },
                new { name = "Queued At", fieldType = "date" },
                new { name = "Cost USD", fieldType = "float" },
                new { name = "Caller Info", fieldType = "string" },
                new { name = "Error Message", fieldType = "string" },
                new { name = "User ID", fieldType = "string" },
                new { name = "User Name", fieldType = "string" },
                new { name = "User Avatar URL", fieldType = "string" },
                new { name = "Creation Provenance", fieldType = "json", description = "Immutable versioned origin, actor, beneficiary, context, trace and assurance snapshot" },
                new { name = "Recording Service", fieldType = "string", description = "Recorder/mirror service; never the originating app" },
            ]));

        _streamClient.DefineStream(new StreamDefinition(
            "compute-job-events", "Compute Job Events",
            "Append-only Compute invocation and lifecycle audit trail", ParentType: "compute-job"));
        var legacySessions = _registry.FindProviders<ISessionProvider>()
            .SelectMany(provider => provider.GetSessions(100_000, includeDismissed: true))
            .ToList();
        var backfilledJobs = _jobTracker.BackfillLegacyJobs(legacySessions);
        if (backfilledJobs > 0)
            _log($"[Audit] Conservatively backfilled {backfilledJobs} legacy jobs ({legacySessions.Count} exact session snapshots available)", null);
        _jobAuditCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _jobAuditTask = new JobAuditOutboxService(_streamClient, _log).RunAsync(_jobAuditCts.Token);

        var registry = _app.CreateEndpointRegistry();
        registry.MapAuthEndpoints();

        GlobalEndpoints.Initialize();
        GlobalEndpoints.Map(registry, _registry, _jobTracker, _logger);
        HardwareEndpoints.Map(registry, _hardwareMonitor);
        SettingsEndpoints.Map(registry, _configManager, _registry, _providerConfig);

        SuiteTelemetryEndpoints.Map(registry);
        // Read-path cutover: session list/history reads come from RedLeaf
        // (hard dependency by decision); plugin SQLite stays write-only
        // (dual-write) until the verification window closes.
        var redLeafReader = new RedLeafSessionReader(
            _config.RedLeafUrl, new JwtService(new JwtOptions { SigningKey = signingKey }), _qualityModes);
        var inputQueueStore = new SessionInputQueueStore(_config, _inputAttachments);
        var inputQueue = new SessionInputQueueService(inputQueueStore, _inputAttachments, _registry,
            _jobTracker, broadcaster, _log);
        UnifiedSessionEndpoints.Map(registry, _registry, _jobTracker, _log, _config, _docker, _callbacks,
            _qualityModes, redLeafReader, _providerConfig, _inputAttachments, inputQueue);
        _ = RunAttachmentCleanupAsync(ct);
        GenericCapabilityEndpoints.Map(_app, registry, _registry, _jobTracker, _log, _hardwareMonitor, _config);

        RegisterWsEvents(broadcaster, transcriptPipeline);
        _ = inputQueue.RunAsync(ct);

        // Tunnel and autostart endpoints are skipped: the Leaf kernel owns the one tunnel
        // and the one autostart entry. The dormant tunnel service only satisfies the
        // discovery endpoint's signature.
        var descriptor = new RedComputeServiceDescriptor(_config, _registry, App.LogService, registry);
        _app.MapAppHostEndpoints(descriptor, new CloudflareTunnelService(logger: null), "RedCompute",
            () => new RedBamboo.AppHost.Tunnel.TunnelConfig { Enabled = false },
            App.LogService, mapRemoteAccess: false, mapAutoStart: false);

        // Quality tiers and modes are entity-owned. Load RedLeaf or the last-known-good
        // disk snapshot before accepting requests. If neither exists, tier-based requests
        // fail closed while the background retry loop waits for RedLeaf.
        await _qualityModes.InitialSyncAsync(ct);

        _log($"[Relay] Starting on port {_config.ApiPort}", null);
        await _app.StartAsync(ct);
        _log($"[Relay] Listening at http://127.0.0.1:{_config.ApiPort}", null);

        // Provider entities were synchronously loaded before capability initialization. Keep
        // retrying only when that initial RedLeaf/cache sync could not supply an entity snapshot.
        _ = _providerConfig.EnsureLoadedAsync(ct);
        _ = _qualityModes.EnsureLoadedAsync(ct);
    }

    private void RegisterWsEvents(WebSocketBroadcaster broadcaster, SessionTranscriptPipeline transcriptPipeline)
    {
        broadcaster.RegisterEvent(new WsEventSchema("job.created",
            "Fired when a new job is queued", "JobRecord",
            ["id", "capabilitySlug", "providerName", "status", "queuedAt", "inputJson", "callerInfo", "name", "rationale", "creationProvenance", "userId", "userName", "userAvatarUrl"]));
        broadcaster.RegisterEvent(new WsEventSchema("job.updated",
            "Fired when a job's status, progress, or output changes", "JobRecord",
            ["id", "capabilitySlug", "providerName", "status", "progress", "startedAt", "completedAt", "errorMessage", "outputSizeBytes", "durationMs", "creationProvenance", "userId", "userName", "userAvatarUrl"]));
        broadcaster.RegisterEvent(new WsEventSchema("job.event",
            "Fired for each immutable job invocation or lifecycle event", "JobLifecycleEvent",
            ["id", "jobId", "kind", "occurredAt", "provenance", "dataJson"]));
        broadcaster.RegisterEvent(new WsEventSchema("capability.status",
            "Fired when a capability's backend status changes (polled every 5s)",
            Fields: ["slug", "displayName", "status", "sleeping", "provider"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.created",
            "Fired when a new AI session is started", "UnifiedSessionInfo",
            ["id", "provider", "projectName", "projectPath", "status", "startedAt", "model", "providerSessionId", "title", "messageCount", "permissionMode"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.updated",
            "Fired when a session's status, tokens, cost, or title changes", "UnifiedSessionInfo",
            ["id", "provider", "projectName", "status", "stopReason", "model", "title", "messageCount", "costUsd", "inputTokens", "outputTokens"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.ended",
            "Fired when a session stops or errors out",
            Fields: ["id", "reason", "stopReason"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.stream",
            "Fired for each streaming event from an active session (text, tool calls, thinking, errors)",
            Fields: ["sessionId", "event", "timestamp"]));
        broadcaster.RegisterEvent(new WsEventSchema("session.input-queue.updated",
            "Fired when durable input is queued, delivered, cancelled, retried, or blocked",
            "SessionInputQueueChanged",
            ["sessionId", "itemIds", "transition", "depth", "state", "blockedReason", "errorCode", "deliveredMessageUid"]));
        broadcaster.RegisterEvent(new WsEventSchema("hardware.snapshot",
            "Fired every 2 seconds with live system hardware metrics",
            Fields: ["timestamp", "cpu", "ram", "gpus"]));

        _jobTracker.JobCreated += job => broadcaster.Broadcast("job.created", job);
        _jobTracker.JobUpdated += job => broadcaster.Broadcast("job.updated", job);
        _jobTracker.JobEventAppended += evt => broadcaster.Broadcast("job.event", evt);

        foreach (var source in _registry.FindProviders<IPluginEventSource>())
        {
            source.PluginEvent += (type, data) => broadcaster.Broadcast(type, data);
            source.PluginEvent += _callbacks.OnSessionEvent;
        }

        foreach (var sp in _registry.FindProviders<ISessionProvider>())
        {
            var providerId = sp.ProviderId;
            sp.SessionStreamEvent += (sessionId, evt) =>
                transcriptPipeline.HandleLiveEvent(providerId, sessionId, evt);
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
                    var defaultStatus = (await _registry.GetStatus(slug)).ToString();

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
                        provider = entry.IsExternal
                            ? entry.Definition.WorkerDisplayName
                            : entry.ActiveProvider?.Name,
                        defaultProvider = entry.DefaultProviderName,
                        executionMode = entry.Definition.ExecutionMode.ToString().ToLowerInvariant(),
                        providers = provStatuses
                    });
                }
                catch { }
            }
        }
    }

    private async Task RunAttachmentCleanupAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var removed = await _inputAttachments.CleanupExpiredAsync(ct);
                if (removed > 0) _log($"[Attachments] Removed {removed} expired upload(s)", null);
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log($"[Attachments] Cleanup failed: {ex.Message}", null);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task StopAsync()
    {
        await _docker.StopAllAsync();

        SuiteMirror.SessionUpserted = null;
        SuiteMirror.MessagesAdded = null;

        if (_jobAuditCts != null)
        {
            _jobAuditCts.Cancel();
            if (_jobAuditTask != null)
                try { await _jobAuditTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _jobAuditTask = null;
            _jobAuditCts.Dispose();
            _jobAuditCts = null;
        }

        if (_logForwarder != null)
        {
            App.LogService.OnLogEntry -= _logForwarder;
            _logForwarder = null;
        }
        if (_telemetryForwarder != null && _app != null)
        {
            _app.Services.GetRequiredService<TelemetryService>().OnEntry -= _telemetryForwarder;
            _telemetryForwarder = null;
        }
        if (_streamClient != null)
        {
            await _streamClient.DisposeAsync();
            _streamClient = null;
        }

        if (_app != null)
        {
            _log("[Relay] Shutting down", null);
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static string SessionEntitySlug(string provider, string sessionId)
    {
        var sanitized = new string(sessionId.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"ai-session-{provider}-{sanitized}";
    }
}
