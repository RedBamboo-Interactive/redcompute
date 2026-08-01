using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Owns the one-to-one relationship between an interactive Codex session and its compute job.
/// Keeping this out of the transport makes the lifecycle independently testable and makes every
/// terminal path use the same status/result semantics.
/// </summary>
internal sealed class CodexSessionJobLifecycle
{
    private readonly IJobTracker _jobs;

    public CodexSessionJobLifecycle(IJobTracker jobs) => _jobs = jobs;

    public void Start(CodexSessionInfo info, string? callerInfo)
    {
        var job = _jobs.CreateJob(
            "ai-session", "Codex", InputJson(info, resumed: false),
            callerInfo: callerInfo,
            idempotencyKey: IdempotencyKey(info.Id),
            name: info.Title ?? info.ProjectName,
            rationale: "Interactive session",
            userId: info.UserId,
            userName: info.UserName,
            userAvatarUrl: info.UserAvatarUrl);

        _jobs.MarkRunning(job.Id);
        info.JobId = job.Id;
    }

    public void Resume(CodexSessionInfo info)
    {
        if (info.JobId is { } linkedId && _jobs.GetJob(linkedId) != null)
        {
            _jobs.MarkRunning(linkedId);
            return;
        }

        var job = _jobs.CreateJob(
            "ai-session", "Codex", InputJson(info, resumed: true),
            callerInfo: info.Source,
            idempotencyKey: IdempotencyKey(info.Id),
            name: info.Title ?? info.ProjectName,
            rationale: "Resumed interactive session",
            userId: info.UserId,
            userName: info.UserName,
            userAvatarUrl: info.UserAvatarUrl);

        _jobs.MarkRunning(job.Id);
        info.JobId = job.Id;
    }

    public void Complete(CodexSessionInfo info)
    {
        if (info.JobId is { } jobId)
            _jobs.MarkCompleted(jobId, resultJson: ResultJson(info), costUsd: info.CostUsd);
    }

    public void Fail(CodexSessionInfo info, string error)
    {
        if (info.JobId is { } jobId)
            _jobs.MarkFailed(jobId, error, resultJson: ResultJson(info));
    }

    public void Rename(CodexSessionInfo info)
    {
        if (info.JobId is { } jobId && !string.IsNullOrWhiteSpace(info.Title))
            _jobs.UpdateName(jobId, info.Title);
    }

    /// <summary>
    /// Reconstruct the job row from the surviving session record. LastActivity is the only honest
    /// terminal timestamp available for the affected sessions; the result metadata calls this out
    /// rather than pretending it is an exact historical stop time.
    /// </summary>
    public JobRecord Restore(CodexSessionRecord session)
    {
        var status = session.Status switch
        {
            "Error" => JobStatus.Failed,
            "Stopped" => JobStatus.Completed,
            _ when session.Dismissed => JobStatus.Completed,
            _ => JobStatus.Running,
        };

        var terminal = status is JobStatus.Completed or JobStatus.Failed;
        DateTimeOffset? completedAt = terminal ? session.LastActivity ?? session.StartedAt : null;
        var info = ToInfo(session);
        var job = new JobRecord
        {
            CapabilitySlug = "ai-session",
            ProviderName = "Codex",
            Status = status,
            QueuedAt = session.StartedAt,
            StartedAt = session.StartedAt,
            CompletedAt = completedAt,
            InputJson = InputJson(info, resumed: false),
            ResultJson = JsonSerializer.Serialize(new
            {
                sessionId = session.Id,
                messages = session.MessageCount,
                inputTokens = session.InputTokens,
                outputTokens = session.OutputTokens,
                cachedInputTokens = session.CachedInputTokens,
                recovered = true,
                terminalTimestampInferredFromLastActivity = terminal,
            }),
            ErrorMessage = status == JobStatus.Failed
                ? "Recovered Codex session ended in Error; the original process error was not persisted"
                : null,
            CallerInfo = session.Source,
            IdempotencyKey = IdempotencyKey(session.Id),
            Name = session.Title ?? session.ProjectName,
            Rationale = "Interactive session recovered after missing job-link regression",
            CostUsd = session.CostUsd,
            UserId = session.UserId,
            UserName = session.UserName,
            UserAvatarUrl = session.UserAvatarUrl,
            Progress = terminal ? 1.0 : 0.0,
        };

        return _jobs.RestoreJob(job);
    }

    internal static string IdempotencyKey(string sessionId) => $"ai-session:codex:{sessionId}";

    private static string InputJson(CodexSessionInfo info, bool resumed) => JsonSerializer.Serialize(new
    {
        projectPath = info.ProjectPath,
        projectName = info.ProjectName,
        sessionId = info.Id,
        model = info.Model,
        effort = info.Effort,
        resumed,
    });

    private static string ResultJson(CodexSessionInfo info) => JsonSerializer.Serialize(new
    {
        sessionId = info.Id,
        messages = info.MessageCount,
        inputTokens = info.InputTokens,
        outputTokens = info.OutputTokens,
        cachedInputTokens = info.CachedInputTokens,
    });

    private static CodexSessionInfo ToInfo(CodexSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = r.Status,
        StartedAt = r.StartedAt,
        Model = r.Model,
        Title = r.Title,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CachedInputTokens = r.CachedInputTokens,
        JobId = r.JobId,
        ThreadId = r.ThreadId,
        ProcessId = r.ProcessId,
        LastActivity = r.LastActivity,
        Effort = r.Effort,
        Source = r.Source,
        ContextWindow = r.ContextWindow,
        UserId = r.UserId,
        UserName = r.UserName,
        UserAvatarUrl = r.UserAvatarUrl,
        StopReason = r.StopReason,
    };
}
