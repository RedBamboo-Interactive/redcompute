using System.Net;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// 2026-11-11 (fca95f0f / 80570766). Two framework costs on delegated Code workers:
// (1) a restart that cut a worker mid-turn left it Stopped until its supervisor wrote a continuation
//     by hand; RestartContinuations now continues it once, under the delegated prompt's own authority;
// (2) a worker woken by a Monitor every minute turned each woken turn into one discussion event;
//     background-turn reports are now coalesced to one per window, while completions, pauses and
//     questions still go out at once.
public sealed class RestartContinuityTests
{
    private const string CompleteUrl = "http://localhost/complete";
    private const string QuestionUrl = "http://localhost/question";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    // ---- callback coalescing ----

    [Fact]
    public async Task Background_turn_reports_coalesce_inside_the_window_and_the_completion_goes_out_at_once()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);

        await EndTurn(f, callbacks, Running());
        Assert.Equal(1, Assert.Single(Completions(posts)).GetProperty("deliverySequence").GetInt32());

        // Five Monitor wakes inside the window: each turn ends with the work still running.
        for (var i = 0; i < 5; i++)
        {
            now = now.AddMinutes(1);
            await WakeAndEnd(f, callbacks, Running());
        }
        Assert.Single(Completions(posts));

        // The real end: the work is done and the woken turn ends with nothing running.
        now = now.AddMinutes(1);
        await WakeAndEnd(f, callbacks);
        var completions = Completions(posts);
        Assert.Equal(2, completions.Count);
        var final = completions[1];
        Assert.Equal("Idle", final.GetProperty("status").GetString());
        Assert.Equal("idle", final.GetProperty("workState").GetString());
        Assert.Equal(0, final.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(5, final.GetProperty("coalescedTurns").GetInt32());
        Assert.NotEqual(1, final.GetProperty("deliverySequence").GetInt32());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task One_trailing_report_when_the_window_ends_with_the_session_still_in_the_background()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        await EndTurn(f, callbacks, Running());
        now = now.AddMinutes(2);
        await WakeAndEnd(f, callbacks, Running());
        now = now.AddMinutes(2);
        await WakeAndEnd(f, callbacks, Running());
        Assert.Single(Completions(posts));

        // Still inside the window: the sweep sends nothing.
        now = now.AddMinutes(20);
        await Sweep(callbacks);
        Assert.Single(Completions(posts));

        now = now.AddMinutes(7); // 31 minutes after the first report
        await Sweep(callbacks);
        await Sweep(callbacks);
        var completions = Completions(posts);
        Assert.Equal(2, completions.Count);
        var trailing = completions[1];
        Assert.Equal("background", trailing.GetProperty("workState").GetString());
        Assert.Equal(1, trailing.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal(2, trailing.GetProperty("coalescedTurns").GetInt32());
        Assert.NotEqual(completions[0].GetProperty("deliverySequence").GetInt32(), trailing.GetProperty("deliverySequence").GetInt32());

        // The next window starts from the trailing report; the completion still arrives at once.
        now = now.AddMinutes(1);
        await WakeAndEnd(f, callbacks, Running());
        Assert.Equal(2, Completions(posts).Count);
        await WakeAndEnd(f, callbacks);
        Assert.Equal(3, Completions(posts).Count);
        Assert.Equal("idle", Completions(posts)[2].GetProperty("workState").GetString());
        var sequences = Completions(posts).Select(p => p.GetProperty("deliverySequence").GetInt32()).ToArray();
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
    }

    [Fact]
    public async Task No_trailing_report_once_the_background_work_finished_and_settled()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        await EndTurn(f, callbacks, Running());
        now = now.AddMinutes(1);
        await WakeAndEnd(f, callbacks, Running());

        // The work stops without waking a turn: the settle reports the completion, never a stale notice.
        f.Runtime.Info.BackgroundTasks = [Bg("b1", BackgroundTaskStatuses.Completed)];
        await callbacks.ObserveAsync("session", settled: true); await callbacks.DrainAsync();
        now = now.AddMinutes(45);
        await Sweep(callbacks);
        var completions = Completions(posts);
        Assert.Equal(2, completions.Count);
        Assert.Equal("idle", completions[1].GetProperty("workState").GetString());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Theory]
    [InlineData(PendingSessionQuestion.QuestionKind)]
    [InlineData(PendingSessionQuestion.PermissionKind)]
    public async Task A_question_or_an_approval_goes_out_at_once_while_background_reports_are_held(string kind)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now, QuestionUrl);
        var pending = new List<PendingSessionQuestion>();
        callbacks.PendingQuestions = _ => pending;
        await EndTurn(f, callbacks, Running());
        now = now.AddMinutes(1);
        await WakeAndEnd(f, callbacks, Running());
        Assert.Single(Completions(posts));

        // The next woken turn parks on a question (or an approval card) inside the window.
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        pending.Add(Question("req-1", kind));
        await callbacks.QuestionsChangedAsync("session"); await callbacks.DrainAsync();

        var question = Assert.Single(posts, p => p.Url == QuestionUrl).Body;
        Assert.Equal("req-1", question.GetProperty("requestId").GetString());
        Assert.Equal(kind, question.GetProperty("kind").GetString());
        Assert.Single(Completions(posts));
    }

    [Fact]
    public async Task A_restart_pause_goes_out_at_once_while_reports_are_held_and_says_RedCompute_continues_it()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        await EndTurn(f, callbacks, Running());
        now = now.AddMinutes(1);
        await WakeAndEnd(f, callbacks, Running());

        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = SessionStopReasons.MaintenanceRestart;
        f.Runtime.Info.StoppedAt = now;
        await Sweep(callbacks);
        var paused = Completions(posts)[^1];
        Assert.Equal(2, Completions(posts).Count);
        Assert.Equal("Stopped", paused.GetProperty("status").GetString());
        Assert.Equal("maintenance_restart", paused.GetProperty("stopReason").GetString());
        Assert.True(paused.GetProperty("autoContinue").GetBoolean());
    }

    [Fact]
    public async Task A_plain_turn_keeps_the_exact_payload_shape()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var (callbacks, posts) = await DeliveredPrompt(f, () => DateTimeOffset.UtcNow);
        await EndTurn(f, callbacks);
        var payload = Assert.Single(Completions(posts));
        foreach (var added in new[] { "coalescedTurns", "trigger", "autoContinue", "backgroundTasksRunning" })
            Assert.False(payload.TryGetProperty(added, out _), added);
    }

    // ---- restart continuation ----

    [Fact]
    public async Task A_worker_cut_mid_turn_by_a_planned_restart_is_continued_once_and_its_completion_still_arrives()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        f.Runtime.Info.AccessLevel = SessionAccessLevels.Full;

        // The drain ran out of time and force-stopped the turn; the old process reported the pause.
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = SessionStopReasons.MaintenanceRestart;
        f.Runtime.Info.StoppedAt = now;
        await Sweep(callbacks);
        Assert.Equal("Stopped", Assert.Single(Completions(posts)).GetProperty("status").GetString());

        // The replacement process: a fresh callback store over the same database, and the checkpoint.
        callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Recording(posts), now: () => now);
        var checkpoint = new PlannedRestartData("20261111-161856-633-cb4931cb", now,
            [new PlannedRestartSession("test", "session", null)
                { ForcedStop = true, WorkState = SessionWorkStates.Working, BackgroundTasks = ["Section 4: shared 40px control set"] }]);
        var queue = f.Service();
        var continuations = new RestartContinuations(callbacks, f.Store, queue.AdmitAsync, _ => f.Runtime.Info,
            checkpoint, now.AddSeconds(40), (_, _) => { });

        Assert.Equal(1, await continuations.RunOnceAsync());
        Assert.Equal(1, f.Runtime.Resumes);
        Assert.Equal(1, f.Runtime.Deliveries);
        Assert.True(f.Runtime.ResumeHadFreshIdentity);

        var items = await f.Store.ListAsync("session", "owner", true);
        var continuation = Assert.Single(items, i => i.MessageUid != "prompt");
        Assert.Equal(SessionInputQueueState.Delivered, continuation.State);
        var original = Assert.Single(items, i => i.MessageUid == "prompt");
        Assert.Equal(JsonSerializer.Serialize(original.Provenance), JsonSerializer.Serialize(continuation.Provenance));
        Assert.Equal("owner", continuation.OwnerUserId);
        var text = continuation.DisplayContent;
        Assert.Contains("deployment 20261111-161856-633-cb4931cb", text);
        Assert.Contains("still running when the drain's wait ran out", text);
        Assert.Contains("background task \"Section 4: shared 40px control set\"", text);
        Assert.Contains("same access level (full)", text);
        using (var metadata = JsonDocument.Parse(continuation.MetadataJson!))
        {
            Assert.Equal("restart-continuation", metadata.RootElement.GetProperty("source").GetString());
            Assert.Equal("prompt", metadata.RootElement.GetProperty("continues").GetString());
            Assert.True(metadata.RootElement.GetProperty("forcedStop").GetBoolean());
        }

        // Exactly once: a second pass, and a second boot, admit nothing more.
        Assert.Equal(0, await continuations.RunOnceAsync());
        var reboot = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Recording(posts), now: () => now);
        Assert.Equal(0, await new RestartContinuations(reboot, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info,
            checkpoint, now.AddSeconds(40), (_, _) => { }).RunOnceAsync());
        Assert.Equal(2, (await f.Store.ListAsync("session", "owner", true)).Count);
        Assert.Equal(1, f.Runtime.Deliveries);

        // The continued turn ends: the supervisor's URL hears it, marked as the continuation.
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle;
        await Sweep(callbacks);
        var done = Completions(posts)[^1];
        Assert.Equal(2, Completions(posts).Count);
        Assert.Equal("Idle", done.GetProperty("status").GetString());
        Assert.Equal(continuation.MessageUid, done.GetProperty("promptMessageUid").GetString());
        Assert.Equal("restart-continuation", done.GetProperty("trigger").GetString());
        Assert.True(done.GetProperty("executionObserved").GetBoolean());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task A_worker_whose_background_work_a_restart_killed_is_continued_with_what_it_lost()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        await EndTurn(f, callbacks, Running());
        Assert.Single(Completions(posts));
        f.Runtime.Info.BackgroundTasks = [];
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = SessionStopReasons.MaintenanceRestart;
        f.Runtime.Info.StoppedAt = now;

        var checkpoint = new PlannedRestartData("run-1", now,
            [new PlannedRestartSession("test", "session", null) { WorkState = SessionWorkStates.Background, BackgroundTasks = ["proof run"] }]);
        var continuations = new RestartContinuations(callbacks, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info,
            checkpoint, now, (_, _) => { });
        Assert.Equal(1, await continuations.RunOnceAsync());
        var text = Assert.Single(await f.Store.ListAsync("session", "owner", true), i => i.MessageUid != "prompt").DisplayContent;
        Assert.Contains("the restart stopped the background work it was waiting on", text);
        Assert.Contains("background task \"proof run\"", text);
    }

    [Fact]
    public async Task An_unplanned_restart_is_continued_and_says_so()
    {
        using var f = new SessionRecoveryTests.Fixture("orphaned_on_restart");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, _) = await DeliveredPrompt(f, () => now);
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = SessionStopReasons.OrphanedOnRestart;
        f.Runtime.Info.StoppedAt = now;

        var continuations = new RestartContinuations(callbacks, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info,
            null, now, (_, _) => { });
        Assert.Equal(1, await continuations.RunOnceAsync());
        var text = Assert.Single(await f.Store.ListAsync("session", "owner", true), i => i.MessageUid != "prompt").DisplayContent;
        Assert.Contains("an unplanned RedCompute restart", text);
        Assert.Contains("Any background task that was running was lost", text);
    }

    [Theory]
    [InlineData("user_stopped", 0, true)]       // an explicit stop stays terminal
    [InlineData("idle_timeout", 0, true)]       // parking is not a restart
    [InlineData("maintenance_restart", 25, true)] // stopped long before this boot: not this restart's work
    [InlineData("maintenance_restart", 0, false)] // the turn completed: its completion is pending, nothing to continue
    public async Task Nothing_is_continued_that_this_restart_did_not_cut(string reason, int minutesBeforeBoot, bool turnStillOpen)
    {
        using var f = new SessionRecoveryTests.Fixture(reason);
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now, failDeliveries: true);
        if (!turnStillOpen) await EndTurn(f, callbacks);
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = reason;
        f.Runtime.Info.StoppedAt = now.AddMinutes(-minutesBeforeBoot);
        if (turnStillOpen) await Sweep(callbacks);

        var continuations = new RestartContinuations(callbacks, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info,
            null, now, (_, _) => { });
        Assert.Equal(0, await continuations.RunOnceAsync());
        Assert.Single(await f.Store.ListAsync("session", "owner", true));
        Assert.Equal(0, f.Runtime.Resumes);
        _ = posts;
    }

    [Fact]
    public async Task A_prompt_still_queued_is_left_to_the_queue()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var now = DateTimeOffset.UtcNow;
        f.Runtime.Info.StoppedAt = now;
        await f.Store.EnqueueAsync(f.Submission());
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Recording([]), now: () => now);
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt");

        var continuations = new RestartContinuations(callbacks, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info,
            null, now, (_, _) => { });
        Assert.Equal(0, await continuations.RunOnceAsync());
        Assert.Single(await f.Store.ListAsync("session", "owner", true));
    }

    // ---- the drain records what it cut ----

    [Fact]
    public void A_checkpoint_from_an_older_build_still_loads_and_the_new_fields_round_trip()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
            "restart-continuity", Guid.NewGuid().ToString("N"), "planned-restart.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(path, $$"""{"runId":"old","createdAt":"{{now:O}}","sessions":[{"provider":"claude-code","sessionId":"a","jobId":null}]}""");
            var old = Assert.Single(PlannedRestartCheckpoint.LoadForStartup(path, now)!.Sessions);
            Assert.False(old.ForcedStop); Assert.Null(old.WorkState); Assert.Null(old.BackgroundTasks);

            PlannedRestartCheckpoint.Write(new("new", now, [new("codex", "b", null)
                { ForcedStop = true, WorkState = "working", BackgroundTasks = ["t"] }]), path);
            var loaded = Assert.Single(PlannedRestartCheckpoint.LoadForStartup(path, now)!.Sessions);
            Assert.True(loaded.ForcedStop); Assert.Equal("working", loaded.WorkState); Assert.Equal(["t"], loaded.BackgroundTasks!);
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
        }
    }

    // ---- helpers ----

    private static async Task<(SessionPromptCallbacks, List<(string Url, JsonElement Body)>)> DeliveredPrompt(
        SessionRecoveryTests.Fixture f, Func<DateTimeOffset> now, string? questionUrl = null, bool failDeliveries = false)
    {
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var posts = new List<(string Url, JsonElement Body)>();
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info,
            Recording(posts, failDeliveries ? HttpStatusCode.BadGateway : HttpStatusCode.OK), now: now, backgroundNoticeWindow: Window);
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt", questionUrl); await callbacks.DrainAsync();
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session"); await callbacks.DrainAsync();
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        return (callbacks, posts);
    }

    private static async Task EndTurn(SessionRecoveryTests.Fixture f, SessionPromptCallbacks callbacks, params BackgroundTaskInfo[] tasks)
    {
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.BackgroundTasks = tasks;
        await Sweep(callbacks);
    }

    private static async Task WakeAndEnd(SessionRecoveryTests.Fixture f, SessionPromptCallbacks callbacks, params BackgroundTaskInfo[] tasks)
    {
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        await EndTurn(f, callbacks, tasks);
    }

    private static async Task Sweep(SessionPromptCallbacks callbacks)
    {
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
    }

    private static List<JsonElement> Completions(List<(string Url, JsonElement Body)> posts)
    {
        lock (posts) return posts.Where(p => p.Url == CompleteUrl).Select(p => p.Body).ToList();
    }

    private static BackgroundTaskInfo[] Running() => [Bg("b1", BackgroundTaskStatuses.Running)];

    private static BackgroundTaskInfo Bg(string id, string status) => new() { TaskId = id, Kind = "shell", Status = status, Description = "tail the proof log" };

    private static PendingSessionQuestion Question(string requestId, string kind)
    {
        var input = JsonSerializer.SerializeToElement(new { command = "npm test" });
        return new PendingSessionQuestion
        {
            RequestId = requestId, Kind = kind, ToolName = kind == PendingSessionQuestion.PermissionKind ? "Bash" : "AskUserQuestion",
            ToolUseId = "toolu_1", Questions = JsonSerializer.SerializeToElement(Array.Empty<object>()), Input = input,
            AskedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
    }

    private static HttpClient Recording(List<(string Url, JsonElement Body)> posts, HttpStatusCode status = HttpStatusCode.OK)
        => new(new Handler(async r =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
            lock (posts) posts.Add((r.RequestUri!.ToString(), body));
            return new(status);
        }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
