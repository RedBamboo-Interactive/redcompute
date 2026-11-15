using System.Text;
using RedCompute.Core.Configuration;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Plugin.Kev;

public interface IKevProcessController
{
    Task<KevOwnedProcess> StartAsync(
        ProviderConfig config,
        Action<string> log,
        CancellationToken ct = default);

    Task StopAsync(
        KevOwnedProcess process,
        Action<string> log,
        CancellationToken ct = default);
}

public sealed class KevOwnedProcess
{
    internal KevOwnedProcess(
        IHostProcess hostProcess,
        string? wslDistro,
        int? wslProcessGroupId,
        string? wslStartTime)
    {
        HostProcess = hostProcess;
        WslDistro = wslDistro;
        WslProcessGroupId = wslProcessGroupId;
        WslStartTime = wslStartTime;
    }

    internal IHostProcess HostProcess { get; }
    public int HostProcessId => HostProcess.Id;
    public string? WslDistro { get; }
    public int? WslProcessGroupId { get; }
    public string? WslStartTime { get; }
}

public sealed class KevProcessController : IKevProcessController
{
    private const string Marker = "REDCOMPUTE_KEV_PROCESS=";

    private readonly IExecutionHost _host;

    public KevProcessController(IExecutionHost? host = null)
    {
        _host = host ?? LocalExecutionHost.Shared;
        LaunchServerProcess = spec => _host.Processes.Start(spec);
        LaunchStopProcess = spec => _host.Processes.Start(spec);
    }

    /// <summary>Launch seam for the sidecar (WSL wrapper or cmd.exe); the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchServerProcess { get; set; }

    /// <summary>Launch seam for the WSL stop script; the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchStopProcess { get; set; }

    public async Task<KevOwnedProcess> StartAsync(
        ProviderConfig config,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.LaunchCommand))
            throw new InvalidOperationException("launchCommand is not configured");

        // Both launch paths (wsl.exe and cmd.exe) are Windows-only.
        if (!_host.Platform.CanLaunchLocalBackends)
            throw new PlatformNotSupportedException(PlatformSupport.LocalBackendUnsupported("Launching Kev"));

        if (string.IsNullOrWhiteSpace(config.WslDistro))
            return StartNative(config.LaunchCommand, log, config);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.LaunchCommand));
        var merge = ExtraBool(config, "MergeLora", false) ? "1" : "0";
        var dtype = ExtraString(config, "Dtype") ?? "bfloat16";
        var dollar = ((char)36).ToString();
        var wrapper =
            "set -euo pipefail; " +
            $"export KEV_MERGE={merge}; " +
            $"export KEV_DTYPE='{ShellLiteral(dtype)}'; " +
            $"command={dollar}(printf '%s' '{encoded}' | base64 -d); " +
            $"setsid bash -lc \"exec {dollar}command\" & child={dollar}!; " +
            $"start={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\"); " +
            $"printf '{Marker}%s:%s\n' \"{dollar}child\" \"{dollar}start\"; " +
            "cleanup() { " +
            $"current={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\" 2>/dev/null || true); " +
            $"[ \"{dollar}current\" = \"{dollar}start\" ] && kill -TERM -- \"-{dollar}child\" 2>/dev/null || true; }}; " +
            $"trap cleanup TERM INT EXIT; wait \"{dollar}child\"";

        // Uncontained, as the sidecar always ran (design section 6).
        var process = LaunchServerProcess(new HostSpawnSpec
            {
                FileName = "wsl.exe",
                ArgumentList = ["-d", config.WslDistro, "--", "bash", "-lc", wrapper],
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "kev",
            })
            ?? throw new InvalidOperationException("wsl.exe did not start");
        var owned = new TaskCompletionSource<(int Pid, string StartTime)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputLine += line =>
        {
            if (line is null) return;
            if (TryParseMarker(line, out var pid, out var startTime))
                owned.TrySetResult((pid, startTime));
            // Other output is drained but not forwarded: a sidecar may print
            // request bodies containing raw decision state.
        };
        // Drain only; never copy possible raw decision state into ordinary logs.
        process.ErrorLine += _ => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var identity = await owned.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new KevOwnedProcess(
                process, config.WslDistro, identity.Pid, identity.StartTime);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    public async Task StopAsync(
        KevOwnedProcess owned,
        Action<string> log,
        CancellationToken ct = default)
    {
        try
        {
            if (owned.WslDistro is not null &&
                owned.WslProcessGroupId is { } pid &&
                owned.WslStartTime is { } startTime)
            {
                using var stop = LaunchStopProcess(new HostSpawnSpec
                {
                    FileName = "wsl.exe",
                    ArgumentList = ["-d", owned.WslDistro, "--", "bash", "-lc", BuildWslStopScript(pid, startTime)],
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    StandardOutput = StdioMode.Pipe,
                    StandardError = StdioMode.Pipe,
                    Containment = ContainmentMode.None,
                    OwnerLabel = "kev-stop",
                });
                if (stop is not null)
                    await stop.WaitForExitAsync(ct);
            }
            else if (!owned.HostProcess.HasExited)
            {
                owned.HostProcess.Kill(entireProcessTree: true);
            }

            if (!owned.HostProcess.HasExited)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                try { await owned.HostProcess.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    owned.HostProcess.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            var hostProcessId = owned.HostProcessId;
            owned.HostProcess.Dispose();
            log($"[Kev] Stopped owned process tree (host pid {hostProcessId})");
        }
    }

    internal static string BuildWslStopScript(int pid, string startTime)
    {
        if (pid <= 1 || !startTime.All(char.IsDigit))
            throw new ArgumentException("Invalid owned WSL process identity");

        var dollar = ((char)36).ToString();
        return
            $"pid={pid}; expected={startTime}; " +
            $"current={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}pid/stat\" 2>/dev/null || true); " +
            $"if [ \"{dollar}current\" = \"{dollar}expected\" ]; then " +
            $"kill -TERM -- \"-{dollar}pid\" 2>/dev/null || true; " +
            "for i in 1 2 3 4 5 6 7 8 9 10; do " +
            $"kill -0 -- \"-{dollar}pid\" 2>/dev/null || exit 0; sleep 0.25; done; " +
            $"current={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}pid/stat\" 2>/dev/null || true); " +
            $"[ \"{dollar}current\" = \"{dollar}expected\" ] && kill -KILL -- \"-{dollar}pid\" 2>/dev/null || true; fi";
    }

    private KevOwnedProcess StartNative(string command, Action<string> log, ProviderConfig config)
    {
        var process = LaunchServerProcess(new HostSpawnSpec
            {
                FileName = "cmd.exe",
                RawArguments = $"/d /s /c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                EnvironmentOverlay = new Dictionary<string, string>
                {
                    ["KEV_MERGE"] = ExtraBool(config, "MergeLora", false) ? "1" : "0",
                    ["KEV_DTYPE"] = ExtraString(config, "Dtype") ?? "bfloat16",
                },
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "kev",
            })
            ?? throw new InvalidOperationException("configured Kev process did not start");
        // Drain child output without relaying possible raw decision state.
        process.OutputLine += _ => { };
        process.ErrorLine += _ => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new KevOwnedProcess(process, null, null, null);
    }

    private static bool TryParseMarker(string line, out int pid, out string startTime)
    {
        pid = 0;
        startTime = "";
        if (!line.StartsWith(Marker, StringComparison.Ordinal))
            return false;
        var parts = line[Marker.Length..].Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out pid) ||
            pid <= 1 || !parts[1].All(char.IsDigit))
            return false;
        startTime = parts[1];
        return true;
    }

    private static string? ExtraString(ProviderConfig config, string key)
    {
        if (config.Extra is null || !config.Extra.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is System.Text.Json.JsonElement json &&
            json.ValueKind == System.Text.Json.JsonValueKind.String)
            return json.GetString();
        return value.ToString();
    }

    private static bool ExtraBool(ProviderConfig config, string key, bool fallback)
    {
        var value = ExtraString(config, key);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string ShellLiteral(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
}
