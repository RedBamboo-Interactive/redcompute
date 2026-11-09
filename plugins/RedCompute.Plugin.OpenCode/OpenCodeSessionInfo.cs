namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionInfo
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public string? ScratchDirectory { get; set; }
    public Guid? RepositoryId { get; set; }
    public string Status { get; set; } = "Starting";
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ContextWindow { get; set; }
    public Guid? JobId { get; set; }
    public string? OpenCodeSessionId { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public int? ProcessId { get; set; }
    public string? DeveloperInstructions { get; set; }
    /// <summary>
    /// Access level: ask (request_permission parks as a permission card) or full (allow_once, the
    /// behaviour before access levels). Null on a row written before access levels.
    /// </summary>
    public string? AccessLevel { get; set; }
    public string? MaxAccessLevel { get; set; }
    public string? AccessLevelBeforePlan { get; set; }
    public bool Unattended { get; set; }

    public RedCompute.Core.Sessions.SessionAccessState Access
    {
        get => new(AccessLevel, MaxAccessLevel, Unattended, AccessLevelBeforePlan);
        set
        {
            AccessLevel = value.AccessLevel;
            MaxAccessLevel = value.MaxAccessLevel;
            Unattended = value.Unattended;
            AccessLevelBeforePlan = value.AccessLevelBeforePlan;
        }
    }
}
