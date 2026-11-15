using System.Net.Http;
using System.Text;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Plugin.LocalWsl;

public class LocalWslProvider : IPluginProvider
{
    private const string ProcessMarker = "REDCOMPUTE_LOCALWSL_PROCESS=";
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly string _providerType;
    private readonly Action<string> _log;
    private readonly IExecutionHost _executionHost;
    private IHostProcess? _process;

    /// <summary>Launch seam for the backend server process; the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchServerProcess { get; set; }

    /// <summary>Launch seam for the WSL stop script; the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchStopProcess { get; set; }

    /// <summary>Launch seam for the distro address lookup (<c>hostname -I</c>); the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchHostnameProcess { get; set; }
    private int? _wslProcessGroupId;
    private string? _wslStartTime;
    private BackendStatus _status = BackendStatus.Stopped;

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    private string? _backendHost;
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static string ProviderTypeName => "LocalWsl";
    public string Name => "Local WSL";
    public string DisplayName => "Local WSL";
    public string ProviderType => _providerType;
    public string CapabilitySlug => _capabilitySlug;
    public bool IsProxy => true;
    public bool SupportsProgress => false;
    public bool SupportsRerun => true;
    public Dictionary<string, ParameterSchema> InputParameters => new();
    public ReturnSchema OutputSchema => new ReturnSchema { ContentType = "application/octet-stream", Streaming = false };
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(5);

    public LocalWslProvider(ProviderConfig config, string capabilitySlug, Action<string> log,
        IExecutionHost? host = null)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _providerType = config.Type; // "LocalWsl" or "LocalNative"
        _log = log;
        _executionHost = host ?? LocalExecutionHost.Shared;
        LaunchServerProcess = spec => _executionHost.Processes.Start(spec);
        LaunchStopProcess = spec => _executionHost.Processes.Start(spec);
        LaunchHostnameProcess = spec => _executionHost.Processes.Start(spec);
    }

    public string? StatusDetail { get; private set; }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_status == BackendStatus.Running) return true;

        // Both launch paths (wsl.exe, and cmd.exe for LocalNative) are Windows-only. Fail fast
        // with a typed status rather than spawning, probing a WSL host, or waiting on a timeout.
        if (!_executionHost.Platform.CanLaunchLocalBackends)
        {
            _status = BackendStatus.Unsupported;
            StatusDetail = PlatformSupport.LocalBackendUnsupported($"{_capabilitySlug} ({_providerType})");
            _log($"[LocalWsl] {StatusDetail}");
            return false;
        }

        _status = BackendStatus.Starting;

        _backendHost = _config.WslDistro != null ? ResolveWslHost(_config.WslDistro) : "127.0.0.1";
        _log($"[LocalWsl] Backend host: {_backendHost}");

        // Check if already running externally
        if (await CheckHealthAsync())
        {
            _status = BackendStatus.Running;
            _log($"[LocalWsl] Backend already running on port {_config.BackendPort}");
            return true;
        }

        try
        {
            _process = LaunchServerProcess(BuildSpawnSpec());
            if (_process == null)
            {
                _status = BackendStatus.Error;
                return false;
            }

            var ownedWsl = new TaskCompletionSource<(int Pid, string StartTime)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _process.OutputLine += line =>
            {
                if (line == null) return;
                if (TryParseProcessMarker(line, out var pid, out var startTime))
                {
                    _wslProcessGroupId = pid;
                    _wslStartTime = startTime;
                    ownedWsl.TrySetResult((pid, startTime));
                    return;
                }
                _log($"[Backend] {line}");
            };
            _process.ErrorLine += line => { if (line != null) _log($"[Backend:err] {line}"); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (_process.HasExited)
                {
                    _status = BackendStatus.Error;
                    _log($"[LocalWsl] Backend process exited with code {_process.ExitCode}");
                    await StopOwnedProcessAsync(CancellationToken.None);
                    return false;
                }
                if (await CheckHealthAsync())
                {
                    if (_config.WslDistro != null)
                    {
                        try
                        {
                            await ownedWsl.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                        }
                        catch
                        {
                            _status = BackendStatus.Error;
                            _log("[LocalWsl] Backend became healthy but its owned WSL process identity was not captured");
                            await StopOwnedProcessAsync(CancellationToken.None);
                            return false;
                        }
                    }
                    _status = BackendStatus.Running;
                    _log($"[LocalWsl] Backend healthy on port {_config.BackendPort}");
                    return true;
                }
                await Task.Delay(2000, ct);
            }

            _status = BackendStatus.Error;
            _log("[LocalWsl] Backend failed to become healthy within timeout");
            await StopOwnedProcessAsync(CancellationToken.None);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _status = BackendStatus.Error;
            await StopOwnedProcessAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _status = BackendStatus.Error;
            _log($"[LocalWsl] Start failed: {ex.Message}");
            await StopOwnedProcessAsync(CancellationToken.None);
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _status = BackendStatus.Draining;
        await StopOwnedProcessAsync(ct);
        _status = BackendStatus.Stopped;
        _log("[LocalWsl] Backend stopped");
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);

    public string? GetProxyTargetUrl()
    {
        if (_status != BackendStatus.Running) return null;
        return _executionHost.Network.ResolveServiceBaseUrl($"http://{_backendHost ?? "127.0.0.1"}:{_config.BackendPort}");
    }

    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>The backend launch: uncontained, as local backends always ran (design section 6).</summary>
    internal HostSpawnSpec BuildSpawnSpec()
    {
        var extraArgs = BuildBackendArguments();

        if (_config.WslDistro != null)
        {
            var command = BuildWslBackendCommand();
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            var dollar = ((char)36).ToString();
            var lifecycleScript =
                "set -euo pipefail; " +
                $"command={dollar}(printf '%s' '{encoded}' | base64 -d); " +
                $"setsid bash -lc \"{dollar}command\" & child={dollar}!; " +
                $"start={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\"); " +
                $"printf '{ProcessMarker}%s:%s\\n' \"{dollar}child\" \"{dollar}start\"; " +
                "cleanup() { " +
                $"current={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\" 2>/dev/null || true); " +
                $"[ \"{dollar}current\" = \"{dollar}start\" ] && kill -TERM -- \"-{dollar}child\" 2>/dev/null || true; }}; " +
                $"trap cleanup TERM INT EXIT; wait \"{dollar}child\"";
            // wsl.exe reparses the command tail through the distribution shell. Passing a
            // lifecycle script containing '$' directly therefore expands its variables before
            // bash -lc receives it. Base64-wrap the whole script so that the reparse-safe outer
            // command contains no shell variables; the inner bash owns every expansion.
            var lifecycleEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(lifecycleScript));
            var wrapper = $"printf '%s' '{lifecycleEncoded}' | base64 -d | bash";

            return new HostSpawnSpec
            {
                FileName = "wsl.exe",
                ArgumentList = ["-d", _config.WslDistro, "--", "bash", "-lc", wrapper],
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "localwsl",
            };
        }

        return new HostSpawnSpec
        {
            FileName = "cmd.exe",
            RawArguments = $"/c cd /d \"{_config.ServerPath}\" && python3 server.py{extraArgs}",
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "localwsl",
        };
    }

    internal string BuildWslBackendCommand()
    {
        var venvActivate = _config.VenvPath != null
            ? $"if [ -f {_config.VenvPath}/bin/activate ]; then source {_config.VenvPath}/bin/activate; fi; "
            : "";
        var serverPath = ProviderHelpers.ConvertToWslPath(_config.ServerPath ?? ".");
        return $"{venvActivate}cd '{ShellLiteral(serverPath)}' && exec python3 server.py{BuildBackendArguments()}";
    }

    private string BuildBackendArguments()
    {
        var extraArgs = "";
        if (!string.IsNullOrEmpty(_config.Model))
            extraArgs += $" --model {_config.Model}";
        if (!string.IsNullOrEmpty(_config.ModelRevision))
            extraArgs += $" --revision {_config.ModelRevision}";
        if (_config.BackendPort > 0)
            extraArgs += $" --port {_config.BackendPort}";
        return extraArgs;
    }

    private async Task StopOwnedProcessAsync(CancellationToken ct)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            _wslProcessGroupId = null;
            _wslStartTime = null;
            return;
        }

        try
        {
            if (_config.WslDistro != null &&
                _wslProcessGroupId is { } pid &&
                _wslStartTime is { } startTime)
            {
                using var stop = LaunchStopProcess(new HostSpawnSpec
                {
                    FileName = "wsl.exe",
                    ArgumentList = ["-d", _config.WslDistro, "--", "bash", "-lc", BuildWslStopScript(pid, startTime)],
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    StandardOutput = StdioMode.Pipe,
                    StandardError = StdioMode.Pipe,
                    Containment = ContainmentMode.None,
                    OwnerLabel = "localwsl-stop",
                });
                if (stop is not null) await stop.WaitForExitAsync(ct);
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.HasExited)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            process.Dispose();
            _wslProcessGroupId = null;
            _wslStartTime = null;
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

    private static bool TryParseProcessMarker(string line, out int pid, out string startTime)
    {
        pid = 0;
        startTime = "";
        if (!line.StartsWith(ProcessMarker, StringComparison.Ordinal)) return false;
        var parts = line[ProcessMarker.Length..].Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out pid) ||
            pid <= 1 || !parts[1].All(char.IsDigit)) return false;
        startTime = parts[1];
        return true;
    }

    private static string ShellLiteral(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            var host = _backendHost ?? "127.0.0.1";
            var endpoint = _config.HealthEndpoint ?? "/health";
            var baseUrl = _executionHost.Network.ResolveServiceBaseUrl($"http://{host}:{_config.BackendPort}");
            var response = await HealthClient.GetAsync($"{baseUrl}{endpoint}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveWslHost(string distro)
    {
        try
        {
            var proc = LaunchHostnameProcess(new HostSpawnSpec
            {
                FileName = "wsl.exe",
                RawArguments = $"-d {distro} hostname -I",
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "localwsl-hostname",
            });
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            var ip = output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }
}
