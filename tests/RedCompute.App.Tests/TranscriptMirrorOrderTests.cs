using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptMirrorOrderTests
{
    [Fact]
    public async Task MirroredToolUseIsStoredBeforeItsFastSynchronousResult()
    {
        await using var redLeaf = await FakeRedLeaf.Start();
        var pipeline = redLeaf.Pipeline();

        // Claude's order: the tool_use is mirrored from the session store, then a fast tool's
        // result arrives live and is written synchronously with its payload.
        pipeline.MirrorMessages([ToolUse()]);
        pipeline.HandleLiveEvent("claude-code", "session-1", new UnifiedStreamEvent
        {
            Type = "tool_result", MessageId = "toolu_1", MessageUid = "turn-1", ToolResult = "1",
        });

        Assert.Equal(["tool_use:1", "tool_result:2"], redLeaf.Arrivals);
        Assert.Contains("transcript:epoch:1", redLeaf.ExternalIds);
    }

    [Fact]
    public async Task FailedInlineWriteFallsBackToTheBatchedWriter()
    {
        await using var redLeaf = await FakeRedLeaf.Start(rejectFirstRecordWrite: true);
        var pipeline = redLeaf.Pipeline();

        pipeline.MirrorMessages([ToolUse()]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (redLeaf.Arrivals.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(["tool_use:1"], redLeaf.Arrivals);
    }

    private static AiMessageSnapshot ToolUse() => new()
    {
        Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "tool_use",
        ToolName = "Bash", ToolInput = "{}", MessageId = "toolu_1", MessageUid = "turn-1",
        Timestamp = DateTimeOffset.UtcNow,
    };

    private sealed class Sequences : ITranscriptSequenceStore
    {
        private long _value;
        public TranscriptStamp Next(string provider, string sessionId) => new("epoch", Interlocked.Increment(ref _value));
    }

    private sealed class FakeRedLeaf : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private RedLeafStreamClient _client = null!;
        private int _rejectRecordWrites;
        public ConcurrentQueue<string> Arrivals { get; } = new();
        public ConcurrentBag<string> ExternalIds { get; } = [];

        private FakeRedLeaf(WebApplication server) => _server = server;

        public SessionTranscriptPipeline Pipeline() =>
            new(_client, new WebSocketBroadcaster(), (_, _) => { }, null, new Sequences());

        public static async Task<FakeRedLeaf> Start(bool rejectFirstRecordWrite = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var server = builder.Build();
            var f = new FakeRedLeaf(server) { _rejectRecordWrites = rejectFirstRecordWrite ? 1 : 0 };
            server.MapGet("/api/entities/{slug}", () => Results.Ok(new { id = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
            server.MapPost("/api/streams/{stream}/records", async (HttpContext ctx) =>
            {
                if (Interlocked.Exchange(ref f._rejectRecordWrites, 0) == 1) return Results.StatusCode(503);
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var record in body.RootElement.GetProperty("records").EnumerateArray())
                {
                    if (record.TryGetProperty("external_id", out var id) && id.ValueKind == JsonValueKind.String)
                        f.ExternalIds.Add(id.GetString()!);
                    var data = record.GetProperty("data");
                    f.Arrive(data.ValueKind == JsonValueKind.String ? data.GetString()! : data.GetRawText());
                }
                return Results.Ok(new { created = 1 });
            });
            server.MapPost("/api/streams/{stream}/records/payload", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                f.Arrive(form["data"].ToString());
                return Results.Ok(new
                {
                    id = 42L,
                    payload = new { available = true, length = 1L, contentType = "text/plain; charset=utf-8", encoding = "utf-8", sha256 = "" },
                });
            });
            server.Urls.Add("http://127.0.0.1:0");
            await server.StartAsync();
            f._client = new RedLeafStreamClient(server.Urls.Single(), "RedComputeTests", new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            return f;
        }

        private void Arrive(string json)
        {
            using var data = JsonDocument.Parse(json);
            Arrivals.Enqueue($"{data.RootElement.GetProperty("event_type").GetString()}:{data.RootElement.GetProperty("sequence").GetInt64()}");
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
