using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
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

    private sealed class FakeStore(params CodexSessionRecord[] active) : ICodexSessionStore
    {
        public List<CodexSessionRecord> Saved { get; } = [];

        public List<CodexSessionRecord> GetActiveSessions() => [.. active];
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
