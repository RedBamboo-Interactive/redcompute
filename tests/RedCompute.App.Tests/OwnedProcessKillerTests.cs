using System.Diagnostics;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// Recovery kills by a PID recorded before the restart. Windows recycles PIDs, and a recovered
// Claude session once killed the Postgres backend that had inherited its PID.
public sealed class OwnedProcessKillerTests
{
    [Fact]
    public void Leaves_a_reused_pid_alone_when_the_process_is_not_the_cli()
    {
        using var proc = StartPing();
        try
        {
            string? logged = null;

            Assert.False(OwnedProcessKiller.TryKill(proc.Id, "claude", m => logged = m));

            Assert.False(proc.WaitForExit(500));
            Assert.Contains("PID reused", logged);
        }
        finally
        {
            try { proc.Kill(); } catch { }
        }
    }

    [Fact]
    public void Kills_the_process_when_it_is_still_the_cli()
    {
        using var proc = StartPing();
        try
        {
            Assert.True(OwnedProcessKiller.TryKill(proc.Id, "ping"));
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            try { proc.Kill(); } catch { }
        }
    }

    [Fact]
    public void Ignores_missing_pids()
    {
        Assert.False(OwnedProcessKiller.TryKill(null, "claude"));
    }

    private static Process StartPing()
        => Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
}
