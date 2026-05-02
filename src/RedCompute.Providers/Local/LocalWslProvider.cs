using System.Diagnostics;
using System.Net.Http;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.Providers.Local;

public class LocalWslProvider : IBackendProvider
{
    private readonly ProviderConfig _config;
    private readonly CapabilityType _capability;
    private readonly Action<string> _log;
    private Process? _process;
    private BackendStatus _status = BackendStatus.Stopped;
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string Name => $"Local WSL ({_config.WslDistro ?? "default"})";
    public CapabilityType Capability => _capability;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(5);

    public LocalWslProvider(ProviderConfig config, CapabilityType capability, Action<string> log)
    {
        _config = config;
        _capability = capability;
        _log = log;
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_status == BackendStatus.Running) return true;
        _status = BackendStatus.Starting;

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
        return $"http://localhost:{_config.BackendPort}";
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
            var serverPath = ConvertToWslPath(_config.ServerPath ?? ".");
            var command = $"{venvActivate}cd {serverPath} && python -m uvicorn main:app --host 0.0.0.0 --port {_config.BackendPort}";

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
            Arguments = $"/c cd /d \"{_config.ServerPath}\" && python -m uvicorn main:app --host 0.0.0.0 --port {_config.BackendPort}",
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
            var endpoint = _config.HealthEndpoint ?? "/health";
            var response = await HealthClient.GetAsync($"http://localhost:{_config.BackendPort}{endpoint}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;
        // T:\Projects\Foo → /mnt/t/Projects/Foo
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var drive = char.ToLower(windowsPath[0]);
            var rest = windowsPath[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }
        return windowsPath.Replace('\\', '/');
    }
}
