using System.Diagnostics;
using System.IO;
using System.Text;

namespace RedCompute.App.Services;

public static class DefenderExclusionService
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", ".defender-exclusions");

    private static readonly string[] ExcludedProcesses =
    [
        "RedCompute.exe",
        "RedLeaf.exe",
        "CodeRed.exe",
        "claude.exe",
    ];

    public static void EnsureExclusions(Action<string> log)
    {
        if (File.Exists(MarkerPath))
            return;

        var projectRoot = ResolveProjectRoot();
        if (projectRoot == null)
        {
            log("[Defender] Could not resolve project root, skipping exclusions");
            return;
        }

        var script = BuildScript(projectRoot);

        log("[Defender] Adding Windows Defender exclusions (one-time UAC prompt)...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                log("[Defender] Failed to start elevated PowerShell");
                return;
            }

            process.WaitForExit(15000);

            if (process.ExitCode == 0)
            {
                File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
                log("[Defender] Exclusions added successfully");
            }
            else
            {
                log($"[Defender] PowerShell exited with code {process.ExitCode}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            log("[Defender] UAC prompt was declined, skipping exclusions");
        }
        catch (Exception ex)
        {
            log($"[Defender] Failed to add exclusions: {ex.Message}");
        }
    }

    private static string BuildScript(string projectRoot)
    {
        var sb = new StringBuilder();
        sb.Append($"Add-MpPreference -ExclusionPath '{EscapePath(projectRoot)}'; ");

        foreach (var proc in ExcludedProcesses)
            sb.Append($"Add-MpPreference -ExclusionProcess '{proc}'; ");

        sb.Append("exit 0");
        return sb.ToString();
    }

    private static string EscapePath(string path) => path.Replace("'", "''");

    private static string? ResolveProjectRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RedCompute.sln")))
                return dir.Parent?.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
