using System.Reflection;
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

public sealed class ClaudeIdleNativeMcpQueueTests : IDisposable
{
    private const string Logical = "revoked";
    private const string Sibling = "sibling";
    private const string Owner = "owner";
    private const string Token = "synthetic-scoped-authority";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-mcp-queue", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task No_wake_is_required_to_retire_the_revoked_idle_generation_before_one_delivery()
    {
        var (store, queue, provider) = Create();
        var item = await Enqueue(store);
        await queue.ProcessSessionAsync(Logical, default);

        Assert.Equal(0, provider.RevokedCalls);
        Assert.Equal(new[] { "plan:" + Logical, "exit:" + Logical, "resume:" + Logical, "send:" + Logical }, provider.Events);
        var receipt = (await store.GetAsync(Logical, item.Id, Owner))!;
        Assert.Equal(item.Id, receipt.Id);
        Assert.Equal(SessionInputQueueState.Delivered, receipt.State);
        Assert.Equal(1, receipt.AttemptCount);
        Assert.Equal(item.MessageUid, receipt.DeliveredMessageUid);
        Assert.Equal("native-thread", provider.Main.ProviderSessionId);
    }

    [Theory]
    [InlineData(IdleNativeMcpCheck.PlanUnavailable, "native_mcp_plan_unavailable")]
    [InlineData(IdleNativeMcpCheck.ExitUnconfirmed, "native_mcp_exit_unconfirmed")]
    public async Task Unverifiable_policy_or_exit_holds_the_fifo_head_and_leaves_a_sibling_usable(
        IdleNativeMcpCheck result, string error)
    {
        var (store, queue, provider) = Create();
        provider.Check = result;
        var item = await Enqueue(store);
        var later = await Enqueue(store, uid: "later");
        await queue.ProcessSessionAsync(Logical, default);
        var blocked = (await store.GetAsync(Logical, item.Id, Owner))!;
        Assert.Equal(SessionInputQueueState.Pending, blocked.State);
        Assert.Equal(error, blocked.ErrorCode);
        Assert.Equal(0, blocked.AttemptCount);
        Assert.Equal(SessionInputQueueState.Pending, (await store.GetAsync(Logical, later.Id, Owner))!.State);
        Assert.Equal(new[] { "plan:" + Logical }, provider.Events);

        var sibling = await Enqueue(store, Sibling);
        await queue.ProcessSessionAsync(Sibling, default);
        Assert.Equal(SessionInputQueueState.Delivered, (await store.GetAsync(Sibling, sibling.Id, Owner))!.State);
        Assert.Equal(new[] { "plan:" + Logical, "send:" + Sibling }, provider.Events);
        Assert.Equal(0, provider.RevokedCalls);
    }

    [Fact]
    public async Task Explicit_stop_wins_when_a_post_revoke_check_holds_the_delivery_lock()
    {
        var (store, queue, provider) = Create();
        provider.ReleaseCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = await Enqueue(store);
        var processing = queue.ProcessSessionAsync(Logical, default);
        await provider.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = queue.StopSessionAsync(Logical, () =>
        {
            provider.Main.Status = SessionStatus.Stopped;
            provider.Main.StopReason = SessionStopReasons.UserStopped;
            return Task.CompletedTask;
        }, default);
        provider.ReleaseCheck.SetResult();
        await Task.WhenAll(processing, stop);
        await queue.ProcessSessionAsync(Logical, default);

        Assert.Equal(SessionStopReasons.UserStopped, provider.Main.StopReason);
        Assert.Equal(new[] { "plan:" + Logical, "exit:" + Logical }, provider.Events);
        Assert.Equal(0, (await store.GetAsync(Logical, item.Id, Owner))!.AttemptCount);
    }

    [Fact]
    public async Task Unverified_replacement_plan_leaves_the_parked_input_unclaimed()
    {
        var (store, queue, provider) = Create();
        provider.RejectResume = true;
        var item = await Enqueue(store);
        await queue.ProcessSessionAsync(Logical, default);

        Assert.Equal(new[] { "plan:" + Logical, "exit:" + Logical, "resume:" + Logical }, provider.Events);
        Assert.Equal(SessionStopReasons.NativeMcpPolicyChanged, provider.Main.StopReason);
        var receipt = (await store.GetAsync(Logical, item.Id, Owner))!;
        Assert.Equal(SessionInputQueueState.Pending, receipt.State);
        Assert.Equal("native_mcp_plan_unavailable", receipt.ErrorCode);
        Assert.Equal(0, receipt.AttemptCount);
    }

    private (SessionInputQueueStore, SessionInputQueueService, Fixture) Create()
    {
        Directory.CreateDirectory(_root);
        var config = new RedComputeConfig();
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        var store = new SessionInputQueueStore(config, attachments);
        var provider = DispatchProxy.Create<IProvider, Fixture>();
        var fixture = (Fixture)(object)provider;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Fixture" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["claude-code"] = provider }, "claude-code");
        var queue = new SessionInputQueueService(store, attachments, registry, null!, new WebSocketBroadcaster(), (_, _) => { })
        {
            RecoverNativeMcpAuthority = (session, item, _) =>
            {
                Assert.Equal(Logical, session.Id);
                Assert.Equal(session.Id, item.SessionId);
                Assert.Equal(Owner, item.OwnerUserId);
                return Task.FromResult(new SessionRecoveryAuthority(Token, null, false));
            }
        };
        return (store, queue, fixture);
    }

    private static async Task<SessionInputQueueItem> Enqueue(SessionInputQueueStore store, string id = Logical, string uid = "one")
        => (await store.EnqueueAsync(new(id, Owner, [new QueuedSessionInputPart("text", uid)], uid, null,
            new JobProvenance(JobProvenance.CurrentSchemaVersion,
                new JobOrigin("redcompute", new JobAppReference("app", "nova", null, "Nova"), new JobEntrypoint("http", "/message", "POST")),
                new JobActor("agent", "Nova", Id: "nova", EntityId: "1209a868-bdc9-4c45-8bc7-9aa87003d83a"),
                new JobBeneficiary("user", Owner), [new("discussion", "discussion")], new JobTrace(),
                JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            SessionInputDeliveryPolicy.AfterCurrent, uid, [], uid))).Item;

    public interface IProvider : ISessionProvider, IBackendProvider, IIdleNativeMcpProvider { }

    public class Fixture : DispatchProxy
    {
        internal readonly UnifiedSessionInfo Main = Info(Logical, "native-thread");
        private readonly UnifiedSessionInfo _sibling = Info(Sibling, "sibling-thread");
        internal readonly List<string> Events = [];
        internal int RevokedCalls;
        internal IdleNativeMcpCheck Check = IdleNativeMcpCheck.Parked;
        internal bool RejectResume;
        internal readonly TaskCompletionSource CheckEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? ReleaseCheck;
        private bool _oldTreeAlive = true;

        private async Task<IdleNativeMcpCheck> CheckAsync(string? id, object? token)
        {
            Assert.Equal(Token, token);
            Events.Add("plan:" + id);
            CheckEntered.TrySetResult();
            if (ReleaseCheck is not null) await ReleaseCheck.Task;
            if (Check == IdleNativeMcpCheck.Parked)
            {
                _oldTreeAlive = false;
                Main.Status = SessionStatus.Stopped;
                Main.StopReason = SessionStopReasons.NativeMcpPolicyChanged;
                Events.Add("exit:" + id);
            }
            return Check;
        }

        private static UnifiedSessionInfo Info(string id, string thread) => new()
        {
            Id = id, Provider = "claude-code", ProjectName = "Fixture", ProjectPath = ".", UserId = Owner,
            ProviderSessionId = thread, Status = SessionStatus.Idle
        };

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => SessionProviderProxy.Route(method, args, Handle);

        private object? Handle(MethodInfo? method, object?[]? args)
        {
            var id = args?.FirstOrDefault() as string;
            var info = id == Logical ? Main : id == Sibling ? _sibling : null;
            switch (method!.Name)
            {
                case nameof(ISessionProvider.GetSession):
                    return (info, new List<UnifiedMessageRecord>());
                case "get_Capabilities":
                    return SessionCapabilities.Resume;
                case nameof(IIdleNativeMcpProvider.HasResidentNativeMcp):
                    return id == Logical && _oldTreeAlive;
                case nameof(IIdleNativeMcpProvider.CheckIdleNativeMcpAsync):
                    return CheckAsync(id, args![2]);
                case "get_LastStartErrorCode":
                    return RejectResume ? "native_mcp_plan_unavailable" : null;
                case nameof(ISessionProvider.ResumeSessionAsync):
                    Assert.False(_oldTreeAlive);
                    Assert.Equal(Token, SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"]);
                    Events.Add("resume:" + id);
                    if (RejectResume) return Task.FromResult<UnifiedSessionInfo?>(null);
                    Main.Status = SessionStatus.Idle;
                    Main.StopReason = null;
                    return Task.FromResult<UnifiedSessionInfo?>(Main);
                case nameof(ISessionProvider.TrySendInputAsync):
                    if (id == Logical && _oldTreeAlive) RevokedCalls++;
                    Events.Add("send:" + id);
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public void Dispose() => ScratchDirectory.Delete(_root);
}
