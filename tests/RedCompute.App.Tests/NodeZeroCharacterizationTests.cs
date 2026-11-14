using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Characterization tests for the app-level sites that move behind the execution host in node zero
/// (design section 4). They pin today's behaviour on real processes this test starts, and must stay
/// green unchanged across the move.
/// </summary>
[Collection(LaunchGoldenCollection.Name)]
public sealed class NodeZeroCharacterizationTests : IDisposable
{
    private readonly List<Process> _started = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-node-zero-characterization", Guid.NewGuid().ToString("N"));

    public NodeZeroCharacterizationTests() => Directory.CreateDirectory(_root);

    // ---- HardwareMonitorService: VRAM attribution walks PPIDs from each provider's ProcessId ----------

    [WindowsFact("the attribution tree is cmd.exe -> ping.exe")]
    public async Task Gpu_attribution_maps_a_provider_root_and_its_descendants_to_its_capability_only()
    {
        var root = Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });
        var child = await ChildOfAsync(root.Id);
        var unrelated = Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });

        var registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["fake"] = new PidProvider(root.Id) }, "fake");
        registry.Register("tts", new CapabilityDefinition { Slug = "tts", DisplayName = "TTS" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["none"] = new PidProvider(null) }, "none");

        var map = Attribution(registry);

        Assert.Equal("image-gen", map[(uint)root.Id]);
        Assert.Equal("image-gen", map[(uint)child]);
        Assert.False(map.ContainsKey((uint)unrelated.Id));
        Assert.DoesNotContain("tts", map.Values);
        // Nothing outside the provider's own tree is attributed (cmd.exe's conhost is inside it).
        Assert.All(map.Keys, pid => Assert.True(DescendsFrom((int)pid, root.Id), $"PID {pid} attributed outside the tree"));
    }

    private static bool DescendsFrom(int pid, int ancestor)
    {
        for (var depth = 0; depth < 10 && pid > 0; depth++)
        {
            if (pid == ancestor) return true;
            var parent = ParentOf(pid);
            if (parent == pid) return false;
            pid = parent;
        }
        return false;
    }

    [Fact]
    public void Gpu_attribution_is_empty_without_a_registry_or_provider_pids()
    {
        Assert.Empty(Attribution(null));
        var registry = new CapabilityRegistry();
        registry.Register("tts", new CapabilityDefinition { Slug = "tts", DisplayName = "TTS" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["none"] = new PidProvider(null) }, "none");
        Assert.Empty(Attribution(registry));
    }

    private static Dictionary<uint, string> Attribution(CapabilityRegistry? registry)
    {
        using var service = new HardwareMonitorService();
        typeof(HardwareMonitorService).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, registry);
        var build = typeof(HardwareMonitorService).GetMethod("BuildPidToCapabilityMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Dictionary<uint, string>)build.Invoke(service, null)!;
    }

    // ---- OwnedProcessKiller: a recorded PID is killed when it is the CLI or one of its launchers ----

    [WindowsFact("cmd.exe is the npm .cmd shim launcher on Windows")]
    public void Recorded_pid_of_a_launcher_shim_is_killed_as_a_tree()
    {
        var shim = Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });

        Assert.True(OwnedProcessKiller.TryKill(shim.Id, "claude"));
        Assert.True(shim.WaitForExit(5000));
    }

    [Fact]
    public void Recorded_pid_that_no_longer_exists_is_not_killed()
    {
        var gone = Start(new ProcessStartInfo("ping", OperatingSystem.IsWindows() ? "-n 1 127.0.0.1" : "-c 1 127.0.0.1")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        Assert.True(gone.WaitForExit(10_000));
        Assert.False(OwnedProcessKiller.TryKill(gone.Id, "ping"));
    }

    // ---- LongLivedProcessRegistry: launch scripts, state and the detached, kept, user-profile launch ----

    [WindowsFact("the long-lived registry is Windows-only (cmd scripts, desktop-shell parent)")]
    public async Task Long_lived_launch_writes_the_same_scripts_and_state_and_runs_detached_and_kept()
    {
        using var probe = new ProbeHarness("long-lived");
        var server = probe.Install("server");
        var work = Path.Combine(probe.Root, "work");
        Directory.CreateDirectory(work);
        const string Marker = "REDCOMPUTE_GOLDEN_HOST_MARKER";
        Environment.SetEnvironmentVariable(Marker, "host-only");
        var registry = new LongLivedProcessRegistry(probe.Root, _ => { }, monitor: false)
        {
            Clock = () => new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
        };
        try
        {
            LongLivedProcessResult? result = null;
            bool? sessionMember = null, isProtected = null;
            var launches = await probe.RunAsync(() =>
            {
                result = registry.Launch(new LongLivedLaunchRequest("golden server", $"\"{server}\" --serve", work,
                    Description: "golden", Restart: "on-exit",
                    Environment: new Dictionary<string, string> { ["GOLDEN_LONG_LIVED"] = "1" }),
                    new LongLivedOwner("user", "Nova", "session-1"));
                return Task.CompletedTask;
            }, whileHeld: async launch =>
            {
                // The kept tree learns its descendants by parentage scanning: give it its hot window.
                var ticks = CreationTicks(launch.Pid);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!registry.IsProtected(launch.Pid, ticks) && DateTime.UtcNow < deadline) await Task.Delay(50);
                isProtected = registry.IsProtected(launch.Pid, ticks);
                sessionMember = SessionProcessContainment.IsSessionMember(launch.Pid);
            });
            Assert.Equal(200, result!.Status);
            var view = result.Process!;
            probe.Normalizer.Add(view.Id, "<id>");

            var (launch, _) = Assert.Single(launches);
            var entries = Path.Combine(probe.Root, "long-lived-processes");
            var state = JsonNode.Parse(File.ReadAllText(Path.Combine(probe.Root, "long-lived-processes.json")))!.AsArray();
            var record = state.Single()!.AsObject();
            foreach (var volatileKey in new[] { "pid", "processCreatedUtcTicks", "lastSeenAliveAt", "lastLaunchedAt" })
                if (record.ContainsKey(volatileKey)) record[volatileKey] = "<volatile>";

            var golden = new JsonObject
            {
                ["entryScript"] = probe.Normalizer.Apply(File.ReadAllText(Path.Combine(entries, $"{view.Id}.cmd"))),
                ["commandScript"] = probe.Normalizer.Apply(File.ReadAllText(Path.Combine(entries, $"{view.Id}.run.cmd"))),
                ["state"] = probe.Normalizer.Apply(record),
                ["launch"] = new JsonObject
                {
                    ["args"] = new JsonArray(launch.Args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["cwd"] = probe.Normalizer.Apply(launch.Cwd),
                    // The launch runs on the user's registry environment, not RedCompute's: the overlay is
                    // there and a variable only this test host has is not.
                    ["overlay"] = launch.Environment.GetValueOrDefault("GOLDEN_LONG_LIVED"),
                    ["hostOnlyVariableInherited"] = launch.Environment.ContainsKey(Marker),
                    // Kept by the registry (never reaped as a session), outside every session job.
                    ["sessionMember"] = sessionMember,
                    ["protected"] = isProtected,
                },
            };
            Assert.Equal(200, registry.Stop(view.Id).Status);
            LaunchGoldens.Verify("B-long-lived-launch", golden);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Marker, null);
            foreach (var view in registry.List()) registry.Stop(view.Id);
        }
    }

    private static long CreationTicks(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return SessionProcessContainment.CreationUtcTicks(process);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private Process Start(ProcessStartInfo start)
    {
        var process = Process.Start(start)!;
        lock (_started) _started.Add(process);
        return process;
    }

    /// <summary>The first child of <paramref name="parent"/>, found by its parent PID.</summary>
    private static async Task<int> ChildOfAsync(int parent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var candidate in Process.GetProcessesByName("PING"))
            {
                using (candidate)
                    if (ParentOf(candidate.Id) == parent) return candidate.Id;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No child of {parent} appeared");
    }

    private static int ParentOf(int pid)
    {
        var method = typeof(HardwareMonitorService).GetMethod("GetParentProcessId", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (int)method.Invoke(null, [pid])!;
    }

    public void Dispose()
    {
        lock (_started)
            foreach (var process in _started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class PidProvider(int? pid) : IBackendProvider
    {
        public string Name => "fake";
        public string CapabilitySlug => "fake";
        public int? ProcessId => pid;
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
