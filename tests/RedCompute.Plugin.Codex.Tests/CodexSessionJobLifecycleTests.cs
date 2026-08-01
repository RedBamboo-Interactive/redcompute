using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexSessionJobLifecycleTests
{
    [Fact]
    public void StartCreatesAndLinksRunningJob()
    {
        var tracker = new FakeJobTracker();
        var lifecycle = new CodexSessionJobLifecycle(tracker);
        var session = SessionInfo();

        lifecycle.Start(session, "nova");

        var job = Assert.Single(tracker.Jobs.Values);
        Assert.Equal(job.Id, session.JobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal("ai-session", job.CapabilitySlug);
        Assert.Equal("Codex", job.ProviderName);
        Assert.Equal("nova", job.CallerInfo);
        Assert.Equal("ai-session:codex:session-1", job.IdempotencyKey);
        Assert.Equal("user-1", job.UserId);

        using var input = JsonDocument.Parse(job.InputJson);
        Assert.Equal("session-1", input.RootElement.GetProperty("sessionId").GetString());
        Assert.False(input.RootElement.GetProperty("resumed").GetBoolean());
    }

    [Fact]
    public void ResumeReusesLinkedJob()
    {
        var tracker = new FakeJobTracker();
        var lifecycle = new CodexSessionJobLifecycle(tracker);
        var session = SessionInfo();
        lifecycle.Start(session, "nova");
        var originalId = session.JobId;
        tracker.MarkCompleted(originalId!.Value);

        lifecycle.Resume(session);

        Assert.Equal(originalId, session.JobId);
        Assert.Single(tracker.Jobs);
        Assert.Equal(JobStatus.Running, tracker.Jobs[originalId.Value].Status);
    }

    [Fact]
    public void RestoreIsIdempotentAndPreservesHistoricalTimes()
    {
        var tracker = new FakeJobTracker();
        var lifecycle = new CodexSessionJobLifecycle(tracker);
        var started = new DateTimeOffset(2026, 7, 31, 16, 26, 18, TimeSpan.Zero);
        var lastActivity = started.AddMinutes(42);
        var record = SessionRecord("Stopped", started, lastActivity);

        var first = lifecycle.Restore(record);
        var second = lifecycle.Restore(record);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(tracker.Jobs);
        Assert.Equal(JobStatus.Completed, first.Status);
        Assert.Equal(started, first.QueuedAt);
        Assert.Equal(started, first.StartedAt);
        Assert.Equal(lastActivity, first.CompletedAt);
        Assert.Equal(1.0, first.Progress);
        Assert.Contains("terminalTimestampInferredFromLastActivity", first.ResultJson);
    }

    [Theory]
    [InlineData("Idle", JobStatus.Running)]
    [InlineData("Waiting", JobStatus.Running)]
    [InlineData("Error", JobStatus.Failed)]
    public void RestoreMapsSessionState(string sessionStatus, JobStatus expectedJobStatus)
    {
        var lifecycle = new CodexSessionJobLifecycle(new FakeJobTracker());
        var started = DateTimeOffset.Parse("2026-08-01T10:00:00Z");

        var job = lifecycle.Restore(SessionRecord(sessionStatus, started, started.AddMinutes(5)));

        Assert.Equal(expectedJobStatus, job.Status);
        Assert.Equal(expectedJobStatus == JobStatus.Running ? null : started.AddMinutes(5), job.CompletedAt);
    }

    [Fact]
    public void TerminalAndRenameOperationsUpdateLinkedJob()
    {
        var tracker = new FakeJobTracker();
        var lifecycle = new CodexSessionJobLifecycle(tracker);
        var session = SessionInfo();
        lifecycle.Start(session, null);

        session.Title = "Tracked title";
        session.InputTokens = 123;
        lifecycle.Rename(session);
        lifecycle.Complete(session);

        var job = tracker.Jobs[session.JobId!.Value];
        Assert.Equal("Tracked title", job.Name);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Contains("\"inputTokens\":123", job.ResultJson);
    }

    private static CodexSessionInfo SessionInfo() => new()
    {
        Id = "session-1",
        ProjectName = "redcompute",
        ProjectPath = @"T:\Projects\redcompute",
        Status = "Idle",
        StartedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
        Model = "gpt-5.6",
        Effort = "high",
        UserId = "user-1",
        UserName = "Laurent",
    };

    private static CodexSessionRecord SessionRecord(
        string status, DateTimeOffset started, DateTimeOffset lastActivity) => new()
    {
        Id = "historical-1",
        ProjectName = "redcompute",
        ProjectPath = @"T:\Projects\redcompute",
        Status = status,
        StartedAt = started,
        LastActivity = lastActivity,
        MessageCount = 7,
        InputTokens = 1000,
        OutputTokens = 200,
        CachedInputTokens = 300,
        Source = "nova",
    };

    private sealed class FakeJobTracker : IJobTracker
    {
        public Dictionary<Guid, JobRecord> Jobs { get; } = [];

        public JobRecord CreateJob(string capabilitySlug, string providerName, string inputJson,
            string? callerInfo = null, string? idempotencyKey = null, string? name = null,
            string? rationale = null, string? userId = null, string? userName = null,
            string? userAvatarUrl = null)
        {
            var existing = Jobs.Values.FirstOrDefault(j => j.IdempotencyKey == idempotencyKey);
            if (existing != null) return existing;

            var job = new JobRecord
            {
                CapabilitySlug = capabilitySlug,
                ProviderName = providerName,
                InputJson = inputJson,
                CallerInfo = callerInfo,
                IdempotencyKey = idempotencyKey,
                Name = name,
                Rationale = rationale,
                UserId = userId,
                UserName = userName,
                UserAvatarUrl = userAvatarUrl,
            };
            Jobs.Add(job.Id, job);
            return job;
        }

        public void MarkRunning(Guid jobId)
        {
            Jobs[jobId].Status = JobStatus.Running;
            Jobs[jobId].StartedAt = DateTimeOffset.UtcNow;
        }

        public void MarkCompleted(Guid jobId, string? outputLocation = null,
            long? outputSizeBytes = null, string? contentType = null, string? resultJson = null,
            double? costUsd = null)
        {
            Jobs[jobId].Status = JobStatus.Completed;
            Jobs[jobId].CompletedAt = DateTimeOffset.UtcNow;
            Jobs[jobId].ResultJson = resultJson;
            Jobs[jobId].CostUsd = costUsd;
        }

        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null,
            string? resultJson = null)
        {
            Jobs[jobId].Status = JobStatus.Failed;
            Jobs[jobId].CompletedAt = DateTimeOffset.UtcNow;
            Jobs[jobId].ErrorMessage = errorMessage;
            Jobs[jobId].ResultJson = resultJson;
        }

        public void MarkCancelled(Guid jobId) => Jobs[jobId].Status = JobStatus.Cancelled;

        public void UpdateName(Guid jobId, string name) => Jobs[jobId].Name = name;

        public JobRecord? GetJob(Guid jobId) => Jobs.GetValueOrDefault(jobId);

        public JobRecord RestoreJob(JobRecord job)
        {
            var existing = Jobs.Values.FirstOrDefault(j => j.IdempotencyKey == job.IdempotencyKey);
            if (existing != null) return existing;
            Jobs.Add(job.Id, job);
            return job;
        }
    }
}
