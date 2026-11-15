using System.Diagnostics;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// Kills a process recorded by an earlier RedCompute run. Recorded PIDs outlive their process,
/// and Windows recycles PIDs quickly, so by the time a stale row is recovered the PID may belong
/// to something unrelated (a Postgres backend, once). Only kill when the process still looks
/// like the CLI the provider launches. Internal to node zero's execution host
/// (<see cref="IHostProcesses.TryKillRecorded"/>).
/// </summary>
internal static class OwnedProcessKiller
{
    // npm .cmd shims run the CLI under cmd.exe/node.exe; Docker-exec sessions run under docker.exe.
    private static readonly string[] LauncherNames = ["node", "cmd", "docker"];

    public static bool TryKill(int? pid, string cliName, Action<string>? log = null)
    {
        if (pid is not { } p) return false;
        try
        {
            using var proc = Process.GetProcessById(p);
            if (!IsExpectedName(proc.ProcessName, cliName))
            {
                log?.Invoke($"Not killing PID {p}: it is now '{proc.ProcessName}', not {cliName} (PID reused)");
                return false;
            }
            proc.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExpectedName(string processName, string cliName)
        => string.Equals(processName, cliName, StringComparison.OrdinalIgnoreCase)
           || LauncherNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
}
