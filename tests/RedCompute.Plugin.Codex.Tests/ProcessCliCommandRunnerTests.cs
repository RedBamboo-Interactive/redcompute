using System.Diagnostics;
using RedCompute.Core.Hardware;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class ProcessCliCommandRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "npm-runner-" + Guid.NewGuid().ToString("N"));

    [WindowsTheory("npm batch shims must execute through cmd.exe, including paths with spaces")]
    [InlineData("Program Files/nodejs", "npm.cmd", new[] { "view", "@openai/codex", "versions", "--json" })]
    [InlineData("nodejs", "npm.cmd", new[] { "install", "--global", "@openai/codex@0.163.0" })]
    [InlineData("Program Files/nodejs", "NPM.CMD", new[] { "--version", "", "value with spaces", "a&b", @"trailing\" })]
    [InlineData("Program Files/nodejs", "npm.bat", new[] { "--version" })]
    public async Task BatchShim_ExecutesAndPreservesArguments(string folder, string file, string[] arguments)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var shim = Path.Combine(directory, file);
        // This stand-in only echoes its arguments. It never calls npm or changes an installation.
        File.WriteAllLines(shim,
            new[] { "@echo off" }
                .Concat(arguments.Select((_, index) => $"echo(%{index + 1}"))
                .Concat(["echo npm-stand-in-stderr 1>&2", "exit /b 17"]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new ProcessCliCommandRunner().RunAsync(new CliCommand(shim, arguments), timeout.Token);

        Assert.True(result.ExitCode == 17, $"Exit {result.ExitCode}; stdout: {result.StandardOutput}; stderr: {result.StandardError}");
        Assert.Equal(string.Join(Environment.NewLine, arguments.Select(arg => $"\"{arg}\"")),
            result.StandardOutput.TrimEnd('\r', '\n'));
        Assert.Equal("npm-stand-in-stderr", result.StandardError.Trim());
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npm.exe")]
    public async Task Executable_UsesArgumentListWithoutShellQuoting(string file)
    {
        ProcessStartInfo? captured = null;
        var host = new LocalExecutionHost("npm-test")
        {
            StartProcess = (start, _, _) =>
            {
                captured = start;
                return null;
            },
        };
        string[] arguments = ["view", "@openai/codex", "value with spaces", "", "a&b", "embedded\"quote", @"trailing\"];
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ProcessCliCommandRunner(host).RunAsync(new CliCommand(file, arguments)));

        Assert.NotNull(captured);
        Assert.Equal(file, captured.FileName);
        Assert.Equal(arguments, captured.ArgumentList);
        Assert.Empty(captured.Arguments);
        Assert.False(captured.UseShellExecute);
    }

    [Theory]
    [InlineData("npm.cmd")]
    [InlineData("npm.bat")]
    public async Task NonWindowsBatchExtension_StillLaunchesDirectly(string file)
    {
        ProcessStartInfo? captured = null;
        var local = new LocalExecutionHost("npm-test")
        {
            StartProcess = (start, _, _) =>
            {
                captured = start;
                return null;
            },
        };
        string[] arguments = ["view", "@openai/codex", "versions", "--json"];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessCliCommandRunner(new LinuxHost(local.Processes)).RunAsync(new CliCommand(file, arguments)));

        Assert.NotNull(captured);
        Assert.Equal(file, captured.FileName);
        Assert.Equal(arguments, captured.ArgumentList);
        Assert.Empty(captured.Arguments);
    }

    // Only process dispatch and the OS flag are needed by this runner. No OS calls are made.
    private sealed class LinuxHost(IHostProcesses processes) : IExecutionHost
    {
        public string NodeId => "linux-test";
        public IHostProcesses Processes => processes;
        public IHostPlatform Platform { get; } = new LinuxPlatform();
        public HostPath Path(string value) => new(NodeId, value);
        public IHostPathRules PathRules => throw new NotSupportedException();
        public IHostContainment Containment => throw new NotSupportedException();
        public IHostFileSystem Files => throw new NotSupportedException();
        public IHostNetwork Network => throw new NotSupportedException();
        public IHostHardware Hardware => throw new NotSupportedException();
        public IHostQueries Queries => throw new NotSupportedException();
    }

    private sealed class LinuxPlatform : IHostPlatform
    {
        public bool IsWindows => false;
        public bool IsLinux => true;
        public HostPlatformInfo Info => throw new NotSupportedException();
        public bool CanLaunchLocalBackends => throw new NotSupportedException();
        public string? GetEnvironmentVariable(string name) => throw new NotSupportedException();
        public string GetFolderPath(Environment.SpecialFolder folder) => throw new NotSupportedException();
        public string ComputeDataRoot => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
