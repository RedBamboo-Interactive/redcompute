using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services;

/// <summary>
/// git on a node, as repository validation and placement resolution run it: no shell, uncontained,
/// no optional locks or prompts, a 10 second limit. One launch shape for every caller, pinned by the
/// A-git-* launch goldens.
/// </summary>
internal static class HostGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// stdout lines, or null on a non-zero exit. Throws <see cref="TimeoutException"/> when git takes
    /// too long, and <see cref="InvalidOperationException"/> when it does not start.
    /// </summary>
    public static async Task<List<string>?> RunAsync(IExecutionHost host, string workingDirectory, string[] args,
        CancellationToken ct)
    {
        var spec = new HostSpawnSpec
        {
            FileName = "git",
            ArgumentList = ["-C", workingDirectory, .. args],
            WorkingDirectory = host.Path(workingDirectory),
            EnvironmentOverlay = new Dictionary<string, string>
            {
                ["GIT_OPTIONAL_LOCKS"] = "0",
                ["GIT_TERMINAL_PROMPT"] = "0",
            },
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
            Containment = ContainmentMode.None,
            OwnerLabel = "git",
        };

        using var process = host.Processes.Start(spec) ?? throw new InvalidOperationException("git did not start");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            if (process.ExitCode != 0) return null;
            // Output ends with a newline; drop only that final empty element (an empty --show-prefix line before it is a value).
            var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("git timed out");
        }
    }
}
