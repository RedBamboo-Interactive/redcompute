using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptMirrorOrderTests
{
    [Fact]
    public async Task Switch_and_return_write_once_under_the_original_logical_transcript_with_native_origin()
    {
        await using var redLeaf = await FakeRedLeaf.Start();
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var routes = new SessionBindingTranscriptRoutes(f.Store);
        var pipeline = redLeaf.Pipeline(routes);
        var claude = new NativeSessionBinding("session", "native-claude",
            new("claude-code", "default-claude", "local", "claude-x", "low"));
        var codex = new NativeSessionBinding("binding-review", "native-codex",
            new("codex", "default-codex", "local", "codex-x", "low"));

        pipeline.MirrorCompletedMessages([Message(claude, "first")]);
        pipeline.MirrorCompletedMessages([Message(codex, "uncommitted")]);
        await Commit("next", 0, codex);
        Assert.Null(routes.Resolve("claude-code", "session"));
        pipeline.MirrorCompletedMessages([Message(codex, "second")]);
        pipeline.MirrorCompletedMessages([Message(codex, "second")]);

        await Commit("return", 1, claude);
        pipeline.MirrorCompletedMessages([Message(codex, "late")]);
        pipeline.MirrorCompletedMessages([Message(claude, "third")]);

        Assert.Equal(["text:1", "text:2", "text:3"], redLeaf.Arrivals);
        Assert.Equal(["session", "session", "session"], redLeaf.Data.Select(row => row.GetProperty("session_id").GetString()));
        Assert.Equal(["claude-code", "codex", "claude-code"], redLeaf.Data.Select(row => row.GetProperty("provider").GetString()));
        Assert.Equal(["first", "second", "third"], redLeaf.Data.Select(row => row.GetProperty("message_uid").GetString()));
        var restored = new SessionBindingTranscriptRoutes(new(f.Config, f.Attachments));
        Assert.Null(restored.Resolve("codex", "binding-review"));
        Assert.Equal(new LogicalTranscriptAddress("session", "claude-code", "claude-code"),
            restored.Resolve("claude-code", "session"));

        async Task Commit(string key, long generation, NativeSessionBinding to)
        {
            var item = (await f.Store.EnqueueAsync(f.Submission(key) with
            {
                Input = [],
                IdempotencyKey = key,
                BindingChange = new(generation, to.Target),
            })).Item;
            var pending = new SessionBindingMove(item.Id, generation, to.Target, generation + 1, BindingMoveState.Pending);
            var prepared = pending with { State = BindingMoveState.Prepared, Prepared = to };
            Assert.True(await f.Store.TryAdvanceBindingMoveAsync(item, claude, pending, default));
            Assert.True(await f.Store.TryAdvanceBindingMoveAsync(item with { BindingMove = pending }, claude, prepared, default));
            var commit = prepared with { State = BindingMoveState.Committed, CommittedGeneration = generation + 1 };
            Assert.True(await f.Store.TryAdvanceBindingMoveAsync(item with { BindingMove = prepared }, claude, commit, default));
        }

        static AiMessageSnapshot Message(NativeSessionBinding native, string uid) => new()
        {
            Provider = native.Target.ProviderId, SessionId = native.RuntimeSessionId, Role = "assistant",
            EventType = "text", Content = uid, ProviderPartId = "native-part", MessageUid = uid,
            Timestamp = DateTimeOffset.UnixEpoch,
        };
    }

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

    // LIVE fe9ef992, 13 Nov 14:56Z: RedLeaf committed an inline final answer but answered after
    // the 15 s client timeout. The fallback re-sent it through the batched writer without its
    // idempotency key, and the session read showed the text twice under one sequence.
    [Fact]
    public async Task InlineWriteWhoseAcknowledgementIsLostIsStoredOnce()
    {
        await using var redLeaf = await FakeRedLeaf.Start(loseFirstRecordAcknowledgement: true);
        var pipeline = redLeaf.Pipeline();
        var at = new DateTimeOffset(2026, 11, 13, 14, 56, 1, 148, TimeSpan.Zero);

        pipeline.MirrorMessages([new AiMessageSnapshot
        {
            Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "text",
            Content = "It's 16:00, time for Tinder's gaba.", Phase = "final_answer",
            MessageId = "msg_1:1", MessageUid = "turn-1", Timestamp = at,
        }]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (redLeaf.RecordPosts.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(
            [("transcript:epoch:1", at), ("transcript:epoch:1", at)],
            redLeaf.RecordPosts.ToArray());
        Assert.Equal(["text:1"], redLeaf.Arrivals);
    }

    // The completed-prefix publisher retries a failed part under its receipt's stamp. A payload
    // write RedLeaf committed before the acknowledgement was lost must not become a second row.
    [Fact]
    public async Task PayloadRetryAfterALostAcknowledgementIsStoredOnce()
    {
        await using var redLeaf = await FakeRedLeaf.Start(loseFirstPayloadAcknowledgement: true);
        var pipeline = redLeaf.Pipeline();
        var result = new AiMessageSnapshot
        {
            Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "tool_result",
            ToolName = "Bash", ToolResult = "alpha", MessageId = "toolu_1", ProviderPartId = "part-1",
            MessageUid = "turn-1", Timestamp = DateTimeOffset.UtcNow,
        };

        Assert.ThrowsAny<Exception>(() => pipeline.MirrorCompletedMessages([result]));
        pipeline.MirrorCompletedMessages([result]);

        Assert.Equal(["transcript:epoch:1", "transcript:epoch:1"], redLeaf.PayloadExternalIds.ToArray());
        Assert.Equal(["tool_result:1"], redLeaf.Arrivals);
    }

    // Background work is attributed by id. RedLeaf record data is schemaless, so the key must be
    // written on every record path a child can take: inline (tool_use, task), payload (results).
    [Fact]
    public async Task ChildRecordsCarryTheirParentToolUseIdIntoRedLeafOnEveryPath()
    {
        await using var redLeaf = await FakeRedLeaf.Start();
        var pipeline = redLeaf.Pipeline();

        pipeline.MirrorMessages([
            new AiMessageSnapshot
            {
                Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "tool_use",
                ToolName = "Bash", ToolInput = "{}", MessageId = "toolu_child", MessageUid = "turn-1",
                ParentToolUseId = "toolu_agent", Timestamp = DateTimeOffset.UtcNow,
            },
            new AiMessageSnapshot
            {
                Provider = "claude-code", SessionId = "session-1", Role = "system", EventType = "task",
                ToolInput = "{\"taskId\":\"task-1\",\"status\":\"running\"}", MessageId = "task-1",
                MessageUid = "turn-1", ParentToolUseId = "toolu_agent", Timestamp = DateTimeOffset.UtcNow,
            },
            new AiMessageSnapshot
            {
                Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "text",
                Content = "noted", MessageUid = "turn-2", Timestamp = DateTimeOffset.UtcNow,
                TurnOrigin = new TurnOrigin { Kind = TurnOrigin.BackgroundTask, TaskId = "task-1" },
            },
        ]);
        pipeline.HandleLiveEvent("claude-code", "session-1", new UnifiedStreamEvent
        {
            Type = "tool_result", MessageId = "toolu_child", MessageUid = "turn-1", ToolResult = "alpha",
            ParentToolUseId = "toolu_agent",
        });

        var data = redLeaf.Data.ToList();
        Assert.Equal(4, data.Count);
        Assert.All(data.Take(2).Append(data[3]), record =>
            Assert.Equal("toolu_agent", record.GetProperty("parent_tool_use_id").GetString()));
        Assert.Equal("tool-output", data[3].GetProperty("payload_kind").GetString());
        Assert.Equal(JsonValueKind.Null, data[2].GetProperty("parent_tool_use_id").ValueKind);
        var origin = data[2].GetProperty("turn_origin");
        Assert.Equal(("background-task", "task-1"),
            (origin.GetProperty("kind").GetString(), origin.GetProperty("task_id").GetString()));
    }

    // Persist before publish on the live path. When the payload write fails (a 5xx, or the 15 s client timeout of a starved
    // machine) the result used to be published inline anyway, with nothing stored: a client saw it at a stamp the transcript
    // did not have, and the completed turn's mirror then stored the same output again under a new sequence.
    [Fact]
    public async Task ToolResultWhosePayloadWriteFailsIsNotPublishedBeforeItIsStored()
    {
        await using var redLeaf = await FakeRedLeaf.Start(rejectFirstPayloadWrite: true);
        var frames = new FrameSocket();
        var pipeline = redLeaf.Pipeline(frames);

        pipeline.HandleLiveEvent("claude-code", "session-1", ToolResultEvent());

        Assert.Empty(frames.Of("tool_result"));
        await redLeaf.WaitForArrivals(1);
        Assert.Equal(["tool_result:1"], redLeaf.Arrivals);
        Assert.Contains("transcript:epoch:1", redLeaf.ExternalIds);
        Assert.Equal("alpha", redLeaf.Data.Single().GetProperty("tool_result").GetString());
    }

    [Fact]
    public async Task AWithheldToolResultIsNotStoredAgainByTheCompletedTurnsMirror()
    {
        await using var redLeaf = await FakeRedLeaf.Start(rejectFirstPayloadWrite: true);
        var pipeline = redLeaf.Pipeline(new FrameSocket());

        pipeline.HandleLiveEvent("claude-code", "session-1", ToolResultEvent());
        pipeline.MirrorMessages([new AiMessageSnapshot
        {
            Provider = "claude-code", SessionId = "session-1", Role = "assistant", EventType = "tool_result",
            ToolName = "Bash", ToolResult = "alpha", MessageId = "toolu_1", MessageUid = "turn-1",
            Timestamp = DateTimeOffset.UtcNow,
        }]);

        await redLeaf.WaitForArrivals(1);
        // One failed attempt at the stamp; the mirror wrote nothing under a new sequence.
        Assert.Equal(["transcript:epoch:1"], redLeaf.PayloadExternalIds.ToArray());
        Assert.Equal(["tool_result:1"], redLeaf.Arrivals);
    }

    // The write RedLeaf committed before its acknowledgement was lost: the queued copy carries the same identity.
    [Fact]
    public async Task ToolResultWhoseAcknowledgementIsLostIsStoredOnceAndNotPublished()
    {
        await using var redLeaf = await FakeRedLeaf.Start(loseFirstPayloadAcknowledgement: true);
        var frames = new FrameSocket();
        var pipeline = redLeaf.Pipeline(frames);

        pipeline.HandleLiveEvent("claude-code", "session-1", ToolResultEvent());

        await redLeaf.WaitForRecordPosts(1);
        Assert.Empty(frames.Of("tool_result"));
        Assert.Equal(["tool_result:1"], redLeaf.Arrivals);
    }

    [Fact]
    public async Task AStoredToolResultIsPublishedWithItsPayloadReference()
    {
        await using var redLeaf = await FakeRedLeaf.Start();
        var frames = new FrameSocket();
        var pipeline = redLeaf.Pipeline(frames);

        pipeline.HandleLiveEvent("claude-code", "session-1", ToolResultEvent());

        var frame = Assert.Single(frames.Of("tool_result"));
        Assert.Equal(42L, frame.GetProperty("payloadRef").GetProperty("recordId").GetInt64());
        Assert.Equal(["tool_result:1"], redLeaf.Arrivals);
    }

    private static UnifiedStreamEvent ToolResultEvent() => new()
    {
        Type = "tool_result", ToolName = "Bash", MessageId = "toolu_1", MessageUid = "turn-1", ToolResult = "alpha",
    };

    /// <summary>The session.stream events a client would have been sent.</summary>
    private sealed class FrameSocket : WebSocket
    {
        private readonly ConcurrentQueue<JsonElement> _events = new();

        public WebSocketBroadcaster Broadcaster { get; } = new();

        public FrameSocket()
        {
            // AppHost cbe0265 added allowsLogEvents; the release pin predates it, so pass it only when AddClient takes it.
            var addClient = typeof(WebSocketBroadcaster).GetMethod("AddClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            addClient.Invoke(Broadcaster, addClient.GetParameters().Length == 3
                ? new object?[] { "test", this, false }
                : new object?[] { "test", this });
        }

        public List<JsonElement> Of(string type) => _events.Where(e => e.GetProperty("type").GetString() == type).ToList();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            if (json.RootElement.GetProperty("type").GetString() == SessionTranscriptPipeline.StreamEventType)
                _events.Enqueue(json.RootElement.GetProperty("data").GetProperty("event").Clone());
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
        private int _loseRecordAcknowledgements;
        private int _losePayloadAcknowledgements;
        private int _rejectPayloadWrites;
        private readonly ConcurrentDictionary<string, byte> _stored = new();
        public ConcurrentQueue<string> Arrivals { get; } = new();
        public ConcurrentQueue<(string? ExternalId, DateTimeOffset CreatedAt)> RecordPosts { get; } = new();
        public ConcurrentQueue<string> PayloadExternalIds { get; } = new();
        public ConcurrentBag<string> ExternalIds { get; } = [];
        public ConcurrentQueue<JsonElement> Data { get; } = new();

        private FakeRedLeaf(WebApplication server) => _server = server;

        public SessionTranscriptPipeline Pipeline(SessionBindingTranscriptRoutes? bindings = null) =>
            new(_client, new WebSocketBroadcaster(), (_, _) => { }, null, new Sequences(), bindings);

        public SessionTranscriptPipeline Pipeline(FrameSocket frames) =>
            new(_client, frames.Broadcaster, (_, _) => { }, null, new Sequences());

        /// <summary>The batched writer flushes on its own tick: wait for what it was handed, without a clock of its own.</summary>
        public Task WaitForArrivals(int count) => WaitFor(() => Arrivals.Count >= count);
        public Task WaitForRecordPosts(int count) => WaitFor(() => RecordPosts.Count >= count);

        private static async Task WaitFor(Func<bool> done)
        {
            var give = DateTime.UtcNow + Step.Backstop;
            while (!done())
            {
                if (DateTime.UtcNow > give) throw new TimeoutException("The batched writer never delivered the queued record");
                await Task.Delay(25);
            }
        }

        public static async Task<FakeRedLeaf> Start(bool rejectFirstRecordWrite = false,
            bool loseFirstRecordAcknowledgement = false, bool loseFirstPayloadAcknowledgement = false,
            bool rejectFirstPayloadWrite = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var server = builder.Build();
            var f = new FakeRedLeaf(server)
            {
                _rejectRecordWrites = rejectFirstRecordWrite ? 1 : 0,
                _loseRecordAcknowledgements = loseFirstRecordAcknowledgement ? 1 : 0,
                _losePayloadAcknowledgements = loseFirstPayloadAcknowledgement ? 1 : 0,
                _rejectPayloadWrites = rejectFirstPayloadWrite ? 1 : 0,
            };
            server.MapGet("/api/entities/{slug}", () => Results.Ok(new { id = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
            server.MapPost("/api/streams/{stream}/records", async (HttpContext ctx) =>
            {
                if (Interlocked.Exchange(ref f._rejectRecordWrites, 0) == 1) return Results.StatusCode(503);
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
                foreach (var record in body.RootElement.GetProperty("records").EnumerateArray())
                {
                    string? externalId = null;
                    if (record.TryGetProperty("external_id", out var id) && id.ValueKind == JsonValueKind.String)
                        f.ExternalIds.Add(externalId = id.GetString()!);
                    f.RecordPosts.Enqueue((externalId, record.GetProperty("created_at").GetDateTimeOffset()));
                    // RedLeaf's contract: an external_id is unique within the stream.
                    if (externalId is not null && !f._stored.TryAdd(externalId, 0)) continue;
                    var data = record.GetProperty("data");
                    f.Arrive(data.ValueKind == JsonValueKind.String ? data.GetString()! : data.GetRawText());
                }
                // Committed, but the producer never learns it (a timeout after RedLeaf's commit).
                if (Interlocked.Exchange(ref f._loseRecordAcknowledgements, 0) == 1) return Results.StatusCode(503);
                return Results.Ok(new { created = 1 });
            });
            server.MapPost("/api/streams/{stream}/records/payload", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var externalId = form.ContainsKey("external_id") ? form["external_id"].ToString() : null;
                f.PayloadExternalIds.Enqueue(externalId ?? "(none)");
                if (Interlocked.Exchange(ref f._rejectPayloadWrites, 0) == 1) return Results.StatusCode(503);
                if (externalId is null || f._stored.TryAdd(externalId, 0))
                    f.Arrive(form["data"].ToString());
                if (Interlocked.Exchange(ref f._losePayloadAcknowledgements, 0) == 1) return Results.StatusCode(503);
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
            Data.Enqueue(data.RootElement.Clone());
            Arrivals.Enqueue($"{data.RootElement.GetProperty("event_type").GetString()}:{data.RootElement.GetProperty("sequence").GetInt64()}");
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
