using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.PluginSdk;
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

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-containment", Guid.NewGuid().ToString("N"));
    private readonly List<(int Pid, long Created)> _spawned = [];
    private readonly string _previousLedger = SessionProcessContainment.LedgerDirectory;

    public SessionProcessContainmentTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sleeper.ps1"), "Set-Content -Path $args[0] -Value $PID; Start-Sleep 600");
        // `start /b` from a cmd that exits at once: the sleeper's parent is dead, like `nohup x &`.
        File.WriteAllText(Path.Combine(_root, "detach.cmd"), """
            @start "probe" /b "%~1" -NoProfile -ExecutionPolicy Bypass -File "%~2" "%~3"

            """);
        File.WriteAllText(Path.Combine(_root, "orphan-root.ps1"), """
            & cmd /c "$PSScriptRoot\detach.cmd" $args[0] "$PSScriptRoot\sleeper.ps1" $args[1]; Start-Sleep 600
            """);
        // PowerShell 7's Start-Process, run from a pwsh that then exits: the sleeper leaves every job.
        File.WriteAllText(Path.Combine(_root, "start-process.ps1"), """
            Start-Process -WindowStyle Hidden -FilePath $args[0] -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"$PSScriptRoot\sleeper.ps1",$args[1]
            """);
        File.WriteAllText(Path.Combine(_root, "escapee-root.ps1"), """
            & pwsh -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\start-process.ps1" $args[0] $args[1]; Start-Sleep 600
            """);
        SessionProcessContainment.LedgerDirectory = Path.Combine(_root, "ledger");
    }

    [Fact]
    public async Task A_child_whose_launching_shell_exited_dies_with_the_session_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pidFile = Path.Combine(_root, "orphan.pid");
        // `cmd /c start /b` returns at once, the way `nohup x &` does: the sleeper's parent is dead.
        var root = StartScript("orphan-root.ps1", WindowsPowerShell, pidFile);
        var tree = SessionProcessContainment.Contain(root, "test");
        Assert.NotNull(tree);

        var orphan = await WaitForPidAsync(pidFile);
        Assert.False(ParentAlive(orphan), "the probe must reproduce the orphan shape a PPID walk cannot see");
        Assert.True(SessionProcessContainment.IsSessionMember(orphan));

        var reaped = WaitForReap(tree!);
        root.Kill(); // the root alone, the way a CLI exits: nothing walks the tree for us
        await reaped;
        await AssertGoneAsync(orphan);
    }

    [Fact]
    public async Task A_pwsh_Start_Process_escapee_is_proven_by_parentage_and_reaped()
    {
        if (!OperatingSystem.IsWindows() || FindOnPath("pwsh.exe") is null) return;
        var pidFile = Path.Combine(_root, "escapee.pid");
        var root = StartScript("escapee-root.ps1", WindowsPowerShell, pidFile);
        var tree = SessionProcessContainment.Contain(root, "test")!;

        var escapee = await WaitForPidAsync(pidFile);
        await WaitUntilAsync(() => tree.Escapees.Any(entry => entry.Pid == escapee), "escapee recorded");
        Assert.False(SessionProcessContainment.IsSessionMember(escapee), "pwsh 7 Start-Process leaves the job");
        Assert.Single(Directory.GetFiles(SessionProcessContainment.LedgerDirectory, "*.json"));

        var reaped = WaitForReap(tree);
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
        var pidFile = Path.Combine(_root, "kept.pid");
        var root = StartScript("escapee-root.ps1", WindowsPowerShell, pidFile);
        var tree = SessionProcessContainment.Contain(root, "test")!;
        var kept = await WaitForPidAsync(pidFile);
        await WaitUntilAsync(() => tree.Escapees.Any(entry => entry.Pid == kept), "escapee recorded");

        var adopted = registry.Adopt(new LongLivedAdoptRequest(kept, "kept preview", 5289), new LongLivedOwner("user", "Nova", "s1"));
        Assert.Equal(200, adopted.Status);

        var reaped = WaitForReap(tree);
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
        var root = StartRoot("Start-Sleep 600");
        var tree = SessionProcessContainment.Contain(root, "test")!;

        var result = registry.Adopt(new LongLivedAdoptRequest(root.Id, "inside"), new LongLivedOwner("user", "Nova", null));
        Assert.Equal(409, result.Status);
        Assert.Empty(registry.List());

        var reaped = WaitForReap(tree);
        root.Kill();
        await reaped;
    }

    [Fact]
    public async Task The_boot_sweep_kills_only_exact_recorded_identities_and_never_its_own_live_ledgers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var matching = StartSleeper("matching");
        var stale = StartSleeper("stale");
        var current = StartSleeper("current");
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
        var pidFile = Path.Combine(_root, "server.pid");
        var result = registry.Launch(new LongLivedLaunchRequest("test server",
            $"\"{WindowsPowerShell}\" -NoProfile -Command \"Write-Output listening; Set-Content -Path '{pidFile}' -Value $PID; Start-Sleep 600\"",
            _root, Description: "fixture"), new LongLivedOwner("user", "Nova", "session-1"));
        Assert.True(result.Status == 200, result.Error);
        var view = result.Process!;
        Track(view.Pid);
        var server = await WaitForPidAsync(pidFile);

        Assert.True(view.Running);
        Assert.Equal("session-1", view.SessionId);
        Assert.False(SessionProcessContainment.IsSessionMember(view.Pid));
        await WaitUntilAsync(() => File.Exists(view.LogPath) && ReadShared(view.LogPath!).Contains("listening"), "log written");

        // Parented to the desktop shell: not in this (RedCompute's) process tree, nor in its jobs.
        Assert.Equal("desktop", view.LaunchedVia);
        Assert.Equal(DesktopShellPid(), ParentPid(view.Pid));
        await WaitUntilAsync(() => registry.Get(view.Id)!.ProcessIds.Contains(server), "the server tracked by parentage");

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
        var pidFile = Path.Combine(_root, "detached-server.pid");
        // The command itself detaches and returns, the way `pnpm exec` trees lost their root on 12 Nov.
        var result = registry.Launch(new LongLivedLaunchRequest("detaching server",
            $"start \"probe\" /b \"{WindowsPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Sleeper}\" \"{pidFile}\"",
            _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);
        var server = await WaitForPidAsync(pidFile);
        // Let the script's own cmd finish: only the detached server (and its console host) remain.
        await Task.Delay(1500);

        await WaitUntilAsync(() => registry.Get(result.Process!.Id)!.ProcessIds.Contains(server), "the server tracked by parentage");
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
        File.WriteAllText(chain, $"@\"{WindowsPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Sleeper}\" %1" + "\r\n");
        var pidFile = Path.Combine(_root, "chained.pid");
        var registry = NewRegistry();
        var result = registry.Launch(new LongLivedLaunchRequest("chained",
            $"set CHAINED=1&& \"{chain}\" \"{pidFile}\"", _root), new LongLivedOwner("user", "Nova", null));
        Assert.True(result.Status == 200, result.Error);

        var server = await WaitForPidAsync(pidFile);
        await WaitUntilAsync(() => registry.Get(result.Process!.Id)!.ProcessIds.Contains(server), "the server tracked by parentage");
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
        var ordinary = StartSleeper("ordinary");
        Assert.True(InJob(ordinary.Id, hostJob), "control: an ordinary child inherits its host's job");

        var registry = NewRegistry();
        var pidFile = Path.Combine(_root, "survivor.pid");
        var result = registry.Launch(new LongLivedLaunchRequest("survivor",
            $"\"{WindowsPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Sleeper}\" \"{pidFile}\"", _root),
            new LongLivedOwner("user", "Nova", "session-1"));
        Assert.True(result.Status == 200, result.Error);
        Track(result.Process!.Pid);
        var server = await WaitForPidAsync(pidFile);
        await WaitUntilAsync(() => registry.Get(result.Process.Id)!.ProcessIds.Contains(server), "the server tracked by parentage");
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
            await WaitUntilAsync(() => ReadShared(log).Contains("[given]"), "the probe line");
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
        }, "the exit to be recorded");
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

    [Fact]
    public async Task An_entry_that_died_while_RedCompute_was_down_is_shown_dead_or_relaunched_as_it_asked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registry = NewRegistry();
        var owner = new LongLivedOwner("user", "Nova", null);
        var keptPid = Path.Combine(_root, "kept.pid");
        var relaunchedPid = Path.Combine(_root, "relaunched.pid");
        var plain = registry.Launch(new LongLivedLaunchRequest("plain",
            $"\"{WindowsPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Sleeper}\" \"{keptPid}\"", _root), owner).Process!;
        var restarting = registry.Launch(new LongLivedLaunchRequest("restarting",
            $"\"{WindowsPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{Sleeper}\" \"{relaunchedPid}\"", _root,
            Restart: "on-exit"), owner).Process!;
        var first = await WaitForPidAsync(keptPid);
        var second = await WaitForPidAsync(relaunchedPid);
        await WaitUntilAsync(() => registry.Get(plain.Id)!.ProcessIds.Contains(first)
                                   && registry.Get(restarting.Id)!.ProcessIds.Contains(second), "both servers tracked");

        // RedCompute is gone, and so, meanwhile, are both servers: every process their ledgers name.
        registry.Dispose();
        foreach (var pid in new[] { plain.Pid, restarting.Pid })
            using (var process = Process.GetProcessById(pid)) process.Kill(entireProcessTree: true);
        File.Delete(relaunchedPid);
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
            return File.Exists(relaunchedPid);
        }, "the relaunch after its first backoff");
        var relaunched = await WaitForPidAsync(relaunchedPid);
        var view = restarted.Get(restarting.Id)!;
        Assert.Equal("running", view.State);
        Assert.Equal(1, view.RestartCount);
        Assert.NotEqual(restarting.Pid, view.Pid);
        Track(view.Pid);
        await WaitUntilAsync(() => restarted.Get(restarting.Id)!.ProcessIds.Contains(relaunched), "the relaunched server tracked");

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
        }, "the relaunching to give up");
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

    private string Sleeper => Path.Combine(_root, "sleeper.ps1");

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

    private Process StartRoot(string command)
    {
        var process = Process.Start(new ProcessStartInfo(WindowsPowerShell)
        {
            ArgumentList = { "-NoProfile", "-Command", command },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        })!;
        _spawned.Add(Identity(process));
        return process;
    }

    private Process StartSleeper(string name)
    {
        var process = Process.Start(new ProcessStartInfo(WindowsPowerShell)
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Sleeper, Path.Combine(_root, name + ".pid") },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
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

    private static Task WaitForReap(ContainedProcessTree tree)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Reaped += _ => done.TrySetResult();
        if (tree.IsReaped) done.TrySetResult();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private async Task<int> WaitForPidAsync(string file)
    {
        var pid = 0;
        await WaitUntilAsync(() => File.Exists(file) && int.TryParse(ReadShared(file).Trim(), out pid), $"{Path.GetFileName(file)} written");
        Track(pid);
        return pid;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(100);
        }
    }

    private static async Task AssertGoneAsync(int pid)
        => await WaitUntilAsync(() => !IsAlive(pid), $"PID {pid} to exit");

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

    private static int ParentPid(int pid)
    {
        using var query = Process.Start(new ProcessStartInfo(WindowsPowerShell)
        {
            ArgumentList = { "-NoProfile", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var parent = int.Parse(query.StandardOutput.ReadToEnd().Trim());
        query.WaitForExit();
        return parent;
    }

    private static bool ParentAlive(int pid)
    {
        var parent = ParentPid(pid);
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
