using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexSessionInfo
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public string? ScratchDirectory { get; set; }
    public Guid? RepositoryId { get; set; }
    public string Status { get; set; } = "Starting";
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public Guid? JobId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>App-server thread id — what thread/resume needs to reattach.</summary>
    public string? ThreadId { get; set; }

    /// <summary>The thread's history contract, fixed when it was created. Null until a start or resume reports it.</summary>
    public string? HistoryMode { get; set; }

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

    public Core.Sessions.SessionAccessState Access
    {
        get => new(AccessLevel, MaxAccessLevel, Unattended, AccessLevelBeforePlan, AccessLevelSource);
        set
        {
            AccessLevel = value.AccessLevel;
            MaxAccessLevel = value.MaxAccessLevel;
            Unattended = value.Unattended;
            AccessLevelBeforePlan = value.AccessLevelBeforePlan;
            AccessLevelSource = value.AccessLevelSource;
        }
    }
    public string? Source { get; set; }
    public int? ContextWindow { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }

    // How the most recent turn ended (Core.Sessions.SessionTurnOutcomes) and when. Live state only.
    public string? LastTurnOutcome { get; set; }
    public DateTimeOffset? LastTurnEndedAt { get; set; }
}
