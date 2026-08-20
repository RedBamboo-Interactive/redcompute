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

    public void Start(CodexSessionInfo info, JobProvenance provenance, bool confidential = false)
    {
        var job = _jobs.CreateJob(new JobSubmission(
            "ai-session", "Codex", InputJson(info, resumed: false), provenance,
            IdempotencyKey(info.Id), info.Title ?? info.ProjectName, "Interactive session",
            Confidential: confidential));

        if (!job.IsIdempotencyReuse)
            _jobs.StartInvocation(job.Id, provenance);
        info.JobId = job.Id;
    }

    public void Resume(CodexSessionInfo info, JobProvenance? provenance = null)
    {
        provenance ??= info.JobId is { } jobId
            ? _jobs.GetJob(jobId)?.CreationProvenance
            : null;
        if (provenance is null)
            throw new JobProvenanceValidationException(
                "A tracked job with provenance is required to resume this session");
        if (info.JobId is { } linkedId && _jobs.GetJob(linkedId) != null)
        {
            _jobs.StartInvocation(linkedId, provenance, JobEventKind.Resumed);
            return;
        }

        var job = _jobs.CreateJob(new JobSubmission(
            "ai-session", "Codex", InputJson(info, resumed: true), provenance,
            IdempotencyKey(info.Id), info.Title ?? info.ProjectName, "Resumed interactive session"));

        _jobs.StartInvocation(job.Id, provenance, JobEventKind.Resumed);
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
    /// Creates a separately auditable child operation for semantic title inference.
    /// If the parent session has no verified provenance, inference fails closed and the
    /// deterministic title remains instead of becoming an untracked hidden model call.
    /// </summary>
    public JobRecord? StartTitleGeneration(
        CodexSessionInfo info,
        string qualityTier,
        string model)
    {
        var parent = info.JobId is { } parentId ? _jobs.GetJob(parentId) : null;
        if (parent?.CreationProvenance is not { } provenance) return null;
        var childProvenance = provenance with
        {
            Trace = provenance.Trace with { ParentJobId = parent.Id.ToString() },
        };

        var job = _jobs.CreateJob(new JobSubmission(
            "ai-session",
            "Codex",
            JsonSerializer.Serialize(new
            {
                operation = "session-title",
                sessionId = info.Id,
                qualityTier,
                model,
            }),
            childProvenance,
            $"ai-session:codex:{info.Id}:title",
            "Generate discussion title",
            "Asynchronous semantic title for an interactive session",
            Confidential: parent.Confidential));

        if (!job.IsIdempotencyReuse)
            _jobs.StartInvocation(job.Id, childProvenance);
        return job;
    }

    public void CompleteTitleGeneration(
        JobRecord? job,
        string title,
        string model,
        string qualityTier,
        int inputTokens,
        int outputTokens,
        double? costUsd)
    {
        if (job is null) return;
        _jobs.MarkCompleted(job.Id, resultJson: JsonSerializer.Serialize(new
        {
            title,
            model,
            qualityTier,
            inputTokens,
            outputTokens,
        }), costUsd: costUsd);
    }

    public void FailTitleGeneration(JobRecord? job, string error)
    {
        if (job is null) return;
        _jobs.MarkFailed(job.Id, error, resultJson: JsonSerializer.Serialize(new
        {
            operation = "session-title",
            fallback = "opening-message",
        }));
    }

    public void CancelTitleGeneration(JobRecord? job)
    {
        if (job is not null) _jobs.MarkCancelled(job.Id);
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
        repositoryId = info.RepositoryId,
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
        RepositoryId = r.RepositoryId,
        Status = r.Status,
        StartedAt = r.StartedAt,
        Model = r.Model,
        Title = r.Title,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CachedInputTokens = r.CachedInputTokens,
        ContextTokens = r.ContextTokens,
        JobId = r.JobId,
        ThreadId = r.ThreadId,
        ProcessId = r.ProcessId,
        LastActivity = r.LastActivity,
        Effort = r.Effort,
        QualityTier = r.QualityTier,
        ProviderEntity = r.ProviderEntity,
        Source = r.Source,
        ContextWindow = r.ContextWindow,
        UserId = r.UserId,
        UserName = r.UserName,
        UserAvatarUrl = r.UserAvatarUrl,
        StopReason = r.StopReason,
    };
}
