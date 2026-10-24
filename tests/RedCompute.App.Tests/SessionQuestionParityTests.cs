using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// One matrix, both providers, through the real provider classes (ClaudeCodeProvider over
// ClaudeSessionService, CodexProvider over CodexInteractiveService) wired to the callback registry
// and the session read exactly as RelayServer wires them. A structured question parks the turn;
// it is readable (workState awaiting_answer), notified once, and cleared by every resolution
// path with a question_resolved outcome. Pending questions are control state: they never become
// transcript records beyond each provider's pre-existing ones.
[Collection("Unified session endpoint mapping")]
public sealed class SessionQuestionParityTests : IDisposable
{
    private const string CompleteUrl = "http://localhost/complete";
    private const string QuestionUrl = "http://localhost/question";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-question-parity", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public SessionQuestionParityTests() => Directory.CreateDirectory(_root);

    public static TheoryData<string> Providers => new() { "claude-code", "codex" };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Question_parks_is_read_notified_once_answered_cleared_and_the_turn_still_completes(string provider)
    {
        using var h = Harness(provider);
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        var registry = Registry(h, posts);
        registry.Register(h.SessionId, CompleteUrl, "owner", "operation", questionUrl: QuestionUrl);

        h.Park("req-1");

        var parked = await ReadAsync(h);
        Assert.Equal((int)RedCompute.Core.Sessions.SessionStatus.Active, parked.GetProperty("session").GetProperty("status").GetInt32());
        Assert.Equal(SessionWorkStates.AwaitingAnswer, parked.GetProperty("session").GetProperty("workState").GetString());
        var pending = Assert.Single(parked.GetProperty("pendingQuestions").EnumerateArray());
        Assert.Equal("req-1", pending.GetProperty("requestId").GetString());
        Assert.Equal("Which database?", pending.GetProperty("questions")[0].GetProperty("question").GetString());
        Assert.Equal(provider == "codex", pending.GetProperty("expiresAt").ValueKind == JsonValueKind.Null);

        var (url, question) = await Next(posts);
        Assert.Equal(QuestionUrl, url);
        Assert.Equal("req-1", question.GetProperty("requestId").GetString());
        await AssertNoMore(posts); // once, and the parked turn did not fire the completion

        Assert.Equal(QuestionAnswerResult.Answered, h.Provider.SubmitQuestionAnswer(h.SessionId,
            new SessionQuestionAnswer { RequestId = "req-1", PositionalAnswers = ["Postgres"] }));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "req-1", Content: "answered" });
        Assert.Empty(h.Provider.GetPendingQuestions(h.SessionId));
        var answered = await ReadAsync(h);
        Assert.Empty(answered.GetProperty("pendingQuestions").EnumerateArray());
        Assert.Equal(SessionWorkStates.Working, answered.GetProperty("session").GetProperty("workState").GetString());

        h.EndTurn();
        var idle = await ReadAsync(h);
        Assert.Equal(SessionWorkStates.Idle, idle.GetProperty("session").GetProperty("workState").GetString());
        var (completeUrl, completion) = await Next(posts);
        Assert.Equal(CompleteUrl, completeUrl);
        Assert.Equal("Idle", completion.GetProperty("status").GetString());
        Assert.Equal("operation", completion.GetProperty("callbackId").GetString());
        Assert.Equal(SessionWorkStates.Idle, completion.GetProperty("workState").GetString());
        await AssertNoMore(posts);
        // Codex has no background work concept: it never reports any, so never workState background.
        if (provider == "codex") Assert.Empty(h.Provider.GetBackgroundTasks(h.SessionId));
    }

    [Theory]
    [InlineData("claude-code", "decline", "declined")]
    [InlineData("codex", "decline", "declined")]
    [InlineData("claude-code", "cancel", "cancelled")]
    [InlineData("codex", "cancel", "cancelled")]
    [InlineData("claude-code", "turn_end", "cancelled")]
    [InlineData("codex", "turn_end", "cancelled")]
    [InlineData("claude-code", "session_end", "session_ended")]
    [InlineData("codex", "session_end", "session_ended")]
    public async Task Every_resolution_path_clears_the_question_with_an_outcome_and_no_new_transcript_record(
        string provider, string path, string outcome)
    {
        using var h = Harness(provider);
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        var registry = Registry(h, posts);
        registry.Register(h.SessionId, CompleteUrl, "owner", "operation", questionUrl: QuestionUrl);
        h.Park("req-1");
        Assert.Equal(QuestionUrl, (await Next(posts)).Url);
        var recordsAfterPark = h.PersistedEventTypes();

        switch (path)
        {
            case "decline":
                Assert.Equal(QuestionAnswerResult.Declined, h.Provider.SubmitQuestionAnswer(h.SessionId,
                    new SessionQuestionAnswer { RequestId = "req-1", Decline = true }));
                break;
            case "cancel": h.Cancel("req-1"); break;
            case "turn_end": h.EndTurnWhileParked("req-1"); break;
            case "session_end": await h.Provider.StopSessionAsync(h.SessionId); break;
        }

        Assert.Empty(h.Provider.GetPendingQuestions(h.SessionId));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "req-1" } && e.Content == outcome);
        // Resolved means forgotten: a later opt-in registration has nothing to be told.
        registry.Register(h.SessionId, CompleteUrl, "owner", "later", questionUrl: QuestionUrl);
        await Task.Delay(300);
        while (posts.Reader.TryRead(out var post)) Assert.NotEqual(QuestionUrl, post.Url);

        // Control state, not transcript. The only question records are each provider's own
        // pre-existing ones: Codex persists its question item and an answered/declined result.
        var added = h.PersistedEventTypes().Skip(recordsAfterPark.Count).ToList();
        if (provider == "codex" && path == "decline") Assert.Equal(["question_resolved"], added.Where(IsQuestionRecord));
        else Assert.DoesNotContain(added, IsQuestionRecord);
        Assert.Equal(provider == "codex" ? 1 : 0, recordsAfterPark.Count(t => t == "question"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Registrant_without_question_url_gets_exactly_one_terminal_callback_and_no_question(string provider)
    {
        using var h = Harness(provider);
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        var registry = Registry(h, posts);
        registry.Register(h.SessionId, CompleteUrl, "owner", "operation");

        h.Park("req-1");
        await AssertNoMore(posts);
        h.Provider.SubmitQuestionAnswer(h.SessionId, new SessionQuestionAnswer { RequestId = "req-1", Response = "Postgres" });
        h.EndTurn();

        var (url, completion) = await Next(posts);
        Assert.Equal(CompleteUrl, url);
        Assert.Equal("Idle", completion.GetProperty("status").GetString());
        Assert.Equal("operation", completion.GetProperty("callbackId").GetString());
        Assert.False(completion.TryGetProperty("requestId", out _));
        await AssertNoMore(posts);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Parked_question_blocks_steering_and_the_answer_lets_queued_input_deliver(string provider)
    {
        using var h = Harness(provider);
        h.Park("req-1");
        var refused = await h.Provider.TrySteerInputAsync(h.SessionId, [SessionInputPart.TextPart("steer")]);
        Assert.Equal(SessionInputDeliveryStatus.NotSteerable, refused.Status);
        Assert.Equal("question_parked", refused.ErrorCode);

        var config = new RedComputeConfig();
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        var store = new SessionInputQueueStore(config, attachments);
        var capabilities = new CapabilityRegistry();
        capabilities.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [provider] = h.Backend }, provider);
        var queue = new SessionInputQueueService(store, attachments, capabilities, h.Jobs, new WebSocketBroadcaster(), (_, _) => { },
            deliveryPaused: () => false);
        var admitted = await store.EnqueueAsync(Submission(h.SessionId, "steer"));

        await queue.ProcessSessionAsync(h.SessionId, default);
        var demoted = (await store.GetAsync(h.SessionId, admitted.Item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Pending, demoted.State);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, demoted.DeliveryPolicy);

        h.Provider.SubmitQuestionAnswer(h.SessionId, new SessionQuestionAnswer { RequestId = "req-1", Response = "Postgres" });
        h.EndTurn();
        await queue.ProcessSessionAsync(h.SessionId, default);

        var delivered = (await store.GetAsync(h.SessionId, admitted.Item.Id, "owner"))!;
        Assert.True(delivered.State == SessionInputQueueState.Delivered,
            $"{delivered.State} {delivered.ErrorCode} status={h.Provider.GetSession(h.SessionId).Info!.Status} next={delivered.NextAttemptAt}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("claude-code", "answer", new[] { "working" })]
    [InlineData("codex", "answer", new[] { "working" })]
    [InlineData("claude-code", "decline", new[] { "working" })]
    [InlineData("codex", "decline", new[] { "working" })]
    [InlineData("claude-code", "cancel", new[] { "working" })]
    [InlineData("codex", "cancel", new[] { "idle" })] // an interrupt ends the Codex turn: one update
    [InlineData("claude-code", "turn_end", new[] { "working", "idle" })] // CLI cancel, then result
    [InlineData("codex", "turn_end", new[] { "idle" })]
    public void Parking_and_resolving_publish_exactly_one_session_update_per_transition(string provider, string path, string[] after)
    {
        using var h = Harness(provider);
        var updates = WorkStateUpdates(h);
        var before = updates.Count;

        h.Park("req-1");
        Assert.Equal([SessionWorkStates.AwaitingAnswer], updates.Skip(before));

        var parked = updates.Count;
        switch (path)
        {
            case "answer":
                h.Provider.SubmitQuestionAnswer(h.SessionId, new SessionQuestionAnswer { RequestId = "req-1", PositionalAnswers = ["Postgres"] });
                break;
            case "decline":
                h.Provider.SubmitQuestionAnswer(h.SessionId, new SessionQuestionAnswer { RequestId = "req-1", Decline = true });
                break;
            case "cancel": h.Cancel("req-1"); break;
            case "turn_end": h.EndTurnWhileParked("req-1"); break;
        }
        Assert.Equal(after, updates.Skip(parked));
    }

    // Each broadcast session.updated as RelayServer projects it (SessionEventProjection).
    private static List<string> WorkStateUpdates(ProviderHarness h)
    {
        var updates = new List<string>();
        ((IPluginEventSource)h.Provider).PluginEvent += (type, data) =>
        {
            if (type != "session.updated" || data is not UnifiedSessionInfo session) return;
            SessionEventProjection.ApplyWorkState(session, h.Provider, (_, _) => { });
            lock (updates) updates.Add(session.WorkState!);
        };
        return updates;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Session_read_keeps_messages_and_their_order_while_a_question_is_pending(string provider)
    {
        using var h = Harness(provider);
        var before = await ReadAsync(h);
        h.Park("req-1");
        var parked = await ReadAsync(h);

        Assert.Equal(before.GetProperty("messages").GetRawText(), parked.GetProperty("messages").GetRawText());
        Assert.Equal(before.GetProperty("transcript").GetRawText(), parked.GetProperty("transcript").GetRawText());
        Assert.Single(parked.GetProperty("pendingQuestions").EnumerateArray());
        Assert.DoesNotContain("req-1", parked.GetProperty("messages").GetRawText());
    }

    private static bool IsQuestionRecord(string eventType) => eventType is "question" or "question_resolved";

    private static SessionCallbackRegistry Registry(ProviderHarness h, Channel<(string Url, JsonElement Body)> posts)
    {
        var http = new HttpClient(new Handler(async r =>
        {
            await posts.Writer.WriteAsync((r.RequestUri!.ToString(), JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachQuestionSource(h.Provider.GetPendingQuestions, id => h.Provider.GetSession(id).Info);
        // As RelayServer wires every session provider.
        h.Provider.SessionStreamEvent += registry.OnStreamEvent;
        ((IPluginEventSource)h.Provider).PluginEvent += registry.OnSessionEvent;
        return registry;
    }

    private async Task<JsonElement> ReadAsync(ProviderHarness h)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "read-bytes"), Path.Combine(_root, "read.db"));
        var store = new SessionInputQueueStore(config, attachments);
        var capabilities = new CapabilityRegistry();
        capabilities.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [h.Provider.ProviderId] = h.Backend }, h.Provider.ProviderId);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var queue = new SessionInputQueueService(store, attachments, capabilities, null!, new WebSocketBroadcaster(), (_, _) => { });
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), capabilities, new NullJobs(), (_, _) => { }, config,
            redLeafReader: Reader(h), attachmentStore: attachments, inputQueue: queue);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/ai-session/sessions/{id}"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "signed")),
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = "GET";
        context.Request.RouteValues["id"] = h.SessionId;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return JsonSerializer.Deserialize<JsonElement>(await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    // The suite mirror as RedLeaf would hold it: the provider's current status and one fixed
    // transcript record, so the read's messages are independent of the live question state.
    private static RedLeafSessionReader Reader(ProviderHarness h)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
            new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))),
            Path.Combine(Path.GetTempPath(), $"redcompute-parity-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        var redLeaf = new HttpClient(new Handler(request =>
        {
            var status = h.Provider.GetSession(h.SessionId).Info!.Status.ToString();
            object body = request.RequestUri!.PathAndQuery.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1", name = "Parity",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = h.SessionId, provider = h.Provider.ProviderId, status, user_id = "owner",
                                started_at = "2026-10-24T10:00:00Z",
                            }),
                        },
                    },
                }
                : new
                {
                    items = new[]
                    {
                        new
                        {
                            id = 1L, createdAt = "2026-10-24T10:00:01Z",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = h.SessionId, role = "user", event_type = "text", content = "go",
                                epoch = "epoch-1", sequence = 1L, timestamp = "2026-10-24T10:00:01Z",
                            }),
                        },
                    },
                };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        })) { BaseAddress = new Uri("http://redleaf/") };
        return new RedLeafSessionReader(redLeaf, quality);
    }

    private static SessionInputQueueSubmission Submission(string sessionId, string text) => new(
        sessionId, "owner", [new("text", text)], text, null,
        new(JobProvenance.CurrentSchemaVersion,
            new("redcompute", new("app", "nova", null, "Nova"), new("http", "/accepted", "POST")),
            new("user", "Owner", Id: "owner"), new("user", "owner", "Owner"), [], new(),
            JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
        SessionInputDeliveryPolicy.SteerCurrent, "m_" + Guid.NewGuid().ToString("N"), [], Guid.NewGuid().ToString("N"));

    private static async Task<(string Url, JsonElement Body)> Next(Channel<(string Url, JsonElement Body)> posts)
        => await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task AssertNoMore(Channel<(string Url, JsonElement Body)> posts)
    {
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out var extra), $"Unexpected callback to {extra.Url}: {extra.Body}");
    }

    private ProviderHarness Harness(string provider) => provider == "codex" ? new CodexHarness(this) : new ClaudeHarness(this);

    private Process StartProcess(ProcessStartInfo start)
    {
        start.CreateNoWindow = true;
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = Process.Start(start)!;
        lock (_processes) _processes.Add(process);
        return process;
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static JsonElement QuestionsJson() => JsonSerializer.SerializeToElement(new[]
    {
        new
        {
            id = "q-db", header = "Database", question = "Which database?", multiSelect = false,
            options = new[] { new { label = "Postgres", description = "Shared" }, new { label = "SQLite", description = "Local" } },
        },
    });

    private abstract class ProviderHarness : IDisposable
    {
        private readonly List<UnifiedStreamEvent> _events = [];
        public abstract ISessionProvider Provider { get; }
        public IJobTracker Jobs { get; } = new ClaudeBackgroundWorkTests.MemoryJobTracker();
        public IBackendProvider Backend => (IBackendProvider)Provider;
        public abstract string SessionId { get; }
        public IReadOnlyList<UnifiedStreamEvent> Events { get { lock (_events) return [.. _events]; } }
        protected void Observe() => Provider.SessionStreamEvent += (_, e) => { lock (_events) _events.Add(e); };
        public abstract void Park(string requestId);
        public abstract void Cancel(string requestId);
        public abstract void EndTurn();
        public abstract void EndTurnWhileParked(string requestId);
        public abstract List<string> PersistedEventTypes();
        public virtual void Dispose() { }
    }

    private sealed class ClaudeHarness : ProviderHarness
    {
        private readonly ClaudeSessionService _service;
        private readonly ClaudeBackgroundWorkTests.RecordingStore _store = new();
        private readonly ClaudeCodeProvider _provider;
        private readonly string _id;

        public ClaudeHarness(SessionQuestionParityTests owner)
        {
            var claudePath = Path.Combine(owner._root, "claude.exe");
            if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
            var jobs = Jobs;
            _service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, jobs, _store,
                new ClaudeBackgroundWorkTests.NullQualityModes(), () => null, (_, _) => { }, action => action());
            _service.LaunchSessionProcess = _ => owner.StartProcess(new ProcessStartInfo
                { FileName = Path.Combine(Environment.SystemDirectory, "more.com") });
            _provider = new ClaudeCodeProvider("ai-session", _service, _store, jobs, (_, _) => { });
            Observe();
            var info = _service.StartSession(owner._root, "haiku", "owner", null, null, "high", null, null, null,
                JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
            _id = info!.Id;
            Assert.True(_service.SendInput(_id, [SessionInputPart.TextPart("go")], messageUid: "user-1").GetAwaiter().GetResult());
        }

        public override ISessionProvider Provider => _provider;
        public override string SessionId => _id;

        public override void Park(string requestId) => Feed(JsonSerializer.Serialize(new
        {
            type = "control_request", request_id = requestId,
            request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", tool_use_id = "toolu_ask",
                input = new { questions = QuestionsJson() } },
        }));
        public override void Cancel(string requestId)
            => Feed($$"""{"type":"control_cancel_request","request_id":"{{requestId}}"}""");
        public override void EndTurn()
            => Feed("""{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.01}""");
        // The CLI never ends a turn over a live can_use_tool request: it cancels the request first.
        public override void EndTurnWhileParked(string requestId)
        {
            Cancel(requestId);
            EndTurn();
        }
        public override List<string> PersistedEventTypes() => _store.Messages.Select(m => m.EventType).ToList();
        private void Feed(string line) => Assert.True(_service.ProcessOutputLine(_id, line));
    }

    private sealed class CodexHarness : ProviderHarness
    {
        private const string Responder =
            "while ($null -ne ($l = [Console]::In.ReadLine())) { try { $m = $l | ConvertFrom-Json } catch { continue }; " +
            "if ($m.method -and $null -ne $m.id) { [Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":' + ($m.id | ConvertTo-Json -Compress) + " +
            "',\"result\":{\"turn\":{\"id\":\"turn-2\"}}}'); [Console]::Out.Flush() } }";
        private readonly CodexInteractiveService _service;
        private readonly CodexStore _store = new();
        private readonly CodexProvider _provider;
        private readonly object _session;
        private readonly Type _sessionType;
        private int _requestIds;

        public CodexHarness(SessionQuestionParityTests owner)
        {
            _service = new CodexInteractiveService(new CodexConfig(), _store, null!, null!, null!, null!, null!, () => null, (_, _) => { });
            _provider = new CodexProvider("ai-session", _service, Jobs, (_, _) => { });
            Observe();
            _sessionType = typeof(CodexInteractiveService).GetNestedType("ManagedSession", BindingFlags.NonPublic)!;
            _session = Activator.CreateInstance(_sessionType, nonPublic: true)!;
            _sessionType.GetProperty("Info")!.SetValue(_session, new CodexSessionInfo
            {
                Id = "codex-session", ProjectName = "Test", ProjectPath = ".", ThreadId = "thread-1", Status = "Active",
                UserId = "owner", Title = "Parity",
            });
            var start = new ProcessStartInfo { FileName = "powershell.exe" };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(Responder);
            var process = owner.StartProcess(start);
            var connection = (CodexAppServerConnection)Activator.CreateInstance(typeof(CodexAppServerConnection),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [process, (Action<string, Guid?>)((_, _) => { })], null)!;
            var read = typeof(CodexAppServerConnection).GetMethod("ReadStdoutAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            _ = Task.Run(() => (Task)read.Invoke(connection, null)!);
            _sessionType.GetProperty("Connection")!.SetValue(_session, connection);
            var sessions = typeof(CodexInteractiveService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["codex-session", _session]);
            Notify("turn/started", new { turn = new { id = "turn-1" } });
        }

        public override ISessionProvider Provider => _provider;
        public override string SessionId => "codex-session";

        public override void Park(string requestId)
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                threadId = "thread-1", turnId = "turn-1", itemId = requestId, questions = QuestionsJson(),
            }));
            using var id = JsonDocument.Parse((++_requestIds).ToString());
            typeof(CodexInteractiveService).GetMethod("HandleUserInputRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, [_session, id.RootElement, request.RootElement]);
        }
        // Codex has no separate request cancellation: an interrupt ends the turn the question was in.
        public override void Cancel(string requestId) => CompleteTurn("interrupted");
        public override void EndTurn() => CompleteTurn("completed");
        public override void EndTurnWhileParked(string requestId) => CompleteTurn("completed");
        public override List<string> PersistedEventTypes() => _store.Messages.Select(m => m.EventType).ToList();

        private void CompleteTurn(string status) => Notify("turn/completed", new
        {
            turn = new { status, items = new[] { new { type = "agentMessage", phase = "final_answer", text = "done" } } },
        });

        private void Notify(string method, object payload)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            typeof(CodexInteractiveService).GetMethod("OnNotification", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, ["codex-session", method, json.RootElement]);
        }
    }

    private sealed class CodexStore : ICodexSessionStore
    {
        private readonly List<CodexMessageRecord> _messages = [];
        public List<CodexMessageRecord> Messages { get { lock (_messages) return [.. _messages]; } }
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public void SaveSession(CodexSessionRecord record) { }
        public CodexSessionRecord? FindSession(string sessionId) => null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { lock (_messages) _messages.Add(message); }
        public void AddMessages(List<CodexMessageRecord> messages) { lock (_messages) _messages.AddRange(messages); }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => Messages;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class NullJobs : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
