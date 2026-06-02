using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace RedCompute.App.Services;

public static class DockerDesktopService
{
    private const string DockerDesktopPath = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
    private const string DockerPipe = "dockerDesktopLinuxEngine";
    private const int PollIntervalMs = 2000;
    private const int TimeoutMs = 30000;

    public static async Task EnsureRunningAsync(Action<string> log)
    {
        if (!File.Exists(DockerDesktopPath))
        {
            log("[Docker] Docker Desktop not installed, skipping auto-start");
            return;
        }

        if (IsDaemonReady())
        {
            log("[Docker] Docker Desktop already running");
            return;
        }

        log("[Docker] Starting Docker Desktop...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DockerDesktopPath,
                UseShellExecute = false,
                CreateNoWindow = true,
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

            if (IsDaemonReady())
            {
                log($"[Docker] Docker Desktop ready ({elapsed / 1000}s)");
                return;
            }
        }

        log($"[Docker] Docker Desktop did not become ready within {TimeoutMs / 1000}s");
    }

    private static bool IsDaemonReady()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "info" },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
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
