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

    // Start-up starts only a capability's default provider. OpenCode sits beside the default Codex, so its model list
    // stayed empty until the second read after every restart (25 Nov 2026): warm-up must reach the others too.
    [Fact]
    public async Task Warm_up_reaches_every_provider_that_asks_for_it_not_only_the_default()
    {
        var codex = WarmProbe.Create();
        var opencode = WarmProbe.Create();
        var failing = WarmProbe.Create(fail: true);
        var plain = DispatchProxy.Create<ITestProvider, ShutdownProvider>();
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider>
            {
                ["codex"] = codex, ["opencode"] = opencode, ["broken"] = failing, ["plain"] = plain,
            }, "codex");
        var log = new List<string>();

        await registry.WarmProvidersAsync(log.Add);

        Assert.Equal(1, WarmProbe.Of(codex).Warmed);
        Assert.Equal(1, WarmProbe.Of(opencode).Warmed);
        Assert.Equal(1, WarmProbe.Of(failing).Warmed);
        Assert.Contains(log, line => line.Contains("Warm-up failed") && line.Contains("cli missing"));
        Assert.Empty(((ShutdownProvider)(object)plain).Calls);
    }

    public interface IWarmTestProvider : IBackendProvider, IProviderWarmup { }

    public class WarmProbe : DispatchProxy
    {
        public int Warmed;
        private bool _fail;

        public static IWarmTestProvider Create(bool fail = false)
        {
            var proxy = DispatchProxy.Create<IWarmTestProvider, WarmProbe>();
            Of(proxy)._fail = fail;
            return proxy;
        }

        public static WarmProbe Of(IWarmTestProvider provider) => (WarmProbe)(object)provider;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IProviderWarmup.WarmAsync)) throw new NotSupportedException(method.Name);
            Interlocked.Increment(ref Warmed);
            return _fail ? Task.FromException(new InvalidOperationException("cli missing")) : Task.CompletedTask;
        }
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
            => SessionProviderProxy.Route(method, args, InvokeProvider);

        private object? InvokeProvider(MethodInfo? method, object?[]? args)
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
