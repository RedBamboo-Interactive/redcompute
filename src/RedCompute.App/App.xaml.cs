#if WINDOWS
using System.Windows;

namespace RedCompute.App;

// Windows shell: WPF supplies the process entry point and message loop. RedComputeHost
// owns the actual startup and shutdown sequence (see Program.cs for other platforms).
//
// RedCompute is headless: nothing in it uses this dispatcher. The host therefore starts and
// stops on the thread pool, so no service loop, provider reader or queue continuation ever
// captures the dispatcher's context and serialises onto this one thread (journal 2026-11-12,
// 9711f8ea). Code that truly needs the UI thread would marshal with Dispatcher.InvokeAsync.
public partial class App : Application
{
    /// <summary>How long a closing app waits for the host's graceful stop before it exits anyway.</summary>
    /// <remarks>
    /// Below the kernel's 10 s grace (ComputeStopGrace in redleaf
    /// src/RedLeaf.App/Api/StaticServer.cs), after which RedLeaf kills the process tree. Under it,
    /// a stop that finishes in time is never cut off mid-flush (StopAsync disposes the loggers and
    /// log persistence after Registry.StopAll), and a slow one ends here rather than by a kill.
    /// </remarks>
    internal static readonly TimeSpan StopBound = TimeSpan.FromSeconds(8);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // The single-instance mutex is owned by this thread, which also releases it in OnExit.
        if (!RedComputeHost.TryEnterSingleInstance())
        {
            Shutdown(RedComputeHost.SecondInstanceExitCode);
            return;
        }

        base.OnStartup(e);
        // A startup failure is rethrown here, on the dispatcher, exactly as before.
        await HostThreads.StartOffCaller(() => RedComputeHost.StartAsync(e.Args));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Synchronous on purpose: once OnExit returns the process ends, so an awaited stop
        // never finished. The stop runs on the pool and the wait is bounded.
        HostThreads.WaitOffCaller(RedComputeHost.StopAsync, StopBound,
            message => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [App] {message}"));
        RedComputeHost.ReleaseSingleInstance();
        base.OnExit(e);
    }
}
#endif
