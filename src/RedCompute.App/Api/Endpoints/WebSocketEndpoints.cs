using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using RedCompute.Core.Logging;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class WebSocketEndpoints
{
    private static readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, LoggingService logger, CloudflareTunnelService tunnelService, ClaudeSessionService claudeService)
    {
        jobTracker.JobCreated += job => Broadcast("job.created", job);
        jobTracker.JobUpdated += job => Broadcast("job.updated", job);
        logger.LogEntryCreated += entry => Broadcast("log.entry", entry);
        tunnelService.StatusChanged += (status, error) => Broadcast("tunnel.status", new
        {
            status = status.ToString(),
            hostname = App.ConfigManager.Config.Tunnel.Hostname,
            error
        });

        claudeService.SessionCreated += session => Broadcast("claude.session.created", session);
        claudeService.SessionUpdated += session => Broadcast("claude.session.updated", session);
        claudeService.SessionEnded += (id, reason) => Broadcast("claude.session.ended", new { id, reason });
        claudeService.StreamEvent += (sessionId, evt) => Broadcast("claude.stream", new { sessionId, @event = evt });

        _ = PollCapabilityStatus(registry);

        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "websocket_required", message = "This endpoint requires a WebSocket connection" });
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid().ToString();
            _clients[id] = ws;

            try
            {
                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            finally
            {
                _clients.TryRemove(id, out _);
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        });

        app.MapGet("/ws/schema", () => Results.Ok(new
        {
            description = "WebSocket real-time event stream. Connect at ws://host:port/ws",
            events = new object[]
            {
                new
                {
                    type = "job.created",
                    description = "Fired when a new job is queued",
                    dataSchema = "JobRecord",
                    fields = new[] { "id", "capabilitySlug", "providerName", "status", "queuedAt", "inputJson", "callerInfo", "name", "rationale" }
                },
                new
                {
                    type = "job.updated",
                    description = "Fired when a job's status, progress, or output changes",
                    dataSchema = "JobRecord",
                    fields = new[] { "id", "capabilitySlug", "status", "progress", "startedAt", "completedAt", "errorMessage", "outputSizeBytes", "durationMs" }
                },
                new
                {
                    type = "log.entry",
                    description = "Fired for every new log entry",
                    dataSchema = "LogEntry",
                    fields = new[] { "id", "timestamp", "tag", "tagCategory", "message", "tagColor", "isError", "jobId" }
                },
                new
                {
                    type = "capability.status",
                    description = "Fired when a capability's backend status changes (polled every 5s)",
                    fields = new[] { "slug", "displayName", "status", "sleeping", "provider" }
                },
                new
                {
                    type = "tunnel.status",
                    description = "Fired when the Cloudflare tunnel status changes",
                    fields = new[] { "status", "hostname", "error" }
                },
                new
                {
                    type = "claude.session.created",
                    description = "Fired when a new AI session is started",
                    dataSchema = "ClaudeSessionInfo",
                    fields = new[] { "id", "projectName", "projectPath", "status", "startedAt", "model", "claudeSessionId", "title", "messageCount", "permissionMode" }
                },
                new
                {
                    type = "claude.session.updated",
                    description = "Fired when a session's status, tokens, cost, or title changes",
                    dataSchema = "ClaudeSessionInfo",
                    fields = new[] { "id", "projectName", "status", "model", "title", "messageCount", "costUsd", "inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens", "contextTokens", "contextWindow", "effort", "permissionMode" }
                },
                new
                {
                    type = "claude.session.ended",
                    description = "Fired when a session stops or errors out",
                    fields = new[] { "id", "reason" }
                },
                new
                {
                    type = "claude.stream",
                    description = "Fired for each streaming event from an active session (text, tool calls, thinking, errors)",
                    fields = new[] { "sessionId", "event" },
                    eventSchema = new
                    {
                        type_field = "type",
                        types = new[] { "text", "thinking", "tool_use", "tool_result", "error", "status" },
                        fields = new[] { "type", "content", "toolName", "toolInput", "toolResult", "isPartial", "messageId" }
                    }
                }
            }
        }));
    }

    private static void Broadcast<T>(string type, T data)
    {
        var message = JsonSerializer.Serialize(new { type, data }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                _ = ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private static async Task PollCapabilityStatus(CapabilityRegistry registry)
    {
        var lastStatuses = new Dictionary<string, string>();

        while (true)
        {
            await Task.Delay(5000);

            foreach (var (slug, entry) in registry.Capabilities)
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

                    var key = $"{slug}:{defaultStatus}:{entry.IsSleeping}:{string.Join(",", provStatuses.Select(p => p.ToString()))}";
                    if (lastStatuses.TryGetValue(slug, out var prev) && prev == key)
                        continue;

                    lastStatuses[slug] = key;
                    Broadcast("capability.status", new
                    {
                        slug,
                        displayName = entry.Definition.DisplayName,
                        status = defaultStatus,
                        sleeping = entry.IsSleeping,
                        provider = entry.ActiveProvider?.Name,
                        defaultProvider = entry.DefaultProviderName,
                        providers = provStatuses
                    });
                }
                catch { }
            }
        }
    }
}
