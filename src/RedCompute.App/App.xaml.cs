#if WINDOWS
using System.Windows;

namespace RedCompute.App;

// Windows shell: WPF supplies the process entry point and message loop. RedComputeHost
// owns the actual startup and shutdown sequence (see Program.cs for other platforms).
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!RedComputeHost.TryEnterSingleInstance())
        {
            Shutdown(RedComputeHost.SecondInstanceExitCode);
            return;
        }

        base.OnStartup(e);
        await RedComputeHost.StartAsync(e.Args);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await RedComputeHost.StopAsync();
        RedComputeHost.ReleaseSingleInstance();
        base.OnExit(e);
    }
}
#endif
