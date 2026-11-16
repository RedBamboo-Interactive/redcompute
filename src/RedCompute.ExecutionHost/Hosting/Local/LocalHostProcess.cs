using System.Diagnostics;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// A <see cref="Process"/> as an <see cref="IHostProcess"/>: every member forwards 1:1 and the
/// streams are the process's own instances. The data events forward <c>e.Data</c> unchanged, on the
/// thread <see cref="Process"/> raises them on.
/// </summary>
internal sealed class LocalHostProcess : IHostProcess
{
    public LocalHostProcess(string nodeId, Process process)
    {
        NodeId = nodeId;
        Process = process;
        process.OutputDataReceived += (_, e) => OutputLine?.Invoke(e.Data);
        process.ErrorDataReceived += (_, e) => ErrorLine?.Invoke(e.Data);
        process.Exited += (_, e) => Exited?.Invoke(this, e);
    }

    /// <summary>The process itself, for code inside the local host.</summary>
    internal Process Process { get; }

    public string NodeId { get; }
    public int Id => Process.Id;
    public DateTime StartTimeUtc => Process.StartTime.ToUniversalTime();
    public long CreatedUtcTicks => SessionProcessContainment.CreationUtcTicks(Process);
    public StreamWriter StandardInput => Process.StandardInput;
    public StreamReader StandardOutput => Process.StandardOutput;
    public StreamReader StandardError => Process.StandardError;
    public void BeginOutputReadLine() => Process.BeginOutputReadLine();
    public void BeginErrorReadLine() => Process.BeginErrorReadLine();
    public event Action<string?>? OutputLine;
    public event Action<string?>? ErrorLine;

    public bool EnableRaisingEvents
    {
        get => Process.EnableRaisingEvents;
        set => Process.EnableRaisingEvents = value;
    }

    public event EventHandler? Exited;
    public bool HasExited => Process.HasExited;
    public int ExitCode => Process.ExitCode;
    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);
    public bool WaitForExit(int milliseconds) => Process.WaitForExit(milliseconds);
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Process.WaitForExitAsync(cancellationToken);
    public void Dispose() => Process.Dispose();
}
