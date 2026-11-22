using RedCompute.Core.Providers;
using RedCompute.Node;
using RedCompute.Node.Runtimes;
using RedCompute.PluginSdk.Hosting;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// A start that arrives while a stop is in progress (review P4-P7, item 3). The stop is held open with a gated host: a
/// kill takes effect only when the test releases it, and a process can hide its exit from the stop's poll. A start
/// during the stop is refused with <c>runtime_stopping</c>; and a stop reaps only the process it stopped, never one
/// started after it.
/// </summary>
public sealed class RuntimeStopRaceTests : IAsyncLifetime
{
    private const string Id = "comfy-m";
    private readonly NodeHome _home = new("race");
    private GatedHost _host = null!;
    private RuntimeManager _manager = null!;

    public Task InitializeAsync()
    {
        new RuntimeRegistry(_home.Host, _home.Paths).Add(new RuntimeRegistration
        {
            Id = Id, Kind = RuntimeKinds.ComfyUi, Managed = HostRuntimeManagement.Spawn, Port = RuntimeAgentTests.FreePort(),
            ServerPath = _home.Root, AcceptedGraphsRunInstalledNodes = true,
        });
        _host = new GatedHost(_home.Host);
        var env = _home.Environment();
        _manager = new RuntimeManager(new NodeEnvironment
        {
            Paths = env.Paths, Host = _host, KeyStores = env.KeyStores, Log = env.Log, SweepContainmentAtStart = false,
        }, RuntimeAgentTests.Sleeper, (_, _) => Task.FromResult(true));
        _manager.Load();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _host.ReleaseAll();
        await _manager.StopAllManagedAsync("test end");
        await _manager.DisposeAsync();
        foreach (var process in _host.Started)
            if (RuntimeAgentTests.Alive(process.Id)) System.Diagnostics.Process.GetProcessById(process.Id).Kill(entireProcessTree: true);
        _home.Dispose();
    }

    [Fact]
    public async Task A_start_during_a_stop_is_refused_as_stopping_and_starts_once_the_stop_is_done()
    {
        await _manager.StartAsync(Id, RuntimeRequester.Server);
        var first = Assert.Single(_host.Started);
        first.HoldKill = true;

        var stop = _manager.StopAsync(Id, RuntimeRequester.Server, TimeSpan.FromSeconds(20));
        await Wait.UntilAsync(() => first.KillRequested, "the stop's kill");

        var refused = await Assert.ThrowsAsync<HostRuntimeException>(() => _manager.StartAsync(Id, RuntimeRequester.Server));
        Assert.Equal(HostRuntimeException.Stopping, refused.Code);
        Assert.Single(_host.Started); // nothing spawned during the stop
        Assert.True(RuntimeAgentTests.Alive(first.Id));

        first.ReleaseKill();
        Assert.Equal(BackendStatus.Stopped, (await stop.WaitAsync(TimeSpan.FromSeconds(30))).Status);
        Assert.False(RuntimeAgentTests.Alive(first.Id));

        // Once stopped, a start goes through: a new process, tracked.
        await _manager.StartAsync(Id, RuntimeRequester.Server);
        Assert.Equal(2, _host.Started.Count);
        Assert.Equal(_host.Started[1].Id, RuntimeAgentTests.PidOf(_home, Id));
    }

    [Fact]
    public async Task A_stop_reaps_only_the_process_it_stopped_not_one_started_after_its_exit()
    {
        await _manager.StartAsync(Id, RuntimeRequester.Server);
        var first = Assert.Single(_host.Started);
        first.HideExit = true; // the stop's poll keeps seeing it alive after it really exited

        var stop = _manager.StopAsync(Id, RuntimeRequester.Server, TimeSpan.FromSeconds(20));
        // The kill went through and the exit handler reaped the first process: the stop is over as far as the runtime
        // goes, and a start is allowed again, while the stop's own poll has not seen the exit yet.
        await Wait.UntilAsync(() => _manager.Status(Id)!.Status == BackendStatus.Stopped, "the exit handler's reap", timeoutMs: 20_000);
        Assert.False(stop.IsCompleted);
        await _manager.StartAsync(Id, RuntimeRequester.Server);
        var second = _host.Started[1];
        Assert.True(RuntimeAgentTests.Alive(second.Id));

        first.HideExit = false;
        var view = await stop.WaitAsync(TimeSpan.FromSeconds(30));

        // The second process is still the runtime: tracked, its state file kept, not reaped by the first one's stop.
        Assert.NotEqual(BackendStatus.Stopped, view.Status);
        Assert.NotEqual(BackendStatus.Stopped, _manager.Status(Id)!.Status);
        Assert.True(RuntimeAgentTests.Alive(second.Id));
        Assert.Equal(second.Id, RuntimeAgentTests.PidOf(_home, Id));
        await _manager.StopAsync(Id, RuntimeRequester.Server, TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => !RuntimeAgentTests.Alive(second.Id), "the second process stopped");
    }

    [Fact]
    public async Task A_stop_whose_kill_does_not_take_ends_in_error_and_never_leaves_the_runtime_stopping()
    {
        // Review follow-up 2: the process outlives the grace. The stop ends, Stopping is cleared, the runtime reads
        // Error "did not stop", and a later start is not refused as runtime_stopping; a later stop tries again.
        await _manager.StartAsync(Id, RuntimeRequester.Server);
        var first = Assert.Single(_host.Started);
        first.HoldKill = true;

        var view = await _manager.StopAsync(Id, RuntimeRequester.Server, TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(BackendStatus.Error, view.Status);
        Assert.Contains("did not stop", view.StatusDetail);
        Assert.True(RuntimeAgentTests.Alive(first.Id));
        Assert.Contains(_home.Environment().Audit.Read(), record => (string?)record["outcome"] == "stop-failed");

        // Not runtime_stopping: the live process is still the runtime, so nothing new is spawned.
        var started = await _manager.StartAsync(Id, RuntimeRequester.Server);
        Assert.Equal(BackendStatus.Error, started.Status);
        Assert.Single(_host.Started);
        Assert.Equal(first.Id, RuntimeAgentTests.PidOf(_home, Id));

        // A later stop, whose kill now takes, stops it.
        first.ReleaseKill();
        first.HoldKill = false;
        Assert.Equal(BackendStatus.Stopped, (await _manager.StopAsync(Id, RuntimeRequester.Server, TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(30))).Status);
        await Wait.UntilAsync(() => !RuntimeAgentTests.Alive(first.Id), "the process stopped");
    }

    /// <summary>
    /// The node's real host, with every started process wrapped in a <see cref="GatedProcess"/>, and no containment
    /// (Keep returns null, as a platform without it does): the stop then goes through the process's own kill.
    /// </summary>
    private sealed class GatedHost(IExecutionHost inner) : IExecutionHost, IHostProcesses, IHostContainment
    {
        private readonly List<GatedProcess> _started = [];

        public IReadOnlyList<GatedProcess> Started { get { lock (_started) return _started.ToList(); } }

        public void ReleaseAll()
        {
            foreach (var process in Started)
            {
                process.HideExit = false;
                process.ReleaseKill();
            }
        }

        public string NodeId => inner.NodeId;
        public HostPath Path(string value) => inner.Path(value);
        public IHostPathRules PathRules => inner.PathRules;
        public IHostProcesses Processes => this;
        public IHostContainment Containment => this;
        public IHostFileSystem Files => inner.Files;
        public IHostNetwork Network => inner.Network;
        public IHostPlatform Platform => inner.Platform;
        public IHostHardware Hardware => inner.Hardware;
        public IHostQueries Queries => inner.Queries;
        public IHostRuntimes Runtimes => inner.Runtimes;

        public IHostProcess? Start(HostSpawnSpec spec)
        {
            var process = inner.Processes.Start(spec);
            if (process is null) return null;
            var gated = new GatedProcess(process);
            lock (_started) _started.Add(gated);
            return gated;
        }

        public bool TryKillRecorded(int? pid, string cliName, Action<string>? log = null) => inner.Processes.TryKillRecorded(pid, cliName, log);
        public bool KillIdentity(int pid, long createdUtcTicks, string owner) => inner.Processes.KillIdentity(pid, createdUtcTicks, owner);
        public IReadOnlyList<HostProcessInfo> List() => inner.Processes.List();
        public IReadOnlyList<HostProcessInfo> List(bool executablePaths) => inner.Processes.List(executablePaths);
        public HostProcessInfo? Find(int pid) => inner.Processes.Find(pid);

        public Action<string>? Log { get => inner.Containment.Log; set => inner.Containment.Log = value; }
        public Func<int, long, bool>? IsProtected { get => inner.Containment.IsProtected; set => inner.Containment.IsProtected = value; }
        public string LedgerDirectory => inner.Containment.LedgerDirectory;
        public int SweepLedgers() => 0;
        public bool IsSessionMember(int pid) => inner.Containment.IsSessionMember(pid);
        public IContainedTree? FindByRoot(int rootPid) => null;
        public IContainedTree? Keep(int rootPid, string owner, string ledgerPath) => null;
        public IContainedTree? Keep(int rootPid, long createdUtcTicks, string owner, string ledgerPath) => null;
        public IContainedTree? Resume(string owner, string ledgerPath) => null;
        public IContainedTree? Resume(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities) => null;
        public DetachedLaunch StartDetachedKept(DetachedSpawnSpec spec) => throw new NotSupportedException("not in this test");
    }

    /// <summary>A real process whose kill can be held until released, and whose exit can be hidden from HasExited.</summary>
    internal sealed class GatedProcess(IHostProcess inner) : IHostProcess
    {
        private readonly object _gate = new();
        private bool? _pendingKill;
        private readonly int _pid = inner.Id;
        private readonly long _created = inner.CreatedUtcTicks;

        public bool HoldKill { get; set; }
        public volatile bool HideExit;
        public bool KillRequested { get; private set; }

        public void ReleaseKill()
        {
            bool? pending;
            lock (_gate)
            {
                HoldKill = false;
                pending = _pendingKill;
                _pendingKill = null;
            }
            if (pending is { } tree) inner.Kill(tree);
        }

        public void Kill(bool entireProcessTree)
        {
            lock (_gate)
            {
                KillRequested = true;
                if (HoldKill)
                {
                    _pendingKill = entireProcessTree;
                    return;
                }
            }
            inner.Kill(entireProcessTree);
        }

        public bool HasExited => !HideExit && inner.HasExited;
        public string NodeId => inner.NodeId;
        public int Id => _pid; // kept: the manager disposes the process once it is reaped
        public DateTime StartTimeUtc => inner.StartTimeUtc;
        public long CreatedUtcTicks => _created;
        public StreamWriter StandardInput => inner.StandardInput;
        public StreamReader StandardOutput => inner.StandardOutput;
        public StreamReader StandardError => inner.StandardError;
        public void BeginOutputReadLine() => inner.BeginOutputReadLine();
        public void BeginErrorReadLine() => inner.BeginErrorReadLine();

        public event Action<string?>? OutputLine
        {
            add => inner.OutputLine += value;
            remove => inner.OutputLine -= value;
        }

        public event Action<string?>? ErrorLine
        {
            add => inner.ErrorLine += value;
            remove => inner.ErrorLine -= value;
        }

        public bool EnableRaisingEvents { get => inner.EnableRaisingEvents; set => inner.EnableRaisingEvents = value; }

        public event EventHandler? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public int ExitCode => inner.ExitCode;
        public bool WaitForExit(int milliseconds) => inner.WaitForExit(milliseconds);
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => inner.WaitForExitAsync(cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
