namespace RedCompute.Core.Sessions;

public static class SessionStopReasons
{
    public const string UserStopped = "user_stopped";
    public const string MaintenanceRestart = "maintenance_restart";
    public const string OrphanedOnRestart = "orphaned_on_restart";
    public const string IdleTimeout = "idle_timeout";
    public const string LegacyIdleTtlExpired = "idle_ttl_expired";

    public static bool IsRestartRecoverable(string? reason)
        => reason is MaintenanceRestart or OrphanedOnRestart;

    public static bool IsAutoResumable(string? reason)
        => IsRestartRecoverable(reason) || reason is IdleTimeout or LegacyIdleTtlExpired;
}
