using System.Net;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionPromptCallbackRestartOrderingTests
{
    [Fact]
    public async Task Preexisting_restart_stop_does_not_pause_newly_queued_prompt_before_auto_resume()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.StoppedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);

        // Nova preregisters the exact prompt before admission. At this point the
        // restart stop predates the prompt and there is no queue item to observe.
        await callbacks.RegisterAsync(
            "session", "operation", "http://localhost/callback", "owner", "prompt");
        await callbacks.DrainAsync();
        Assert.Empty(payloads);

        // Admission persists Pending before recovery authority and provider resume.
        // A callback sweep can run in this window while the old stop is still visible.
        await f.Store.EnqueueAsync(f.Submission());
        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();

        // Recovery resumes the stopped provider and delivers the accepted prompt.
        await f.Service().ProcessSessionAsync("session", default);
        await callbacks.ObserveAsync("session", observedActive: true);
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.ObserveAsync("session", endedReason: "completed");
        await callbacks.DrainAsync();

        Assert.DoesNotContain(payloads, payload =>
            payload.GetProperty("status").GetString() == "Stopped");
        var completion = Assert.Single(payloads);
        Assert.Equal("Ended", completion.GetProperty("status").GetString());
        Assert.Equal("completed", completion.GetProperty("reason").GetString());
        Assert.Equal("prompt", completion.GetProperty("deliveredMessageUid").GetString());
        Assert.True(completion.GetProperty("executionObserved").GetBoolean());
    }

    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public async Task Restart_interrupting_admitted_prompt_emits_one_pause_then_real_completion(string stopReason)
    {
        using var f = new SessionRecoveryTests.Fixture(stopReason);
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.StoppedAt = null;
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync(
            "session", "operation", "http://localhost/callback", "owner", "prompt");

        var batch = await f.Store.ClaimBatchAsync(
            "session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true);
        await f.Store.MarkDeliveredAsync(batch, "prompt");

        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = stopReason;
        f.Runtime.Info.StoppedAt = batch[0].CreatedAt;
        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();
        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();

        var pause = Assert.Single(payloads);
        Assert.Equal("Stopped", pause.GetProperty("status").GetString());
        Assert.Equal(stopReason, pause.GetProperty("stopReason").GetString());

        f.Runtime.Info.Status = SessionStatus.Active;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.StoppedAt = null;
        await callbacks.ObserveAsync("session", observedActive: true);
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.ObserveAsync("session", endedReason: "completed");
        await callbacks.DrainAsync();

        Assert.Equal(2, payloads.Count);
        var completion = payloads[1];
        Assert.Equal("Ended", completion.GetProperty("status").GetString());
        Assert.Equal("completed", completion.GetProperty("reason").GetString());
        Assert.Equal("prompt", completion.GetProperty("deliveredMessageUid").GetString());
        Assert.True(completion.GetProperty("executionObserved").GetBoolean());
    }

    [Fact]
    public async Task Restart_at_or_after_admission_before_delivery_emits_one_pause()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.StoppedAt = null;
        var item = (await f.Store.EnqueueAsync(f.Submission())).Item;
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync(
            "session", "operation", "http://localhost/callback", "owner", "prompt");

        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = "maintenance_restart";
        f.Runtime.Info.StoppedAt = item.CreatedAt;
        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();
        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();

        var pause = Assert.Single(payloads);
        Assert.Equal("Stopped", pause.GetProperty("status").GetString());
        Assert.Equal("maintenance_restart", pause.GetProperty("stopReason").GetString());
        Assert.False(pause.GetProperty("executionObserved").GetBoolean());
    }

    [Fact]
    public async Task Repeated_same_reason_restarts_each_pause_once_then_complete()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.StoppedAt = null;
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync(
            "session", "operation", "http://localhost/callback", "owner", "prompt");
        var batch = await f.Store.ClaimBatchAsync(
            "session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true);
        await f.Store.MarkDeliveredAsync(batch, "prompt");

        for (var restart = 0; restart < 2; restart++)
        {
            f.Runtime.Info.Status = SessionStatus.Stopped;
            f.Runtime.Info.StopReason = "maintenance_restart";
            f.Runtime.Info.StoppedAt = batch[0].CreatedAt.AddMinutes(restart + 1);
            await callbacks.SweepAsync(default);
            await callbacks.DrainAsync();
            await callbacks.SweepAsync(default);
            await callbacks.DrainAsync();
            Assert.Equal(restart + 1, payloads.Count);

            f.Runtime.Info.Status = SessionStatus.Active;
            f.Runtime.Info.StopReason = null;
            f.Runtime.Info.StoppedAt = null;
            await callbacks.ObserveAsync("session", observedActive: true);
        }

        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.ObserveAsync("session", endedReason: "completed");
        await callbacks.DrainAsync();

        Assert.Equal(["Stopped", "Stopped", "Ended"], payloads
            .Select(payload => payload.GetProperty("status").GetString()!).ToArray());
    }

    [Fact]
    public async Task Usage_limit_remains_immediate_when_it_predates_admission()
    {
        using var f = new SessionRecoveryTests.Fixture("usage_limit");
        f.Runtime.Info.StoppedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);

        await callbacks.RegisterAsync(
            "session", "operation", "http://localhost/callback", "owner", "prompt");
        await callbacks.DrainAsync();

        var pause = Assert.Single(payloads);
        Assert.Equal("Stopped", pause.GetProperty("status").GetString());
        Assert.Equal("usage_limit", pause.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Two_callback_ids_are_independent_while_preexisting_stop_is_suppressed()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.StoppedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync(
            "session", "operation-one", "http://localhost/callback", "owner", "prompt");
        await callbacks.RegisterAsync(
            "session", "operation-two", "http://localhost/callback", "owner", "prompt");
        await f.Store.EnqueueAsync(f.Submission());

        await callbacks.SweepAsync(default);
        await callbacks.DrainAsync();
        Assert.Empty(payloads);

        await f.Service().ProcessSessionAsync("session", default);
        await callbacks.ObserveAsync("session", observedActive: true);
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.ObserveAsync("session", endedReason: "completed");
        await callbacks.DrainAsync();

        Assert.Equal(2, payloads.Count);
        Assert.Equal(["operation-one", "operation-two"], payloads
            .Select(payload => payload.GetProperty("callbackId").GetString()!)
            .OrderBy(id => id).ToArray());
        Assert.All(payloads, payload =>
            Assert.Equal("Ended", payload.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Persisted_callback_entry_from_before_stop_timestamp_change_deserializes()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info.StopReason = null;
        f.Runtime.Info.StoppedAt = null;
        await f.Store.EnqueueAsync(f.Submission());
        new SessionCallbackStore(f.Attachments.DatabasePath).Save("session", "legacy-operation", """
            {"SessionId":"session","CallbackId":"legacy-operation","PromptMessageUid":"prompt","Url":"http://localhost/callback","UserId":"owner"}
            """);
        var payloads = new List<JsonElement>();
        using var http = CallbackClient(payloads);
        var callbacks = new SessionPromptCallbacks(
            f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);

        var batch = await f.Store.ClaimBatchAsync(
            "session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session", observedActive: true);
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.ObserveAsync("session", endedReason: "completed");
        await callbacks.DrainAsync();

        var completion = Assert.Single(payloads);
        Assert.Equal("legacy-operation", completion.GetProperty("callbackId").GetString());
        Assert.Equal("Ended", completion.GetProperty("status").GetString());
    }

    private static HttpClient CallbackClient(List<JsonElement> payloads) => new(new Handler(async request =>
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            await request.Content!.ReadAsStringAsync());
        lock (payloads) payloads.Add(payload);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
