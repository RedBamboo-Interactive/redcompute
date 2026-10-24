using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// Incident 7f614fcd534a (2026-10-24): prompt 13344 completed, prompts 13355 and 13378 completed as
// one batch, then the Claude CLI started a turn of its own (a peer message woke it) and parked on a
// question at 18:26. Every prompt-scoped row had completed, so nobody was told. A question opt-in
// is now a session-scoped subscription: it keeps receiving questions, and it reports once the end
// of any turn the provider started without a queued input.
public sealed class SessionUnpromptedTurnTests
{
    private const string CompleteUrl = "http://localhost/complete";
    private const string QuestionUrl = "http://localhost/question";

    [Fact]
    public async Task Turn_started_after_every_prompt_completed_notifies_its_question_and_reports_its_end_once()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");                       // 13344: its own turn
        await h.DeliverAsync("b", "c");                  // 13355 + 13378: one batch, one turn
        Assert.Equal(3, h.Posts.Count);
        Assert.All(h.Posts, p => Assert.Equal(CompleteUrl, p.Url));
        Assert.Equal(["a", "b", "b"], h.Posts.Select(p => p.Body.GetProperty("deliveredMessageUid").GetString()));
        h.Posts.Clear();

        // The provider starts a turn with nothing queued.
        await h.ActiveAsync();
        Assert.Empty(h.Posts);
        h.Pending.Add(SessionQuestionCallbackTests.Question("00bc1c7d"));
        await h.Callbacks.QuestionsChangedAsync("session");
        await h.Callbacks.QuestionsChangedAsync("session");
        await h.Callbacks.DrainAsync();
        var (questionUrl, question) = Assert.Single(h.Posts);
        Assert.Equal(QuestionUrl, questionUrl);
        Assert.Equal("00bc1c7d", question.GetProperty("requestId").GetString());
        h.Posts.Clear();

        // Answered; the turn ends.
        h.Pending.Clear();
        await h.Callbacks.QuestionResolvedAsync("session", "00bc1c7d");
        await h.IdleAsync();
        var (url, completion) = Assert.Single(h.Posts);
        Assert.Equal(CompleteUrl, url);
        Assert.Equal("unprompted", completion.GetProperty("trigger").GetString());
        Assert.Equal(1, completion.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("Idle", completion.GetProperty("status").GetString());
        Assert.StartsWith("session-subscription:", completion.GetProperty("callbackId").GetString());
        Assert.Equal(JsonValueKind.Null, completion.GetProperty("deliveredMessageUid").ValueKind);
        Assert.Equal(JsonValueKind.Null, completion.GetProperty("promptMessageUid").ValueKind);

        // Nothing more for that turn, however often it is observed; the next one is distinct.
        await h.Callbacks.SweepAsync(default); await h.IdleAsync();
        Assert.Single(h.Posts);
        await h.ActiveAsync(); await h.IdleAsync();
        Assert.Equal(2, h.Posts.Count);
        Assert.Equal(2, h.Posts[1].Body.GetProperty("deliverySequence").GetInt32());
        Assert.Equal(completion.GetProperty("callbackId").GetString(), h.Posts[1].Body.GetProperty("callbackId").GetString());
    }

    [Fact]
    public async Task Registrant_without_a_question_url_sees_exactly_what_it_saw_before()
    {
        using var h = new Harness(questionUrl: null);
        await h.DeliverAsync("a");
        await h.DeliverAsync("b", "c");
        Assert.Equal(3, h.Posts.Count);
        h.Posts.Clear();

        await h.ActiveAsync();
        h.Pending.Add(SessionQuestionCallbackTests.Question("q"));
        await h.Callbacks.QuestionsChangedAsync("session");
        h.Pending.Clear();
        await h.IdleAsync();
        await h.Callbacks.DrainAsync();
        Assert.Empty(h.Posts);
        Assert.DoesNotContain("session", h.RowKinds());
    }

    [Fact]
    public async Task Session_end_clears_the_subscription_and_a_resumable_stop_keeps_it()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        h.Posts.Clear();

        // A maintenance restart stops the session for a resume: the subscription stays.
        h.F.Runtime.Info.Status = SessionStatus.Stopped; h.F.Runtime.Info.StopReason = "maintenance_restart";
        await h.Callbacks.ObserveAsync("session", endedReason: "stopped", endedStopReason: "maintenance_restart");
        Assert.Contains("session", h.RowKinds());

        // An explicit stop ends it for good.
        h.F.Runtime.Info.StopReason = "user_stopped";
        await h.Callbacks.ObserveAsync("session", endedReason: "stopped", endedStopReason: "user_stopped");
        Assert.DoesNotContain("session", h.RowKinds());
        h.F.Runtime.Info.StopReason = null;
        await h.ActiveAsync(); await h.IdleAsync();
        Assert.Empty(h.Posts);
    }

    [Fact]
    public async Task Turn_running_when_the_session_ends_is_reported_once_as_ended()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        h.Posts.Clear();

        await h.ActiveAsync();
        h.F.Runtime.Info.Status = SessionStatus.Stopped; h.F.Runtime.Info.StopReason = "user_stopped";
        await h.Callbacks.ObserveAsync("session", endedReason: "stopped", endedStopReason: "user_stopped");
        await h.Callbacks.DrainAsync();
        var (_, ended) = Assert.Single(h.Posts);
        Assert.Equal("unprompted", ended.GetProperty("trigger").GetString());
        Assert.Equal("Ended", ended.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Restart_preserves_the_subscription()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        h.Posts.Clear();

        h.Reopen();
        await h.ActiveAsync();
        h.Pending.Add(SessionQuestionCallbackTests.Question("q"));
        await h.Callbacks.QuestionsChangedAsync("session");
        await h.Callbacks.DrainAsync();
        h.Pending.Clear();
        await h.IdleAsync();
        Assert.Equal([QuestionUrl, CompleteUrl], h.Posts.Select(p => p.Url));
        Assert.Equal("unprompted", h.Posts[1].Body.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task Re_registering_the_same_question_url_keeps_one_subscription()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        await h.DeliverAsync("b");
        await h.DeliverAsync("c");
        h.Posts.Clear();
        Assert.Single(h.RowKinds(), kind => kind == "session");

        await h.ActiveAsync(); await h.IdleAsync();
        Assert.Single(h.Posts);
    }

    [Fact]
    public async Task Input_that_joins_an_unprompted_turn_reports_it_through_its_own_registration()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        h.Posts.Clear();

        await h.ActiveAsync();
        // Steered in while the self-started turn runs; it owns the report of that turn.
        await h.F.Store.EnqueueAsync(h.F.Submission("d"));
        await h.Callbacks.RegisterAsync("session", "d", CompleteUrl, "owner", "d", QuestionUrl);
        var batch = await h.F.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await h.Callbacks.ObserveAsync("session", observedActive: true);
        await h.F.Store.MarkDeliveredAsync(batch, "d");
        await h.IdleAsync();
        var (_, completion) = Assert.Single(h.Posts);
        Assert.Equal("d", completion.GetProperty("deliveredMessageUid").GetString());
        Assert.False(completion.TryGetProperty("trigger", out _));
    }

    [Fact]
    public async Task Prompt_from_a_caller_without_a_registration_is_not_unprompted()
    {
        using var h = new Harness();
        await h.DeliverAsync("a");
        h.Posts.Clear();

        // Someone (Code UI, another discussion) queues a prompt without a completion callback.
        await h.F.Store.EnqueueAsync(h.F.Submission("ui"));
        var batch = await h.F.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await h.ActiveAsync();
        await h.F.Store.MarkDeliveredAsync(batch, "ui");
        await h.IdleAsync();
        Assert.Empty(h.Posts);
    }

    [Fact]
    public async Task Turn_woken_for_an_armed_registration_is_reported_by_it_alone()
    {
        using var h = new Harness();
        await h.F.Store.EnqueueAsync(h.F.Submission("a"));
        await h.Callbacks.RegisterAsync("session", "a", CompleteUrl, "owner", "a", QuestionUrl);
        var batch = await h.F.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await h.ActiveAsync();
        await h.F.Store.MarkDeliveredAsync(batch, "a");
        h.F.Runtime.Info.BackgroundTasks = [new BackgroundTaskInfo { TaskId = "b1", Kind = "agent", Status = "running" }];
        await h.IdleAsync();
        Assert.Equal(1, Assert.Single(h.Posts).Body.GetProperty("backgroundTasksRunning").GetInt32());

        // The background work wakes a turn; the armed registration reports it, nothing else does.
        h.F.Runtime.Info.BackgroundTasks = [new BackgroundTaskInfo { TaskId = "b1", Kind = "agent", Status = "completed" }];
        await h.ActiveAsync(); await h.IdleAsync();
        Assert.Equal(2, h.Posts.Count);
        Assert.Equal(2, h.Posts[1].Body.GetProperty("deliverySequence").GetInt32());
        Assert.False(h.Posts[1].Body.TryGetProperty("trigger", out _));
    }

    [Fact]
    public async Task Registry_reports_the_unprompted_turn_from_session_events()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        using var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync((r.RequestUri!.ToString(), JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())));
            return new(HttpStatusCode.OK);
        }));
        var pending = new List<PendingSessionQuestion>();
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachDurableStore(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info);
        registry.AttachQuestionSource(_ => [.. pending], _ => f.Runtime.Info);
        // An already-finished in-memory registration opts in: the subscription outlives it.
        Assert.False(registry.RegisterIfStillActive("session", CompleteUrl, SessionStatus.Idle, "owner", questionUrl: QuestionUrl));
        var first = await Next(posts);
        Assert.Equal(CompleteUrl, first.Url);

        f.Runtime.Info.Status = SessionStatus.Active;
        registry.OnSessionEvent("session.updated", f.Runtime.Info);
        await Task.Delay(200);
        pending.Add(SessionQuestionCallbackTests.Question("q"));
        registry.OnStreamEvent("session", new UnifiedStreamEvent { Type = "question", RequestId = "q" });
        Assert.Equal(QuestionUrl, (await Next(posts)).Url);
        pending.Clear();
        registry.OnStreamEvent("session", new UnifiedStreamEvent { Type = "question_resolved", RequestId = "q" });
        await Task.Delay(200);
        f.Runtime.Info.Status = SessionStatus.Idle;
        registry.OnSessionEvent("session.updated", f.Runtime.Info);
        var (url, body) = await Next(posts);
        Assert.Equal(CompleteUrl, url);
        Assert.Equal("unprompted", body.GetProperty("trigger").GetString());
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out _));
    }

    private static async Task<(string Url, JsonElement Body)> Next(Channel<(string Url, JsonElement Body)> posts)
        => await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    // Drives one session through the durable outbox the way the provider and queue do.
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string? _questionUrl;
        public SessionRecoveryTests.Fixture F { get; } = new("maintenance_restart");
        public List<(string Url, JsonElement Body)> Posts { get; } = [];
        public List<PendingSessionQuestion> Pending { get; } = [];
        public SessionPromptCallbacks Callbacks { get; private set; }

        public Harness(string? questionUrl = QuestionUrl)
        {
            _questionUrl = questionUrl;
            F.Runtime.Info.Status = SessionStatus.Idle; F.Runtime.Info.StopReason = null;
            _http = new HttpClient(new Handler(async r => {
                var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
                lock (Posts) Posts.Add((r.RequestUri!.ToString(), body));
                return new(HttpStatusCode.OK);
            }));
            Callbacks = Create();
        }

        private SessionPromptCallbacks Create() => new(F.Attachments.DatabasePath, F.Store, _ => F.Runtime.Info, _http)
            { PendingQuestions = _ => [.. Pending] };

        public void Reopen() => Callbacks = Create();

        /// <summary>Queues and registers the prompts, delivers them as one turn, and lets it end.</summary>
        public async Task DeliverAsync(params string[] uids)
        {
            foreach (var uid in uids)
            {
                await F.Store.EnqueueAsync(F.Submission(uid));
                await Callbacks.RegisterAsync("session", uid, CompleteUrl, "owner", uid, _questionUrl);
            }
            var batch = await F.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
            Assert.Equal(uids, batch.Select(item => item.MessageUid));
            await ActiveAsync();
            await F.Store.MarkDeliveredAsync(batch, uids[0]);
            await IdleAsync();
        }

        public async Task ActiveAsync()
        {
            F.Runtime.Info.Status = SessionStatus.Active;
            await Callbacks.ObserveAsync("session", observedActive: true);
            await Callbacks.DrainAsync();
        }

        public async Task IdleAsync()
        {
            F.Runtime.Info.Status = SessionStatus.Idle;
            await Callbacks.ObserveAsync("session");
            await Callbacks.DrainAsync();
        }

        public string?[] RowKinds() => new SessionCallbackStore(F.Attachments.DatabasePath).Load()
            .Select(row => JsonDocument.Parse(row.Json).RootElement.TryGetProperty("Kind", out var kind) ? kind.GetString() : null)
            .ToArray();

        public void Dispose() { _http.Dispose(); F.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
