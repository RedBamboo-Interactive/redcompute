using System.Reflection;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionSteeringQueueTests
{
    [Fact]
    public async Task ActiveSupportedSessionSteersAndMayOvertakeAfterCurrent()
    {
        using var f = new Fixture();
        var waiting = await f.Store.EnqueueAsync(f.Submission("waiting", SessionInputDeliveryPolicy.AfterCurrent));
        var steer = await f.Store.EnqueueAsync(f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));

        await f.Service().ProcessSessionAsync("session", default);

        Assert.Equal(1, f.Runtime.Steers);
        Assert.Equal(0, f.Runtime.Sends);
        Assert.Equal("steer", f.Runtime.LastSteerText);
        Assert.Equal(SessionInputQueueState.Delivered,
            (await f.Store.GetAsync("session", steer.Item.Id, "owner"))!.State);
        Assert.Equal(SessionInputQueueState.Pending,
            (await f.Store.GetAsync("session", waiting.Item.Id, "owner"))!.State);
    }

    [Theory]
    [InlineData(SessionInputDeliveryStatus.NotSteerable)]
    [InlineData(SessionInputDeliveryStatus.Unsupported)]
    public async Task ProviderRefusalDemotesInPlaceThenIdleDeliverySendsNormally(
        SessionInputDeliveryStatus status)
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = status == SessionInputDeliveryStatus.Unsupported
            ? SessionInputDeliveryResult.Unsupported()
            : SessionInputDeliveryResult.NotSteerable("parked_question", "Question parked");
        var admitted = await f.Store.EnqueueAsync(f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));
        var service = f.Service();

        await service.ProcessSessionAsync("session", default);

        var demoted = (await f.Store.GetAsync("session", admitted.Item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Pending, demoted.State);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, demoted.DeliveryPolicy);
        Assert.Equal("steer_unavailable", demoted.ErrorCode);

        f.Runtime.Info.Status = SessionStatus.Idle;
        await service.ProcessSessionAsync("session", default);

        Assert.Equal(1, f.Runtime.Sends);
        Assert.Equal(SessionInputQueueState.Delivered,
            (await f.Store.GetAsync("session", admitted.Item.Id, "owner"))!.State);
    }

    [Fact]
    public async Task UnsupportedCapabilityDemotesWithoutCallingProviderSteering()
    {
        using var f = new Fixture();
        f.Runtime.SupportsSteering = false;
        var admitted = await f.Store.EnqueueAsync(f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));

        await f.Service().ProcessSessionAsync("session", default);

        var demoted = (await f.Store.GetAsync("session", admitted.Item.Id, "owner"))!;
        Assert.Equal(0, f.Runtime.Steers);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, demoted.DeliveryPolicy);
        Assert.Equal("steer_unavailable", demoted.ErrorCode);
    }

    [Fact]
    public async Task UnconfirmedCommittedSteerFailsNonRetryablyForGhostErrorDisplay()
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = SessionInputDeliveryResult.Unavailable(
            "steer_unconfirmed", "Provider accepted the write but did not confirm delivery", retryable: false);
        var admitted = await f.Store.EnqueueAsync(f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));

        await f.Service().ProcessSessionAsync("session", default);

        var failed = (await f.Store.GetAsync("session", admitted.Item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Failed, failed.State);
        Assert.Equal("steer_unconfirmed", failed.ErrorCode);
        Assert.False(failed.ErrorRetryable);
        Assert.Null(failed.NextAttemptAt);
        Assert.Equal(1, f.Runtime.Steers);
        Assert.Equal(0, f.Runtime.Sends);
    }

    [Fact]
    public async Task MaintenancePauseDurablyAdmitsThenDemotesSteer()
    {
        using var f = new Fixture { Paused = true };

        var result = await f.Service().AdmitAsync(
            f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));

        Assert.False(result.Existing);
        Assert.Equal(SessionInputQueueState.Pending, result.Item.State);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, result.Item.DeliveryPolicy);
        Assert.Equal("steer_unavailable", result.Item.ErrorCode);
        Assert.Equal(0, f.Runtime.Steers);
    }

    [Fact]
    public async Task RestartRecoveryReconcilesDeliveringSteersAgainstCanonicalTranscriptImmediately()
    {
        using var f = new Fixture();
        var persisted = await f.Store.EnqueueAsync(f.Submission("persisted", SessionInputDeliveryPolicy.SteerCurrent));
        var claimed = Assert.Single(await f.Store.ClaimSteerBatchAsync(
            "session", "old-process", TimeSpan.FromMinutes(30)));
        Assert.True(claimed.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(20));
        f.Runtime.Messages.Add(new UnifiedMessageRecord
        {
            SessionId = "session", Role = "user", EventType = "text", Content = "persisted",
            MessageUid = claimed.DeliveredMessageUid, Timestamp = DateTimeOffset.UtcNow,
        });

        await f.Service().RecoverSteeringDeliveriesAsync(default);

        var delivered = (await f.Store.GetAsync("session", persisted.Item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Delivered, delivered.State);
        Assert.Equal(claimed.DeliveredMessageUid, delivered.DeliveredMessageUid);

        var missing = await f.Store.EnqueueAsync(f.Submission("missing", SessionInputDeliveryPolicy.SteerCurrent));
        var missingClaim = Assert.Single(await f.Store.ClaimSteerBatchAsync(
            "session", "old-process", TimeSpan.FromMinutes(30)));
        Assert.True(missingClaim.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(20));

        await f.Service().RecoverSteeringDeliveriesAsync(default);

        var demoted = (await f.Store.GetAsync("session", missing.Item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Pending, demoted.State);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, demoted.DeliveryPolicy);
        Assert.Equal("steer_unavailable", demoted.ErrorCode);
        Assert.Null(demoted.DeliveredMessageUid);
        Assert.Equal(0, f.Runtime.Steers);
        Assert.Equal(0, f.Runtime.Sends);
    }

    // 2026-11-04: one queued item can be asked to steer. Pending only, never an interrupt.

    [Fact]
    public async Task PromoteSteersOnePendingItemIntoTheRunningTurnAndLeavesTheRestQueued()
    {
        using var f = new Fixture();
        var earlier = await f.Store.EnqueueAsync(f.Submission("earlier", SessionInputDeliveryPolicy.AfterCurrent));
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Delivered, promotion.Outcome);
        Assert.Equal(1, f.Runtime.Steers);
        Assert.Equal(0, f.Runtime.Sends);
        Assert.Equal(0, f.Runtime.Interrupts);
        Assert.Equal("note", f.Runtime.LastSteerText);
        Assert.Equal(SessionInputDeliveryPolicy.SteerCurrent, promotion.Item.DeliveryPolicy);
        Assert.Equal(SessionInputQueueState.Pending,
            (await f.Store.GetAsync("session", earlier.Item.Id, "owner"))!.State);
    }

    [Fact]
    public async Task PromoteKeepsTheRequestedPolicySoAKeyedRetryOfTheOriginalStillMatches()
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = SessionInputDeliveryResult.Busy();
        var submission = f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent);
        var note = await f.Store.EnqueueAsync(submission);

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Steering, promotion.Outcome);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, promotion.Item.Requested);
        Assert.Equal(SessionInputDeliveryReasons.Promoted, promotion.Item.DeliveryReason);
        var replay = await f.Store.EnqueueAsync(submission);
        Assert.True(replay.Existing);
        Assert.Equal(note.Item.Id, replay.Item.Id);
    }

    [Fact]
    public async Task PromoteOfALegacyRowWithNoRequestedPolicyRecordsTheOriginalPolicy()
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = SessionInputDeliveryResult.Busy();
        var submission = f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent);
        var note = await f.Store.EnqueueAsync(submission);
        await using (var connection = new SqliteConnection($"Data Source={f.Attachments.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var legacy = connection.CreateCommand();
            legacy.CommandText = "UPDATE SessionQueuedInputs SET RequestedDeliveryPolicy=NULL";
            await legacy.ExecuteNonQueryAsync();
        }

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, promotion.Item.Requested);
        Assert.True((await f.Store.EnqueueAsync(submission)).Existing);
    }

    [Fact]
    public async Task PromoteIsIdempotentOnAnItemAlreadySteeringDeliveringOrDelivered()
    {
        using var f = new Fixture();
        var service = f.Service();
        var steering = await f.Store.EnqueueAsync(f.Submission("steer", SessionInputDeliveryPolicy.SteerCurrent));
        var claimed = Assert.Single(await f.Store.ClaimSteerBatchAsync("session", "other", TimeSpan.FromMinutes(30)));
        Assert.Equal(steering.Item.Id, claimed.Id);

        var delivering = (await service.PromoteToSteerAsync("session", claimed.Id, "owner"))!;
        Assert.Equal(SessionSteerOutcomes.Delivering, delivering.Outcome);
        Assert.Equal(0, f.Runtime.Steers);

        await f.Store.MarkDeliveredAsync([claimed], claimed.MessageUid);
        var delivered = (await service.PromoteToSteerAsync("session", claimed.Id, "owner"))!;
        Assert.Equal(SessionSteerOutcomes.Delivered, delivered.Outcome);
        Assert.Equal(0, f.Runtime.Steers);

        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));
        f.Runtime.SteerResult = SessionInputDeliveryResult.Busy();
        var first = (await service.PromoteToSteerAsync("session", note.Item.Id, "owner"))!;
        var again = (await service.PromoteToSteerAsync("session", note.Item.Id, "owner"))!;
        Assert.Equal(SessionSteerOutcomes.Steering, first.Outcome);
        Assert.Equal(SessionSteerOutcomes.Steering, again.Outcome);
        f.Runtime.SteerResult = SessionInputDeliveryResult.Accepted();
        await service.ProcessSessionAsync("session", default);
        await service.ProcessSessionAsync("session", default);
        Assert.Equal(SessionInputQueueState.Delivered, (await f.Store.GetAsync("session", note.Item.Id, "owner"))!.State);
        Assert.Equal(3, f.Runtime.Steers); // two Busy passes, then the one accepted delivery
    }

    [Fact]
    public async Task PromoteRefusesAFailedOrCancelledOrInterruptItemAndAnUnknownOneIsNull()
    {
        using var f = new Fixture();
        var service = f.Service();
        Assert.Null(await service.PromoteToSteerAsync("session", "q_missing", "owner"));

        var other = await f.Store.EnqueueAsync(f.Submission("mine", SessionInputDeliveryPolicy.AfterCurrent));
        Assert.Null(await service.PromoteToSteerAsync("session", other.Item.Id, "someone-else"));

        await service.CancelAsync("session", other.Item.Id, "owner");
        var cancelled = await Assert.ThrowsAsync<SessionInputQueueStoreException>(
            () => service.PromoteToSteerAsync("session", other.Item.Id, "owner"));
        Assert.Equal("not_promotable", cancelled.Code);

        var interrupt = await f.Store.EnqueueAsync(f.Submission("stop", SessionInputDeliveryPolicy.InterruptCurrent));
        var refused = await Assert.ThrowsAsync<SessionInputQueueStoreException>(
            () => service.PromoteToSteerAsync("session", interrupt.Item.Id, "owner"));
        Assert.Equal("not_promotable", refused.Code);
        Assert.Equal(SessionInputDeliveryPolicy.InterruptCurrent,
            (await f.Store.GetAsync("session", interrupt.Item.Id, "owner"))!.DeliveryPolicy);
    }

    [Fact]
    public async Task PromoteOfAFailedItemIsRefused()
    {
        using var f = new Fixture();
        var failed = await f.Store.EnqueueAsync(f.Submission("failed", SessionInputDeliveryPolicy.SteerCurrent));
        var claimed = await f.Store.ClaimSteerBatchAsync("session", "other", TimeSpan.FromMinutes(30));
        await f.Store.FailAsync(claimed, "provider_rejected", "No", retryable: false);

        var refused = await Assert.ThrowsAsync<SessionInputQueueStoreException>(
            () => f.Service().PromoteToSteerAsync("session", failed.Item.Id, "owner"));
        Assert.Equal("not_promotable", refused.Code);
    }

    [Fact]
    public async Task PromoteWithoutSteeringSupportAnswersSoAndLeavesTheItemQueued()
    {
        using var f = new Fixture();
        f.Runtime.SupportsSteering = false;
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionSteerRefusals.Unsupported, promotion.Reason);
        var stored = (await f.Store.GetAsync("session", note.Item.Id, "owner"))!;
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, stored.DeliveryPolicy);
        Assert.Null(stored.DeliveryReason);
        Assert.Equal(0, f.Runtime.Steers);
    }

    [Fact]
    public async Task PromoteDuringMaintenanceAnswersSoAndLeavesTheItemQueued()
    {
        using var f = new Fixture { Paused = true };
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionSteerRefusals.Maintenance, promotion.Reason);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent,
            (await f.Store.GetAsync("session", note.Item.Id, "owner"))!.DeliveryPolicy);
    }

    [Theory]
    [InlineData(SessionStatus.Starting)]
    [InlineData(SessionStatus.Stopped)]
    [InlineData(SessionStatus.Error)]
    public async Task PromoteOnASessionThatIsNotRunningAnswersSo(SessionStatus status)
    {
        using var f = new Fixture();
        f.Runtime.Info.Status = status;
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionSteerRefusals.SessionNotRunning, promotion.Reason);
    }

    [Fact]
    public async Task PromoteBehindAFailedHeadAnswersSoInsteadOfWaitingSilently()
    {
        using var f = new Fixture();
        var head = await f.Store.EnqueueAsync(f.Submission("head", SessionInputDeliveryPolicy.SteerCurrent));
        var claimed = await f.Store.ClaimSteerBatchAsync("session", "other", TimeSpan.FromMinutes(30));
        await f.Store.FailAsync(claimed, "provider_rejected", "No", retryable: false);
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(head.Item.Id, promotion.Queue.HeadItemId);
        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionSteerRefusals.FailedHead, promotion.Reason);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent,
            (await f.Store.GetAsync("session", note.Item.Id, "owner"))!.DeliveryPolicy);
    }

    [Fact]
    public async Task PromoteCannotSkipRecoveryRevalidationOfAnInputWaitingForIt()
    {
        using var f = new Fixture();
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));
        await f.Store.RequireRecoveryAsync(note.Item, default);

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionSteerRefusals.RecoveryRequired, promotion.Reason);
        Assert.Equal(0, f.Runtime.Steers);
    }

    [Fact]
    public async Task PromoteIntoAParkedQuestionDemotesThenDeliversOnceAfterTheTurn()
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = SessionInputDeliveryResult.NotSteerable("question_parked", "Question parked");
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));
        var service = f.Service();

        var promotion = (await service.PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Unavailable, promotion.Outcome);
        Assert.Equal(SessionInputDeliveryReasons.SteerUnavailable, promotion.Reason);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, promotion.Item.DeliveryPolicy);
        Assert.Equal(SessionInputDeliveryReasons.SteerUnavailable, promotion.Item.DeliveryReason);

        f.Runtime.Info.Status = SessionStatus.Idle;
        await service.ProcessSessionAsync("session", default);
        await service.ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Sends);
        Assert.Equal(SessionInputQueueState.Delivered, (await f.Store.GetAsync("session", note.Item.Id, "owner"))!.State);
    }

    [Fact]
    public async Task PromoteOfADemotedSteerCanTryAgainAndClearsTheDemotion()
    {
        using var f = new Fixture();
        f.Runtime.SteerResult = SessionInputDeliveryResult.Busy();
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.SteerCurrent));
        await f.Store.DemotePendingSteerAsync("session");

        var promotion = (await f.Service().PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionSteerOutcomes.Steering, promotion.Outcome);
        Assert.Null(promotion.Item.ErrorCode);
        Assert.Null(promotion.Item.DeliveryReason); // it was asked to steer, so nothing differs now
        Assert.Equal(SessionInputDeliveryPolicy.SteerCurrent, promotion.Item.Requested);
    }

    [Fact]
    public async Task StorePromoteClearsADemotionSoTheRowStopsSayingItWaits()
    {
        using var f = new Fixture();
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.SteerCurrent));
        await f.Store.DemotePendingSteerAsync("session");
        Assert.Equal("steer_unavailable", (await f.Store.GetAsync("session", note.Item.Id, "owner"))!.ErrorCode);

        var promoted = (await f.Store.PromoteToSteerAsync("session", note.Item.Id, "owner"))!;

        Assert.Equal(SessionInputDeliveryPolicy.SteerCurrent, promoted.DeliveryPolicy);
        Assert.Null(promoted.ErrorCode);
        Assert.Null(promoted.ErrorMessage);
    }

    [Fact]
    public async Task StorePromoteLeavesARowAClaimOrACancelAlreadyTook()
    {
        using var f = new Fixture();
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));
        var claimed = Assert.Single(await f.Store.ClaimBatchAsync("session", "other", TimeSpan.FromMinutes(2)));
        Assert.Equal(note.Item.Id, claimed.Id);

        Assert.Null(await f.Store.PromoteToSteerAsync("session", note.Item.Id, "owner"));

        var held = (await f.Store.GetAsync("session", note.Item.Id, "owner"))!;
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, held.DeliveryPolicy);
        Assert.Equal(SessionInputQueueState.Delivering, held.State);
        Assert.Null(held.DeliveryReason);

        var cancelled = await f.Store.EnqueueAsync(f.Submission("cancelled", SessionInputDeliveryPolicy.AfterCurrent));
        await f.Store.CancelAsync("session", cancelled.Item.Id, "owner");
        Assert.Null(await f.Store.PromoteToSteerAsync("session", cancelled.Item.Id, "owner"));
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent,
            (await f.Store.GetAsync("session", cancelled.Item.Id, "owner"))!.DeliveryPolicy);
    }

    [Fact]
    public async Task PromoteRacingTurnEndRunsAsTheNextTurnExactlyOnce()
    {
        using var f = new Fixture();
        f.Runtime.Info.Status = SessionStatus.Idle;
        var note = await f.Store.EnqueueAsync(f.Submission("note", SessionInputDeliveryPolicy.AfterCurrent));
        var service = f.Service();

        var promotion = (await service.PromoteToSteerAsync("session", note.Item.Id, "owner"))!;
        await service.ProcessSessionAsync("session", default);

        Assert.Equal(SessionSteerOutcomes.Delivered, promotion.Outcome);
        Assert.Equal(SessionInputDeliveryReasons.NoActiveTurn, promotion.Reason);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, promotion.Item.DeliveryPolicy);
        Assert.Equal(1, f.Runtime.Sends);
        Assert.Equal(0, f.Runtime.Steers);
    }

    [Fact]
    public async Task SeveralSteeredEventsFromOneCallerCoalesceButNotWithAPersonsSteer()
    {
        using var f = new Fixture();
        var note1 = await f.Store.EnqueueAsync(f.Submission("note1", SessionInputDeliveryPolicy.SteerCurrent, "/event", "e1"));
        var note2 = await f.Store.EnqueueAsync(f.Submission("note2", SessionInputDeliveryPolicy.SteerCurrent, "/event", "e2"));
        var person = await f.Store.EnqueueAsync(f.Submission("person", SessionInputDeliveryPolicy.SteerCurrent, "/message"));
        var service = f.Service();

        await service.ProcessSessionAsync("session", default);

        Assert.Equal(1, f.Runtime.Steers);
        Assert.Equal("note1\nnote2", f.Runtime.LastSteerText?.Replace("\r", ""));
        var first = (await f.Store.GetAsync("session", note1.Item.Id, "owner"))!;
        var second = (await f.Store.GetAsync("session", note2.Item.Id, "owner"))!;
        Assert.Equal(first.MessageUid, first.DeliveredMessageUid);
        Assert.Equal(first.MessageUid, second.DeliveredMessageUid);
        Assert.Equal(first.TurnId, second.TurnId);
        Assert.Equal(SessionInputQueueState.Pending, (await f.Store.GetAsync("session", person.Item.Id, "owner"))!.State);

        await service.ProcessSessionAsync("session", default);

        Assert.Equal(2, f.Runtime.Steers);
        var personal = (await f.Store.GetAsync("session", person.Item.Id, "owner"))!;
        Assert.Equal(personal.MessageUid, personal.DeliveredMessageUid);
        Assert.Equal(first.TurnId, personal.TurnId);
        Assert.NotEqual(first.ProvenanceScope, personal.ProvenanceScope);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "redcompute-steering-tests", Guid.NewGuid().ToString("N"));
        internal readonly RedComputeConfig Config = new();
        internal readonly InputAttachmentStore Attachments;
        internal readonly SessionInputQueueStore Store;
        internal readonly RuntimeProvider Runtime;
        internal readonly CapabilityRegistry Registry = new();
        internal bool Paused;

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Attachments = new(Config, Path.Combine(Root, "bytes"), Path.Combine(Root, "input.db"));
            Store = new(Config, Attachments);
            var provider = DispatchProxy.Create<ITestProvider, RuntimeProvider>();
            Runtime = (RuntimeProvider)(object)provider;
            Registry.Register("ai-session",
                new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
                new CapabilityConfig(),
                new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        }

        internal SessionInputQueueService Service() => new(
            Store, Attachments, Registry, null!, new WebSocketBroadcaster(), (_, _) => { },
            deliveryPaused: () => Paused);

        internal SessionInputQueueSubmission Submission(string text, SessionInputDeliveryPolicy policy,
            string route = "/accepted", string? eventId = null) => new(
            "session", "owner", [new("text", text)], text, null,
            new(JobProvenance.CurrentSchemaVersion,
                new("redcompute", new("app", "nova", null, "Nova"), new("http", route, "POST")),
                new("user", "Owner", Id: "owner"), new("user", "owner", "Owner"),
                route == "/message"
                    ? [new JobContextReference("person-send", "x", Route: route)]
                    : eventId is null ? [] : [new JobContextReference("event", eventId)], new(),
                JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            policy, "m_" + Guid.NewGuid().ToString("N"), [], Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class RuntimeProvider : DispatchProxy
    {
        internal UnifiedSessionInfo Info = new()
        {
            Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".",
            UserId = "owner", Status = SessionStatus.Active, SupportsSteering = true,
        };
        internal bool SupportsSteering = true;
        internal int Steers;
        internal int Sends;
        internal int Interrupts;
        internal string? LastSteerText;
        internal List<UnifiedMessageRecord> Messages = [];
        internal SessionInputDeliveryResult SteerResult = SessionInputDeliveryResult.Accepted();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Capabilities":
                    return SessionCapabilities.SendMessage
                        | (SupportsSteering ? SessionCapabilities.SupportsSteering : SessionCapabilities.None);
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case nameof(ISessionProvider.GetSession):
                    Info.SupportsSteering = SupportsSteering;
                    return (Info, Messages.ToList());
                case nameof(ISessionProvider.TrySteerInputAsync):
                    Steers++;
                    LastSteerText = SessionInputFormatting.UserText((IReadOnlyList<SessionInputPart>)args![1]!);
                    return Task.FromResult(SteerResult);
                case nameof(ISessionProvider.InterruptSession):
                    Interrupts++;
                    return InterruptResult.Interrupted;
                case nameof(ISessionProvider.TrySendInputAsync):
                    Sends++;
                    Info.Status = SessionStatus.Active;
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
