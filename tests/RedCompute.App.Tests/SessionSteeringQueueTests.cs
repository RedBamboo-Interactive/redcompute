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

        internal SessionInputQueueSubmission Submission(string text, SessionInputDeliveryPolicy policy) => new(
            "session", "owner", [new("text", text)], text, null,
            new(JobProvenance.CurrentSchemaVersion,
                new("redcompute", new("app", "nova", null, "Nova"), new("http", "/accepted", "POST")),
                new("user", "Owner", Id: "owner"), new("user", "owner", "Owner"), [], new(),
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
                case nameof(ISessionProvider.TrySendInputAsync):
                    Sends++;
                    Info.Status = SessionStatus.Active;
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
