using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services;

/// <summary>Containers RedCompute runs for sessions, through the docker CLI on its execution host.</summary>
public class DockerContainerService
{
    private readonly ConcurrentDictionary<string, string> _managedContainers = new();
    private readonly Action<string, Guid?> _log;
    private readonly IExecutionHost _host;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="host">Where docker runs; the shared local host when none is given.</param>
    public DockerContainerService(Action<string, Guid?> log, IExecutionHost? host = null)
    {
        _log = log;
        _host = host ?? LocalExecutionHost.Shared;
    }

    public async Task<string> EnsureContainerAsync(string imageName)
    {
        var containerName = DeriveContainerName(imageName);

        if (_managedContainers.TryGetValue(containerName, out _))
        {
            if (await IsRunningAsync(containerName))
                return containerName;
            _managedContainers.TryRemove(containerName, out _);
        }

        await _lock.WaitAsync();
        try
        {
            if (_managedContainers.TryGetValue(containerName, out _) && await IsRunningAsync(containerName))
                return containerName;

            if (await IsRunningAsync(containerName))
            {
                _managedContainers[containerName] = imageName;
                _log($"[Docker] Adopted existing container '{containerName}'", null);
                return containerName;
            }

            await RemoveStaleContainerAsync(containerName);

            _log($"[Docker] Starting container '{containerName}' from image '{imageName}'", null);
            var (ok, output) = await RunDockerAsync("run", "-d", "--name", containerName, imageName);
            if (!ok)
                throw new InvalidOperationException($"Failed to start container '{containerName}' from '{imageName}': {output}");

            _managedContainers[containerName] = imageName;
            _log($"[Docker] Container '{containerName}' started", null);
            return containerName;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsRunningAsync(string containerName)
    {
        var (ok, output) = await RunDockerAsync("inspect", "-f", "{{.State.Running}}", containerName);
        return ok && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StopAsync(string containerName)
    {
        _managedContainers.TryRemove(containerName, out _);
        _log($"[Docker] Stopping container '{containerName}'", null);
        await RunDockerAsync("stop", containerName);
        await RunDockerAsync("rm", containerName);
    }

    public async Task StopAllAsync()
    {
        var containers = _managedContainers.Keys.ToList();
        _managedContainers.Clear();

        foreach (var name in containers)
        {
            try
            {
                _log($"[Docker] Stopping managed container '{name}'", null);
                await RunDockerAsync("stop", name);
                await RunDockerAsync("rm", name);
            }
            catch (Exception ex)
            {
                _log($"[Docker] Failed to stop container '{name}': {ex.Message}", null);
            }
        }
    }

    private async Task RemoveStaleContainerAsync(string containerName)
    {
        var (exists, _) = await RunDockerAsync("inspect", containerName);
        if (exists)
        {
            _log($"[Docker] Removing stale container '{containerName}'", null);
            await RunDockerAsync("rm", "-f", containerName);
        }
    }

    internal static string DeriveContainerName(string imageName)
    {
        var name = imageName;
        var slashIdx = name.LastIndexOf('/');
        if (slashIdx >= 0)
            name = name[(slashIdx + 1)..];

        name = name.Replace(':', '-');
        name = Regex.Replace(name, @"[^a-zA-Z0-9_.-]", "-");
        name = name.Trim('-');

        if (string.IsNullOrEmpty(name))
            name = "container";

        return $"rc-{name}";
    }

    /// <summary>The docker CLI, uncontained, stdout and stderr read as UTF-8.</summary>
    private async Task<(bool Success, string Output)> RunDockerAsync(params string[] args)
    {
        var spec = new HostSpawnSpec
        {
            FileName = "docker",
            ArgumentList = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Containment = ContainmentMode.None,
            OwnerLabel = "docker",
        };

        using var process = _host.Processes.Start(spec);
        if (process == null)
            return (false, "Failed to start docker process");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode == 0, process.ExitCode == 0 ? stdout : stderr);
    }
}
