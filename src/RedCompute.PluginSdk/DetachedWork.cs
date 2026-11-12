namespace RedCompute.PluginSdk;

/// <summary>
/// Starts long-running background work (a service loop, a process reader) so that none of its
/// continuations can ever run on the caller's <see cref="SynchronizationContext"/>, such as a WPF
/// dispatcher. A loop that captures a UI context serialises onto that one thread, with every other
/// loop and every synchronous wait it makes (journal 2026-11-12, 9711f8ea).
/// </summary>
/// <remarks>
/// The work still runs synchronously up to its first incomplete await, exactly as a direct call
/// does, so start-up ordering is unchanged: boot recovery that completes synchronously today still
/// completes before the caller's next statement. Only where the continuations run changes.
/// </remarks>
public static class DetachedWork
{
    public static Task Start(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // A custom scheduler would be captured as well; leave it entirely.
        if (TaskScheduler.Current != TaskScheduler.Default)
            return Task.Run(work);

        var caller = SynchronizationContext.Current;
        if (caller is null) return Invoke(work);
        SynchronizationContext.SetSynchronizationContext(null);
        try { return Invoke(work); }
        finally { SynchronizationContext.SetSynchronizationContext(caller); }
    }

    private static Task Invoke(Func<Task> work)
    {
        try { return work() ?? Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }
}
