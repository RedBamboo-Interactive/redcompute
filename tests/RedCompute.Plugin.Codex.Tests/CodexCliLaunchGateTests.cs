using System.Diagnostics;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexCliLaunchGateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"redcompute-codex-launch-gate-{Guid.NewGuid():N}");

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Idle_parking_releases_app_server_without_terminal_event()
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), CapabilitySlug = "ai-session", ProviderName = "Codex",
            CreationProvenance = Provenance(),
        };
        var store = new TestStore(new CodexSessionRecord
        {
            Id = "placeholder", ProjectName = "placeholder", ProjectPath = _root,
            Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        var config = new CodexConfig { CodexPath = codexPath };
        var jobs = new TestJobTracker(job);
        await using var interactive = new CodexInteractiveService(
            config, store, new CodexModelCatalog(config, (_, _) => { }),
            new CodexSessionService(config, jobs, store, (_, _) => { }), jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { });
        interactive.LaunchAppServerProcess = _ => StartSteeringFakeAppServer("item");
        var updates = new List<CodexSessionInfo>();
        var ended = new List<string>();
        interactive.SessionUpdated += updates.Add;
        interactive.SessionEnded += (_, reason) => ended.Add(reason);
        var session = await interactive.StartSessionAsync(
            _root, null, "owner", null, null, null, null, null, null, Provenance());
        Assert.NotNull(session);
        Assert.Equal("Idle", session!.Status);
        updates.Clear();

        Assert.True(await interactive.TryParkIdleSessionAsync(
            session.Id, SessionStopReasons.IdleTimeout));
        Assert.Equal("Stopped", session.Status);
        Assert.Equal(SessionStopReasons.IdleTimeout, session.StopReason);
        Assert.Single(updates);
        Assert.Empty(ended);
        Assert.Empty(interactive.GetResidentIdleSessions());
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task SendInput_on_parked_session_waits_for_install_before_spawning_app_server()
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");

        var jobId = Guid.NewGuid();
        var provenance = Provenance();
        var record = new CodexSessionRecord
        {
            Id = "parked-session",
            ProjectName = "Launch gate test",
            ProjectPath = _root,
            Status = "Stopped",
            StopReason = SessionStopReasons.MaintenanceRestart,
            ThreadId = "thread-1",
            JobId = jobId,
            StartedAt = DateTimeOffset.UtcNow,
            LastActivity = DateTimeOffset.UtcNow,
        };
        var store = new TestStore(record);
        var jobs = new TestJobTracker(new JobRecord
        {
            Id = jobId,
            CapabilitySlug = "ai-session",
            ProviderName = "Codex",
            CreationProvenance = provenance,
        });
        var config = new CodexConfig { CodexPath = codexPath };
        var exec = new CodexSessionService(config, jobs, store, (_, _) => { });
        var catalog = new CodexModelCatalog(config, (_, _) => { });
        var usage = new CodexAccountUsageService(config, (_, _) => { });
        await using var interactive = new CodexInteractiveService(
            config, store, catalog, exec, jobs, usage, new NoQualityModes(), () => null, (_, _) => { });

        var installStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new NpmCliVersionManager(
            "@openai/codex", "Codex", () => codexPath,
            () => CliProviderSessionState.Empty,
            (_, _) => Task.CompletedTask,
            new BlockingInstallRunner(installStarted, releaseInstall),
            () => "npm.cmd",
            () => _root,
            new EmptyProcessInspector(),
            TimeSpan.FromSeconds(5));
        interactive.LaunchGate = manager;

        var spawnCount = 0;
        interactive.LaunchAppServerProcess = _ =>
        {
            Interlocked.Increment(ref spawnCount);
            return StartFakeAppServer();
        };

        var operation = manager.StartInstall("latest");
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var send = interactive.SendInputAsync(
            record.Id, [SessionInputPart.TextPart("hello after install")]);
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref spawnCount));
        Assert.False(send.IsCompleted);

        releaseInstall.TrySetResult();
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, operation.Id)).Status);
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref spawnCount));
    }

    private const string ExecOutput =
        "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n" +
        "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"ok\"}}\n" +
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}\n";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private readonly List<Process> _longRunning = [];

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Running_exec_does_not_block_another_exec_and_keeps_installs_out_until_it_ends()
    {
        var (exec, manager) = CreateExec();
        var launches = 0;
        Process? observer = null;
        exec.LaunchExecProcess = _ =>
        {
            if (Interlocked.Increment(ref launches) > 1)
                return CodexGenerateIsolationTests.StartOutputProcess(ExecOutput);
            var process = StartLongRunningProcess();
            observer = Process.GetProcessById(process.Id);
            return process;
        };

        using var cancel = new CancellationTokenSource();
        var running = exec.ExecuteExecAsync("first", null, _root, null, null, 120, cancel.Token);
        await WaitUntilAsync(() => Volatile.Read(ref observer) is not null);

        var second = await exec.ExecuteExecAsync("second", null, _root, null, null, 30, CancellationToken.None)
            .WaitAsync(Patience);
        Assert.True(second.Success, second.Error);
        Assert.False(running.IsCompleted);

        var refused = Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest"));
        Assert.Equal("cli_runs_active", refused.Code);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        Assert.True(observer!.WaitForExit(5000));
        observer.Dispose();

        var operation = manager.StartInstall("latest");
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, operation.Id)).Status);
    }

    [Fact]
    public async Task Exec_during_install_waits_before_spawning()
    {
        var installStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (exec, manager) = CreateExec(new BlockingInstallRunner(installStarted, releaseInstall));
        var launches = 0;
        exec.LaunchExecProcess = _ =>
        {
            Interlocked.Increment(ref launches);
            return CodexGenerateIsolationTests.StartOutputProcess(ExecOutput);
        };
        var operation = manager.StartInstall("latest");
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var waiting = exec.ExecuteExecAsync("during install", null, _root, null, null, 30, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref launches));

        releaseInstall.TrySetResult();
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, operation.Id)).Status);
        Assert.True((await waiting.WaitAsync(Patience)).Success);
        Assert.Equal(1, Volatile.Read(ref launches));
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Each_exec_retry_passes_the_gate_for_its_own_spawn()
    {
        var (exec, _) = CreateExec();
        var gate = new CountingGate();
        exec.LaunchGate = gate;
        exec.LaunchExecProcess = _ => StartFailingProcess("being used by another process");

        var result = await exec.ExecuteExecAsync("locked", null, _root, null, null, 30, CancellationToken.None)
            .WaitAsync(Patience);

        Assert.False(result.Success);
        Assert.Equal(3, gate.Admissions);
        Assert.Equal(3, gate.Spawns);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Slow_session_handshake_does_not_block_exec_and_keeps_installs_out()
    {
        var (exec, manager) = CreateExec();
        exec.LaunchExecProcess = _ => CodexGenerateIsolationTests.StartOutputProcess(ExecOutput);
        await using var interactive = CreateInteractive(exec, manager, out var codexPath);
        var releaseFile = Path.Combine(_root, "release-handshake");
        var spawned = 0;
        interactive.LaunchAppServerProcess = _ =>
        {
            Interlocked.Increment(ref spawned);
            return StartAppServerWaitingFor(releaseFile);
        };

        var start = interactive.StartSessionAsync(
            _root, null, "owner", null, null, null, null, null, null, Provenance());
        await WaitUntilAsync(() => Volatile.Read(ref spawned) == 1);

        var result = await exec.ExecuteExecAsync("meanwhile", null, _root, null, null, 30, CancellationToken.None)
            .WaitAsync(Patience);
        Assert.True(result.Success, result.Error);
        Assert.False(start.IsCompleted);
        Assert.Equal("cli_runs_active",
            Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest")).Code);

        File.WriteAllText(releaseFile, "");
        var session = await start.WaitAsync(Patience);
        Assert.NotNull(session);
        Assert.Equal("Idle", session!.Status);
        Assert.Equal(0, (await manager.GetStatusAsync().WaitAsync(Patience)).ActiveRunCount);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Concurrent_resumes_of_one_session_still_spawn_one_app_server()
    {
        var (exec, manager) = CreateExec();
        await using var interactive = CreateInteractive(exec, manager, out _,
            new CodexSessionRecord
            {
                Id = "parked-session", ProjectName = "Parked", ProjectPath = _root,
                Status = "Stopped", StopReason = SessionStopReasons.MaintenanceRestart,
                ThreadId = "thread-1", JobId = TestJobId,
                StartedAt = DateTimeOffset.UtcNow, LastActivity = DateTimeOffset.UtcNow,
            });
        var releaseFile = Path.Combine(_root, "release-resume");
        var spawned = 0;
        interactive.LaunchAppServerProcess = _ =>
        {
            Interlocked.Increment(ref spawned);
            return StartAppServerWaitingFor(releaseFile);
        };

        var first = interactive.ResumeSessionAsync("parked-session", Provenance());
        var second = interactive.ResumeSessionAsync("parked-session", Provenance());
        await WaitUntilAsync(() => Volatile.Read(ref spawned) == 1);
        await Task.Delay(200);
        File.WriteAllText(releaseFile, "");

        var resumed = await Task.WhenAll(first, second).WaitAsync(Patience);
        Assert.All(resumed, Assert.NotNull);
        Assert.Equal(1, Volatile.Read(ref spawned));
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Slow_model_catalog_fetch_does_not_block_exec_and_keeps_installs_out()
    {
        var (exec, manager) = CreateExec();
        exec.LaunchExecProcess = _ => CodexGenerateIsolationTests.StartOutputProcess(ExecOutput);
        var releaseFile = Path.Combine(_root, "release-catalog");
        var spawnedFile = Path.Combine(_root, "spawned-catalog");
        var catalog = new CodexModelCatalog(
            new CodexConfig { CodexPath = WriteAppServerCli(releaseFile, spawnedFile) }, (_, _) => { })
        {
            LaunchGate = manager,
        };

        var fetch = catalog.GetAsync(forceRefresh: true);
        await WaitUntilAsync(() => File.Exists(spawnedFile), Patience);

        Assert.True((await exec.ExecuteExecAsync("meanwhile", null, _root, null, null, 30, CancellationToken.None)
            .WaitAsync(Patience)).Success);
        Assert.False(fetch.IsCompleted);
        Assert.Equal("cli_runs_active",
            Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest")).Code);

        File.WriteAllText(releaseFile, "");
        await fetch.WaitAsync(Patience);
        Assert.Equal(0, (await manager.GetStatusAsync().WaitAsync(Patience)).ActiveRunCount);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Slow_account_usage_read_does_not_block_exec_and_keeps_installs_out()
    {
        var (exec, manager) = CreateExec();
        exec.LaunchExecProcess = _ => CodexGenerateIsolationTests.StartOutputProcess(ExecOutput);
        var releaseFile = Path.Combine(_root, "release-usage");
        var spawnedFile = Path.Combine(_root, "spawned-usage");
        var usage = new CodexAccountUsageService(
            new CodexConfig { CodexPath = WriteAppServerCli(releaseFile, spawnedFile) }, (_, _) => { })
        {
            LaunchGate = manager,
        };

        var read = usage.GetAsync(forceRefresh: true, CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(spawnedFile), Patience);

        Assert.True((await exec.ExecuteExecAsync("meanwhile", null, _root, null, null, 30, CancellationToken.None)
            .WaitAsync(Patience)).Success);
        Assert.False(read.IsCompleted);
        Assert.Equal("cli_runs_active",
            Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest")).Code);

        File.WriteAllText(releaseFile, "");
        await read.WaitAsync(Patience);
        Assert.Equal(0, (await manager.GetStatusAsync().WaitAsync(Patience)).ActiveRunCount);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task Abandoned_app_server_handshake_leaves_no_process_behind()
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");
        Process? observer = null;
        using var cancel = new CancellationTokenSource();

        var connect = CodexAppServerConnection.StartAsync(
            codexPath, workingDirectory: null, (_, _) => { }, ct: cancel.Token,
            launchProcess: _ =>
            {
                var process = StartAppServerWaitingFor(Path.Combine(_root, "never-released"));
                observer = Process.GetProcessById(process.Id);
                return process;
            });
        await WaitUntilAsync(() => Volatile.Read(ref observer) is not null);
        using (observer)
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Patience));

            // Once the caller is back the run no longer keeps installs out, so the process must be gone.
            Assert.True(observer!.WaitForExit(5000));
        }
    }

    private static readonly Guid TestJobId = Guid.NewGuid();

    private (CodexSessionService Exec, NpmCliVersionManager Manager) CreateExec(ICliCommandRunner? runner = null)
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        released.TrySetResult();
        var manager = new NpmCliVersionManager(
            "@openai/codex", "Codex", () => codexPath,
            () => CliProviderSessionState.Empty,
            (_, _) => Task.CompletedTask,
            runner ?? new BlockingInstallRunner(
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), released),
            () => "npm.cmd",
            () => _root,
            new EmptyProcessInspector(),
            TimeSpan.FromSeconds(30));
        var exec = new CodexSessionService(
            new CodexConfig { CodexPath = codexPath },
            new TestJobTracker(TestJob()),
            new TestStore(Placeholder()),
            (_, _) => { })
        {
            LaunchGate = manager,
        };
        return (exec, manager);
    }

    private CodexInteractiveService CreateInteractive(
        CodexSessionService exec, NpmCliVersionManager manager, out string codexPath,
        CodexSessionRecord? record = null)
    {
        codexPath = Path.Combine(_root, "codex.exe");
        var config = new CodexConfig { CodexPath = codexPath };
        var jobs = new TestJobTracker(TestJob());
        return new CodexInteractiveService(
            config, new TestStore(record ?? Placeholder()), new CodexModelCatalog(config, (_, _) => { }), exec, jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { })
        {
            LaunchGate = manager,
        };
    }

    private static JobRecord TestJob() => new()
    {
        Id = TestJobId, CapabilitySlug = "ai-session", ProviderName = "Codex",
        CreationProvenance = Provenance(),
    };

    private CodexSessionRecord Placeholder() => new()
    {
        Id = "placeholder", ProjectName = "placeholder", ProjectPath = _root,
        Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
    };

    private Process StartLongRunningProcess()
    {
        var process = StartPowerShell("Start-Sleep -Seconds 120");
        lock (_longRunning) _longRunning.Add(process);
        return process;
    }

    private static Process StartFailingProcess(string stderr)
    {
        var startInfo = PowerShellStartInfo("[Console]::Error.Write($env:RED_COMPUTE_TEST_STDERR); exit 1");
        startInfo.Environment["RED_COMPUTE_TEST_STDERR"] = stderr;
        return Process.Start(startInfo)!;
    }

    private static Process StartAppServerWaitingFor(string releaseFile)
    {
        var startInfo = PowerShellStartInfo(AppServerScript);
        startInfo.Environment["RED_COMPUTE_TEST_RELEASE"] = releaseFile;
        return Process.Start(startInfo)!;
    }

    /// <summary>
    /// A CLI on disk, for the services that spawn through the located executable rather than a seam.
    /// It answers nothing until the release file exists, so the app-server handshake stays pending.
    /// </summary>
    private string WriteAppServerCli(string releaseFile, string spawnedFile)
    {
        var scriptPath = Path.Combine(_root, $"app-server-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, AppServerScript);
        var cliPath = Path.ChangeExtension(scriptPath, ".cmd");
        File.WriteAllText(cliPath,
            "@echo off\r\n" +
            $"set \"RED_COMPUTE_TEST_RELEASE={releaseFile}\"\r\n" +
            $"echo.> \"{spawnedFile}\"\r\n" +
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"\r\n");
        return cliPath;
    }

    private const string AppServerScript = """
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path -LiteralPath $env:RED_COMPUTE_TEST_RELEASE)) {
          if ((Get-Date) -gt $deadline) { exit 1 }
          Start-Sleep -Milliseconds 25
        }
        while ($null -ne ($line = [Console]::In.ReadLine())) {
          try { $request = $line | ConvertFrom-Json } catch { continue }
          if ($null -eq $request.id) { continue }
          if ($request.method -eq 'thread/start') {
            $result = @{ thread = @{ id = 'thread-1' } }
          } else {
            $result = @{}
          }
          $response = @{ jsonrpc = '2.0'; id = $request.id; result = $result } |
            ConvertTo-Json -Compress -Depth 8
          [Console]::Out.WriteLine($response)
          [Console]::Out.Flush()
        }
        """;

    private static Process StartPowerShell(string script) => Process.Start(PowerShellStartInfo(script))!;

    private static ProcessStartInfo PowerShellStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private sealed class CountingGate : ICliProcessLaunchGate
    {
        public int Admissions;
        public int Spawns;

        public Task<T> RunWhenInstallIdleAsync<T>(Func<Action, Task<T>> launch, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Admissions);
            return launch(() => Interlocked.Increment(ref Spawns));
        }

        public Task<T> RunWhenInstallIdleAsync<T>(Func<Task<T>> launch, CancellationToken ct = default)
            => throw new InvalidOperationException("exec must signal its spawn");
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task App_server_token_is_issued_for_the_session_it_serves_on_start_and_resume()
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), CapabilitySlug = "ai-session", ProviderName = "Codex",
            CreationProvenance = Provenance(),
        };
        var store = new TestStore(new CodexSessionRecord
        {
            Id = "parked-session", ProjectName = "Parked", ProjectPath = _root,
            Status = "Stopped", StopReason = SessionStopReasons.MaintenanceRestart,
            ThreadId = "thread-1", JobId = job.Id,
            StartedAt = DateTimeOffset.UtcNow, LastActivity = DateTimeOffset.UtcNow,
        });
        var config = new CodexConfig { CodexPath = codexPath };
        var jobs = new TestJobTracker(job);
        await using var interactive = new CodexInteractiveService(
            config, store, new CodexModelCatalog(config, (_, _) => { }),
            new CodexSessionService(config, jobs, store, (_, _) => { }), jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { });
        var launchedTokens = new List<string?>();
        interactive.LaunchAppServerProcess = startInfo =>
        {
            launchedTokens.Add(startInfo.Environment.TryGetValue("REDLEAF_EXECUTION_TOKEN", out var token) ? token : null);
            return StartSteeringFakeAppServer("item");
        };

        CodexSessionInfo? session;
        using (SessionScratch.PushExecutionTokenIssuer(sessionId => $"token-for-{sessionId}"))
        {
            session = await interactive.StartSessionAsync(
                _root, null, "owner", null, null, null, null, null, null, Provenance());
            Assert.NotNull(session);
            Assert.NotNull(await interactive.ResumeSessionAsync("parked-session", Provenance()));
        }

        Assert.NotEqual("parked-session", session.Id);
        Assert.Equal([$"token-for-{session.Id}", "token-for-parked-session"], launchedTokens);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task MatchingSteerResponseCommitsBeforeUserItemAndItemPersistsInEventOrder()
    {
        await using var fixture = await CreateSteeringFixtureAsync("item");

        var result = await fixture.Service.TrySteerInputAsync(fixture.Session.Id,
            [SessionInputPart.TextPart("steered")], messageUid: "steer-uid",
            displayContent: "Steered display").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.Accepted, result.Status);
        Assert.Equal("Active", fixture.Session.Status);
        await WaitUntilAsync(() => fixture.Store.Messages.Any(m => m.MessageUid == "steer-uid"));
        var persisted = Assert.Single(fixture.Store.Messages,
            message => message.MessageUid == "steer-uid");
        Assert.Equal("Steered display", persisted.Content);
        Assert.Equal("item-steer", persisted.MessageId);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task CompletedBeforeUserItemPersistsCommittedSteerWithoutRedelivery()
    {
        await using var fixture = await CreateSteeringFixtureAsync("completed");

        var result = await fixture.Service.TrySteerInputAsync(fixture.Session.Id,
            [SessionInputPart.TextPart("steered")], messageUid: "steer-completed",
            displayContent: "Persist at completion").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.Accepted, result.Status);
        await WaitUntilAsync(() => fixture.Store.Messages.Any(m => m.MessageUid == "steer-completed"));
        var persisted = Assert.Single(fixture.Store.Messages,
            message => message.MessageUid == "steer-completed");
        Assert.Equal("Persist at completion", persisted.Content);
        Assert.Null(persisted.MessageId);
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task TimeoutAfterSendIsNonRetryableAndNeverBlindlyRedelivered()
    {
        await using var fixture = await CreateSteeringFixtureAsync("timeout");
        fixture.Service.SteerRequestTimeoutSeconds = 1;

        var result = await fixture.Service.TrySteerInputAsync(fixture.Session.Id,
            [SessionInputPart.TextPart("uncertain")], messageUid: "steer-timeout")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("steer_unconfirmed", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.DoesNotContain(fixture.Store.Messages, message => message.MessageUid == "steer-timeout");
    }

    [WindowsFact("the fake codex app-server is a .cmd shim around a PowerShell script")]
    public async Task MismatchedTurnResponseRemainsDemotable()
    {
        await using var fixture = await CreateSteeringFixtureAsync("mismatch");

        var result = await fixture.Service.TrySteerInputAsync(fixture.Session.Id,
            [SessionInputPart.TextPart("wrong turn")], messageUid: "steer-mismatch")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.NotSteerable, result.Status);
        Assert.Equal("turn_changed", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.DoesNotContain(fixture.Store.Messages, message => message.MessageUid == "steer-mismatch");
    }

    private async Task<SteeringFixture> CreateSteeringFixtureAsync(string behavior)
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "test seam only");
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), CapabilitySlug = "ai-session", ProviderName = "Codex",
            CreationProvenance = Provenance(),
        };
        var placeholder = new CodexSessionRecord
        {
            Id = "placeholder", ProjectName = "placeholder", ProjectPath = _root,
            Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        };
        var store = new TestStore(placeholder);
        var config = new CodexConfig { CodexPath = codexPath };
        var jobs = new TestJobTracker(job);
        var exec = new CodexSessionService(config, jobs, store, (_, _) => { });
        var interactive = new CodexInteractiveService(
            config, store, new CodexModelCatalog(config, (_, _) => { }), exec, jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { });
        interactive.LaunchAppServerProcess = _ => StartSteeringFakeAppServer(behavior);
        var session = await interactive.StartSessionAsync(
            _root, null, "owner", null, null, null, null, null, null, Provenance());
        Assert.NotNull(session);
        Assert.True(await interactive.SendInputAsync(session.Id,
            [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));
        await WaitUntilAsync(() => session.Status == "Active");
        return new SteeringFixture(interactive, store, session);
    }

    private static Process StartFakeAppServer()
    {
        const string script = """
            while ($null -ne ($line = [Console]::In.ReadLine())) {
              try { $request = $line | ConvertFrom-Json } catch { continue }
              if ($null -eq $request.id) { continue }
              if ($request.method -eq 'turn/start') {
                $result = @{ turn = @{ id = 'turn-1' } }
              } else {
                $result = @{}
              }
              $response = @{ jsonrpc = '2.0'; id = $request.id; result = $result } |
                ConvertTo-Json -Compress -Depth 8
              [Console]::Out.WriteLine($response)
              [Console]::Out.Flush()
            }
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo)!;
    }

    private static Process StartSteeringFakeAppServer(string behavior)
    {
        var script = $$"""
            $behavior = '{{behavior}}'
            while ($null -ne ($line = [Console]::In.ReadLine())) {
              try { $request = $line | ConvertFrom-Json } catch { continue }
              if ($null -eq $request.id) { continue }
              if ($request.method -eq 'thread/start') {
                $result = @{ thread = @{ id = 'thread-1' } }
              } elseif ($request.method -eq 'turn/start') {
                $result = @{ turn = @{ id = 'turn-1' } }
              } elseif ($request.method -eq 'turn/steer') {
                if ($behavior -eq 'timeout') { continue }
                $result = @{ turnId = $(if ($behavior -eq 'mismatch') { 'turn-2' } else { 'turn-1' }) }
              } else {
                $result = @{}
              }
              $response = @{ jsonrpc = '2.0'; id = $request.id; result = $result } |
                ConvertTo-Json -Compress -Depth 8
              [Console]::Out.WriteLine($response)
              [Console]::Out.Flush()
              if ($request.method -eq 'turn/start') {
                $notification = @{ jsonrpc = '2.0'; method = 'turn/started'; params = @{ threadId = 'thread-1'; turn = @{ id = 'turn-1' } } } |
                  ConvertTo-Json -Compress -Depth 8
                [Console]::Out.WriteLine($notification)
                [Console]::Out.Flush()
              } elseif ($request.method -eq 'turn/steer') {
                if ($behavior -eq 'mismatch') { continue }
                if ($behavior -eq 'completed') {
                  $notification = @{ jsonrpc = '2.0'; method = 'turn/completed'; params = @{ threadId = 'thread-1'; turn = @{ id = 'turn-1'; status = 'completed'; items = @(@{ type = 'agentMessage'; phase = 'final_answer'; text = 'done' }) } } } |
                    ConvertTo-Json -Compress -Depth 8
                } else {
                  $notification = @{ jsonrpc = '2.0'; method = 'item/started'; params = @{ threadId = 'thread-1'; turnId = 'turn-1'; item = @{ type = 'userMessage'; id = 'item-steer'; clientId = $request.params.clientUserMessageId; content = @() } } } |
                    ConvertTo-Json -Compress -Depth 8
                }
                [Console]::Out.WriteLine($notification)
                [Console]::Out.Flush()
              }
            }
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? patience = null)
    {
        using var timeout = new CancellationTokenSource(patience ?? TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed record SteeringFixture(
        CodexInteractiveService Service,
        TestStore Store,
        CodexSessionInfo Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private static async Task<CliInstallOperation> WaitForTerminalAsync(
        NpmCliVersionManager manager, Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var operation = Assert.IsType<CliInstallOperation>(manager.GetOperation(operationId));
            if (operation.Status is not ("queued" or "running")) return operation;
            await Task.Delay(10, timeout.Token);
        }
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
        foreach (var process in _longRunning)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class BlockingInstallRunner(
        TaskCompletionSource started,
        TaskCompletionSource release) : ICliCommandRunner
    {
        public async Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
        {
            if (command.Arguments.FirstOrDefault() == "install")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new CliCommandResult(0, "installed", "");
            }

            return new CliCommandResult(0, "codex-cli 0.163.0", "");
        }
    }

    private sealed class EmptyProcessInspector : ICliProcessInspector
    {
        public IReadOnlyList<CliProcessInfo> FindPackageProcesses(
            string packageDirectory, IReadOnlyCollection<int> ownedRootProcessIds) => [];
    }

    private sealed class NoQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider,
            out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class TestJobTracker(JobRecord job) : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => job;
        public void MarkRunning(Guid jobId) { }
        public void StartInvocation(Guid jobId, JobProvenance provenance,
            JobEventKind kind = JobEventKind.Started) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null,
            long? outputSizeBytes = null, string? contentType = null,
            string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null,
            string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
        public JobRecord? GetJob(Guid jobId) => job.Id == jobId ? job : null;
    }

    private sealed class TestStore(CodexSessionRecord session) : ICodexSessionStore
    {
        public List<CodexMessageRecord> Messages { get; } = [];
        public CodexSessionRecord? FindSession(string sessionId) =>
            session.Id == sessionId ? session : null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) =>
            session.JobId == jobId ? session : null;
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds,
            int limit = 20, bool includeDismissed = false) =>
            excludeIds.Contains(session.Id) ? [] : [session];
        public void SaveSession(CodexSessionRecord record) { }
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) => Messages.Add(message);
        public void AddMessages(List<CodexMessageRecord> messages) => Messages.AddRange(messages);
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) =>
            Messages.Where(message => message.SessionId == sessionId).TakeLast(limit).ToList();
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
