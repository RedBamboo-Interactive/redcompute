namespace RedCompute.Core.Claude;

public enum SessionStatus { Starting, Active, Idle, Stopped, Error }

public class ClaudeSessionInfo
{
    public required string Id { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; set; }
    public string? ClaudeSessionId { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
    public string? Effort { get; set; }
    public Guid? JobId { get; set; }
    public string PermissionMode { get; set; } = "bypassPermissions";
}

public class ProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool HasClaudeMd { get; init; }
    public bool HasIcon { get; init; }
}
