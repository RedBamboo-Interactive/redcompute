namespace RedCompute.Core.Sessions;

/// <summary>
/// Work a provider runs in the background of a session (a subagent, a background shell) after the
/// tool call that launched it has returned. It never makes the session Active: session status
/// describes the main loop only. RedCompute owns this state from the provider's own lifecycle
/// events; clients project it.
/// </summary>
public sealed class BackgroundTaskInfo
{
    public required string TaskId { get; init; }
    /// <summary><see cref="BackgroundTaskKinds"/>: agent, shell or other.</summary>
    public required string Kind { get; init; }
    /// <summary><see cref="BackgroundTaskStatuses"/>.</summary>
    public required string Status { get; init; }
    public string? Description { get; init; }
    public string? AgentType { get; init; }
    public string? Summary { get; init; }
    public BackgroundTaskUsage? Usage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>The provider's latest progress line, for example "Running Echo alpha".</summary>
    public string? LastActivity { get; init; }
    public string? LastToolName { get; init; }
    /// <summary>The tool call that launched the task; its transcript square owns the task.</summary>
    public string? ParentToolUseId { get; init; }
    /// <summary>The logical turn that launched the task.</summary>
    public string? MessageUid { get; init; }
    /// <summary>Set when another background task launched this one (a subagent's own shell).</summary>
    public string? ParentTaskId { get; init; }
}

public sealed class BackgroundTaskUsage
{
    public int? ToolUses { get; init; }
    public long? Tokens { get; init; }
    public long? DurationMs { get; init; }
}

public static class BackgroundTaskKinds
{
    public const string Agent = "agent";
    public const string Shell = "shell";
    public const string Other = "other";
}

public static class BackgroundTaskStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
    /// <summary>The provider process ended (exit, kill, restart) while the task was still running.</summary>
    public const string Lost = "lost";

    public static bool IsTerminal(string? status) => status is Completed or Failed or Stopped or Lost;
}

/// <summary>
/// Why a turn started without user input. A background task finishing wakes the provider, which
/// then talks on its own; clients mark that turn instead of showing an unexplained reply.
/// </summary>
public sealed class TurnOrigin
{
    public const string BackgroundTask = "background-task";

    public required string Kind { get; init; }
    public string? TaskId { get; init; }
}

public enum BackgroundTaskStopStatus { Accepted, SessionNotFound, TaskNotFound, Unsupported, Failed }

public sealed record BackgroundTaskStopResult(BackgroundTaskStopStatus Status, string? Message = null)
{
    public static BackgroundTaskStopResult Accepted() => new(BackgroundTaskStopStatus.Accepted);
    public static BackgroundTaskStopResult Unsupported() => new(BackgroundTaskStopStatus.Unsupported,
        "The provider does not support stopping background tasks");
}
