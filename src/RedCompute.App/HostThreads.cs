namespace RedCompute.App;

/// <summary>
/// How the desktop shell runs the host's lifecycle away from its own thread. Both helpers run the
/// work on the thread pool, where there is no <see cref="SynchronizationContext"/> to capture.
/// </summary>
internal static class HostThreads
{
    /// <summary>Starts <paramref name="work"/> on the pool; the caller awaits it from its own thread.</summary>
    public static Task StartOffCaller(Func<Task> work) => Task.Run(work);

    /// <summary>
    /// Runs <paramref name="work"/> on the pool and blocks the caller until it ends or
    /// <paramref name="bound"/> elapses. Safe on a UI thread: nothing the work awaits needs the
    /// caller. False when the bound elapsed first. A failure is reported, never thrown: this is for
    /// a shutdown that has to finish either way.
    /// </summary>
    public static bool WaitOffCaller(Func<Task> work, TimeSpan bound, Action<string> report)
    {
        var task = Task.Run(work);
        try
        {
            if (task.Wait(bound)) return true;
            report($"Shutdown did not finish within {bound.TotalSeconds:0} s; exiting anyway");
            return false;
        }
        catch (AggregateException ex)
        {
            report($"Shutdown failed: {ex.InnerException?.Message ?? ex.Message}");
            return true;
        }
    }
}
