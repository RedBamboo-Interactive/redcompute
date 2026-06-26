namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionRecord
{
    public required string Id { get; set; }
    public required string ProjectName { get; set; }
    public required string ProjectPath { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Model { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public Guid? JobId { get; set; }
    public bool Dismissed { get; set; }
    public string? OpenCodeSessionId { get; set; }
    public string? Effort { get; set; }
    public string? Source { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
}
