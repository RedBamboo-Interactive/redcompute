using System.Text;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services;

/// <summary>Starts Docker Desktop on demand, on the execution host whose docker daemon it is.</summary>
public static class DockerDesktopService
{
    private const string DockerDesktopPath = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
    private const int PollIntervalMs = 2000;
    private const int TimeoutMs = 30000;

    /// <param name="host">Where Docker runs; the shared local host when none is given.</param>
    public static async Task EnsureRunningAsync(Action<string> log, IExecutionHost? host = null)
    {
        host ??= LocalExecutionHost.Shared;
        // Docker Desktop is a Windows host concern. Elsewhere the daemon is a system service
        // (systemd, rootless dockerd, Podman's socket) that RedCompute neither owns nor starts.
        if (!host.Platform.IsWindows)
        {
            log(IsDaemonReady(host)
                ? "[Docker] Docker daemon already running"
                : "[Docker] Docker daemon is not reachable; start it through the system service manager");
            return;
        }

        if (!host.Files.FileExists(host.Path(DockerDesktopPath)))
        {
            log("[Docker] Docker Desktop not installed, skipping auto-start");
            return;
        }

        if (IsDaemonReady(host))
        {
            log("[Docker] Docker Desktop already running");
            return;
        }

        log("[Docker] Starting Docker Desktop...");
        try
        {
            host.Processes.Start(new HostSpawnSpec
            {
                FileName = DockerDesktopPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                Containment = ContainmentMode.None,
                OwnerLabel = "docker-desktop",
            });
        }
        catch (Exception ex)
        {
            log($"[Docker] Failed to launch Docker Desktop: {ex.Message}");
            return;
        }

        var elapsed = 0;
        while (elapsed < TimeoutMs)
        {
            await Task.Delay(PollIntervalMs);
            elapsed += PollIntervalMs;

            if (IsDaemonReady(host))
            {
                log($"[Docker] Docker Desktop ready ({elapsed / 1000}s)");
                return;
            }
        }

        log($"[Docker] Docker Desktop did not become ready within {TimeoutMs / 1000}s");
    }

    private static bool IsDaemonReady(IExecutionHost host)
    {
        try
        {
            var spec = new HostSpawnSpec
            {
                FileName = "docker",
                ArgumentList = ["info"],
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                Containment = ContainmentMode.None,
                OwnerLabel = "docker",
            };

            using var process = host.Processes.Start(spec);
            if (process == null) return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
