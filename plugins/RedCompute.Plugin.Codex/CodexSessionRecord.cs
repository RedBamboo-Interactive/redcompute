using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexSessionRecord
{
    public required string Id { get; set; }
    public required string ProjectName { get; set; }
    public required string ProjectPath { get; set; }
    public string? ScratchDirectory { get; set; }
    public Guid? RepositoryId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Model { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public Guid? JobId { get; set; }
    public bool Dismissed { get; set; }

    /// <summary>
    /// The app-server thread id. This is what <c>thread/resume</c> takes, and it is the only thing
    /// tying our session row to Codex's own rollout on disk (~/.codex/sessions) — so it is also
    /// what lets a thread started here be picked up in the Codex CLI or desktop app.
    /// </summary>
    public string? ThreadId { get; set; }

    /// <summary>PID of the owning app-server, so orphans can be reaped after an ungraceful exit.</summary>
    public int? ProcessId { get; set; }

    public DateTimeOffset? LastActivity { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    public string? DeveloperInstructions { get; set; }
    public string ExecutionProfile { get; set; } = SessionExecutionProfile.Default;
    /// <summary>
    /// Access level: ask (approvals park as permission cards) or full (approvals accepted for the
    /// session, the behaviour before access levels). Null on a row written before access levels.
    /// </summary>
    public string? AccessLevel { get; set; }
    public string? MaxAccessLevel { get; set; }
    public string? AccessLevelBeforePlan { get; set; }
    public bool Unattended { get; set; }
    /// <summary>
    /// <c>policy</c> or <c>explicit</c> (<see cref="Core.Sessions.SessionAccessLevelSources"/>). Null on
    /// rows written before sources existed, which count as policy.
    /// </summary>
    public string? AccessLevelSource { get; set; }

    public string? Source { get; set; }
    public int? ContextWindow { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? HistoryMode { get; set; }
}
