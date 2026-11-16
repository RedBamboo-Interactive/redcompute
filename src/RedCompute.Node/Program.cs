using System.Runtime.InteropServices;
using RedCompute.Core.Storage;
using RedCompute.Node.Credentials;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Node;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.FirstOrDefault() != "run") AttachToParentConsole();

        // The host's local platform answers folders without touching ComputeStorage, so the paths can be
        // resolved first and the data root set before anything reads it (design 2.1).
        var bootstrap = new LocalExecutionHost("leaf-node");
        NodePaths paths;
        try
        {
            paths = NodePaths.Resolve(bootstrap.Platform.GetEnvironmentVariable, bootstrap.Platform.GetFolderPath,
                OperatingSystem.IsWindows());
        }
        catch (NodeCommandException error)
        {
            Console.Error.WriteLine(error.Message);
            return error.ExitCode;
        }
        ConfigureDataRoot(paths);

        var state = new NodeStateStore(bootstrap, paths).Load();
        var host = new LocalExecutionHost(state.Pairing?.NodeId ?? "leaf-node");
        // The file log; verbs print what the person needs to the console themselves.
        using var log = new NodeLog(paths.LogFile);
        host.Containment.Log = message => log.Info(message);
        var env = new NodeEnvironment
        {
            Paths = paths,
            Host = host,
            KeyStores = new SystemKeyStores(paths),
            Log = log,
        };

        // SIGTERM (systemd stop, logoff) and Ctrl+C stop the agent gracefully: link.bye, then exit.
        using var stop = new CancellationTokenSource();
        var signals = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    stop.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
        try
        {
            return await new NodeCommands(env, Console.Out).RunAsync(args, stop.Token);
        }
        finally
        {
            foreach (var registration in signals) registration.Dispose();
        }
    }

    /// <summary>
    /// <c>REDCOMPUTE_DATA_ROOT</c> = the agent's own <c>compute</c> folder, for this process, before any
    /// code reads <see cref="ComputeStorage"/>: the execution host's containment ledgers then live with
    /// the node, never in a RedCompute data folder on the same machine.
    /// </summary>
    internal static void ConfigureDataRoot(NodePaths paths)
        => Environment.SetEnvironmentVariable(ComputeStorage.DataRootVariable, paths.Compute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    /// <summary>
    /// leaf-node.exe is a Windows-subsystem program (no console window for the background agent). A verb
    /// typed in a terminal attaches to that terminal, unless its output is already redirected.
    /// </summary>
    private static void AttachToParentConsole()
    {
        const int stdOutput = -11;
        if (GetStdHandle(stdOutput) != IntPtr.Zero) return; // redirected to a file or pipe
        AttachConsole(-1);
    }
}
