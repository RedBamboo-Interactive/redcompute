namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeSessionRecord
{
    public required string Id { get; set; }
    public required string ProjectName { get; set; }
    public required string ProjectPath { get; set; }
    public string? ScratchDirectory { get; set; }
    public Guid? RepositoryId { get; set; }
    public required string Status { get; set; }
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Model { get; set; }
    public string? ClaudeSessionId { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public int? ContextWindow { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    /// <summary>
    /// Provider-native developer instructions supplied when the session was created. Reapplied
    /// as <c>--append-system-prompt</c> on every CLI launch for this session (start and resume).
    /// </summary>
    public string? DeveloperInstructions { get; set; }
    public Guid? JobId { get; set; }
    public bool Dismissed { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    /// <summary>
    /// Background tasks running when the row was last saved (JSON list of BackgroundTaskInfo).
    /// Recovery reads it to settle tasks that died with their CLI process as `lost`.
    /// </summary>
    public string? BackgroundTasksJson { get; set; }
    /// <summary>
    /// A rollback the CLI has not applied yet (<see cref="ClaudeForkTarget"/>). Written only through
    /// <see cref="IClaudeSessionStore.SetForkTarget"/>, so saving a session never clears it by accident.
    /// </summary>
    public string? ForkTargetJson { get; set; }
    /// <summary>
    /// Access level (plan, ask, auto, full) applied on every launch. Null for a row written before
    /// access levels: it resolves fresh from policy, and the old hardcoded bypass is never trusted.
    /// </summary>
    public string? AccessLevel { get; set; }
    public string? MaxAccessLevel { get; set; }
    public string? AccessLevelBeforePlan { get; set; }
    public bool Unattended { get; set; }
}
