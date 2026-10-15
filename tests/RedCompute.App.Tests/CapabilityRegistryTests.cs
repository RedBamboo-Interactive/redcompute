using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using System.Reflection;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public async Task External_capability_is_running_without_a_provider()
    {
        var registry = new CapabilityRegistry();
        registry.Register("workflow", new CapabilityDefinition
        {
            Slug = "workflow",
            DisplayName = "Workflow",
            ExecutionMode = CapabilityExecutionMode.External,
            WorkerDisplayName = "RedLeaf Workflow Engine",
        }, new CapabilityConfig(), [], null);

        var entry = Assert.IsType<CapabilityEntry>(registry.Get("workflow"));
        Assert.True(entry.IsExternal);
        Assert.Null(entry.ActiveProvider);
        Assert.Equal(BackendStatus.Running, await registry.GetStatus("workflow"));
    }

    [Fact]
    public async Task Provider_capability_without_a_provider_remains_stopped()
    {
        var registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen",
            DisplayName = "Image Generation",
        }, new CapabilityConfig(), [], null);

        Assert.Equal(BackendStatus.Stopped, await registry.GetStatus("image-gen"));
    }

    [Fact]
    public async Task Infrastructure_shutdown_marks_live_sessions_recoverable_before_stopping_provider()
    {
        var proxy = DispatchProxy.Create<ITestProvider, ShutdownProvider>();
        var provider = (ShutdownProvider)(object)proxy;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition
        {
            Slug = "ai-session",
            DisplayName = "AI Session",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = proxy }, "test");

        await registry.StopAll(SessionStopReasons.OrphanedOnRestart);

        Assert.Equal(
            [$"force:{SessionStopReasons.OrphanedOnRestart}", "stop", "dispose"],
            provider.Calls);
        Assert.Equal(SessionStatus.Stopped, provider.Session.Status);
        Assert.Equal(SessionStopReasons.OrphanedOnRestart, provider.Session.StopReason);
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class ShutdownProvider : DispatchProxy
    {
        public UnifiedSessionInfo Session { get; } = new()
        {
            Id = "session-a",
            Provider = "test",
            ProjectName = "Test",
            ProjectPath = ".",
            Status = SessionStatus.Active,
        };

        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(ISessionProvider.GetSessions):
                    return new List<UnifiedSessionInfo> { Session };
                case nameof(ISessionProvider.ForceKillAsync):
                    var reason = args!.Length > 1 ? (string)args[1]! : "user_stopped";
                    Calls.Add($"force:{reason}");
                    Session.Status = SessionStatus.Stopped;
                    Session.StopReason = reason;
                    return Task.CompletedTask;
                case nameof(IBackendProvider.StopAsync):
                    Calls.Add("stop");
                    return Task.CompletedTask;
                case nameof(IAsyncDisposable.DisposeAsync):
                    Calls.Add("dispose");
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }
}
