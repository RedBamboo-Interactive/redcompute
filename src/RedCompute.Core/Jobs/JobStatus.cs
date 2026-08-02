namespace RedCompute.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Skipped,
    TimedOut
}
