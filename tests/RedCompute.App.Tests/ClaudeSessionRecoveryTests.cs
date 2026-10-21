using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using ClaudeStatus = RedCompute.Plugin.ClaudeCode.SessionStatus;

namespace RedCompute.App.Tests;

// RedCompute only calls StartAsync on a capability's default provider. Claude is not the
// ai-session default, so recovery must happen when the service is constructed or an Idle
// session left by a restart stays "writable" and every send is rejected.
public sealed class ClaudeSessionRecoveryTests
{
    [Fact]
    public void Construction_recovers_idle_session_left_by_unplanned_restart()
    {
        var path = CheckpointPath();
        var store = new FakeStore(Session("idle-1", "Idle", "completed"));
        try
        {
            Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path));

            _ = CreateService(store);

            var saved = Assert.Single(store.Saved);
            Assert.Equal("idle-1", saved.Id);
            Assert.Equal("Stopped", saved.Status);
            Assert.Equal("orphaned_on_restart", saved.StopReason);
            Assert.NotNull(saved.StoppedAt);
            Assert.Null(saved.ProcessId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Construction_marks_checkpointed_session_as_maintenance_restart()
    {
        var path = CheckpointPath();
        var store = new FakeStore(Session("idle-2", "Idle", "completed"));
        try
        {
            PlannedRestartCheckpoint.Write(new PlannedRestartData(
                "run-1",
                DateTimeOffset.UtcNow,
                [new PlannedRestartSession("claude-code", "idle-2", null)]), path);
            Assert.NotNull(PlannedRestartCheckpoint.LoadForStartup(path));

            _ = CreateService(store);

            var saved = Assert.Single(store.Saved);
            Assert.Equal("Stopped", saved.Status);
            Assert.Equal("maintenance_restart", saved.StopReason);
            Assert.NotNull(saved.StoppedAt);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Construction_leaves_explicitly_stopped_session_terminal()
    {
        var path = CheckpointPath();
        var store = new FakeStore(Session("stopped-1", "Stopped", "user_stopped"));
        try
        {
            Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path));

            _ = CreateService(store);

            Assert.Empty(store.Saved);
            Assert.Equal("user_stopped", store.FindSession("stopped-1")!.StopReason);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // A row can say Idle with no process behind it: a restart that skipped recovery, or a late
    // result written over a stop. Treating it as live made every send to that discussion fail
    // as a non-retryable provider_rejected until the user started a new discussion.
    [Fact]
    public void Unloaded_idle_record_is_reported_as_orphaned()
    {
        var store = new FakeStore();
        var service = CreateService(store);
        store.Put(Session("stale-1", "Idle", "completed"));

        var (info, _) = service.GetSession("stale-1");

        Assert.NotNull(info);
        Assert.Equal(ClaudeStatus.Stopped, info!.Status);
        Assert.Equal("orphaned_on_restart", info.StopReason);
        Assert.Contains(service.GetSessions(10), s => s.Id == "stale-1" && s.Status == ClaudeStatus.Stopped);
    }

    [Fact]
    public async Task Send_to_unloaded_idle_record_is_retryable_not_rejected()
    {
        var store = new FakeStore();
        ISessionProvider provider = new ClaudeCodeProvider("ai-session", CreateService(store), store,
            new NullJobTracker(), (_, _) => { });
        store.Put(Session("stale-2", "Idle", "completed"));

        var result = await provider.TrySendInputAsync("stale-2", [SessionInputPart.TextPart("ok, done, shall we test?")]);

        Assert.Equal(SessionInputDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("session_restarting", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Send_to_user_stopped_session_stays_terminal()
    {
        var store = new FakeStore();
        ISessionProvider provider = new ClaudeCodeProvider("ai-session", CreateService(store), store,
            new NullJobTracker(), (_, _) => { });
        store.Put(Session("stopped-2", "Stopped", "user_stopped"));

        var result = await provider.TrySendInputAsync("stopped-2", [SessionInputPart.TextPart("hello")]);

        Assert.Equal(SessionInputDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("session_not_writable", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(ClaudeStatus.Idle, false, ClaudeStatus.Stopped)]
    [InlineData(ClaudeStatus.Active, false, ClaudeStatus.Stopped)]
    [InlineData(ClaudeStatus.Starting, false, ClaudeStatus.Stopped)]
    [InlineData(ClaudeStatus.Idle, true, ClaudeStatus.Starting)]
    [InlineData(ClaudeStatus.Active, true, ClaudeStatus.Starting)]
    [InlineData(ClaudeStatus.Stopped, true, ClaudeStatus.Stopped)]
    [InlineData(ClaudeStatus.Error, false, ClaudeStatus.Error)]
    public void Unloaded_status_reports_forced_restart_window_as_starting(
        ClaudeStatus stored, bool restarting, ClaudeStatus expected)
    {
        Assert.Equal(expected, ClaudeSessionService.UnloadedStatus(stored, restarting).Status);
    }

    private static ClaudeSessionService CreateService(FakeStore store) => new(
        new ClaudeConfig(),
        new NullJobTracker(),
        store,
        new NullQualityModes(),
        () => null,
        (_, _) => { });

    private static ClaudeSessionRecord Session(string id, string status, string? stopReason) => new()
    {
        Id = id,
        ProjectName = "Nova",
        ProjectPath = Path.GetTempPath(),
        Status = status,
        StopReason = stopReason,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static string CheckpointPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"redcompute-claude-recovery-{Guid.NewGuid():N}");
        return Path.Combine(directory, "checkpoint.json");
    }

    private static void Cleanup(string path)
    {
        PlannedRestartCheckpoint.Consume(path);
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeStore(params ClaudeSessionRecord[] sessions) : IClaudeSessionStore
    {
        private readonly List<ClaudeSessionRecord> _sessions = [.. sessions];
        public List<ClaudeSessionRecord> Saved { get; } = [];
        public void Put(ClaudeSessionRecord record) => _sessions.Add(record);

        public ClaudeSessionRecord? FindSession(string sessionId) => _sessions.FirstOrDefault(s => s.Id == sessionId);
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) => _sessions.FirstOrDefault(s => s.JobId == jobId);
        public List<ClaudeSessionRecord> GetActiveSessions() =>
            _sessions.Where(s => s.Status is "Active" or "Idle" or "Starting").ToList();
        public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) =>
            _sessions.Where(s => !excludeIds.Contains(s.Id)).Take(limit).ToList();
        public void SaveSession(ClaudeSessionRecord record) => Saved.Add(record);
        public void DismissSession(string sessionId) { }
        public void AddMessage(ClaudeMessageRecord message) { }
        public void AddMessages(List<ClaudeMessageRecord> messages) { }
        public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public DateTimeOffset GetLastMessageTimestamp(string sessionId) => default;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class NullQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class NullJobTracker : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => throw new NotSupportedException();
        public void MarkRunning(Guid jobId) { }
        public void StartInvocation(Guid jobId, JobProvenance provenance, JobEventKind kind = JobEventKind.Started) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) =>
            throw new InvalidOperationException("Restart recovery must not complete the job");
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) =>
            throw new InvalidOperationException("Restart recovery must not fail the job");
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
        public JobRecord? GetJob(Guid jobId) => null;
    }
}
