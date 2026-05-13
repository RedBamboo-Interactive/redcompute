using System.Diagnostics;
using System.Net.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.LocalWsl;

public class LocalWslProvider : IPluginProvider
{
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly string _providerType;
    private readonly Action<string> _log;
    private Process? _process;
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

    public LocalWslProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _providerType = config.Type; // "LocalWsl" or "LocalNative"
        _log = log;
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_status == BackendStatus.Running) return true;
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
            var startInfo = BuildStartInfo();
            _process = Process.Start(startInfo);
            if (_process == null)
            {
                _status = BackendStatus.Error;
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) _log($"[Backend] {e.Data}"); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log($"[Backend:err] {e.Data}"); };

            var timeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (_process.HasExited)
                {
                    _status = BackendStatus.Error;
                    _log($"[LocalWsl] Backend process exited with code {_process.ExitCode}");
                    return false;
                }
                if (await CheckHealthAsync())
                {
                    _status = BackendStatus.Running;
                    _log($"[LocalWsl] Backend healthy on port {_config.BackendPort}");
                    return true;
                }
                await Task.Delay(2000, ct);
            }

            _status = BackendStatus.Error;
            _log("[LocalWsl] Backend failed to become healthy within timeout");
            return false;
        }
        catch (Exception ex)
        {
            _status = BackendStatus.Error;
            _log($"[LocalWsl] Start failed: {ex.Message}");
            return false;
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _status = BackendStatus.Draining;

        if (_process != null && !_process.HasExited)
        {
            try
            {
                if (_config.WslDistro != null)
                {
                    // Kill via WSL
                    var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = $"-d {_config.WslDistro} pkill -f \"uvicorn|python\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    kill?.WaitForExit(5000);
                }
                else
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _process.Dispose();
            _process = null;
        }

        _status = BackendStatus.Stopped;
        _log("[LocalWsl] Backend stopped");
        return Task.CompletedTask;
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);

    public string? GetProxyTargetUrl()
    {
        if (_status != BackendStatus.Running) return null;
        return $"http://{_backendHost ?? "127.0.0.1"}:{_config.BackendPort}";
    }

    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private ProcessStartInfo BuildStartInfo()
    {
        if (_config.WslDistro != null)
        {
            var venvActivate = _config.VenvPath != null ? $"source {_config.VenvPath}/bin/activate && " : "";
            var serverPath = ProviderHelpers.ConvertToWslPath(_config.ServerPath ?? ".");
            var command = $"{venvActivate}cd {serverPath} && python3 server.py";

            return new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = $"-d {_config.WslDistro} bash -c \"{command}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c cd /d \"{_config.ServerPath}\" && python3 server.py",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            var host = _backendHost ?? "127.0.0.1";
            var endpoint = _config.HealthEndpoint ?? "/health";
            var response = await HealthClient.GetAsync($"http://{host}:{_config.BackendPort}{endpoint}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveWslHost(string distro)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = $"-d {distro} hostname -I",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
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
