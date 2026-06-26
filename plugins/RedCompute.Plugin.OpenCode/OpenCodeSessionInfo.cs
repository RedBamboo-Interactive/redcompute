namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionInfo
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
    public int? ContextWindow { get; set; }
    public Guid? JobId { get; set; }
    public string? OpenCodeSessionId { get; set; }
    public string? Effort { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public int? ProcessId { get; set; }
}

public class ProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool HasClaudeMd { get; init; }
}
