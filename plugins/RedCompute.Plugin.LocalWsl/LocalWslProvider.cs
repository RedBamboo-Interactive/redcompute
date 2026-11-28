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

public class LocalWslProvider : IPluginProvider, IServiceProxyClient
{
    private const string ProcessMarker = RuntimeRecipes.LocalWslProcessMarker;
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
    private readonly HttpClient _healthClient;

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
        // The health probe: a stopped backend on a loopback port is refused in a quarter second, not two.
        _healthClient = _executionHost.Network.CreateProbeClient($"http://127.0.0.1:{_config.BackendPort}", TimeSpan.FromSeconds(5));
        ProxyClient = CreateServiceClient(IServiceProxyClient.ProxyTimeout);
        // Node zero's runtimes list (design 2.4): this backend, started and stopped as always.
        if (_executionHost.Runtimes is LocalHostRuntimes runtimes)
            _runtimeRegistration = runtimes.Register(new LocalRuntimeRegistration(
                $"{capabilitySlug}@{config.BackendPort}", _providerType, $"{capabilitySlug} ({_providerType})", capabilitySlug,
                config.BackendPort, Accelerator: null, () => _status, () => StatusDetail, StartAsync, StopAsync));
    }

    private readonly IDisposable? _runtimeRegistration;

    /// <summary>The generic proxy's client, over the host's handler (<see cref="IServiceProxyClient"/>).</summary>
    public HttpClient ProxyClient { get; }

    /// <summary>
    /// A client for this backend over the host's handler (design 2.2). The service is named by its port
    /// as the host sees it; the host, not the server, knows which address (loopback or the WSL
    /// distro's) it answers on. The caller owns the client.
    /// </summary>
    public HttpClient CreateServiceClient(TimeSpan timeout)
        => _executionHost.Network.CreateServiceClient($"http://127.0.0.1:{_config.BackendPort}", timeout);

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
        _runtimeRegistration?.Dispose();
        await StopAsync();
        _healthClient.Dispose();
        ProxyClient.Dispose();
    }

    /// <summary>The backend launch: the shared recipe (design 2.4) over this provider's config.</summary>
    internal HostSpawnSpec BuildSpawnSpec() => RuntimeRecipes.LocalWsl(Recipe());

    internal string BuildWslBackendCommand() => RuntimeRecipes.LocalWslBackendCommand(Recipe());

    private LocalWslRecipe Recipe() => new()
    {
        WslDistro = _config.WslDistro,
        ServerPath = _config.ServerPath,
        VenvPath = _config.VenvPath,
        Model = _config.Model,
        ModelRevision = _config.ModelRevision,
        BackendPort = _config.BackendPort,
    };

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

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            var host = _backendHost ?? "127.0.0.1";
            var endpoint = _config.HealthEndpoint ?? "/health";
            var baseUrl = _executionHost.Network.ResolveServiceBaseUrl($"http://{host}:{_config.BackendPort}");
            var response = await _healthClient.GetAsync($"{baseUrl}{endpoint}");
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
