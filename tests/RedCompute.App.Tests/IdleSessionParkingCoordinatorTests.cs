using System.Reflection;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class IdleSessionParkingCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-idle-parking", Guid.NewGuid().ToString("N"));
    private readonly RedComputeConfig _config = new();
    private readonly InputAttachmentStore _attachments;
    private readonly SessionInputQueueStore _store;
    private readonly SessionInputQueueService _queue;
    private readonly ParkingProvider _runtime;
    private readonly IdleSessionParkingCoordinator _coordinator;
    private DateTimeOffset _now;

    public IdleSessionParkingCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _attachments = new(_config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        _store = new(_config, _attachments);
        var provider = DispatchProxy.Create<ITestProvider, ParkingProvider>();
        _runtime = (ParkingProvider)(object)provider;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session",
            new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
            new CapabilityConfig(),
            new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        _queue = new(_store, _attachments, registry, null!, new WebSocketBroadcaster(), (_, _) => { });
        _coordinator = new(registry, _queue, TimeSpan.FromHours(1), (_, _) => { }, utcNow: () => _now);
    }

    [Fact]
    public async Task Parks_only_after_one_continuous_eligible_hour()
    {
        var start = DateTimeOffset.Parse("2026-10-23T17:00:00Z");
        await _coordinator.SweepAsync(start);
        await _coordinator.SweepAsync(start.AddMinutes(59));
        Assert.Equal(0, _runtime.Parks);

        await _coordinator.SweepAsync(start.AddHours(1));
        Assert.Equal(1, _runtime.Parks);
        Assert.Equal(SessionStatus.Stopped, _runtime.Info.Status);
        Assert.Equal(SessionStopReasons.IdleTimeout, _runtime.Info.StopReason);
    }

    [Fact]
    public async Task Queue_and_background_work_each_reset_the_eligibility_clock()
    {
        var start = DateTimeOffset.Parse("2026-10-23T17:00:00Z");
        await _coordinator.SweepAsync(start);
        var queued = (await _store.EnqueueAsync(Submission())).Item;
        await _coordinator.SweepAsync(start.AddHours(2));
        Assert.Equal(0, _runtime.Parks);

        await _store.CancelAsync("session", queued.Id, "owner");
        await _coordinator.SweepAsync(start.AddHours(2).AddMinutes(1));
        _runtime.Info.BackgroundTasks = [new BackgroundTaskInfo
        {
            TaskId = "task", Kind = BackgroundTaskKinds.Agent,
            Status = BackgroundTaskStatuses.Running, StartedAt = start,
        }];
        await _coordinator.SweepAsync(start.AddHours(4));
        Assert.Equal(0, _runtime.Parks);

        _runtime.Info.BackgroundTasks = [];
        await _coordinator.SweepAsync(start.AddHours(4).AddMinutes(1));
        await _coordinator.SweepAsync(start.AddHours(5));
        Assert.Equal(0, _runtime.Parks);
        await _coordinator.SweepAsync(start.AddHours(5).AddMinutes(1));
        Assert.Equal(1, _runtime.Parks);
    }

    [Fact]
    public async Task Leaving_idle_resets_the_clock_instead_of_using_historical_last_activity()
    {
        var start = DateTimeOffset.Parse("2026-10-23T17:00:00Z");
        await _coordinator.SweepAsync(start);
        _runtime.Info.Status = SessionStatus.Active;
        await _coordinator.SweepAsync(start.AddHours(2));
        _runtime.Info.Status = SessionStatus.Idle;
        await _coordinator.SweepAsync(start.AddHours(2).AddMinutes(1));
        await _coordinator.SweepAsync(start.AddHours(3));
        Assert.Equal(0, _runtime.Parks);
        await _coordinator.SweepAsync(start.AddHours(3).AddMinutes(1));
        Assert.Equal(1, _runtime.Parks);
    }

    [Fact]
    public async Task Active_and_idle_events_between_sweeps_reset_the_clock()
    {
        var start = DateTimeOffset.Parse("2026-10-23T17:00:00Z");
        await _coordinator.SweepAsync(start);
        _now = start.AddMinutes(30);
        _runtime.Info.Status = SessionStatus.Active;
        _runtime.Emit("session.updated");
        _now = start.AddMinutes(31);
        _runtime.Info.Status = SessionStatus.Idle;
        _runtime.Emit("session.updated");

        await _coordinator.SweepAsync(start.AddMinutes(90));
        Assert.Equal(0, _runtime.Parks);
        await _coordinator.SweepAsync(start.AddMinutes(91));
        Assert.Equal(1, _runtime.Parks);
    }

    private static SessionInputQueueSubmission Submission() => new(
        "session", "owner", [new("text", "queued")], "queued", null,
        RedCompute.Core.Jobs.JobProvenance.DirectRedCompute(
            "POST", "/ai-session/sessions/session/message",
            new RedCompute.Core.Jobs.JobBeneficiary("user", "owner"), "request", null),
        SessionInputDeliveryPolicy.AfterCurrent, "message", [], "key");

    public void Dispose()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "input.db")}");
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    public interface ITestProvider : ISessionProvider, IIdleSessionParkingProvider, IBackendProvider, IPluginEventSource { }

    public class ParkingProvider : DispatchProxy
    {
        internal UnifiedSessionInfo Info = new()
        {
            Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".",
            UserId = "owner", Status = SessionStatus.Idle, BackgroundTasks = [],
        };
        internal int Parks;
        private Action<string, object>? _pluginEvent;

        internal void Emit(string type) => _pluginEvent?.Invoke(type, Info);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ProviderId": return "test";
                case "add_PluginEvent": _pluginEvent += (Action<string, object>)args![0]!; return null;
                case "remove_PluginEvent": _pluginEvent -= (Action<string, object>)args![0]!; return null;
                case nameof(IIdleSessionParkingProvider.GetResidentIdleSessions):
                    return Info.Status == SessionStatus.Idle
                        ? (IReadOnlyList<UnifiedSessionInfo>)[Info]
                        : Array.Empty<UnifiedSessionInfo>();
                case nameof(IIdleSessionParkingProvider.TryParkIdleSessionAsync):
                    if (Info.Status != SessionStatus.Idle) return Task.FromResult(false);
                    Parks++;
                    Info.Status = SessionStatus.Stopped;
                    Info.StopReason = (string)args![1]!;
                    return Task.FromResult(true);
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
