namespace RedCompute.App.Services;

/// <summary>
/// Every live CLI session reads its redirected stdout and stderr with ReadLineAsync. On Windows
/// those pipes are synchronous handles, so each pending read parks a thread-pool worker for the
/// life of the session. Above its minimum (the CPU count) the pool adds workers only slowly, so a
/// dozen sessions starved every request, /ping included, for seconds at a time. A higher floor lets
/// the pool start a worker immediately instead; the readers and their cancellation stay unchanged.
/// </summary>
internal static class ThreadPoolFloor
{
    public const int MinimumWorkerThreads = 256;

    /// <summary>Raises the worker minimum to <paramref name="workers"/> and never lowers it.
    /// Returns the effective worker and completion-port minimums.</summary>
    public static (int Workers, int CompletionPorts) Ensure(int workers = MinimumWorkerThreads)
    {
        ThreadPool.GetMinThreads(out var currentWorkers, out var completionPorts);
        if (currentWorkers < workers)
            ThreadPool.SetMinThreads(workers, completionPorts);

        ThreadPool.GetMinThreads(out currentWorkers, out completionPorts);
        return (currentWorkers, completionPorts);
    }
}
