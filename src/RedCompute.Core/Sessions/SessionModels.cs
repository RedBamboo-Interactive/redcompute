namespace RedCompute.Core.Sessions;

public class ModelInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Fast { get; init; }
}

public class SessionProjectInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool HasClaudeMd { get; init; }
    public bool HasIcon { get; init; }
}

public record SessionExecuteResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error);

public record SessionGenerateResult(
    bool Success, string? Text, string? StreamOutput, string? Model,
    int InputTokens, int OutputTokens, double? CostUsd, string? Error);

public enum InterruptResult { Interrupted, NotActive, NotFound, Error }

public record ImageAttachment(string MediaType, string Base64);
