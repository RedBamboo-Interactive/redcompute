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

    [Fact]
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

    [Fact]
    public async Task SteeringWaitsForMatchingUserMessageItemAndPersistsItInsideActiveTurn()
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
        await using var interactive = new CodexInteractiveService(
            config, store, new CodexModelCatalog(config, (_, _) => { }), exec, jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { });
        interactive.LaunchAppServerProcess = _ => StartSteeringFakeAppServer();

        var session = await interactive.StartSessionAsync(
            _root, null, "owner", null, null, null, null, null, null,
            Provenance());
        Assert.NotNull(session);
        Assert.True(await interactive.SendInputAsync(session.Id,
            [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));

        var result = await interactive.TrySteerInputAsync(session.Id,
            [SessionInputPart.TextPart("steered")], messageUid: "steer-uid",
            displayContent: "Steered display").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.Accepted, result.Status);
        Assert.Equal("Active", session.Status);
        var persisted = Assert.Single(store.Messages,
            message => message.MessageUid == "steer-uid");
        Assert.Equal("Steered display", persisted.Content);
        Assert.Equal("item-steer", persisted.MessageId);
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

    private static Process StartSteeringFakeAppServer()
    {
        const string script = """
            while ($null -ne ($line = [Console]::In.ReadLine())) {
              try { $request = $line | ConvertFrom-Json } catch { continue }
              if ($null -eq $request.id) { continue }
              if ($request.method -eq 'thread/start') {
                $result = @{ thread = @{ id = 'thread-1' } }
              } elseif ($request.method -eq 'turn/start') {
                $result = @{ turn = @{ id = 'turn-1' } }
              } elseif ($request.method -eq 'turn/steer') {
                $result = @{ turnId = 'turn-1' }
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
                $notification = @{ jsonrpc = '2.0'; method = 'item/started'; params = @{ threadId = 'thread-1'; turnId = 'turn-1'; item = @{ type = 'userMessage'; id = 'item-steer'; clientId = $request.params.clientUserMessageId; content = @() } } } |
                  ConvertTo-Json -Compress -Depth 8
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
