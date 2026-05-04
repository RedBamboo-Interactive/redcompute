using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public class CloudflareTunnelService : IAsyncDisposable
{
    public enum TunnelStatus { Stopped, Starting, Running, Error }

    private Process? _process;
    private readonly Lock _lock = new();

    public TunnelStatus Status { get; private set; } = TunnelStatus.Stopped;
    public string? ErrorMessage { get; private set; }

    public event Action<TunnelStatus, string?>? StatusChanged;

    public Task<bool> StartAsync(int localPort, TunnelConfig config)
    {
        lock (_lock)
        {
            if (_process != null)
                return Task.FromResult(false);

            var exe = ResolveCloudflaredPath(config.CloudflaredPath);
            if (exe == null)
            {
                SetStatus(TunnelStatus.Error,
                    "cloudflared not found. Install via: winget install Cloudflare.cloudflared");
                return Task.FromResult(false);
            }

            if (string.IsNullOrWhiteSpace(config.TunnelToken))
            {
                SetStatus(TunnelStatus.Error, "No tunnel token configured");
                return Task.FromResult(false);
            }

            SetStatus(TunnelStatus.Starting, null);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"tunnel run --token {config.TunnelToken}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnData;
            _process.ErrorDataReceived += OnData;
            _process.Exited += OnExited;

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _process = null;
                SetStatus(TunnelStatus.Error, $"Failed to start cloudflared: {ex.Message}");
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            KillProcess();
            SetStatus(TunnelStatus.Stopped, null);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            KillProcess();
        }
        return ValueTask.CompletedTask;
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;

        if (Status == TunnelStatus.Starting &&
            (e.Data.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) ||
             e.Data.Contains("Connection registered", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch(e.Data, @"connection [a-f0-9-]+ registered", RegexOptions.IgnoreCase)))
        {
            SetStatus(TunnelStatus.Running, null);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            var exitCode = _process?.ExitCode;
            _process = null;

            if (Status == TunnelStatus.Stopped) return;

            SetStatus(TunnelStatus.Error,
                $"cloudflared exited unexpectedly (code {exitCode})");
        }
    }

    private void KillProcess()
    {
        if (_process == null) return;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process = null;
    }

    private void SetStatus(TunnelStatus status, string? error)
    {
        Status = status;
        ErrorMessage = error;
        StatusChanged?.Invoke(status, error);
    }

    private static string? ResolveCloudflaredPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "cloudflared.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
