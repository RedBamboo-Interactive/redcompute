namespace RedCompute.Core.Sessions;

public class UnifiedSessionInfo
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; set; }
    public string? ProviderSessionId { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public int? ContextWindow { get; set; }
    public string? Effort { get; set; }
    public Guid? JobId { get; set; }
    public string? PermissionMode { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public Dictionary<string, object?>? ProviderMetadata { get; set; }
}
