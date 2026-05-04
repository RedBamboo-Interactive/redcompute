using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
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

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, LoggingService logger)
    {
        jobTracker.JobCreated += job => Broadcast("job.created", job);
        jobTracker.JobUpdated += job => Broadcast("job.updated", job);
        logger.LogEntryCreated += entry => Broadcast("log.entry", entry);

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
                    var status = entry.ActiveProvider != null
                        ? (await entry.ActiveProvider.GetStatusAsync()).ToString()
                        : "Stopped";

                    var key = $"{slug}:{status}:{entry.IsSleeping}";
                    if (lastStatuses.TryGetValue(slug, out var prev) && prev == key)
                        continue;

                    lastStatuses[slug] = key;
                    Broadcast("capability.status", new
                    {
                        slug,
                        displayName = entry.Definition.DisplayName,
                        status,
                        sleeping = entry.IsSleeping,
                        provider = entry.ActiveProvider?.Name
                    });
                }
                catch { }
            }
        }
    }
}
