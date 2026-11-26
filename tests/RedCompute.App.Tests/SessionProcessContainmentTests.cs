using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

// A session's processes die with the session, however they detached; a registered long-lived
// server does not. Real processes: what failed on 11 Nov was the operating system's view of
// ancestry, which no fake can reproduce. Evidence: memory/artifacts/7e8aa955.
[Collection("Session process containment")]
public sealed class SessionProcessContainmentTests : IDisposable
{
    private static readonly string WindowsPowerShell =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
    private const string PingArguments = "-n 600 127.0.0.1";

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-containment", Guid.NewGuid().ToString("N"));
    private readonly List<(int Pid, long Created)> _spawned = [];
    private readonly string _previousLedger = SessionProcessContainment.LedgerDirectory;

    public SessionProcessContainmentTests()
    {
        Directory.CreateDirectory(_root);
        // The cheapest process that proves each behaviour is ping.exe: one native process, no runtime or profile to start,
        // alive for ten minutes unless something kills it. Only the two tests that are about PowerShell itself start any.
        //
        // `start /b` from a cmd that exits at once: the ping's parent is dead, like `nohup x &`.
        File.WriteAllText(Path.Combine(_root, "detach.cmd"), $"@start \"probe\" /b \"{Ping}\" {PingArguments} >nul\r\n");
        File.WriteAllText(Path.Combine(_root, "orphan-root.cmd"),
            $"@cmd /d /c \"%~dp0detach.cmd\"\r\n@\"{Ping}\" {PingArguments} >nul\r\n");
        // PowerShell 7's Start-Process, run from a pwsh that then exits: the ping leaves every job.
        File.WriteAllText(Path.Combine(_root, "start-process.ps1"), """
            Start-Process -WindowStyle Hidden -FilePath $args[0] -ArgumentList '-n','600','127.0.0.1'
            """);
        File.WriteAllText(Path.Combine(_root, "escapee-root.ps1"), """
            & pwsh -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\start-process.ps1" $args[0]; Start-Sleep 600
            """);
        SessionProcessContainment.LedgerDirectory = Path.Combine(_root, "ledger");
    }

    [Fact]
    public async Task A_child_whose_launching_shell_exited_dies_with_the_session_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        // `cmd /c start /b` returns at once, the way `nohup x &` does: the ping's parent is dead. The root stays alive on a
        // ping of its own, so the orphan is the one member ping whose parent is gone.
        var root = StartCmdScript("orphan-root.cmd");
        var tree = SessionProcessContainment.Contain(root, "test");
        Assert.NotNull(tree);

        var orphan = 0;
        await WaitUntilAsync(() => (orphan = FindProcesses("ping.exe").FirstOrDefault(pid => SessionProcessContainment.IsSessionMember(pid) && !ParentAlive(pid))) != 0,
            "an orphaned ping in the session's job (its launching cmd gone)", () => ChainState([root.Id]));
        Track(orphan);
        Assert.False(ParentAlive(orphan), "the probe must reproduce the orphan shape a PPID walk cannot see");
        Assert.True(SessionProcessContainment.IsSessionMember(orphan));

        var reaped = WaitForReap(tree!, root);
        root.Kill(); // the root alone, the way a CLI exits: nothing walks the tree for us
        await reaped;
        await AssertGoneAsync(orphan);
    }

    [Fact]
    public async Task A_pwsh_Start_Process_escapee_is_proven_by_parentage_and_reaped()
    {
        if (!OperatingSystem.IsWindows() || FindOnPath("pwsh.exe") is null) return;
        var root = StartScript("escapee-root.ps1", Ping);
        var tree = SessionProcessContainment.Contain(root, "test")!;

        var escapee = await WaitForEscapeePingAsync(tree, root);
        Assert.False(SessionProcessContainment.IsSessionMember(escapee), "pwsh 7 Start-Process leaves the job");
        await WaitUntilAsync(() => LedgerNames(SessionProcessContainment.LedgerDirectory, "*.json", escapee),
            $"the ledger in {SessionProcessContainment.LedgerDirectory} to name PID {escapee}", () => ChainState([root.Id, escapee]));
        Assert.Single(Directory.GetFiles(SessionProcessContainment.LedgerDirectory, "*.json"));

        var reaped = WaitForReap(tree, root);
        root.Kill();
        await reaped;
        await AssertGoneAsync(escapee);
        Assert.Empty(Directory.GetFiles(SessionProcessContainment.LedgerDirectory, "*.json"));
    }

    [Fact]
    public async Task An_adopted_escapee_survives_the_reap_and_stops_through_the_registry()
    {
        if (!OperatingSystem.IsWindows() || FindOnPath("pwsh.exe") is null) return;
        var registry = NewRegistry();
        SessionProcessContainment.IsProtected = registry.IsProtected;
        var root = StartScript("escapee-root.ps1", Ping);
        var tree = SessionProcessContainment.Contain(root, "test")!;
        var kept = await WaitForEscapeePingAsync(tree, root);

        var adopted = registry.Adopt(new LongLivedAdoptRequest(kept, "kept preview", 5289), new LongLivedOwner("user", "Nova", "s1"));
        Assert.Equal(200, adopted.Status);

        var reaped = WaitForReap(tree, root);
        root.Kill();
        await reaped;
        await Task.Delay(1000);
        Assert.True(IsAlive(kept), "a registered long-lived process is never reaped");
        Assert.True(registry.Get(adopted.Process!.Id)!.Running);

        Assert.Equal(200, registry.Stop(adopted.Process.Id).Status);
        await AssertGoneAsync(kept);
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task A_session_member_cannot_be_adopted_because_it_would_die_with_its_session()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var root = StartPing();
        var tree = SessionProcessContainment.Contain(root, "test")!;

        var result = registry.Adopt(new LongLivedAdoptRequest(root.Id, "inside"), new LongLivedOwner("user", "Nova", null));
        Assert.Equal(409, result.Status);
        Assert.Empty(registry.List());

        var reaped = WaitForReap(tree, root);
        root.Kill();
        await reaped;
    }

    [Fact]
    public async Task The_boot_sweep_kills_only_exact_recorded_identities_and_never_its_own_live_ledgers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var matching = StartPing();
        var stale = StartPing();
        var current = StartPing();
        Directory.CreateDirectory(SessionProcessContainment.LedgerDirectory);
        WriteLedger("old-run", Identity(matching), writerPid: 1, writerCreated: 1);
        // Same PID, different creation time: the PID was reused, so this is somebody else now.
        WriteLedger("reused", (stale.Id, SessionProcessContainment.CreationUtcTicks(stale) - 1), writerPid: 1, writerCreated: 1);
        using (var self = Process.GetCurrentProcess())
            WriteLedger("this-run", Identity(current), self.Id, SessionProcessContainment.CreationUtcTicks(self));

        Assert.Equal(1, SessionProcessContainment.SweepLedgers());

        await AssertGoneAsync(matching.Id);
        Assert.True(IsAlive(stale.Id));
        Assert.True(IsAlive(current.Id));
        Assert.Equal(["this-run.json"], Directory.GetFiles(SessionProcessContainment.LedgerDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_launched_long_lived_server_runs_outside_every_session_logs_to_file_and_stops_as_a_tree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var result = registry.Launch(new LongLivedLaunchRequest("test server",
            $"echo listening& \"{Ping}\" {PingArguments} >nul",
            _root, Description: "fixture"), new LongLivedOwner("user", "Nova", "session-1"));
        Assert.True(result.Status == 200, result.Error);
        var view = result.Process!;
        Track(view.Pid);

        Assert.True(view.Running);
        Assert.Equal("session-1", view.SessionId);
        Assert.False(SessionProcessContainment.IsSessionMember(view.Pid));
        await WaitUntilAsync(() => File.Exists(view.LogPath) && ReadShared(view.LogPath!).Contains("listening"),
            $"the log {view.LogPath} to say listening", () => ChainState([view.Pid]));

        // Parented to the desktop shell: not in this (RedCompute's) process tree, nor in its jobs.
        Assert.Equal("desktop", view.LaunchedVia);
        Assert.Equal(DesktopShellPid(), ParentPid(view.Pid));
        var server = await WaitForServerPingAsync(registry, view.Id, view.Pid);
        await WaitForRegistryLedgerAsync(server, view.Pid);

        // A restarted RedCompute takes the whole tree back from its ledger and can stop it.
        var reloaded = NewRegistry();
        Assert.True(reloaded.Get(view.Id)!.Running);
        Assert.True(reloaded.IsProtected(server, ProcessCreated(server)));

        Assert.Equal(200, reloaded.Stop(view.Id).Status);
        await AssertGoneAsync(server);
        await AssertGoneAsync(view.Pid);
        Assert.Equal(404, reloaded.Stop(view.Id).Status);
    }

    [Fact]
    public async Task A_long_lived_server_whose_own_root_exited_stays_listed_running_and_stoppable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        // The command itself detaches and returns, the way `pnpm exec` trees lost their root on 12 Nov.
        var result = registry.Launch(new LongLivedLaunchRequest("detaching server",
            $"start \"probe\" /b \"{Ping}\" {PingArguments} >nul",
            _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);
        var server = await WaitForServerPingAsync(registry, result.Process!.Id, result.Process.Pid);
        // Let the script's own cmd finish: only the detached server (and its console host) remain.
        await WaitUntilAsync(() => !IsAlive(result.Process.Pid), $"the launching cmd (PID {result.Process.Pid}) to exit",
            () => ChainState([result.Process.Pid, server]));

        var view = registry.Get(result.Process!.Id)!;
        Assert.True(view.Running);
        Assert.True(registry.IsProtected(server, ProcessCreated(server)));

        Assert.Equal(200, registry.Stop(view.Id).Status);
        await AssertGoneAsync(server);
    }

    [Fact]
    public async Task A_long_lived_command_that_chains_to_a_batch_file_without_call_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        // pnpm.cmd and npm.cmd are batch files: on 12 Nov `pnpm.cmd ... exec vite` never started
        // ("cannot find the batch label specified") because the command ran inside `call :run`.
        var chain = Path.Combine(_root, "chain.cmd");
        File.WriteAllText(chain, $"@\"{Ping}\" {PingArguments} >nul" + "\r\n");
        var registry = NewRegistry();
        var result = registry.Launch(new LongLivedLaunchRequest("chained",
            $"set CHAINED=1&& \"{chain}\"", _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);

        var server = await WaitForServerPingAsync(registry, result.Process!.Id, result.Process.Pid);
        Assert.DoesNotContain("batch label", ReadShared(result.Process!.LogPath!));

        Assert.Equal(200, registry.Stop(result.Process.Id).Status);
        await AssertGoneAsync(server);
    }

    [Fact]
    public void Launch_refuses_a_port_in_use_and_a_relative_directory()
    {
        var registry = NewRegistry();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var owner = new LongLivedOwner("user", "Nova", null);
            Assert.Equal(409, registry.Launch(new LongLivedLaunchRequest("busy", "echo", _root, port), owner).Status);
            Assert.Equal(400, registry.Launch(new LongLivedLaunchRequest("rel", "echo", "relative"), owner).Status);
            Assert.Equal(400, registry.Launch(new LongLivedLaunchRequest("two", "echo a\r\necho b", _root), owner).Status);
            Assert.Empty(registry.List());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_launched_server_is_outside_its_hosts_job_and_survives_a_RedCompute_restart()
    {
        if (!OperatingSystem.IsWindows()) return;
        // RedLeaf holds RedCompute in a kill-on-close job; on 12 Nov stopping RedLeaf killed every
        // launched server, because each was still a member of that job (memory/artifacts/909ca1ed).
        // Stand in for it: this process joins a job of its own, and what it starts the ordinary way
        // joins it too. Kill-on-close kills exactly the members, so membership is the whole question.
        var hostJob = HostJob.Value;
        Assert.True(hostJob != IntPtr.Zero, "this test process must be able to join a host job");
        var ordinary = StartPing();
        Assert.True(InJob(ordinary.Id, hostJob), "control: an ordinary child inherits its host's job");

        var registry = NewRegistry();
        var result = registry.Launch(new LongLivedLaunchRequest("survivor",
            $"\"{Ping}\" {PingArguments} >nul", _root),
            new LongLivedOwner("user", "Nova", "session-1"));
        Assert.True(result.Status == 200, result.Error);
        Track(result.Process!.Pid);
        var server = await WaitForServerPingAsync(registry, result.Process.Id, result.Process.Pid);
        await WaitForRegistryLedgerAsync(server, result.Process.Pid);
        foreach (var pid in registry.Get(result.Process.Id)!.ProcessIds)
            Assert.False(InJob(pid, hostJob), $"PID {pid} of the kept tree must not be in its host's job");

        // RedCompute stops: it lets go of the tree without killing it, and the next one takes it back.
        registry.Dispose();
        Assert.True(IsAlive(server));
        var restarted = NewRegistry();
        var view = restarted.Get(result.Process.Id)!;
        Assert.Equal("running", view.State);
        Assert.Contains(server, view.ProcessIds);
        Assert.True(restarted.IsProtected(server, ProcessCreated(server)));

        Assert.Equal(200, restarted.Stop(view.Id).Status);
        await AssertGoneAsync(server);
    }

    [Fact]
    public void Without_a_desktop_shell_a_launch_inside_a_job_that_forbids_breakaway_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(HostJob.Value != IntPtr.Zero); // a job without JOB_OBJECT_LIMIT_BREAKAWAY_OK
        var previous = DetachedProcessLauncher.ShellWindow;
        DetachedProcessLauncher.ShellWindow = () => IntPtr.Zero;
        try
        {
            var registry = NewRegistry();
            var result = registry.Launch(new LongLivedLaunchRequest("no shell", "echo hi", _root), new LongLivedOwner("user", "Nova", null));
            // A server that would die with its host is refused, never promised.
            Assert.Equal(400, result.Status);
            Assert.Contains("breakaway", result.Error);
            Assert.Empty(registry.List());
        }
        finally
        {
            DetachedProcessLauncher.ShellWindow = previous;
        }
    }

    [Fact]
    public async Task A_launched_server_gets_the_users_environment_and_its_own_never_RedComputes()
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("REDCOMPUTE_PRIVATE_PROBE", "leaked");
        try
        {
            var registry = NewRegistry();
            var result = registry.Launch(new LongLivedLaunchRequest("environment",
                "echo [%LONG_LIVED_PROBE%] [%REDCOMPUTE_PRIVATE_PROBE%] [%USERPROFILE%]", _root,
                Environment: new() { ["LONG_LIVED_PROBE"] = "given" }), new LongLivedOwner("user", "Nova", null));
            Assert.True(result.Status == 200, result.Error);
            var log = result.Process!.LogPath!;
            await WaitUntilAsync(() => ReadShared(log).Contains("[given]"), $"the probe line in {log}", () => ChainState([result.Process.Pid]));
            var line = ReadShared(log);
            // In a batch file an undefined variable expands to nothing: RedCompute's own did not leak.
            Assert.Contains($"[given] [] [{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}]", line);
            registry.Stop(result.Process.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDCOMPUTE_PRIVATE_PROBE", null);
        }
    }

    [Fact]
    public async Task An_exited_entry_shows_its_exit_time_and_stays_listed_until_its_owner_removes_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = registry.Launch(new LongLivedLaunchRequest("short", "exit /b 3", _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);
        var id = result.Process!.Id;

        await WaitUntilAsync(() =>
        {
            registry.Tick();
            return registry.Get(id)!.State == "exited";
        }, "the exit to be recorded", () => $"state {registry.Get(id)!.State}; " + ChainState([result.Process.Pid]));
        var view = registry.Get(id)!;
        Assert.False(view.Running);
        Assert.Empty(view.ProcessIds);
        Assert.NotNull(view.ExitedAt);
        Assert.InRange(view.ExitedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Null(view.Note);

        // A restart keeps it exactly as it was: exited, with the same time, not resurrected.
        registry.Dispose();
        var restarted = NewRegistry();
        restarted.Tick();
        var reloaded = restarted.Get(id)!;
        Assert.Equal("exited", reloaded.State);
        Assert.Equal(view.ExitedAt, reloaded.ExitedAt);

        Assert.Equal(200, restarted.Stop(id).Status);
        Assert.Empty(restarted.List());
    }

    /// <summary>
    /// 2026-11-26, the flake in the test above, and a product race: the view says "exited" as soon as the tree is
    /// empty, but the exit time was set only when a monitor pass released the process. Between the two, an exited
    /// entry showed no time, and a RedCompute stop then saved it with no exit at all, so the next RedCompute said no
    /// RedCompute had been watching. No monitor pass runs here, which holds that window open.
    /// </summary>
    [Fact]
    public async Task An_exit_no_monitor_pass_has_recorded_yet_has_its_time_and_survives_a_restart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = registry.Launch(new LongLivedLaunchRequest("short", "exit /b 3", _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);
        var id = result.Process!.Id;

        await WaitUntilAsync(() => registry.Get(id)!.State == "exited", "the exit to show",
            () => $"state {registry.Get(id)!.State}; " + ChainState([result.Process.Pid]));
        var view = registry.Get(id)!;
        Assert.NotNull(view.ExitedAt);
        Assert.InRange(view.ExitedAt!.Value, before, DateTimeOffset.UtcNow);

        registry.Dispose();
        var restarted = NewRegistry();
        var reloaded = restarted.Get(id)!;
        Assert.Equal("exited", reloaded.State);
        Assert.Equal(view.ExitedAt, reloaded.ExitedAt);
        Assert.Null(reloaded.Note);
        Assert.Equal(200, restarted.Stop(id).Status);
    }

    [Fact]
    public async Task An_entry_that_died_while_RedCompute_was_down_is_shown_dead_or_relaunched_as_it_asked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var owner = new LongLivedOwner("user", "Nova", null);
        var plain = registry.Launch(new LongLivedLaunchRequest("plain",
            $"\"{Ping}\" {PingArguments} >nul", _root), owner).Process!;
        var restarting = registry.Launch(new LongLivedLaunchRequest("restarting",
            $"\"{Ping}\" {PingArguments} >nul", _root,
            Restart: "on-exit"), owner).Process!;
        var first = await WaitForServerPingAsync(registry, plain.Id, plain.Pid);
        var second = await WaitForServerPingAsync(registry, restarting.Id, restarting.Pid);
        await WaitForRegistryLedgerAsync(first, plain.Pid);
        await WaitForRegistryLedgerAsync(second, restarting.Pid);

        // RedCompute is gone, and so, meanwhile, are both servers: every process their ledgers name.
        registry.Dispose();
        foreach (var pid in new[] { plain.Pid, restarting.Pid })
            using (var process = Process.GetProcessById(pid)) process.Kill(entireProcessTree: true);
        // Every identity the ledgers name, a console host included, so none is resumed half-dead.
        foreach (var ledger in Directory.GetFiles(Path.Combine(_root, "registry", "long-lived-processes"), "*.processes.json"))
            foreach (var entry in JsonDocument.Parse(File.ReadAllText(ledger)).RootElement.GetProperty("Escapees").EnumerateArray())
                await AssertGoneAsync(entry.GetProperty("Pid").GetInt32());

        var restarted = NewRegistry();
        var dead = restarted.Get(plain.Id)!;
        Assert.Equal("exited", dead.State);
        Assert.Null(dead.ExitedAt); // nobody saw it end
        Assert.NotNull(dead.LastSeenAliveAt);
        Assert.Contains("no RedCompute was watching", dead.Note);
        Assert.Equal("restarting", restarted.Get(restarting.Id)!.State);

        await WaitUntilAsync(() =>
        {
            restarted.Tick();
            return restarted.Get(restarting.Id)!.RestartCount >= 1;
        }, "the relaunch after its first backoff", () => $"state {restarted.Get(restarting.Id)!.State}, restarts {restarted.Get(restarting.Id)!.RestartCount}; "
            + ChainState([restarted.Get(restarting.Id)!.Pid]));
        var view = restarted.Get(restarting.Id)!;
        Assert.Equal("running", view.State);
        Assert.Equal(1, view.RestartCount);
        Assert.NotEqual(restarting.Pid, view.Pid);
        Track(view.Pid);
        var relaunched = await WaitForServerPingAsync(restarted, restarting.Id, view.Pid);

        Assert.Equal(200, restarted.Stop(restarting.Id).Status);
        await AssertGoneAsync(relaunched);
        Assert.Equal(200, restarted.Stop(plain.Id).Status);
    }

    [Fact]
    public async Task A_relaunching_entry_that_keeps_exiting_at_once_stops_being_relaunched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runs = Path.Combine(_root, "runs.txt");
        var registry = NewRegistry();
        registry.RelaunchDelay = TimeSpan.FromMilliseconds(20);
        var result = registry.Launch(new LongLivedLaunchRequest("crashing", $"echo run>>\"{runs}\"& exit /b 1", _root,
            Restart: "on-exit"), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);
        var id = result.Process!.Id;

        await WaitUntilAsync(() =>
        {
            registry.Tick();
            var view = registry.Get(id)!;
            return view.State == "exited" && view.Note?.Contains("not relaunched") == true;
        }, $"the relaunching to give up (runs recorded in {runs})",
            () => $"state {registry.Get(id)!.State}, restarts {registry.Get(id)!.RestartCount}, runs {ReadShared(runs).Count(c => c == '\n')}; "
                + ChainState([registry.Get(id)!.Pid]));
        var final = registry.Get(id)!;
        Assert.Equal(LongLivedProcessRegistry.QuickExitLimit - 1, final.RestartCount);
        Assert.Equal(LongLivedProcessRegistry.QuickExitLimit, ReadShared(runs).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(400, registry.Launch(new LongLivedLaunchRequest("bad", "echo", _root, Restart: "always"),
            new LongLivedOwner("user", "Nova", null)).Status);
    }

    private LongLivedProcessRegistry NewRegistry() => new(Path.Combine(_root, "registry"), _ => { }, monitor: false);

    /// <summary>
    /// A job this test process joins for the rest of its life, standing in for RedLeaf's. Without
    /// kill-on-close, so nothing ever dies of it, and without breakaway, like RedLeaf's.
    /// </summary>
    private static readonly Lazy<IntPtr> HostJob = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        using var self = Process.GetCurrentProcess();
        return AssignProcessToJobObject(job, self.Handle) ? job : IntPtr.Zero;
    });

    private static bool InJob(int pid, IntPtr job)
    {
        using var process = Process.GetProcessById(pid);
        return IsProcessInJob(process.Handle, job, out var result) && result;
    }

    private static int DesktopShellPid()
    {
        GetWindowThreadProcessId(GetShellWindow(), out var pid);
        return (int)pid;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Every process now: PID, parent PID (recorded at creation, so it outlives the parent) and image name.</summary>
    private static List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var all = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return all;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                all.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return all;
    }

    private static IEnumerable<int> FindProcesses(string exe)
        => Snapshot().Where(entry => string.Equals(entry.Exe, exe, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Pid).ToList();

    private static bool IsPing(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, "ping", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private Process StartPing()
    {
        var process = Process.Start(new ProcessStartInfo(Ping, PingArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true, // a ten-minute ping's lines stay in the pipe, not in the test host's output
        })!;
        _spawned.Add(Identity(process));
        return process;
    }

    private Process StartCmdScript(string script)
    {
        var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/d", "/c", Path.Combine(_root, script) },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        })!;
        _spawned.Add(Identity(process));
        return process;
    }

    private Process StartScript(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(WindowsPowerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_root, script));
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo)!;
        _spawned.Add(Identity(process));
        return process;
    }

    private void Track(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            _spawned.Add(Identity(process));
        }
        catch { }
    }

    private static long ProcessCreated(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return SessionProcessContainment.CreationUtcTicks(process);
    }

    private static (int Pid, long Created) Identity(Process process)
        => (process.Id, SessionProcessContainment.CreationUtcTicks(process));

    private void WriteLedger(string name, (int Pid, long Created) escapee, int writerPid, long writerCreated)
        => File.WriteAllText(Path.Combine(SessionProcessContainment.LedgerDirectory, name + ".json"), JsonSerializer.Serialize(new
        {
            Owner = "test", RootPid = 1, RootCreatedUtcTicks = 1L,
            Escapees = new[] { new { Pid = escapee.Pid, CreatedUtcTicks = escapee.Created } },
            WriterPid = writerPid, WriterCreatedUtcTicks = writerCreated,
        }));

    private static Task WaitForReap(ContainedProcessTree tree, Process root, [CallerMemberName] string test = "")
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Reaped += _ => done.TrySetResult();
        if (tree.IsReaped) done.TrySetResult();
        return WaitUntilAsync(() => done.Task.IsCompleted, "the session tree's Reaped event", () => ChainState([root.Id]), test);
    }

    private static Task AssertGoneAsync(int pid, [CallerMemberName] string test = "")
        => WaitUntilAsync(() => !IsAlive(pid), $"PID {pid} to exit", () => ChainState([pid]), test);

    /// <summary>
    /// How long a wait may go without the chain it is waiting on doing anything (a process appearing or leaving, or any of
    /// them using CPU). A machine too busy to start a process is slow, not stuck, so the wait is not charged for it.
    /// </summary>
    private static readonly TimeSpan StallBudget = TimeSpan.FromSeconds(20);

    /// <summary>A wait that is somehow still moving after this long is a hang all the same.</summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Waits for <paramref name="condition"/> while <paramref name="chain"/> (the live state of whatever is producing it)
    /// keeps changing. It fails when the chain has stood still for <see cref="StallBudget"/>, or at <see cref="Backstop"/>,
    /// and the message names the test, what was expected and the last state seen.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string> chain, [CallerMemberName] string test = "")
    {
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var last = chain();
        while (!condition())
        {
            var now = chain();
            if (now != last)
            {
                last = now;
                quiet.Restart();
            }
            if (quiet.Elapsed > StallBudget)
                throw new TimeoutException($"{test}: stalled waiting for {what}. Nothing in the chain changed for {(int)quiet.Elapsed.TotalSeconds} s "
                    + $"({(int)total.Elapsed.TotalSeconds} s in). Last chain state: {last}");
            if (total.Elapsed > Backstop)
                throw new TimeoutException($"{test}: gave up waiting for {what} after {(int)total.Elapsed.TotalSeconds} s although the chain was still moving. "
                    + $"Last chain state: {last}");
            await Task.Delay(100);
        }
    }

    /// <summary>The live processes at and under <paramref name="roots"/>, by image, PID and CPU time (to 50 ms): what a stalled wait reports.</summary>
    private static string ChainState(IEnumerable<int> roots)
    {
        var snapshot = Snapshot();
        var names = new Dictionary<int, string>();
        foreach (var (pid, _, exe) in snapshot) names[pid] = exe;
        var members = new List<int>();
        var queue = new Queue<int>(roots.Where(names.ContainsKey).Distinct());
        while (queue.TryDequeue(out var pid))
        {
            if (members.Contains(pid)) continue;
            members.Add(pid);
            foreach (var child in snapshot.Where(entry => entry.Parent == pid && entry.Pid != pid)) queue.Enqueue(child.Pid);
        }
        return members.Count == 0
            ? "no process of the chain is alive"
            : string.Join(" > ", members.Select(pid => $"{names[pid]}#{pid} cpu {CpuSeconds(pid)}"));
    }

    private static string CpuSeconds(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return $"{Math.Floor(process.TotalProcessorTime.TotalSeconds * 20) / 20:0.00}s";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>The ping the session's root started through pwsh 7's Start-Process, once the tree has recorded it as an escapee.</summary>
    private async Task<int> WaitForEscapeePingAsync(ContainedProcessTree tree, Process root, [CallerMemberName] string test = "")
    {
        var pid = 0;
        await WaitUntilAsync(() => (pid = tree.Escapees.Select(entry => entry.Pid).FirstOrDefault(IsPing)) != 0,
            "a ping recorded as an escapee of the session (started by pwsh 7 Start-Process)", () => ChainState([root.Id]), test);
        Track(pid);
        return pid;
    }

    /// <summary>The ping a launched entry runs: a process the registry tracks for it, under its root.</summary>
    private async Task<int> WaitForServerPingAsync(LongLivedProcessRegistry registry, string entryId, int rootPid, [CallerMemberName] string test = "")
    {
        var pid = 0;
        await WaitUntilAsync(() => (pid = registry.Get(entryId)?.ProcessIds.FirstOrDefault(IsPing) ?? 0) != 0,
            $"a ping among the processes the registry tracks for entry {entryId}", () => ChainState([rootPid]), test);
        Track(pid);
        return pid;
    }

    private string RegistryLedgerDirectory => Path.Combine(_root, "registry", "long-lived-processes");

    /// <summary>
    /// The registry's ledger on disk names <paramref name="server"/>. A tracked process is visible in memory a moment before its
    /// ledger is written, and a restart reads the ledger, so a test that restarts waits for it.
    /// </summary>
    private Task WaitForRegistryLedgerAsync(int server, int rootPid, [CallerMemberName] string test = "")
        => WaitUntilAsync(() => LedgerNames(RegistryLedgerDirectory, "*.processes.json", server),
            $"a ledger in {RegistryLedgerDirectory} to name PID {server}", () => ChainState([rootPid]), test);

    private static bool LedgerNames(string directory, string pattern, int pid)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var file in Directory.GetFiles(directory, pattern))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadShared(file));
                if (document.RootElement.TryGetProperty("Escapees", out var list)
                    && list.EnumerateArray().Any(entry => entry.GetProperty("Pid").GetInt32() == pid)) return true;
            }
            catch (JsonException)
            {
                // read while being replaced: look again
            }
        }
        return false;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The parent PID recorded when <paramref name="pid"/> was created, or -1 when no such process is running.</summary>
    private static int ParentPid(int pid)
        => Snapshot().Where(entry => entry.Pid == pid).Select(entry => entry.Parent).DefaultIfEmpty(-1).First();

    private static bool ParentAlive(int pid)
    {
        var parent = ParentPid(pid);
        if (parent < 0) return true; // the child itself is gone: nothing to call an orphan
        // A live process under that PID created after the child is a reuse, not its parent.
        try { return IsAlive(parent) && ProcessCreated(parent) <= ProcessCreated(pid); }
        catch { return false; }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string? FindOnPath(string file)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists);

    public void Dispose()
    {
        SessionProcessContainment.IsProtected = null;
        SessionProcessContainment.LedgerDirectory = _previousLedger;
        foreach (var (pid, created) in _spawned.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                // Only our own fixtures, by exact identity: a PID may have been reused since.
                if (SessionProcessContainment.CreationUtcTicks(process) == created) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
