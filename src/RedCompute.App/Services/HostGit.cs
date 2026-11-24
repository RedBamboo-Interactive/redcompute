using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services;

/// <summary>
/// git on a node, as repository validation and placement resolution run it: no shell, uncontained,
/// no optional locks or prompts, 15 seconds per attempt and one more attempt after a timeout. One launch shape for every caller, pinned by the
/// A-git-* launch goldens.
/// </summary>
internal static class HostGit
{
    /// <summary>
    /// How long one attempt may take, from its start. 15 s, up from the 10 s this had: a process start on a
    /// machine whose CPU is saturated (a Nightly, or a PC under a render) has been seen to take several
    /// seconds, and a git that really hangs still dies at 15 s. Settable so the tests can allow for a loaded
    /// host; production keeps the default.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    internal static TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// A timed-out git is killed and tried once more, so one stalled start (the usual cause: the machine,
    /// not the repository) does not fail what the caller was resolving. Two timeouts in a row are a real
    /// stall: at most 2 x <see cref="Timeout"/> = 30 s per call.
    /// </summary>
    internal const int Attempts = 2;

    /// <summary>
    /// stdout lines, or null on a non-zero exit. Throws <see cref="TimeoutException"/> when git takes
    /// too long, and <see cref="InvalidOperationException"/> when it does not start.
    /// </summary>
    public static async Task<List<string>?> RunAsync(IExecutionHost host, string workingDirectory, string[] args,
        CancellationToken ct, TimeSpan? limit = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(host, workingDirectory, args, ct, limit);
            }
            catch (TimeoutException) when (attempt < Attempts)
            {
            }
        }
    }

    private static async Task<List<string>?> RunOnceAsync(IExecutionHost host, string workingDirectory, string[] args,
        CancellationToken ct, TimeSpan? limit)
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
        timeout.CancelAfter(limit ?? Timeout);
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
