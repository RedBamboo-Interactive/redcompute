namespace RedCompute.Core.Sessions;

public static class SessionStopReasons
{
    public const string UserStopped = "user_stopped";
    public const string MaintenanceRestart = "maintenance_restart";
    public const string OrphanedOnRestart = "orphaned_on_restart";
    public const string IdleTimeout = "idle_timeout";
    public const string LegacyIdleTtlExpired = "idle_ttl_expired";
    /// <summary>The session's working directory no longer exists (for example a removed git worktree). Never auto-resumed.</summary>
    public const string ProjectPathMissing = "project_path_missing";
    /// <summary>
    /// The harness ran above the session's access level and would not switch down, so it was
    /// stopped. The recorded level stands; the next launch applies it. Never auto-resumed.
    /// </summary>
    public const string AboveAccessLevel = "above_access_level";

    public static bool IsRestartRecoverable(string? reason)
        => reason is MaintenanceRestart or OrphanedOnRestart;

    public static bool IsAutoResumable(string? reason)
        => IsRestartRecoverable(reason) || reason is IdleTimeout or LegacyIdleTtlExpired;
}
