using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Reports;

/// <summary>
/// One CLI as the node reports it (design 2.5): <c>{name, installed, version, signedIn, authMethod, path}</c>.
/// Never the CLI's output, never a credential.
/// </summary>
public sealed record CliReport(string Name, bool Installed, string? Version, bool SignedIn, string? AuthMethod, string? Path)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["installed"] = Installed,
        ["version"] = Version,
        ["signedIn"] = SignedIn,
        ["authMethod"] = AuthMethod,
        ["path"] = Path,
    };
}

/// <summary>A finished probe command: exit code and its output (kept on the node, never sent).</summary>
public sealed record ProbeOutput(bool Started, int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Success => Started && !TimedOut && ExitCode == 0;
}

/// <summary>Runs one fixed probe command.</summary>
public interface IProbeRunner
{
    Task<ProbeOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The fixed probes (design 2.5), the same the provider plugins run (leaf-capabilities <c>InspectAsync</c>):
/// <c>claude --version</c> + <c>claude auth status --json</c>; <c>codex --version</c> + <c>codex login status</c>;
/// <c>opencode --version</c> + <c>opencode auth list</c> (the credential count only). The agent owns this list
/// and every argv in it; the server can only ask for a re-probe. The CLIs are found with the server's own
/// search orders (<see cref="CliLocators"/>) over a PATH that also has the user bins a login shell would add.
/// </summary>
public sealed partial class CliProber(IExecutionHost host, IProbeRunner runner)
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The argv of every probe, by CLI: the whole list the agent can ever run.</summary>
    public static IReadOnlyDictionary<string, string[][]> Commands { get; } = new Dictionary<string, string[][]>
    {
        ["claude"] = [["--version"], ["auth", "status", "--json"]],
        ["codex"] = [["--version"], ["login", "status"]],
        ["opencode"] = [["--version"], ["auth", "list"]],
    };

    public async Task<IReadOnlyList<CliReport>> ProbeAsync(CancellationToken ct)
    {
        var probeHost = new SearchPathHost(host, SearchPath(host));
        var environment = new Dictionary<string, string> { ["PATH"] = probeHost.Platform.GetEnvironmentVariable("PATH") ?? "" };
        return
        [
            await ProbeAsync("claude", CliLocators.Claude(probeHost, null), environment, ReadClaudeAuth, ct),
            await ProbeAsync("codex", CliLocators.Codex(probeHost, null), environment, ReadCodexAuth, ct),
            await ProbeAsync("opencode", CliLocators.OpenCode(probeHost, null), environment, ReadOpenCodeAuth, ct),
        ];
    }

    private async Task<CliReport> ProbeAsync(string name, string? path, IReadOnlyDictionary<string, string> environment,
        Func<ProbeOutput, (bool SignedIn, string? Method)> readAuth, CancellationToken ct)
    {
        if (path is null) return new CliReport(name, false, null, false, null, null);
        var commands = Commands[name];
        var version = await runner.RunAsync(path, commands[0], environment, CommandTimeout, ct);
        if (!version.Success) return new CliReport(name, false, null, false, null, path);
        var auth = await runner.RunAsync(path, commands[1], environment, CommandTimeout, ct);
        var (signedIn, method) = auth.Started && !auth.TimedOut ? readAuth(auth) : (false, null);
        return new CliReport(name, true, FirstLine(version.StandardOutput), signedIn, signedIn ? method : null, path);
    }

    /// <summary>
    /// <c>claude auth status --json</c>: signed out, the CLI exits non-zero with the same JSON, so both
    /// streams are read; whatever does not read as <c>loggedIn: true</c> is not signed in.
    /// </summary>
    internal static (bool, string?) ReadClaudeAuth(ProbeOutput output)
    {
        foreach (var candidate in new[] { output.StandardOutput, output.StandardError })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            JsonObject? status;
            try { status = JsonNode.Parse(candidate) as JsonObject; }
            catch { continue; }
            if (status?["loggedIn"] is not JsonValue loggedIn || !loggedIn.TryGetValue<bool>(out var authenticated)) continue;
            if (!authenticated) break;
            return (true, status["authMethod"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null);
        }
        return (false, null);
    }

    /// <summary><c>codex login status</c>: exit 0 is signed in; the text names ChatGPT or an API key.</summary>
    internal static (bool, string?) ReadCodexAuth(ProbeOutput output)
    {
        if (!output.Success) return (false, null);
        var text = output.StandardOutput + "\n" + output.StandardError;
        if (text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)) return (true, "chatgpt");
        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase)) return (true, "api-key");
        return (true, null);
    }

    /// <summary><c>opencode auth list</c>: signed in when it counts at least one credential.</summary>
    internal static (bool, string?) ReadOpenCodeAuth(ProbeOutput output)
    {
        if (!output.Success) return (false, null);
        var match = CredentialCountPattern().Match(AnsiEscape().Replace(output.StandardOutput, string.Empty));
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) && count > 0 ? (true, "opencode") : (false, null);
    }

    internal static string FirstLine(string value)
        => AnsiEscape().Replace(value, string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? value.Trim();

    /// <summary>
    /// PATH as this process has it, then the user bins an autostarted agent may lack (design 2.5, "PATH
    /// under a service"). Appended, so the user's own order wins.
    /// </summary>
    internal static string SearchPath(IExecutionHost host)
    {
        var path = host.Platform.GetEnvironmentVariable("PATH") ?? "";
        var entries = path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = host.Platform.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = host.Platform.IsWindows
            ? new[] { host.PathRules.Combine(host.Platform.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") }
            : new[]
            {
                host.PathRules.Combine(home, ".local", "bin"),
                host.PathRules.Combine(home, ".npm-global", "bin"),
                host.PathRules.Combine(home, ".bun", "bin"),
                host.PathRules.Combine(home, ".opencode", "bin"),
                "/usr/local/bin",
                "/usr/bin",
            };
        foreach (var folder in extra)
            if (!entries.Contains(folder, StringComparer.Ordinal)) entries.Add(folder);
        return string.Join(System.IO.Path.PathSeparator, entries);
    }

    [GeneratedRegex("\\u001B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex("(\\d+)\\s+credentials?", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialCountPattern();
}

/// <summary>
/// Runs a probe on the node's execution host as a session-contained tree (killed with the agent), stdin
/// closed, output capped at 64 KiB per stream, killed at the timeout.
/// </summary>
public sealed class HostProbeRunner(IExecutionHost host) : IProbeRunner
{
    private const int OutputCap = 64 * 1024;

    public async Task<ProbeOutput> RunAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
    {
        IHostProcess? process;
        try
        {
            process = host.Processes.Start(new HostSpawnSpec
            {
                FileName = fileName,
                ArgumentList = arguments,
                EnvironmentOverlay = environment,
                StandardInput = StdioMode.Pipe,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true,
                Containment = ContainmentMode.Session,
                OwnerLabel = "leaf-node-probe",
            });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ProbeOutput(false, -1, "", error.Message);
        }
        if (process is null) return new ProbeOutput(false, -1, "", "the process did not start");

        using (process)
        {
            process.StandardInput.Close();
            var stdout = ReadCappedAsync(process.StandardOutput);
            var stderr = ReadCappedAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                ct.ThrowIfCancellationRequested();
            }
            var output = await stdout;
            var error = await stderr;
            return new ProbeOutput(true, timedOut ? -1 : process.ExitCode, output, error, timedOut);
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
                if (text.Length < OutputCap) text.Append(buffer, 0, Math.Min(read, OutputCap - text.Length));
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        return text.ToString();
    }
}

/// <summary>The node's execution host with a different PATH, for locating CLIs (everything else is the host's).</summary>
internal sealed class SearchPathHost(IExecutionHost inner, string path) : IExecutionHost
{
    public string NodeId => inner.NodeId;
    public HostPath Path(string value) => inner.Path(value);
    public IHostPathRules PathRules => inner.PathRules;
    public IHostProcesses Processes => inner.Processes;
    public IHostContainment Containment => inner.Containment;
    public IHostFileSystem Files => inner.Files;
    public IHostNetwork Network => inner.Network;
    public IHostPlatform Platform { get; } = new SearchPathPlatform(inner.Platform, path);
    public IHostHardware Hardware => inner.Hardware;
    public IHostQueries Queries => inner.Queries;

    private sealed class SearchPathPlatform(IHostPlatform platform, string path) : IHostPlatform
    {
        public HostPlatformInfo Info => platform.Info;
        public bool IsWindows => platform.IsWindows;
        public bool IsLinux => platform.IsLinux;
        public bool CanLaunchLocalBackends => platform.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? path : platform.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder) => platform.GetFolderPath(folder);
        public string ComputeDataRoot => platform.ComputeDataRoot;
    }
}
