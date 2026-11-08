#if !WINDOWS
using System.Runtime.InteropServices;

namespace RedCompute.App;

// Headless entry point for non-Windows hosts. Windows uses the WPF App instead; both drive
// the same RedComputeHost lifecycle. SIGINT and SIGTERM run the same graceful shutdown as
// WPF's OnExit, then the process exits 0.
public static class Program
{
    public static int Main(string[] args)
    {
        if (!RedComputeHost.TryEnterSingleInstance())
            return RedComputeHost.SecondInstanceExitCode;

        var shutdown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Request(PosixSignalContext context)
        {
            // Keep the runtime from terminating immediately; shutdown runs below.
            context.Cancel = true;
            shutdown.TrySetResult(context.Signal.ToString());
        }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Request);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Request);

        try
        {
            RedComputeHost.StartAsync(args).GetAwaiter().GetResult();

            var signal = shutdown.Task.GetAwaiter().GetResult();
            RedComputeHost.Log($"[App] {signal} received, shutting down");
            RedComputeHost.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [App] Shutdown complete");
            return 0;
        }
        finally
        {
            RedComputeHost.ReleaseSingleInstance();
        }
    }
}
#endif
