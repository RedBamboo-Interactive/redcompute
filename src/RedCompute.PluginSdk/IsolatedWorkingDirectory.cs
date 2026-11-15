using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.PluginSdk;

/// <summary>
/// A unique, empty process working directory whose lifetime is bounded to one operation, on the
/// host whose process uses it (<see cref="IHostFileSystem.CreateTempDirectory"/>).
/// </summary>
public sealed class IsolatedWorkingDirectory : IDisposable
{
    private readonly IExecutionHost _host;

    public string Path { get; }

    private IsolatedWorkingDirectory(IExecutionHost host, string prefix)
    {
        _host = host;
        Path = host.Files.CreateTempDirectory(prefix).Value;
    }

    /// <summary>A directory on this machine (<see cref="LocalExecutionHost.Shared"/>).</summary>
    public static IsolatedWorkingDirectory Create(string prefix) => new(LocalExecutionHost.Shared, prefix);

    public static IsolatedWorkingDirectory Create(IExecutionHost host, string prefix) => new(host, prefix);

    public void Dispose()
    {
        var path = _host.Path(Path);
        if (_host.Files.DirectoryExists(path))
            _host.Files.DeleteDirectory(path, recursive: true);
    }
}
