using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// A turn can end Idle while its background work (a subagent, a background shell) keeps running;
// that work later wakes the session for another turn. Reporting the first Idle as the end of the
// delegation left nobody told when the woken turn finished. The turn is still reported, with
// backgroundTasksRunning, and the registration re-arms for the woken turn under a distinct
// deliverySequence. Idle with nothing running behaves exactly as before.
public sealed class SessionBackgroundCallbackTests
{
    private const string CompleteUrl = "http://localhost/complete";

    [Fact]
    public async Task In_memory_registration_reports_the_turn_and_rearms_for_the_woken_turn()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        using var http = Http(posts);
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.Register("session", CompleteUrl, "owner", "operation");

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "running"), Background("b0", "completed")));
        var first = await Next(posts);
        Assert.Equal("Idle", first.GetProperty("status").GetString());
        Assert.Equal("operation", first.GetProperty("callbackId").GetString());
        Assert.Equal(1, first.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(1, first.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("background", first.GetProperty("workState").GetString());

        // Background progress and the task finishing re-publish Idle; neither is the woken turn.
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "running")));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "completed")));
        await AssertNoMore(posts);

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Active, Background("b1", "completed")));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "completed")));
        var woken = await Next(posts);
        Assert.Equal("Idle", woken.GetProperty("status").GetString());
        Assert.Equal("operation", woken.GetProperty("callbackId").GetString());
        Assert.Equal(0, woken.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(2, woken.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("idle", woken.GetProperty("workState").GetString());

        // Consumed now, exactly as a plain completion is.
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Active));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle));
        await AssertNoMore(posts);
    }

    [Fact]
    public async Task In_memory_retry_repeats_the_first_delivery_and_the_next_turn_stays_distinct()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        var calls = 0;
        using var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync()));
            return new(Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.Register("session", CompleteUrl, "owner", "operation");

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "running")));
        var first = await Next(posts);
        var retry = await Next(posts);
        Assert.Equal(first.GetRawText(), retry.GetRawText());

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Active));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle));
        var next = await Next(posts);
        Assert.Equal(first.GetProperty("callbackId").GetString(), next.GetProperty("callbackId").GetString());
        Assert.NotEqual(first.GetProperty("deliverySequence").GetInt32(), next.GetProperty("deliverySequence").GetInt32());
    }

    [Fact]
    public async Task In_memory_session_end_after_a_background_turn_is_distinct_and_final()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        using var http = Http(posts);
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.Register("session", CompleteUrl, "owner", "operation");
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "running")));
        await Next(posts);

        registry.OnSessionEvent("session.ended", new { id = "session", reason = "stopped", stopReason = "user_stopped" });
        var ended = await Next(posts);
        Assert.Equal("Ended", ended.GetProperty("status").GetString());
        Assert.Equal(2, ended.GetProperty("deliverySequence").GetInt32());
    }

    [Fact]
    public async Task In_memory_turn_never_woken_reports_once_when_the_session_settles()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        using var http = Http(posts);
        var current = Info(SessionStatus.Idle, Background("b1", "running"));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachQuestionSource(_ => [], _ => current);
        registry.Register("session", CompleteUrl, "owner", "operation");

        registry.OnSessionEvent("session.updated", current);
        Assert.Equal(1, (await Next(posts)).GetProperty("deliverySequence").GetInt32());

        // Settling while the work still runs, or while a turn runs, reports nothing.
        registry.OnSessionSettled("session");
        current = Info(SessionStatus.Active, Background("b1", "stopped"));
        registry.OnSessionSettled("session");
        await AssertNoMore(posts);

        // The task was stopped and the provider never woke a turn.
        current = Info(SessionStatus.Idle, Background("b1", "stopped"));
        registry.OnSessionEvent("session.updated", current);
        await AssertNoMore(posts);
        registry.OnSessionSettled("session");
        var final = await Next(posts);
        Assert.Equal("Idle", final.GetProperty("status").GetString());
        Assert.Equal("operation", final.GetProperty("callbackId").GetString());
        Assert.Equal(2, final.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("idle", final.GetProperty("workState").GetString());

        registry.OnSessionSettled("session");
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Active));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle));
        await AssertNoMore(posts);
    }

    [Fact]
    public async Task In_memory_woken_turn_reports_once_and_a_later_settle_adds_nothing()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        using var http = Http(posts);
        var current = Info(SessionStatus.Idle, Background("b1", "running"));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachQuestionSource(_ => [], _ => current);
        registry.Register("session", CompleteUrl, "owner", "operation");
        registry.OnSessionEvent("session.updated", current);
        await Next(posts);

        current = Info(SessionStatus.Active, Background("b1", "completed"));
        registry.OnSessionEvent("session.updated", current);
        current = Info(SessionStatus.Idle, Background("b1", "completed"));
        registry.OnSessionEvent("session.updated", current);
        Assert.Equal(2, (await Next(posts)).GetProperty("deliverySequence").GetInt32());

        registry.OnSessionSettled("session");
        await AssertNoMore(posts);
    }

    [Fact]
    public async Task In_memory_idle_without_running_background_work_keeps_the_classic_payload()
    {
        var posts = Channel.CreateUnbounded<JsonElement>();
        using var http = Http(posts);
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.Register("session", CompleteUrl, "owner", "operation");

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle, Background("b1", "completed"), Background("b2", "lost")));
        var payload = await Next(posts);
        // The classic fields, unchanged and in order, plus the additive workState and pendingInputs.
        Assert.Equal(["sessionId", "callbackId", "status", "projectPath", "title", "reason", "userId", "stopReason", "workState", "pendingInputs"],
            payload.EnumerateObject().Select(p => p.Name));
        Assert.Equal("idle", payload.GetProperty("workState").GetString());
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Active));
        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle));
        await AssertNoMore(posts);
    }

    [Fact]
    public async Task Durable_subscription_reports_the_turn_and_rearms_for_the_woken_turn()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var (callbacks, payloads, batch) = await DeliveredPrompt(f, HttpStatusCode.OK);

        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [Background("b1", "running")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        var first = Assert.Single(payloads);
        Assert.Equal("Idle", first.GetProperty("status").GetString());
        Assert.Equal("prompt", first.GetProperty("callbackId").GetString());
        Assert.Equal("prompt", first.GetProperty("deliveredMessageUid").GetString());
        Assert.True(first.GetProperty("executionObserved").GetBoolean());
        Assert.Equal(1, first.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(1, first.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("background", first.GetProperty("workState").GetString());
        Assert.Single(new SessionCallbackStore(f.Attachments.DatabasePath).Load());

        // Idle sweeps, including the task finishing before the wake, do not report again.
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        f.Runtime.Info.BackgroundTasks = [Background("b1", "completed")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Single(payloads);
        // A restart in between keeps the re-armed subscription.
        callbacks = new(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Http(payloads));

        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count);
        var woken = payloads[1];
        Assert.Equal("Idle", woken.GetProperty("status").GetString());
        Assert.Equal("prompt", woken.GetProperty("callbackId").GetString());
        Assert.Equal(0, woken.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(2, woken.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("idle", woken.GetProperty("workState").GetString());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
        _ = batch;
    }

    [Fact]
    public async Task Durable_lost_acknowledgement_retries_the_identical_first_delivery_before_the_next_turn()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, payloads, _) = await DeliveredPrompt(f, HttpStatusCode.BadGateway, () => now);

        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [Background("b1", "running")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        now = now.AddSeconds(31);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count);
        Assert.Equal(payloads[0].GetRawText(), payloads[1].GetRawText());

        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(3, payloads.Count);
        Assert.Equal(2, payloads[2].GetProperty("deliverySequence").GetInt32());
    }

    [Fact]
    public async Task Durable_idle_without_running_background_work_keeps_the_classic_payload()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var (callbacks, payloads, _) = await DeliveredPrompt(f, HttpStatusCode.OK);

        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [Background("b1", "stopped")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        var payload = Assert.Single(payloads);
        Assert.False(payload.TryGetProperty("backgroundTasksRunning", out _));
        Assert.False(payload.TryGetProperty("deliverySequence", out _));
        Assert.Equal("idle", payload.GetProperty("workState").GetString());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task Durable_turn_never_woken_reports_once_when_the_session_settles()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var (callbacks, payloads, _) = await DeliveredPrompt(f, HttpStatusCode.OK);
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [Background("b1", "running")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Single(payloads);

        // Settled while the work still runs: nothing.
        await callbacks.ObserveAsync("session", settled: true); await callbacks.DrainAsync();
        Assert.Single(payloads);

        // Stopped, never woken. Ordinary sweeps keep waiting; the settled signal reports.
        f.Runtime.Info.BackgroundTasks = [Background("b1", "stopped")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Single(payloads);
        await callbacks.ObserveAsync("session", settled: true); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count);
        var final = payloads[1];
        Assert.Equal("Idle", final.GetProperty("status").GetString());
        Assert.Equal("prompt", final.GetProperty("callbackId").GetString());
        Assert.Equal(2, final.GetProperty("deliverySequence").GetInt32());
        Assert.Equal("idle", final.GetProperty("workState").GetString());
        Assert.False(final.GetProperty("executionObserved").GetBoolean());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());

        await callbacks.ObserveAsync("session", settled: true); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count);
    }

    [Fact]
    public async Task Durable_settle_never_completes_a_prompt_that_was_not_delivered()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Http(payloads));
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt"); await callbacks.DrainAsync();

        // A queued, never-delivered prompt on an idle session is not a finished turn.
        await callbacks.ObserveAsync("session", settled: true); await callbacks.DrainAsync();
        Assert.Empty(payloads);
    }

    // Several subscriptions resolved by one batched turn each keep their own BackgroundDeliveries,
    // yet every member reports the same deliverySequence for the turn and for the woken turn, so
    // the receiver can key on (deliveredMessageUid, deliverySequence).
    [Fact]
    public async Task Durable_batched_background_turn_reports_one_sequence_across_members()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        var prompts = new[] { "p1", "p2", "p3" };
        foreach (var uid in prompts) await f.Store.EnqueueAsync(f.Submission(uid));
        var payloads = new List<JsonElement>();
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Http(payloads));
        foreach (var uid in prompts) await callbacks.RegisterAsync("session", uid, CompleteUrl, "owner", uid);
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session"); await f.Store.MarkDeliveredAsync(batch, "p1");

        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = [Background("b1", "running")];
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(3, payloads.Count);
        Assert.All(payloads, p => Assert.Equal(("p1", 1), (p.GetProperty("deliveredMessageUid").GetString(), p.GetProperty("deliverySequence").GetInt32())));

        f.Runtime.Info.Status = SessionStatus.Active; f.Runtime.Info.BackgroundTasks = [Background("b1", "completed")];
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(6, payloads.Count);
        Assert.All(payloads.Skip(3), p => Assert.Equal(("p1", 2), (p.GetProperty("deliveredMessageUid").GetString(), p.GetProperty("deliverySequence").GetInt32())));
        Assert.Equal(prompts, payloads.Skip(3).Select(p => p.GetProperty("callbackId").GetString()!).Order());
    }

    [Fact]
    public async Task In_memory_payload_counts_pending_inputs_once_for_every_attempt()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        await f.Store.EnqueueAsync(f.Submission("queued-1"));
        await f.Store.EnqueueAsync(f.Submission("queued-2"));
        var posts = Channel.CreateUnbounded<string>(); var calls = 0;
        using var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync(await r.Content!.ReadAsStringAsync());
            if (Interlocked.Increment(ref calls) > 1) return new(HttpStatusCode.OK);
            // The queue grows while the first attempt is in flight; the retry must not notice.
            await f.Store.EnqueueAsync(f.Submission("queued-3"));
            return new(HttpStatusCode.BadGateway);
        }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachDurableStore(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info);
        registry.Register("session", CompleteUrl, "owner", "operation");

        registry.OnSessionEvent("session.updated", Info(SessionStatus.Idle));
        var first = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(first).GetProperty("pendingInputs").GetInt32());
        Assert.Equal(first, retry);
    }

    // Registers an exact-prompt subscription and drives its prompt through observed delivery, so the
    // next Idle observation is the prompt's own turn ending.
    private static async Task<(SessionPromptCallbacks, List<JsonElement>, object)> DeliveredPrompt(
        SessionRecoveryTests.Fixture f, HttpStatusCode firstStatus, Func<DateTimeOffset>? now = null)
    {
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        var calls = 0;
        var http = new HttpClient(new Handler(async r => {
            var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
            lock (payloads) payloads.Add(body);
            return new(++calls == 1 ? firstStatus : HttpStatusCode.OK);
        }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: now);
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt"); await callbacks.DrainAsync();
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session"); await callbacks.DrainAsync();
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        Assert.Empty(payloads);
        return (callbacks, payloads, batch);
    }

    private static BackgroundTaskInfo Background(string id, string status) => new() { TaskId = id, Kind = "shell", Status = status };

    private static UnifiedSessionInfo Info(SessionStatus status, params BackgroundTaskInfo[] tasks) => new()
    {
        Id = "session", Provider = "test", ProjectName = "test", ProjectPath = "test", Status = status, BackgroundTasks = tasks,
    };

    private static async Task<JsonElement> Next(Channel<JsonElement> posts)
        => await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task AssertNoMore(Channel<JsonElement> posts)
    {
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out _));
    }

    private static HttpClient Http(Channel<JsonElement> posts) => new(new Handler(async r => {
        await posts.Writer.WriteAsync(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync()));
        return new(HttpStatusCode.OK);
    }));

    private static HttpClient Http(List<JsonElement> payloads) => new(new Handler(async r => {
        var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
        lock (payloads) payloads.Add(body);
        return new(HttpStatusCode.OK);
    }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
