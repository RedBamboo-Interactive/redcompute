using System.Diagnostics;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Test launch seams written against a start info, used where a Codex seam is typed on the execution
/// host's spec. The spec reaches the test as the local host converts it (field for field the start info
/// the site built before the host existed), and the process it returns is wrapped as the local host
/// wraps one, so the test's own launch and its assertions are unchanged.
/// </summary>
internal static class HostSeams
{
    public static Func<HostSpawnSpec, IHostProcess?> From(Func<ProcessStartInfo, Process?> launch)
        => spec => Wrap(launch(LocalExecutionHost.ToStartInfo(spec)));

    public static IHostProcess? Wrap(Process? process)
        => process is null ? null : new LocalHostProcess(INodeRegistry.LocalAlias, process);
}
