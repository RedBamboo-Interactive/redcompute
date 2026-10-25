using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// A session whose working directory was removed (for example a deleted git worktree) must refuse to start or
/// resume with the typed reason <c>project_path_missing</c> on every provider, and park its record under a
/// stop reason that auto-resume never retries.
/// </summary>
public sealed class ProjectPathMissingTests
{
    private static string MissingPath() => Path.Combine(Path.GetTempPath(), $"rc-gone-{Guid.NewGuid():N}");

    [Fact]
    public void Reason_is_not_auto_resumable()
    {
        Assert.Equal("project_path_missing", SessionStopReasons.ProjectPathMissing);
        Assert.False(SessionStopReasons.IsAutoResumable(SessionStopReasons.ProjectPathMissing));
    }

    [Fact]
    public async Task Claude_start_and_resume_report_project_path_missing()
    {
        var gone = MissingPath();
        var store = new ClaudeStore(new ClaudeSessionRecord
        {
            Id = "s1", ProjectName = "p", ProjectPath = gone, ClaudeSessionId = "conv", Status = "Stopped",
            StopReason = SessionStopReasons.MaintenanceRestart,
        });
        var service = new ClaudeSessionService(new ClaudeConfig(), new Jobs(), store, new NoModes(), () => null, (_, _) => { });

        Assert.Null(service.StartSession(gone, null, null, null, null, null, null, null, null, default!));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
        Assert.Contains(gone, service.LastStartError);

        Assert.Null(await service.ResumeSessionAsync("s1"));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
        Assert.Equal(SessionStopReasons.ProjectPathMissing, store.Saved.Last().StopReason);
        Assert.False(SessionStopReasons.IsAutoResumable(store.Saved.Last().StopReason));

        // A later, unrelated failure must not keep reporting the stale typed reason.
        Assert.Null(await service.ResumeSessionAsync("unknown"));
        Assert.Null(service.LastStartErrorCode);
    }

    [Fact]
    public async Task OpenCode_resume_reports_project_path_missing_and_parks_the_record()
    {
        var gone = MissingPath();
        var store = new OpenCodeStore(new OpenCodeSessionRecord
        {
            Id = "s1", ProjectName = "p", ProjectPath = gone, OpenCodeSessionId = "oc", Status = "Stopped",
            StopReason = SessionStopReasons.OrphanedOnRestart,
        });
        var service = new OpenCodeSessionService(new OpenCodeConfig(), null!, store, (_, _) => { });

        Assert.Null(await service.ResumeSession("s1"));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
        Assert.Equal(SessionStopReasons.ProjectPathMissing, store.Saved.Last().StopReason);

        Assert.Null(await service.StartSession(gone, null, null, null, null, null, null, null, null, null, null, null, default!));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
    }

    [Fact]
    public async Task Codex_start_and_resume_refuse_instead_of_inheriting_the_service_cwd()
    {
        var gone = MissingPath();
        var config = new CodexConfig();
        var jobs = new Jobs();
        var store = new CodexStore(new CodexSessionRecord
        {
            Id = "s1", ProjectName = "p", ProjectPath = gone, ThreadId = "thread", Status = "Stopped",
            StopReason = SessionStopReasons.MaintenanceRestart,
        });
        var service = new CodexInteractiveService(config, store, new CodexModelCatalog(config, (_, _) => { }),
            new CodexSessionService(config, jobs, store, (_, _) => { }), jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoModes(), () => null, (_, _) => { });

        Assert.Null(await service.StartSessionAsync(gone, null, null, null, null, null, null, null, null, default!));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);

        Assert.Null(await service.ResumeSessionAsync("s1"));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
        Assert.Equal(SessionStopReasons.ProjectPathMissing, store.Saved.Last().StopReason);
        Assert.False(SessionStopReasons.IsAutoResumable(store.Saved.Last().StopReason));
    }

    private sealed class NoModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class Jobs : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class ClaudeStore(ClaudeSessionRecord record) : IClaudeSessionStore
    {
        public List<ClaudeSessionRecord> Saved { get; } = [];
        public ClaudeSessionRecord? FindSession(string sessionId) => sessionId == record.Id ? record : null;
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<ClaudeSessionRecord> GetActiveSessions() => [];
        public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(ClaudeSessionRecord r) => Saved.Add(r);
        public void DismissSession(string sessionId) { }
        public void AddMessage(ClaudeMessageRecord message) { }
        public void AddMessages(List<ClaudeMessageRecord> messages) { }
        public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public DateTimeOffset GetLastMessageTimestamp(string sessionId) => default;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class OpenCodeStore(OpenCodeSessionRecord record) : IOpenCodeSessionStore
    {
        public List<OpenCodeSessionRecord> Saved { get; } = [];
        public OpenCodeSessionRecord? FindSession(string sessionId) => sessionId == record.Id ? record : null;
        public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<OpenCodeSessionRecord> GetActiveSessions() => [];
        public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(OpenCodeSessionRecord r) => Saved.Add(r);
        public void DismissSession(string sessionId) { }
        public void AddMessage(OpenCodeMessageRecord message) { }
        public void AddMessages(List<OpenCodeMessageRecord> messages) { }
        public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class CodexStore(CodexSessionRecord record) : ICodexSessionStore
    {
        public List<CodexSessionRecord> Saved { get; } = [];
        public CodexSessionRecord? FindSession(string sessionId) => sessionId == record.Id ? record : null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(CodexSessionRecord r) => Saved.Add(r);
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { }
        public void AddMessages(List<CodexMessageRecord> messages) { }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
