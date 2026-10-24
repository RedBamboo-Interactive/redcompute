namespace RedCompute.Core.Sessions;

/// <summary>
/// What a session is doing right now, derived in this one place from the turn state
/// (<see cref="SessionStatus"/>), its parked questions and its background tasks. Status stays the
/// turn state because steering, queue delivery and idle parking depend on it; this is the
/// projection clients read instead of re-deriving it.
/// </summary>
public static class SessionWorkStates
{
    /// <summary>A turn is running (or the session is starting one).</summary>
    public const string Working = "working";
    /// <summary>The running turn is parked on a structured question and waits for an answer.</summary>
    public const string AwaitingAnswer = "awaiting_answer";
    /// <summary>The turn ended but background tasks still run and may wake the session again.</summary>
    public const string Background = "background";
    /// <summary>Nothing is running.</summary>
    public const string Idle = "idle";

    public static string Derive(SessionStatus status, int pendingQuestions, IEnumerable<BackgroundTaskInfo> backgroundTasks)
    {
        if (pendingQuestions > 0) return AwaitingAnswer;
        if (status is SessionStatus.Active or SessionStatus.Starting) return Working;
        if (status == SessionStatus.Idle && RunningBackgroundTasks(backgroundTasks) > 0) return Background;
        return Idle;
    }

    /// <summary>Background tasks that have not reached a terminal state.</summary>
    public static int RunningBackgroundTasks(IEnumerable<BackgroundTaskInfo> backgroundTasks)
        => backgroundTasks.Count(task => !BackgroundTaskStatuses.IsTerminal(task.Status));
}
