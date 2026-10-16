using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexSessionRecoveryTests
{
    [Fact]
    public void Planned_restart_preserves_recoverable_stop_reason()
    {
        var path = CheckpointPath();
        var store = new FakeStore(Session("session-1"));
        try
        {
            PlannedRestartCheckpoint.Write(new PlannedRestartData(
                "run-1",
                DateTimeOffset.UtcNow,
                [new PlannedRestartSession("codex", "session-1", null)]), path);
            Assert.NotNull(PlannedRestartCheckpoint.LoadForStartup(path));

            CodexSessionService.RecoverSessions(store, (_, _) => { });

            var saved = Assert.Single(store.Saved);
            Assert.Equal("Stopped", saved.Status);
            Assert.Equal("maintenance_restart", saved.StopReason);
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Unplanned_restart_marks_session_as_orphan_recoverable()
    {
        var path = CheckpointPath();
        var store = new FakeStore(Session("session-2"));
        try
        {
            Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path));

            CodexSessionService.RecoverSessions(store, (_, _) => { });

            var saved = Assert.Single(store.Saved);
            Assert.Equal("Stopped", saved.Status);
            Assert.Equal("orphaned_on_restart", saved.StopReason);
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "orphaned_on_restart")]
    [InlineData(true, "maintenance_restart")]
    public void Windows_terminated_session_becomes_restart_recoverable_with_identity_intact(
        bool checkpointed, string expectedReason)
    {
        var path = CheckpointPath();
        var session = WindowsTerminated("session-3");
        var startedAt = session.StartedAt;
        var lastActivity = session.LastActivity;
        var jobId = session.JobId;
        var store = new FakeStore(session);
        try
        {
            if (checkpointed)
                PlannedRestartCheckpoint.Write(new PlannedRestartData(
                    "run-2",
                    DateTimeOffset.UtcNow,
                    [new PlannedRestartSession("codex", "session-3", jobId)]), path);
            PlannedRestartCheckpoint.LoadForStartup(path);

            CodexSessionService.RecoverSessions(store, (_, _) => { });

            var saved = Assert.Single(store.Saved);
            Assert.Same(session, saved);
            Assert.Equal("Stopped", saved.Status);
            Assert.Equal(expectedReason, saved.StopReason);
            Assert.Null(saved.ProcessId);
            Assert.Equal("thread-3", saved.ThreadId);
            Assert.Equal(jobId, saved.JobId);
            Assert.Equal(7, saved.MessageCount);
            Assert.Equal("Discord ZIP Attachment Review", saved.Title);
            Assert.Equal("user-1", saved.UserId);
            Assert.Equal(startedAt, saved.StartedAt);
            Assert.Equal(lastActivity, saved.LastActivity);
            Assert.False(saved.Dismissed);
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Error", "process_exited:1", "thread-4", false)]
    [InlineData("Error", "process_exited:-1", "thread-4", false)]
    [InlineData("Stopped", "process_exited", "thread-4", false)]
    [InlineData("Error", "process_exited:1073807364", "thread-4", true)]
    [InlineData("Error", "process_exited:1073807364", null, false)]
    [InlineData("Error", "process_exited:1073807364", " ", false)]
    [InlineData("Stopped", "user_stopped", "thread-4", false)]
    [InlineData("Error", "provider_session_missing", "thread-4", false)]
    public void Other_terminal_sessions_are_left_untouched(
        string status, string stopReason, string? threadId, bool dismissed)
    {
        var session = WindowsTerminated("session-4");
        session.Status = status;
        session.StopReason = stopReason;
        session.ThreadId = threadId;
        session.Dismissed = dismissed;
        var store = new FakeStore(session);

        CodexSessionService.RecoverSessions(store, (_, _) => { });

        Assert.Empty(store.Saved);
        Assert.Equal(status, session.Status);
        Assert.Equal(stopReason, session.StopReason);
    }

    [Fact]
    public void Windows_terminated_recovery_is_idempotent_and_never_touches_the_job()
    {
        PlannedRestartCheckpoint.LoadForStartup(CheckpointPath());
        var store = new FakeStore(WindowsTerminated("session-5"));
        var jobs = DispatchProxy.Create<IJobTracker, RecordingJobTracker>();

        _ = new CodexSessionService(new CodexConfig(), jobs, store, (_, _) => { });
        CodexSessionService.RecoverSessions(store, (_, _) => { });

        var saved = Assert.Single(store.Saved);
        Assert.Equal("orphaned_on_restart", saved.StopReason);
        Assert.Empty(((RecordingJobTracker)(object)jobs).Calls);
    }

    [Fact]
    public void Missing_rollout_can_be_recreated_only_for_an_empty_session()
    {
        var error = new CodexAppServerException(
            "{\"code\":-32600,\"message\":\"no rollout found for thread id thread-1\"}");
        var withHistory = Session("with-history");
        withHistory.MessageCount = 1;

        Assert.True(CodexInteractiveService.CanRecreateMissingEmptyRollout(
            Session("empty"), error));
        Assert.False(CodexInteractiveService.CanRecreateMissingEmptyRollout(
            withHistory, error));
        Assert.False(CodexInteractiveService.CanRecreateMissingEmptyRollout(
            Session("other-error"), new CodexAppServerException("transport unavailable")));
    }

    [Fact]
    public void Replacement_thread_id_uses_the_thread_start_contract()
    {
        using var document = JsonDocument.Parse("""{"thread":{"id":"thread-2"}}""");

        Assert.Equal(
            "thread-2",
            CodexInteractiveService.ThreadIdFromStartResult(document.RootElement));
    }

    [Fact]
    public void Resume_reuses_the_persisted_scratch_directory()
    {
        var record = Session("scratch-session");
        record.ScratchDirectory = @"S:\Nova\discussion";

        Assert.Equal(
            @"S:\Nova\discussion",
            CodexInteractiveService.ScratchDirectoryForResume(record));
    }

    [Fact]
    public void Conversation_only_resume_keeps_the_project_as_its_read_only_scratch()
    {
        var record = Session("conversation-session");
        record.ScratchDirectory = @"S:\Nova\discussion";
        record.ExecutionProfile = SessionExecutionProfile.ConversationOnly;

        Assert.Equal(
            record.ProjectPath,
            CodexInteractiveService.ScratchDirectoryForResume(record));
    }

    private static string CheckpointPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"redcompute-codex-recovery-{Guid.NewGuid():N}");
        return Path.Combine(directory, "checkpoint.json");
    }

    private static CodexSessionRecord Session(string id) => new()
    {
        Id = id,
        ProjectName = "Nova",
        ProjectPath = @"L:\Workspaces\Nova",
        Status = "Idle",
        StartedAt = DateTimeOffset.UtcNow,
        LastActivity = DateTimeOffset.UtcNow,
    };

    private static CodexSessionRecord WindowsTerminated(string id) => new()
    {
        Id = id,
        ProjectName = "Nova",
        ProjectPath = @"L:\Workspaces\Nova",
        Status = "Error",
        StopReason = CodexSessionService.WindowsTerminatedStopReason,
        StartedAt = new DateTimeOffset(2026, 10, 14, 20, 0, 0, TimeSpan.Zero),
        LastActivity = new DateTimeOffset(2026, 10, 14, 23, 29, 5, TimeSpan.Zero),
        Title = "Discord ZIP Attachment Review",
        ThreadId = "thread-3",
        JobId = Guid.Parse("7a1318aa-c08a-4255-a237-a693213cba4d"),
        ProcessId = 4242,
        MessageCount = 7,
        UserId = "user-1",
    };

    public class RecordingJobTracker : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return null;
        }
    }

    private sealed class FakeStore(params CodexSessionRecord[] sessions) : ICodexSessionStore
    {
        public List<CodexSessionRecord> Saved { get; } = [];

        public List<CodexSessionRecord> GetActiveSessions() =>
            [.. sessions.Where(s => s.Status is "Active" or "Starting" or "Idle" or "Waiting")];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) =>
            [.. sessions.Where(s => s.Status == status && s.StopReason == stopReason)];
        public void SaveSession(CodexSessionRecord record) => Saved.Add(record);
        public CodexSessionRecord? FindSession(string sessionId) => null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(
            HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { }
        public void AddMessages(List<CodexMessageRecord> messages) { }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
