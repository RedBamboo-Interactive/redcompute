namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionRecord
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
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ContextWindow { get; set; }
    public Guid? JobId { get; set; }
    public bool Dismissed { get; set; }
    public string? OpenCodeSessionId { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    public string? DeveloperInstructions { get; set; }
    /// <summary>
    /// Access level: ask (request_permission parks as a permission card) or full (allow_once, the
    /// behaviour before access levels). Null on a row written before access levels.
    /// </summary>
    public string? AccessLevel { get; set; }
    public string? MaxAccessLevel { get; set; }
    public string? AccessLevelBeforePlan { get; set; }
    public bool Unattended { get; set; }
    /// <summary>
    /// <c>policy</c> or <c>explicit</c> (<see cref="RedCompute.Core.Sessions.SessionAccessLevelSources"/>). Null on
    /// rows written before sources existed, which count as policy.
    /// </summary>
    public string? AccessLevelSource { get; set; }
}
