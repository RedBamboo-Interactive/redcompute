using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The interpreter a native ComfyUI with a venv runs on, per host OS: <c>Scripts\python.exe</c> on Windows (node zero's
/// launch, pinned by <see cref="RuntimeRecipePinTests"/>), <c>bin/python</c> on Linux and macOS, as <c>python -m venv</c>
/// lays them out. The node agent builds its spawned runtimes from the same recipe on its own machine.
/// </summary>
public sealed class RuntimeRecipeVenvTests
{
    private static ComfyUiRecipe Recipe(string venv, string server) => new()
    {
        ServerPath = server,
        VenvPath = venv,
        ServerArgs = "--cpu --disable-all-custom-nodes",
    };

    [Fact]
    public void A_windows_host_runs_the_venvs_Scripts_python_exe()
    {
        var host = new OsHost(windows: true, linux: false);
        Assert.Equal(@"C:\Comfy\venv\Scripts\python.exe", RuntimeRecipes.ComfyUiNativePython(@"C:\Comfy\venv", @"C:\Comfy\ComfyUI", host));
        var spec = RuntimeRecipes.ComfyUi(Recipe(@"C:\Comfy\venv", @"C:\Comfy\ComfyUI"), host);
        Assert.Equal(@"C:\Comfy\venv\Scripts\python.exe", spec.FileName);
    }

    [Fact]
    public void A_linux_host_runs_the_venvs_bin_python()
    {
        var host = new OsHost(windows: false, linux: true);
        Assert.Equal("/home/leaf/comfyui/venv/bin/python",
            RuntimeRecipes.ComfyUiNativePython("/home/leaf/comfyui/venv", "/home/leaf/comfyui/ComfyUI", host));
        var spec = RuntimeRecipes.ComfyUi(Recipe("/home/leaf/comfyui/venv", "/home/leaf/comfyui/ComfyUI"), host);
        Assert.Equal("/home/leaf/comfyui/venv/bin/python", spec.FileName);
        // Only the interpreter differs by OS: the arguments are the recipe's, as on Windows.
        Assert.Equal("main.py --listen 127.0.0.1 --port 8188 --cpu --disable-all-custom-nodes", spec.RawArguments);
        Assert.Equal("/home/leaf/comfyui/ComfyUI", spec.WorkingDirectory?.Value);
    }

    [Fact]
    public void A_macos_host_runs_the_venvs_bin_python()
    {
        var host = new OsHost(windows: false, linux: false);
        Assert.Equal("/Users/me/comfy/venv/bin/python", RuntimeRecipes.ComfyUiNativePython("/Users/me/comfy/venv", "/Users/me/comfy/ComfyUI", host));
    }

    [Fact]
    public void This_machines_own_host_picks_its_own_layout()
    {
        var venv = Path.Combine(Path.GetTempPath(), "comfy-venv");
        var expected = OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");
        Assert.Equal(expected, RuntimeRecipes.ComfyUiNativePython(venv, null, LocalExecutionHost.Shared));
    }

    [Fact]
    public void Without_a_venv_nothing_changes_on_either_os()
    {
        foreach (var host in new[] { new OsHost(windows: true, linux: false), new OsHost(windows: false, linux: true) })
            Assert.Equal("python", RuntimeRecipes.ComfyUiNativePython(null, null, host));
    }

    /// <summary>This machine's host with another OS's platform answer and path rules (nothing here touches the disk but FileExists).</summary>
    private sealed class OsHost(bool windows, bool linux) : IExecutionHost
    {
        private readonly LocalExecutionHost _local = LocalExecutionHost.Shared;
        public string NodeId => _local.NodeId;
        public HostPath Path(string value) => _local.Path(value);
        public IHostPathRules PathRules { get; } = new Rules(windows);
        public IHostProcesses Processes => _local.Processes;
        public IHostContainment Containment => _local.Containment;
        public IHostFileSystem Files => _local.Files;
        public IHostNetwork Network => _local.Network;
        public IHostPlatform Platform { get; } = new OsPlatform(LocalExecutionHost.Shared.Platform, windows, linux);
        public IHostHardware Hardware => _local.Hardware;
        public IHostQueries Queries => _local.Queries;
        public IHostRuntimes Runtimes => _local.Runtimes;
    }

    private sealed class OsPlatform(IHostPlatform inner, bool windows, bool linux) : IHostPlatform
    {
        public HostPlatformInfo Info => inner.Info;
        public bool IsWindows => windows;
        public bool IsLinux => linux;
        public bool CanLaunchLocalBackends => inner.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder) => inner.GetFolderPath(folder);
        public string ComputeDataRoot => inner.ComputeDataRoot;
    }

    private sealed class Rules(bool windows) : IHostPathRules
    {
        public char DirectorySeparator => windows ? '\\' : '/';
        public StringComparison Comparison => windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public bool IsPathFullyQualified(string path) => windows ? path.Length > 2 && path[1] == ':' : path.StartsWith('/');
        public string GetFullPath(string path) => path;
        public string Combine(params string[] parts) => string.Join(DirectorySeparator, parts.Select(p => p.TrimEnd('\\', '/')));
    }
}
