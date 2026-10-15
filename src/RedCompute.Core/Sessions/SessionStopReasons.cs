namespace RedCompute.Core.Sessions;

public static class SessionStopReasons
{
    public const string MaintenanceRestart = "maintenance_restart";
    public const string OrphanedOnRestart = "orphaned_on_restart";

    public static bool IsRestartRecoverable(string? reason)
        => reason is MaintenanceRestart or OrphanedOnRestart;
}
