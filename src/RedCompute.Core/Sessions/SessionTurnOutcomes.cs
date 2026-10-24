namespace RedCompute.Core.Sessions;

/// <summary>
/// How a session turn ended. Providers report <see cref="Completed"/>, <see cref="Interrupted"/> or
/// <see cref="Failed"/> through <see cref="UnifiedSessionInfo.LastTurnOutcome"/>; RedCompute adds
/// <see cref="Stopped"/> for a turn the session was stopped under.
/// </summary>
public static class SessionTurnOutcomes
{
    /// <summary>The turn ran to its natural end, including one that raced an interrupt and won.</summary>
    public const string Completed = "completed";
    /// <summary>An interrupt aborted the turn (gracefully, or by replacing the provider process).</summary>
    public const string Interrupted = "interrupted";
    /// <summary>The turn ended with a provider or model error.</summary>
    public const string Failed = "failed";
    /// <summary>The session was stopped while the turn ran.</summary>
    public const string Stopped = "stopped";
}

/// <summary>Where an interrupt RedCompute sent to a running turn came from.</summary>
public static class SessionInterruptSources
{
    /// <summary>An input admitted with delivery interrupt-current.</summary>
    public const string InterruptCurrent = "interrupt-current";
    /// <summary>POST input-queue/send-now.</summary>
    public const string SendNow = "send-now";
    /// <summary>POST /interrupt, a plain stop of the running turn.</summary>
    public const string Interrupt = "interrupt";
}

/// <summary>
/// The most recent turn of a session as RedCompute recorded it: overlaid on session reads and
/// session.updated events so clients label an interrupted turn as interrupted, not completed.
/// </summary>
public sealed record SessionTurnSummary(
    string TurnId,
    string? Outcome,
    DateTimeOffset? EndedAt,
    string? InterruptSource,
    string? InterruptedByMessageUid);
