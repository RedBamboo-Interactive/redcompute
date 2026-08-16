namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionRecord
{
    public required string Id { get; set; }
    public required string ProjectName { get; set; }
    public required string ProjectPath { get; set; }
    public Guid? RepositoryId { get; set; }
    public required string Status { get; set; }
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
}
