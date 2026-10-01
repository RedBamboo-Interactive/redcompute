using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionPromptCallbackTests
{
    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public async Task Restart_pause_retains_subscription_and_transient_idle_cannot_complete_pending_prompt(string stopReason)
    {
        using var f = new SessionRecoveryTests.Fixture(stopReason);
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async r => {
            payloads.Add(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())); return new(HttpStatusCode.OK); }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "prompt", "http://localhost/callback", "owner", "prompt"); await callbacks.DrainAsync();
        Assert.Single(payloads); Assert.Equal("Stopped", payloads[0].GetProperty("status").GetString());
        await callbacks.SweepAsync(default); await callbacks.DrainAsync(); Assert.Single(payloads);
        // Reopen after a process restart, with the same accepted pending queue item.
        callbacks = new(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await callbacks.SweepAsync(default); await callbacks.DrainAsync(); Assert.Single(payloads);
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session"); await callbacks.DrainAsync();
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count);
        Assert.Equal("Idle", payloads[1].GetProperty("status").GetString());
        Assert.Equal("prompt", payloads[1].GetProperty("deliveredMessageUid").GetString());
        await callbacks.SweepAsync(default); await callbacks.DrainAsync(); Assert.Equal(2, payloads.Count);
        // Old completion cannot consume the next prompt's still-pending subscription.
        await f.Store.EnqueueAsync(f.Submission("later"));
        await callbacks.RegisterAsync("session", "later", "http://localhost/callback", "owner", "later"); await callbacks.DrainAsync();
        await callbacks.ObserveAsync("session"); await callbacks.DrainAsync(); Assert.Equal(2, payloads.Count);
    }

    [Fact]
    public async Task Lost_acknowledgement_retries_identical_durable_payload_after_reopen()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<string>(); var calls = 0; var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler(async r => {
            payloads.Add(await r.Content!.ReadAsStringAsync()); return new(++calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK); }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: () => now);
        await callbacks.RegisterAsync("session", "prompt", "http://localhost/callback", "owner", "prompt"); await callbacks.DrainAsync();
        now = now.AddSeconds(31);
        callbacks = new(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: () => now);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, payloads.Count); Assert.Equal(payloads[0], payloads[1]);
    }
    [Fact]
    public async Task Pending_subscription_retains_delivery_evidence_past_queue_retention()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "prompt", "http://localhost/callback", "owner", "prompt"); await callbacks.DrainAsync();
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        await f.Store.CleanupTerminalAsync(TimeSpan.FromSeconds(-1));
        Assert.NotNull(await f.Store.FindPromptAsync("session", "prompt"));
    }
    [Theory]
    [InlineData("completed")]
    [InlineData("terminated")]
    [InlineData("unknown")]
    public async Task Ended_reports_reason_and_only_real_delivered_prompt_proof(string reason)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var payloads = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async r => { payloads.Add(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())); return new(HttpStatusCode.OK); }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "prompt", "http://localhost/callback", "owner", "prompt"); await callbacks.DrainAsync();
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        // A fast Active event may already have transitioned to Idle by observation time.
        await callbacks.ObserveAsync("session", observedActive: true); await callbacks.DrainAsync();
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        await callbacks.ObserveAsync("session", endedReason: reason); await callbacks.DrainAsync();
        var payload = Assert.Single(payloads);
        Assert.Equal("Ended", payload.GetProperty("status").GetString()); Assert.Equal(reason, payload.GetProperty("reason").GetString());
        Assert.Equal("prompt", payload.GetProperty("deliveredMessageUid").GetString());
    }
    [Fact]
    public async Task Attached_production_store_preserves_legacy_arbitrary_operation_id()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var received = new TaskCompletionSource<JsonElement>();
        using var http = new HttpClient(new Handler(async r => { received.TrySetResult(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())); return new(HttpStatusCode.OK); }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachDurableStore(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info);
        registry.Register("session", "http://localhost/callback", "owner", "logical-operation-not-a-message");
        f.Runtime.Info.Status = SessionStatus.Idle; registry.OnSessionEvent("session.updated", f.Runtime.Info);
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("logical-operation-not-a-message", payload.GetProperty("callbackId").GetString());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_explicit_supersession_acknowledges_terminal_callback_and_releases_subscription(bool supersede)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle;
        var target = new DeploymentVerificationTarget("redleaf", "run-one");
        var submission = f.Submission() with { MetadataJson = supersede ? DeploymentVerificationTarget.WithMetadata(null, target) : null };
        var item = (await f.Store.EnqueueAsync(submission)).Item;
        var payloads = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async r => { payloads.Add(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())); return new(HttpStatusCode.OK); }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "operation", "http://localhost/callback", "owner", "prompt");
        if (supersede) await f.Service().SupersedeVerificationAsync("session", item.Id, "owner", target, new("redleaf", "run-two"), default);
        else await f.Service().CancelAsync("session", item.Id, "owner", default);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        var payload = Assert.Single(payloads);
        Assert.Equal("Cancelled", payload.GetProperty("status").GetString());
        Assert.Equal(supersede ? "superseded" : "cancelled", payload.GetProperty("reason").GetString());
        Assert.False(payload.GetProperty("executionObserved").GetBoolean());
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
        await f.Store.CleanupTerminalAsync(TimeSpan.FromSeconds(-1));
        Assert.Equal(supersede, await f.Store.FindPromptAsync("session", "prompt") is not null);
    }

    [Fact]
    public async Task Definitive_unaccepted_cleanup_requires_exact_owner_reference_and_never_removes_accepted_input()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "operation", "http://localhost/callback", "owner", "prompt");
        Assert.False(await callbacks.RemoveUnacceptedAsync("session", "operation", "prompt", "other", default));
        Assert.False(await callbacks.RemoveUnacceptedAsync("session", "operation", "other", "owner", default));
        Assert.True(await callbacks.RemoveUnacceptedAsync("session", "operation", "prompt", "owner", default));
        await f.Store.EnqueueAsync(f.Submission());
        await callbacks.RegisterAsync("session", "operation", "http://localhost/callback", "owner", "prompt");
        Assert.False(await callbacks.RemoveUnacceptedAsync("session", "operation", "prompt", "owner", default));
        await callbacks.DrainAsync();
    }

    [Fact]
    public async Task Merged_batch_reports_exact_prompt_separately_from_actual_delivery_uid()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart"); f.Runtime.Info.Status = SessionStatus.Idle;
        await f.Store.EnqueueAsync(f.Submission("first")); await f.Store.EnqueueAsync(f.Submission("second"));
        var payloads = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async r => { payloads.Add(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())); return new(HttpStatusCode.OK); }));
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http);
        await callbacks.RegisterAsync("session", "operation-second", "http://localhost/callback", "owner", "second");
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1)); Assert.Equal(2, batch.Count);
        await callbacks.ObserveAsync("session", observedActive: true); await f.Store.MarkDeliveredAsync(batch, "first");
        await callbacks.ObserveAsync("session", endedReason: "completed"); await callbacks.DrainAsync();
        var payload = Assert.Single(payloads);
        Assert.Equal("second", payload.GetProperty("promptMessageUid").GetString());
        Assert.Equal("first", payload.GetProperty("deliveredMessageUid").GetString());
        Assert.True(payload.GetProperty("executionObserved").GetBoolean());
    }

    [Fact]
    public async Task Held_webhook_cannot_stall_owning_input_scheduler_and_retry_survives_reopen()
    {
        using var callbackFixture = new SessionRecoveryTests.Fixture("maintenance_restart");
        await callbackFixture.Store.EnqueueAsync(callbackFixture.Submission());
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var now = DateTimeOffset.UtcNow; var calls = 0; var logs = new List<string>();
        using var http = new HttpClient(new Handler(async _ => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return new(HttpStatusCode.BadGateway); }));
        var callbacks = new SessionPromptCallbacks(callbackFixture.Attachments.DatabasePath, callbackFixture.Store, _ => callbackFixture.Runtime.Info, http,
            (message, _) => logs.Add(message), () => now);
        await callbacks.RegisterAsync("session", "operation", "http://localhost/callback", "owner", "prompt");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart"); f.Runtime.Info.Status = SessionStatus.Idle;
        f.Runtime.Info = new() { Id = "unrelated", Provider = "test", ProjectName = "Other", ProjectPath = ".", UserId = "owner", Status = SessionStatus.Idle };
        var service = new SessionInputQueueService(f.Store, f.Attachments, f.Registry, null!, new RedBamboo.AppHost.WebSockets.WebSocketBroadcaster(), (_, _) => { }, reconcileCallbacks: callbacks.SweepAsync);
        using var stop = new CancellationTokenSource(); var scheduler = service.RunAsync(stop.Token);
        service.Signal("unrelated"); await Task.Delay(100);
        var item = (await f.Store.EnqueueAsync(f.Submission() with { SessionId = "unrelated" })).Item; service.Signal("unrelated");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (f.Runtime.Deliveries == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(1, f.Runtime.Deliveries); Assert.Equal(1, calls);
        stop.Cancel(); await scheduler;
        release.SetResult(); await callbacks.DrainAsync(); Assert.NotEmpty(logs);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync(); Assert.Equal(1, calls);
        now = now.AddSeconds(31);
        using var retryHttp = new HttpClient(new Handler(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var reopened = new SessionPromptCallbacks(callbackFixture.Attachments.DatabasePath, callbackFixture.Store, _ => callbackFixture.Runtime.Info, retryHttp, now: () => now);
        await reopened.SweepAsync(default); await reopened.DrainAsync(); Assert.Equal(2, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
