using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// An agent or one-shot run holds the launch gate for its spawn only. While it keeps running, every
// other launch proceeds, and the run itself is what keeps an install away from its files.
public sealed class ClaudeCliLaunchGateTests : IDisposable
{
    private const string OneshotOutput =
        "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[]}\n" +
        "{\"type\":\"result\",\"result\":\"ok\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}\n";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"redcompute-claude-launch-gate-{Guid.NewGuid():N}");
    private readonly List<Process> _processes = [];
    private readonly string _claudePath;
    private readonly ClaudeDeveloperInstructionsTests.MemoryStore _store = new();
    private readonly ClaudeSessionService _service;
    private readonly FakeRunner _runner = new();
    private readonly NpmCliVersionManager _manager;

    public ClaudeCliLaunchGateTests()
    {
        Directory.CreateDirectory(_root);
        _claudePath = Path.Combine(_root, "claude.exe");
        File.WriteAllText(_claudePath, "test seam only");
        _service = new ClaudeSessionService(
            new ClaudeConfig { ClaudePath = _claudePath },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), _store,
            new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { });
        _manager = new NpmCliVersionManager(
            "@anthropic-ai/claude-code", "Claude Code", () => _claudePath,
            () => CliProviderSessionState.Empty,
            (_, _) => Task.CompletedTask,
            _runner, () => "npm.cmd", () => _root, new EmptyInspector(), Patience);
        _service.LaunchGate = _manager;
        _service.LaunchSessionProcess = _ => Track(StartPowerShell("[Console]::In.ReadToEnd()"));
        _service.LaunchOneshotProcess = _ => StartOutputProcess(OneshotOutput);
    }

    [Fact]
    public async Task Running_agent_does_not_block_a_oneshot_a_session_start_or_a_session_resume()
    {
        using var cancel = new CancellationTokenSource();
        var (agent, observer) = await StartLongRunningAgentAsync(cancel.Token);

        // The agent waiting on a one-shot of its own is the deadlock this gate used to cause.
        var oneshot = await RunOneshotAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.True(oneshot.Success, oneshot.Error);

        var started = await _service.StartSessionAsync(_root, null, "owner", null, null, null, null, null, null,
            Provenance()).WaitAsync(Patience);
        Assert.NotNull(started);

        _store.SaveSession(new ClaudeSessionRecord
        {
            Id = "parked-session", ProjectName = "Parked", ProjectPath = _root,
            Status = "Stopped", ClaudeSessionId = "conversation-1", MessageCount = 1,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var resumed = await _service.ResumeSessionAsync("parked-session", Provenance()).WaitAsync(Patience);
        Assert.NotNull(resumed);

        Assert.False(agent.IsCompleted);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.WaitAsync(Patience));
        Assert.True(observer.WaitForExit(5000));
        observer.Dispose();
        await _service.StopAllAsync();
    }

    [Fact]
    public async Task Install_is_refused_while_an_agent_runs_and_allowed_once_cancellation_killed_it()
    {
        using var cancel = new CancellationTokenSource();
        var (agent, observer) = await StartLongRunningAgentAsync(cancel.Token);

        Assert.Equal("cli_runs_active", Assert.Throws<CliInstallRejectedException>(
            () => _manager.StartInstall("latest")).Code);
        Assert.DoesNotContain(_runner.Commands, command => command.Arguments.FirstOrDefault() == "install");

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.WaitAsync(Patience));
        Assert.True(observer.WaitForExit(5000));
        observer.Dispose();

        await AssertInstallCompletesAsync();
    }

    [Fact]
    public async Task Agent_timeout_kills_the_process_and_ends_the_run()
    {
        var (agent, observer) = await StartLongRunningAgentAsync(CancellationToken.None, timeoutSeconds: 1);

        var result = await agent.WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.Equal("Execution timed out after 1s", result.Error);
        Assert.True(observer.WaitForExit(5000));
        observer.Dispose();
        await AssertInstallCompletesAsync();
    }

    [Fact]
    public async Task CancelExecution_still_reaches_a_running_agent_outside_the_gate()
    {
        var (agent, observer) = await StartLongRunningAgentAsync(CancellationToken.None, streamKey: "agent-key");

        _service.CancelExecution("agent-key");

        var result = await agent.WaitAsync(Patience);
        Assert.False(result.Success);
        Assert.True(observer.WaitForExit(5000));
        observer.Dispose();
        await AssertInstallCompletesAsync();
    }

    [Fact]
    public async Task Cancelled_oneshot_kills_its_process_before_it_stops_keeping_installs_out()
    {
        Process? observer = null;
        _service.LaunchOneshotProcess = _ =>
        {
            var process = Track(StartPowerShell("Start-Sleep -Seconds 120"));
            observer = Process.GetProcessById(process.Id);
            return process;
        };
        using var cancel = new CancellationTokenSource();
        var oneshot = RunOneshotAsync(cancel.Token);
        await WaitUntilAsync(() => Volatile.Read(ref observer) is not null);

        Assert.Equal("cli_runs_active", Assert.Throws<CliInstallRejectedException>(
            () => _manager.StartInstall("latest")).Code);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oneshot.WaitAsync(Patience));
        Assert.True(observer!.WaitForExit(5000));
        observer.Dispose();
        await AssertInstallCompletesAsync();
    }

    [Fact]
    public async Task Launches_during_an_install_wait_before_spawning()
    {
        _runner.HoldInstall();
        var launches = 0;
        _service.LaunchOneshotProcess = _ =>
        {
            Interlocked.Increment(ref launches);
            return StartOutputProcess(OneshotOutput);
        };
        _service.LaunchAgentProcess = _ =>
        {
            Interlocked.Increment(ref launches);
            return StartOutputProcess(OneshotOutput);
        };
        var operation = _manager.StartInstall("latest");
        await _runner.InstallStarted.WaitAsync(Patience);

        var oneshot = RunOneshotAsync(CancellationToken.None);
        var agent = RunAgentAsync(CancellationToken.None, 30, null);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref launches));

        _runner.ReleaseInstall();
        Assert.Equal("completed", (await WaitForTerminalAsync(operation.Id)).Status);
        Assert.True((await oneshot.WaitAsync(Patience)).Success);
        Assert.True((await agent.WaitAsync(Patience)).Success);
        Assert.Equal(2, Volatile.Read(ref launches));
    }

    [Fact]
    public async Task Agent_that_fails_to_start_releases_the_gate()
    {
        _service.LaunchAgentProcess = _ => null;

        var result = await RunAgentAsync(CancellationToken.None, 30, null).WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.Equal("Failed to start process", result.Error);
        Assert.True((await RunOneshotAsync(CancellationToken.None).WaitAsync(Patience)).Success);
        await AssertInstallCompletesAsync();
    }

    private async Task<(Task<ClaudeSessionService.ExecuteResult> Agent, Process Observer)> StartLongRunningAgentAsync(
        CancellationToken ct, int timeoutSeconds = 120, string? streamKey = null)
    {
        Process? observer = null;
        _service.LaunchAgentProcess = _ =>
        {
            var process = Track(StartPowerShell("Start-Sleep -Seconds 120"));
            observer = Process.GetProcessById(process.Id);
            return process;
        };
        var agent = RunAgentAsync(ct, timeoutSeconds, streamKey);
        await WaitUntilAsync(() => Volatile.Read(ref observer) is not null);
        return (agent, observer!);
    }

    private Task<ClaudeSessionService.ExecuteResult> RunAgentAsync(
        CancellationToken ct, int timeoutSeconds, string? streamKey)
        => _service.ExecuteAgentAsync("prompt", null, _root, null, null, 1, null, null, null,
            timeoutSeconds, ct, streamKey);

    private async Task<ClaudeSessionService.OneshotResult> RunOneshotAsync(CancellationToken ct)
    {
        using var messages = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]");
        return await _service.ExecuteOneshotAsync("claude-test", null, messages.RootElement, 32, ct, null, 60);
    }

    private async Task AssertInstallCompletesAsync()
    {
        var operation = _manager.StartInstall("latest");
        Assert.Equal("completed", (await WaitForTerminalAsync(operation.Id)).Status);
    }

    private async Task<CliInstallOperation> WaitForTerminalAsync(Guid operationId)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
        {
            var operation = Assert.IsType<CliInstallOperation>(_manager.GetOperation(operationId));
            if (operation.Status is not ("queued" or "running")) return operation;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private Process Track(Process process)
    {
        lock (_processes) _processes.Add(process);
        return process;
    }

    private static Process StartOutputProcess(string output)
    {
        var startInfo = PowerShellStartInfo("[Console]::Out.Write($env:RED_COMPUTE_TEST_OUTPUT)");
        startInfo.Environment["RED_COMPUTE_TEST_OUTPUT"] = output;
        return Process.Start(startInfo)!;
    }

    private static Process StartPowerShell(string script) => Process.Start(PowerShellStartInfo(script))!;

    private static ProcessStartInfo PowerShellStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static JobProvenance Provenance() => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", "test", null, "Test"),
            new JobEntrypoint("test", "/test")),
        new JobActor("system", "Test", Id: "test"),
        new JobBeneficiary("system", Reason: "CLI launch gate test"),
        [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        lock (_processes)
        {
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeRunner : ICliCommandRunner
    {
        private readonly TaskCompletionSource _installStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseInstall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdInstall;

        public List<CliCommand> Commands { get; } = [];
        public Task InstallStarted => _installStarted.Task;
        public void HoldInstall() => _holdInstall = true;
        public void ReleaseInstall() => _releaseInstall.TrySetResult();

        public async Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
        {
            lock (Commands) Commands.Add(command);
            if (command.Arguments.FirstOrDefault() != "install")
                return new CliCommandResult(0, "2.1.300 (Claude Code)", "");
            _installStarted.TrySetResult();
            if (_holdInstall) await _releaseInstall.Task.WaitAsync(ct);
            return new CliCommandResult(0, "installed", "");
        }
    }

    private sealed class EmptyInspector : ICliProcessInspector
    {
        public IReadOnlyList<CliProcessInfo> FindPackageProcesses(
            string packageDirectory, IReadOnlyCollection<int> ownedRootProcessIds) => [];
    }
}
