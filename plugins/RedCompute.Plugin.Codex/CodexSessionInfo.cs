namespace RedCompute.Plugin.Codex;

public class ProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool HasClaudeMd { get; init; }
}

public class CodexSessionInfo
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
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

    /// <summary>App-server thread id — what thread/resume needs to reattach.</summary>
    public string? ThreadId { get; set; }

    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    public string? Source { get; set; }
    public int? ContextWindow { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
}
