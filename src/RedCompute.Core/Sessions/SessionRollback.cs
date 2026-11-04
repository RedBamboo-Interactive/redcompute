namespace RedCompute.Core.Sessions;

/// <summary>
/// Why a session cannot be rolled back right now. A rollback is only valid on an idle session;
/// the route reports every reason that applies so the caller can say which.
/// </summary>
public static class SessionRollbackBlockers
{
    public const string TurnActive = "turn_active";
    public const string QuestionParked = "question_parked";
    public const string BackgroundTasksRunning = "background_tasks_running";
    public const string InputQueued = "input_queued";
    public const string SessionStopped = "session_stopped";
}

/// <summary>Stable refusal codes of the rollback route, beyond the idle blockers.</summary>
public static class SessionRollbackRefusals
{
    public const string Unsupported = "rollback_unsupported";
    public const string NotIdle = "rollback_not_idle";
    public const string TargetNotFound = "target_not_found";
    public const string TargetNotUserMessage = "target_not_user_message";
    public const string TargetBeforeCompaction = "target_before_compaction";
    public const string TargetNotMapped = "target_not_mapped";
    public const string TargetMidTurn = "target_mid_turn";
    public const string ProviderThreadLegacy = "provider_thread_legacy";
    public const string ProviderHistoryMissing = "provider_history_missing";
    public const string ProviderRejected = "provider_rejected";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string Incomplete = "rollback_incomplete";
}

/// <summary>One canonical user turn, oldest first, as the provider's rollback needs to see it.</summary>
public sealed record SessionRollbackUserTurn(string MessageUid, string? Content);

/// <summary>
/// What the shared rollback service asks a provider to do: drop <see cref="TargetMessageUid"/> and
/// everything after it from the provider's own context. Files are never touched. A provider must
/// treat a repeat of the same <see cref="RollbackId"/> as already done: the service re-issues it
/// when a restart interrupts the rollback at an unknown point.
/// </summary>
public sealed record SessionRollbackRequest(
    string SessionId,
    string RollbackId,
    string TargetMessageUid,
    IReadOnlyList<SessionRollbackUserTurn> KeptUserTurns,
    IReadOnlyList<SessionRollbackUserTurn> DroppedUserTurns);

public enum SessionRollbackStatus
{
    Applied,
    Refused,
    Unavailable,
    Unsupported,
}

public sealed record SessionRollbackResult(SessionRollbackStatus Status, string? Code = null, string? Message = null)
{
    public static SessionRollbackResult Applied() => new(SessionRollbackStatus.Applied);
    public static SessionRollbackResult Refused(string code, string message) => new(SessionRollbackStatus.Refused, code, message);
    public static SessionRollbackResult Unavailable(string message) =>
        new(SessionRollbackStatus.Unavailable, SessionRollbackRefusals.ProviderUnavailable, message);
    public static SessionRollbackResult Unsupported() =>
        new(SessionRollbackStatus.Unsupported, SessionRollbackRefusals.Unsupported, "This provider cannot roll a conversation back");
}
