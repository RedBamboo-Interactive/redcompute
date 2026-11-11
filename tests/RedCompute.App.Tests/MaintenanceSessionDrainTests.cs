using System.Reflection;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class MaintenanceSessionDrainTests
{
    [Fact]
    public async Task Graceful_session_completion_does_not_force_stop_provider()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.IdleAfterReads = 2;
        var logs = new List<string>();
        var drain = CreateDrain(registry, logs);

        var restartSessions = await drain.DrainAsync(_ => Task.CompletedTask);

        Assert.Equal(0, provider.ForceKillCalls);
        Assert.Equal(SessionStatus.Idle, provider.Session.Status);
        Assert.Contains(restartSessions, session => session.SessionId == provider.Session.Id);
        Assert.Contains(logs, line => line.Contains("Waiting for 1 active provider session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grace_timeout_force_stops_blocker_and_reaches_quiescence()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        var logs = new List<string>();
        var drain = CreateDrain(registry, logs);

        var restartSessions = await drain.DrainAsync(_ => Task.CompletedTask);

        Assert.Equal(1, provider.ForceKillCalls);
        Assert.Equal(MaintenanceSessionDrain.StopReason, provider.ForceKillReason);
        Assert.Equal(SessionStatus.Stopped, provider.Session.Status);
        Assert.Contains(restartSessions, session =>
            session.Provider == provider.Session.Provider
            && session.SessionId == provider.Session.Id
            && session.JobId == provider.Session.JobId);
        Assert.Contains(logs, line => line.Contains("force-stopping 1 blocking session", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("Forced maintenance drain reached quiescence", StringComparison.Ordinal));
    }

    // 2026-11-11: the replacement process tells a cut worker what happened, so the drain records it.
    [Fact]
    public async Task The_checkpoint_says_which_turn_was_forced_and_which_background_work_was_running()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.Session.BackgroundTasks =
        [
            new BackgroundTaskInfo { TaskId = "b1", Kind = "shell", Status = BackgroundTaskStatuses.Running, Description = "proof run" },
            new BackgroundTaskInfo { TaskId = "b0", Kind = "shell", Status = BackgroundTaskStatuses.Completed, Description = "done" },
        ];
        var drain = CreateDrain(registry, []);

        var planned = Assert.Single(await drain.DrainAsync(_ => Task.CompletedTask));

        Assert.True(planned.ForcedStop);
        Assert.Equal(SessionWorkStates.Working, planned.WorkState);
        Assert.Equal(["proof run"], planned.BackgroundTasks!);
    }

    [Fact]
    public async Task A_turn_that_ends_in_time_is_not_recorded_as_forced()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.IdleAfterReads = 2;
        var planned = Assert.Single(await CreateDrain(registry, []).DrainAsync(_ => Task.CompletedTask));
        Assert.False(planned.ForcedStop);
    }

    [Fact]
    public async Task Grace_timeout_also_breaks_stuck_queue_delivery_after_provider_stop()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        var drain = CreateDrain(registry, []);
        var queueWaits = 0;

        await drain.DrainAsync(async ct =>
        {
            queueWaits++;
            if (queueWaits == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        Assert.Equal(2, queueWaits);
        Assert.Equal(1, provider.ForceKillCalls);
        Assert.Equal(SessionStatus.Stopped, provider.Session.Status);
    }

    [Fact]
    public async Task Deployment_fails_only_when_provider_remains_active_after_force_deadline()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.IgnoreForceKill = true;
        var logs = new List<string>();
        var drain = CreateDrain(registry, logs, forcedTimeout: TimeSpan.FromMilliseconds(40));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => drain.DrainAsync(_ => Task.CompletedTask));

        Assert.Equal(1, provider.ForceKillCalls);
        Assert.Contains("provider sessions remained active", error.Message, StringComparison.Ordinal);
        Assert.Contains(provider.Session.Id, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_reports_active_count_then_zero_without_stopping_a_completed_turn()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.IdleAfterReads = 2;
        var counts = new List<int>();
        var drain = new MaintenanceSessionDrain(registry, (_, _) => { },
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), counts.Add);
        await drain.DrainAsync(_ => Task.CompletedTask);
        Assert.Contains(1, counts);
        Assert.Equal(0, counts.Last());
        Assert.Equal(0, provider.ForceKillCalls);
    }

    [Fact]
    public async Task A_parked_approval_is_denied_with_why_instead_of_holding_the_restart()
    {
        var (registry, provider) = RegistryWithSession(SessionStatus.Active);
        provider.Pending.Add(SessionAccessEndpointTests.Permission("perm-1"));
        provider.Pending.Add(SessionQuestionCallbackTests.Question("q-1"));
        provider.IdleOnDeny = true;
        var drain = CreateDrain(registry, []);

        await drain.DrainAsync(_ => Task.CompletedTask);

        var answer = Assert.Single(provider.Answers);
        Assert.Equal(("perm-1", SessionPermissionDecisions.Deny, MaintenanceSessionDrain.RestartDenial),
            (answer.RequestId, answer.Decision, answer.DeclineReason));
        Assert.Equal(0, provider.ForceKillCalls);
    }

    private static MaintenanceSessionDrain CreateDrain(
        CapabilityRegistry registry,
        List<string> logs,
        TimeSpan? forcedTimeout = null)
        => new(
            registry,
            (message, _) => logs.Add(message),
            TimeSpan.FromMilliseconds(40),
            forcedTimeout ?? TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(5));

    private static (CapabilityRegistry Registry, FakeProvider Provider) RegistryWithSession(SessionStatus status)
    {
        var proxy = DispatchProxy.Create<ITestProvider, FakeProvider>();
        var provider = (FakeProvider)(object)proxy;
        provider.Session.Status = status;
        var registry = new CapabilityRegistry();
        registry.Register(
            "ai-session",
            new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
            new CapabilityConfig(),
            new Dictionary<string, IBackendProvider> { ["test"] = proxy },
            "test");
        return (registry, provider);
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class FakeProvider : DispatchProxy
    {
        private int _reads;

        public UnifiedSessionInfo Session { get; } = new()
        {
            Id = "session-a",
            Provider = "test",
            ProjectName = "Test project",
            ProjectPath = ".",
            Title = "Blocking test session",
            JobId = Guid.NewGuid(),
        };

        public int? IdleAfterReads { get; set; }
        public List<PendingSessionQuestion> Pending { get; } = [];
        public List<SessionQuestionAnswer> Answers { get; } = [];
        public bool IdleOnDeny { get; set; }
        public bool IgnoreForceKill { get; set; }
        public int ForceKillCalls { get; private set; }
        public string? ForceKillReason { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionProvider.GetSessions))
            {
                _reads++;
                if (IdleAfterReads is { } target && _reads >= target)
                    Session.Status = SessionStatus.Idle;
                return new List<UnifiedSessionInfo> { Session };
            }

            if (method.Name == nameof(ISessionProvider.GetPendingQuestions))
                return (IReadOnlyList<PendingSessionQuestion>)[.. Pending];
            if (method.Name == nameof(ISessionProvider.SubmitQuestionAnswer))
            {
                var answer = (SessionQuestionAnswer)args![1]!;
                Answers.Add(answer);
                Pending.RemoveAll(q => q.RequestId == answer.RequestId);
                if (IdleOnDeny) Session.Status = SessionStatus.Idle;
                return QuestionAnswerResult.Denied;
            }

            if (method.Name == nameof(ISessionProvider.ForceKillAsync))
            {
                ForceKillCalls++;
                ForceKillReason = args!.Length > 1 ? (string?)args[1] : null;
                if (!IgnoreForceKill)
                {
                    Session.Status = SessionStatus.Stopped;
                    Session.StopReason = ForceKillReason ?? "user_stopped";
                }
                return Task.CompletedTask;
            }

            throw new NotSupportedException(method.Name);
        }
    }
}
