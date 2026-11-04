using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

// thread/revert with the app server faked. The shapes come from a real 0.163 app server in scratch:
// a thread with RedCompute's parameters reports historyMode paginated, a fresh server needs
// thread/resume first, and thread/turns/list answers with data and a cursor.
public sealed class CodexRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-codex-rollback", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public CodexRollbackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Rollback_reverts_before_the_turn_that_carried_the_message_and_marks_from_it()
    {
        await using var f = await Fixture.Start(this);
        await f.TurnAsync("first", "u1");
        await f.TurnAsync("second", "u2");
        await f.TurnAsync("third", "u3");

        var result = await f.Service.RollbackAsync(f.Request("u2", ["u1"], ["u2", "u3"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        var revert = Assert.Single(f.Sent("thread/revert"));
        Assert.Equal("thread-1", revert.GetProperty("threadId").GetString());
        Assert.Equal("turn-2", revert.GetProperty("beforeTurnId").GetString());
        Assert.Equal(["u1"], f.Store.GetMessages(f.Id).Where(m => m.Role == "user").Select(m => m.MessageUid));
        Assert.Contains(f.Store.All, m => m.MessageUid == "u3" && m.RolledBack); // Kept on disk.
        Assert.Equal(1, f.Session.MessageCount);
    }

    [Fact]
    public async Task The_next_message_runs_on_the_kept_thread()
    {
        await using var f = await Fixture.Start(this);
        await f.TurnAsync("first", "u1");
        await f.TurnAsync("second", "u2");
        await f.Service.RollbackAsync(f.Request("u2", ["u1"], ["u2"]));

        await f.TurnAsync("again", "u4");

        Assert.Equal("turn-3", f.Store.GetUserTurnId(f.Id, "u4"));
        Assert.Equal(["u1", "u4"], f.Store.GetMessages(f.Id).Where(m => m.Role == "user").Select(m => m.MessageUid));
    }

    [Fact]
    public async Task A_parked_session_is_resumed_on_a_fresh_server_before_the_revert()
    {
        await using var f = await Fixture.Start(this);
        await f.TurnAsync("first", "u1");
        await f.TurnAsync("second", "u2");
        Assert.True(await f.Service.TryParkIdleSessionAsync(f.Id, SessionStopReasons.IdleTimeout));

        var result = await f.Service.RollbackAsync(f.Request("u2", ["u1"], ["u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        var methods = f.Log().Select(entry => entry.Method).ToList();
        Assert.True(methods.LastIndexOf("thread/resume") < methods.LastIndexOf("thread/revert"));
        Assert.Contains("thread/resume", methods);
    }

    [Fact]
    public async Task A_legacy_thread_does_not_advertise_and_is_refused()
    {
        await using var f = await Fixture.Start(this, historyMode: "legacy");
        await f.TurnAsync("first", "u1");

        Assert.False(f.Service.ThreadSupportsRollback(f.Id));
        var result = await f.Service.RollbackAsync(f.Request("u1", [], ["u1"]));

        Assert.Equal(SessionRollbackRefusals.ProviderThreadLegacy, result.Code);
        Assert.Empty(f.Sent("thread/revert"));
    }

    [Fact]
    public async Task A_thread_reporting_no_mode_does_not_advertise()
    {
        await using var f = await Fixture.Start(this, historyMode: "");

        Assert.False(f.Service.ThreadSupportsRollback(f.Id));
    }

    [Fact]
    public async Task A_paginated_thread_advertises()
    {
        await using var f = await Fixture.Start(this);

        Assert.True(f.Service.ThreadSupportsRollback(f.Id));
        // What session.updated carries, as well as what a REST read says.
        Assert.True(CodexInteractiveService.SupportsRollback("paginated"));
        Assert.False(CodexInteractiveService.SupportsRollback("legacy"));
        Assert.False(CodexInteractiveService.SupportsRollback(null));
    }

    [Fact]
    public async Task A_turn_from_before_this_feature_is_refused_rather_than_guessed()
    {
        await using var f = await Fixture.Start(this);
        f.Store.AddMessage(new CodexMessageRecord
        {
            SessionId = f.Id, Role = "user", EventType = "text", Content = "old",
            MessageUid = "old", Timestamp = DateTimeOffset.UtcNow,
        });

        var result = await f.Service.RollbackAsync(f.Request("old", [], ["old"]));

        Assert.Equal(SessionRollbackRefusals.TargetNotMapped, result.Code);
        Assert.Empty(f.Sent("thread/revert"));
    }

    [Fact]
    public async Task A_steered_message_has_no_boundary_of_its_own()
    {
        await using var f = await Fixture.Start(this);
        await f.TurnAsync("first", "u1");
        f.Store.AddMessage(new CodexMessageRecord
        {
            SessionId = f.Id, Role = "user", EventType = "text", Content = "steered",
            MessageUid = "steered", ProviderTurnId = "turn-1", Timestamp = DateTimeOffset.UtcNow,
        });

        var mid = await f.Service.RollbackAsync(f.Request("steered", ["u1"], ["steered"]));
        Assert.Equal(SessionRollbackRefusals.TargetMidTurn, mid.Code);
        Assert.Empty(f.Sent("thread/revert"));

        // Its turn can still be rewound from the message that started it.
        var start = await f.Service.RollbackAsync(f.Request("u1", [], ["u1", "steered"]));
        Assert.Equal(SessionRollbackStatus.Applied, start.Status);
    }

    [Fact]
    public async Task A_provider_refusal_with_the_turn_still_present_changes_nothing()
    {
        await using var f = await Fixture.Start(this, revert: "reject");
        await f.TurnAsync("first", "u1");

        var result = await f.Service.RollbackAsync(f.Request("u1", [], ["u1"]));

        Assert.Equal(SessionRollbackStatus.Refused, result.Status);
        Assert.Equal(SessionRollbackRefusals.ProviderRejected, result.Code);
        Assert.DoesNotContain(f.Store.All, m => m.RolledBack);
        Assert.Equal(1, f.Session.MessageCount);
    }

    [Fact]
    public async Task A_retry_after_the_provider_already_applied_it_is_proved_by_the_turn_being_gone()
    {
        await using var f = await Fixture.Start(this, revert: "already-gone");
        await f.TurnAsync("first", "u1");

        var result = await f.Service.RollbackAsync(f.Request("u1", [], ["u1"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        Assert.Contains(f.Store.All, m => m.MessageUid == "u1" && m.RolledBack);
    }

    [Fact]
    public async Task A_failed_revert_with_an_unreadable_history_is_not_claimed_as_applied()
    {
        await using var f = await Fixture.Start(this, revert: "unreadable");
        await f.TurnAsync("first", "u1");

        var result = await f.Service.RollbackAsync(f.Request("u1", [], ["u1"]));

        Assert.Equal(SessionRollbackStatus.Unavailable, result.Status);
        Assert.DoesNotContain(f.Store.All, m => m.RolledBack);
    }

    [Fact]
    public void History_mode_is_read_from_a_thread_result()
    {
        using var known = JsonDocument.Parse("""{"thread":{"id":"t","historyMode":"paginated"}}""");
        using var missing = JsonDocument.Parse("""{"thread":{"id":"t"}}""");

        Assert.Equal("paginated", CodexInteractiveService.HistoryModeOf(known.RootElement));
        Assert.Null(CodexInteractiveService.HistoryModeOf(missing.RootElement));
    }

    public void Dispose()
    {
        lock (_processes)
        {
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private string _logPath = "";
        public required CodexInteractiveService Service { get; init; }
        public required RollbackStore Store { get; init; }
        public required CodexSessionInfo Session { get; init; }
        public string Id => Session.Id;

        public static async Task<Fixture> Start(CodexRollbackTests owner, string historyMode = "paginated", string revert = "ok")
        {
            var codexPath = Path.Combine(owner._root, "codex.exe");
            File.WriteAllText(codexPath, "test seam only");
            var logPath = Path.Combine(owner._root, $"requests-{Guid.NewGuid():N}.log");
            var store = new RollbackStore();
            var config = new CodexConfig { CodexPath = codexPath };
            var jobs = new OneJob();
            var service = new CodexInteractiveService(
                config, store, new CodexModelCatalog(config, (_, _) => { }),
                new CodexSessionService(config, jobs, store, (_, _) => { }), jobs,
                new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
                () => null, (_, _) => { });
            service.LaunchAppServerProcess = _ => owner.StartServer(logPath, historyMode, revert);
            var session = await service.StartSessionAsync(
                owner._root, null, "owner", null, null, null, null, null, null, Provenance());
            Assert.NotNull(session);
            return new Fixture { Service = service, Store = store, Session = session!, _logPath = logPath };
        }

        public async Task TurnAsync(string text, string uid)
        {
            Assert.True(await Service.SendInputAsync(Id, [SessionInputPart.TextPart(text)], messageUid: uid));
            var patience = Stopwatch.StartNew();
            while (!(Session.Status == "Idle" && Store.GetUserTurnId(Id, uid) is not null))
            {
                Assert.True(patience.Elapsed < TimeSpan.FromSeconds(30), $"Turn never settled: {Session.Status}");
                await Task.Delay(20);
            }
        }

        public SessionRollbackRequest Request(string target, string[] kept, string[] dropped) => new(
            Id, "rollback-1", target,
            kept.Select(uid => new SessionRollbackUserTurn(uid, uid)).ToList(),
            dropped.Select(uid => new SessionRollbackUserTurn(uid, uid)).ToList());

        public List<(string Method, JsonElement Params)> Log()
        {
            if (!File.Exists(_logPath)) return [];
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                return (root.GetProperty("method").GetString()!,
                    root.TryGetProperty("params", out var value) ? value.Clone() : default);
            }).ToList();
        }

        public List<JsonElement> Sent(string method) =>
            Log().Where(entry => entry.Method == method).Select(entry => entry.Params).ToList();

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private Process StartServer(string logPath, string historyMode, string revert)
    {
        var mode = historyMode.Length == 0 ? "" : $"historyMode = '{historyMode}';";
        var script = $$"""
            $log = '{{logPath}}'
            $revert = '{{revert}}'
            $turn = [int](Get-Content -LiteralPath ($log + '.turns') -ErrorAction SilentlyContinue)
            while ($null -ne ($line = [Console]::In.ReadLine())) {
              try { $request = $line | ConvertFrom-Json } catch { continue }
              if ($null -eq $request.id) { continue }
              [System.IO.File]::AppendAllText($log, ($line + "`n"))
              $response = $null
              if ($request.method -eq 'thread/start' -or $request.method -eq 'thread/resume') {
                $result = @{ thread = @{ id = 'thread-1'; {{mode}} } }
              } elseif ($request.method -eq 'turn/start') {
                $turn++
                Set-Content -LiteralPath ($log + '.turns') -Value $turn
                $result = @{ turn = @{ id = "turn-$turn" } }
              } elseif ($request.method -eq 'thread/revert' -and $revert -ne 'ok') {
                $response = @{ jsonrpc = '2.0'; id = $request.id; error = @{ code = -32600; message = 'no such turn in thread' } }
              } elseif ($request.method -eq 'thread/turns/list') {
                if ($revert -eq 'already-gone') { $result = @{ data = @() } }
                elseif ($revert -eq 'unreadable') { $result = @{ turns = 'not the expected shape' } }
                else { $result = @{ data = @(@{ id = 'turn-1' }, @{ id = 'turn-0' }) } }
              } else {
                $result = @{}
              }
              if ($null -eq $response) { $response = @{ jsonrpc = '2.0'; id = $request.id; result = $result } }
              [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 8))
              [Console]::Out.Flush()
              if ($request.method -eq 'turn/start') {
                $started = @{ jsonrpc = '2.0'; method = 'turn/started'; params = @{ threadId = 'thread-1'; turn = @{ id = "turn-$turn" } } }
                [Console]::Out.WriteLine(($started | ConvertTo-Json -Compress -Depth 8))
                $completed = @{ jsonrpc = '2.0'; method = 'turn/completed'; params = @{ threadId = 'thread-1'; turn = @{ id = "turn-$turn"; status = 'completed'; items = @(@{ type = 'agentMessage'; phase = 'final_answer'; text = 'ok' }) } } }
                [Console]::Out.WriteLine(($completed | ConvertTo-Json -Compress -Depth 8))
                [Console]::Out.Flush()
              }
            }
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        var process = Process.Start(startInfo)!;
        lock (_processes) _processes.Add(process);
        return process;
    }

    private static JobProvenance Provenance() => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", "test", null, "Test"), new JobEntrypoint("test", "/test")),
        new JobActor("system", "Test", Id: "test"),
        new JobBeneficiary("system", Reason: "Codex rollback test"),
        [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    private sealed class NoQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class OneJob : IJobTracker
    {
        private readonly JobRecord _job = new()
        {
            Id = Guid.NewGuid(), CapabilitySlug = "ai-session", ProviderName = "Codex", CreationProvenance = Provenance(),
        };
        public JobRecord CreateJob(JobSubmission submission) => _job;
        public void MarkRunning(Guid jobId) { }
        public void StartInvocation(Guid jobId, JobProvenance provenance, JobEventKind kind = JobEventKind.Started) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
        public JobRecord? GetJob(Guid jobId) => _job.Id == jobId ? _job : null;
    }

    private sealed class RollbackStore : ICodexSessionStore
    {
        private readonly Dictionary<string, CodexSessionRecord> _sessions = [];
        private readonly List<CodexMessageRecord> _messages = [];

        public List<CodexMessageRecord> All { get { lock (_messages) return [.. _messages]; } }

        public void SetUserTurnId(string sessionId, string messageUid, string turnId)
        {
            lock (_messages)
            {
                var message = _messages.LastOrDefault(m => m.SessionId == sessionId && m.Role == "user" && m.MessageUid == messageUid);
                if (message is not null) message.ProviderTurnId = turnId;
            }
        }

        public string? GetUserTurnId(string sessionId, string messageUid)
        {
            lock (_messages)
                return _messages.LastOrDefault(m => m.SessionId == sessionId && m.Role == "user" && m.MessageUid == messageUid)?.ProviderTurnId;
        }

        public void MarkMessagesRolledBack(string sessionId, string messageUid)
        {
            lock (_messages)
            {
                var from = _messages.FindIndex(m => m.SessionId == sessionId && m.Role == "user" && m.MessageUid == messageUid);
                if (from < 0) return;
                for (var i = from; i < _messages.Count; i++)
                    if (_messages[i].SessionId == sessionId) _messages[i].RolledBack = true;
            }
        }

        public CodexSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(CodexSessionRecord record) { lock (_sessions) _sessions[record.Id] = record; }
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { lock (_messages) _messages.Add(message); }
        public void AddMessages(List<CodexMessageRecord> messages) { lock (_messages) _messages.AddRange(messages); }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) =>
            All.Where(m => m.SessionId == sessionId && !m.RolledBack).TakeLast(limit).ToList();
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
