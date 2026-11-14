using System.Net;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// 2026-11-14 (Heartbeat 3cad133c). A DNS outage of about an hour: every Claude CLI gave up after
// its own ~4.5-minute retry with "API Error: Can't reach the API server — check your internet or
// DNS (ENOTFOUND)". Two delegated workers ended their turns failed; the completions read as plain
// Idle, woke both supervisors into the same outage, and nothing ever tried again. A transient
// failure is now recorded with its error, retried with backoff while the supervisor hears nothing,
// and reported failed, with the error, once the retries are spent.
public sealed class TransientTurnRetryTests
{
    private const string CompleteUrl = "http://localhost/complete";
    private const string Enotfound = "API Error: Can't reach the API server — check your internet or DNS (ENOTFOUND)";

    [Theory]
    [InlineData(Enotfound, true)]
    [InlineData("API Error: getaddrinfo EAI_AGAIN api.anthropic.com", true)]
    [InlineData("API Error: Connection error.", true)]
    [InlineData("API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", true)]
    [InlineData("API Error: 503 Service Unavailable", true)]
    [InlineData("Selected model is at capacity. Please try a different model.", true)]
    [InlineData("request to https://api.anthropic.com/v1/messages failed, reason: read ECONNRESET", true)]
    [InlineData("API Error: Opus 5.5's safeguards flagged this message", false)]
    [InlineData("Claude AI usage limit reached|1731600000", false)]
    [InlineData("API Error: 401 authentication_error: OAuth token has expired", false)]
    [InlineData("API Error: 400 invalid_request_error: prompt is too long", false)]
    [InlineData("provider unavailable", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_connectivity_and_provider_availability_errors_are_transient(string? error, bool transient)
        => Assert.Equal(transient, SessionTurnErrors.IsTransient(error));

    [Fact]
    public async Task A_turn_failed_by_the_network_is_retried_silently_and_its_supervisor_hears_the_retried_completion()
    {
        using var f = new SessionRecoveryTests.Fixture("completed");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        var retries = Retries(f, callbacks, () => now);

        await FailTurn(f, callbacks, Enotfound);
        var failed = f.Store.GetLastEndedTurn("session")!;
        Assert.Equal(SessionTurnOutcomes.Failed, failed.Outcome);
        Assert.Equal(Enotfound, failed.Error);
        Assert.Equal(Enotfound, failed.Summary.Error);
        Assert.True(failed.Summary.Transient);
        // The supervisor is not woken into the outage.
        Assert.Empty(Completions(posts));

        // Not yet due.
        now = failed.EndedAt!.Value + TimeSpan.FromSeconds(50);
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Equal(0, f.Runtime.Deliveries);

        now = failed.EndedAt!.Value + TransientTurnRetries.Delays[0];
        Assert.Equal(1, await retries.RunOnceAsync());
        Assert.Equal(1, f.Runtime.Deliveries);
        // Idempotent: the same failed turn is never retried twice.
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Equal(1, f.Runtime.Deliveries);

        var items = await f.Store.ListAsync("session", "owner", true);
        var retry = Assert.Single(items, i => i.MessageUid != "prompt");
        var original = Assert.Single(items, i => i.MessageUid == "prompt");
        Assert.Equal(JsonSerializer.Serialize(original.Provenance), JsonSerializer.Serialize(retry.Provenance));
        Assert.Equal("owner", retry.OwnerUserId);
        Assert.Contains("<transient-retry attempt=\"1\"", retry.DisplayContent);
        Assert.Contains("ENOTFOUND", retry.DisplayContent);
        using (var metadata = JsonDocument.Parse(retry.MetadataJson!))
        {
            Assert.Equal("transient-retry", metadata.RootElement.GetProperty("source").GetString());
            Assert.Equal(1, metadata.RootElement.GetProperty("attempt").GetInt32());
            Assert.Equal("prompt", metadata.RootElement.GetProperty("continues").GetString());
            Assert.Equal(failed.TurnId, metadata.RootElement.GetProperty("failedTurnId").GetString());
        }

        // The network is back: the retried turn completes, and exactly one completion goes out.
        await CompleteTurn(f, callbacks);
        var done = Assert.Single(Completions(posts));
        Assert.Equal("Idle", done.GetProperty("status").GetString());
        Assert.Equal("completed", done.GetProperty("turnOutcome").GetString());
        Assert.Equal(retry.MessageUid, done.GetProperty("promptMessageUid").GetString());
        Assert.Equal("transient-retry", done.GetProperty("trigger").GetString());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("turnError").ValueKind);
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    // The 14 Nov Nature worker: its delegated turn failed, the next delegated prompt it had queued ran
    // straight into the same outage and failed too. The first prompt stays held (the outage is not
    // over), and one retry answers both.
    [Fact]
    public async Task A_queued_prompt_failing_in_the_same_outage_keeps_the_first_held_and_one_retry_takes_both()
    {
        using var f = new SessionRecoveryTests.Fixture("completed");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        var retries = Retries(f, callbacks, () => now);
        await f.Store.EnqueueAsync(f.Submission("next"));
        await callbacks.RegisterAsync("session", "next", CompleteUrl, "owner", "next"); await callbacks.DrainAsync();

        await FailTurn(f, callbacks, Enotfound);
        // The queue hands the next prompt to the idle session at once; it fails the same way.
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await f.Store.MarkDeliveredAsync(batch, "next");
        await FailTurn(f, callbacks, Enotfound);
        Assert.Empty(Completions(posts));

        now = f.Store.GetLastEndedTurn("session")!.EndedAt!.Value + TransientTurnRetries.Delays[0];
        Assert.Equal(1, await retries.RunOnceAsync());
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Single(await f.Store.ListAsync("session", "owner", true), i => i.MessageUid is not ("prompt" or "next"));

        await CompleteTurn(f, callbacks);
        var done = Assert.Single(Completions(posts));
        Assert.Equal("completed", done.GetProperty("turnOutcome").GetString());
        Assert.Equal("transient-retry", done.GetProperty("trigger").GetString());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task When_the_retries_run_out_the_supervisor_hears_the_failure_and_its_error_once()
    {
        using var f = new SessionRecoveryTests.Fixture("completed");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        var retries = Retries(f, callbacks, () => now);

        await FailTurn(f, callbacks, Enotfound);
        for (var attempt = 1; attempt <= TransientTurnRetries.Delays.Length; attempt++)
        {
            Assert.Empty(Completions(posts));
            now = f.Store.GetLastEndedTurn("session")!.EndedAt!.Value + TransientTurnRetries.Delays[attempt - 1];
            Assert.Equal(1, await retries.RunOnceAsync());
            Assert.Equal(attempt, f.Runtime.Deliveries);
            await FailTurn(f, callbacks, Enotfound, alreadyActive: true);
        }

        // A restart, or a later pass, admits nothing more.
        now = now.AddHours(1);
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Equal(TransientTurnRetries.Delays.Length, f.Runtime.Deliveries);

        var failure = Assert.Single(Completions(posts));
        Assert.Equal("Idle", failure.GetProperty("status").GetString());
        Assert.Equal("failed", failure.GetProperty("turnOutcome").GetString());
        Assert.Equal(Enotfound, failure.GetProperty("turnError").GetString());
        Assert.Equal("transient-retry", failure.GetProperty("trigger").GetString());
        Assert.NotEqual("completed", failure.GetProperty("stopReason").ValueKind == JsonValueKind.String
            ? failure.GetProperty("stopReason").GetString() : null);
    }

    [Fact]
    public async Task A_failure_that_is_not_transient_is_reported_at_once_with_its_error_and_never_retried()
    {
        using var f = new SessionRecoveryTests.Fixture("completed");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        var retries = Retries(f, callbacks, () => now);
        const string refusal = "API Error: Opus 5.5's safeguards flagged this message";

        await FailTurn(f, callbacks, refusal);
        var failure = Assert.Single(Completions(posts));
        Assert.Equal("failed", failure.GetProperty("turnOutcome").GetString());
        Assert.Equal(refusal, failure.GetProperty("turnError").GetString());

        now = now.AddHours(1);
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Equal(0, f.Runtime.Deliveries);
    }

    [Fact]
    public async Task A_session_the_user_stopped_is_not_retried()
    {
        using var f = new SessionRecoveryTests.Fixture("completed");
        var now = DateTimeOffset.UtcNow;
        var (callbacks, posts) = await DeliveredPrompt(f, () => now);
        var retries = Retries(f, callbacks, () => now);

        await FailTurn(f, callbacks, Enotfound);
        Assert.Empty(Completions(posts));
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = "user_stopped";
        await Sweep(callbacks);

        now = now.AddHours(1);
        Assert.Equal(0, await retries.RunOnceAsync());
        Assert.Equal(0, f.Runtime.Deliveries);
        Assert.Equal("Stopped", Assert.Single(Completions(posts)).GetProperty("status").GetString());
    }

    [Fact]
    public void A_retry_counts_its_attempt_from_its_own_metadata_and_stops_after_the_last()
    {
        var turn = new SessionTurnRecord("session", "t1", DateTimeOffset.UtcNow.AddMinutes(-2), true, DateTimeOffset.UtcNow.AddMinutes(-1),
            "Idle", null, SessionTurnOutcomes.Failed, 0, null, null, Enotfound);
        var session = new UnifiedSessionInfo { Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".", Status = SessionStatus.Idle };
        SessionInputQueueItem Prompt(int attempt) => Item(attempt == 0 ? null
            : JsonSerializer.Serialize(new { source = TransientTurnRetries.Trigger, attempt }));

        var first = TransientTurnRetries.Qualify(Prompt(0), turn, turn, session, DateTimeOffset.UtcNow)!;
        Assert.Equal(1, first.Attempt);
        Assert.Equal(turn.EndedAt!.Value + TransientTurnRetries.Delays[0], first.DueAt);
        Assert.Equal(TransientTurnRetries.Delays.Length,
            TransientTurnRetries.Qualify(Prompt(TransientTurnRetries.Delays.Length - 1), turn, turn, session, DateTimeOffset.UtcNow)!.Attempt);
        Assert.Null(TransientTurnRetries.Qualify(Prompt(TransientTurnRetries.Delays.Length), turn, turn, session, DateTimeOffset.UtcNow));
        // A later turn failing the same way keeps it held, due after that later failure.
        var laterFailure = turn with { TurnId = "t2", EndedAt = turn.EndedAt!.Value.AddMinutes(3) };
        Assert.Equal(laterFailure.EndedAt!.Value + TransientTurnRetries.Delays[0],
            TransientTurnRetries.Qualify(Prompt(0), turn, laterFailure, session, DateTimeOffset.UtcNow)!.DueAt);
        // Anything after it ending otherwise, or a failure too old: not retried.
        Assert.Null(TransientTurnRetries.Qualify(Prompt(0), turn,
            laterFailure with { Outcome = SessionTurnOutcomes.Completed, Error = null }, session, DateTimeOffset.UtcNow));
        Assert.Null(TransientTurnRetries.Qualify(Prompt(0), turn, turn, session, DateTimeOffset.UtcNow + TransientTurnRetries.Window));
        // Idle parking does not cancel it; a restart stop is RestartContinuations' to continue.
        Assert.NotNull(TransientTurnRetries.Qualify(Prompt(0), turn, turn,
            new UnifiedSessionInfo { Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".", Status = SessionStatus.Stopped, StopReason = "idle_timeout" }, DateTimeOffset.UtcNow));
        Assert.Null(TransientTurnRetries.Qualify(Prompt(0), turn, turn,
            new UnifiedSessionInfo { Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".", Status = SessionStatus.Stopped, StopReason = SessionStopReasons.MaintenanceRestart }, DateTimeOffset.UtcNow));

        static SessionInputQueueItem Item(string? metadata) => new(1, "q1", null, "session", "owner",
            [new("text", "x")], "x", metadata, null!, "scope", SessionInputDeliveryPolicy.AfterCurrent,
            SessionInputQueueState.Delivered, "prompt", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null, null,
            null, null, false, "prompt", DateTimeOffset.UtcNow, TurnId: "t1");
    }

    // ---- helpers ----

    private static TransientTurnRetries Retries(SessionRecoveryTests.Fixture f, SessionPromptCallbacks callbacks, Func<DateTimeOffset> now)
        => new(callbacks, f.Store, f.Service().AdmitAsync, _ => f.Runtime.Info, (_, _) => { }, now);

    private static async Task<(SessionPromptCallbacks, List<(string Url, JsonElement Body)>)> DeliveredPrompt(
        SessionRecoveryTests.Fixture f, Func<DateTimeOffset> now)
    {
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var posts = new List<(string Url, JsonElement Body)>();
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, Recording(posts), now: now);
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt"); await callbacks.DrainAsync();
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        return (callbacks, posts);
    }

    /// <summary>The provider runs the delivered turn and it fails with <paramref name="error"/>.</summary>
    private static async Task FailTurn(SessionRecoveryTests.Fixture f, SessionPromptCallbacks callbacks, string error, bool alreadyActive = false)
    {
        if (!alreadyActive) f.Runtime.Info.Status = SessionStatus.Active;
        f.Store.MarkTurnActive("session");
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.LastTurnOutcome = SessionTurnOutcomes.Failed;
        f.Runtime.Info.LastTurnError = error;
        f.Runtime.Info.LastTurnEndedAt = DateTimeOffset.UtcNow;
        await Sweep(callbacks);
    }

    private static async Task CompleteTurn(SessionRecoveryTests.Fixture f, SessionPromptCallbacks callbacks)
    {
        f.Store.MarkTurnActive("session");
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = "completed";
        f.Runtime.Info.LastTurnOutcome = SessionTurnOutcomes.Completed;
        f.Runtime.Info.LastTurnError = null;
        f.Runtime.Info.LastTurnEndedAt = DateTimeOffset.UtcNow;
        await Sweep(callbacks);
    }

    private static async Task Sweep(SessionPromptCallbacks callbacks)
    {
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
    }

    private static List<JsonElement> Completions(List<(string Url, JsonElement Body)> posts)
    {
        lock (posts) return posts.Where(p => p.Url == CompleteUrl).Select(p => p.Body).ToList();
    }

    private static HttpClient Recording(List<(string Url, JsonElement Body)> posts)
        => new(new Handler(async r =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
            lock (posts) posts.Add((r.RequestUri!.ToString(), body));
            return new(HttpStatusCode.OK);
        }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
