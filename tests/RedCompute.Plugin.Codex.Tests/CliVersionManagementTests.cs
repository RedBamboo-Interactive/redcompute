using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public class CliVersionManagementTests
{
    [Theory]
    [InlineData("codex-cli 0.163.0", "0.163.0")]
    [InlineData("2.1.300 (Claude Code)", "2.1.300")]
    [InlineData("claude v3.0.0-beta.2+win", "3.0.0-beta.2+win")]
    public void ParseInstalledVersion_ReadsCliOutput(string output, string expected)
    {
        Assert.Equal(expected, NpmCliVersionManager.ParseInstalledVersion(output));
    }

    [Fact]
    public void ParseAvailableVersions_OrdersSemanticVersionsNewestFirst()
    {
        var versions = NpmCliVersionManager.ParseAvailableVersions(
            "[\"1.9.0\",\"2.0.0-beta.2\",\"1.10.0\",\"2.0.0\",\"2.0.0-beta.10\"]");

        Assert.Equal(
            ["2.0.0", "2.0.0-beta.10", "2.0.0-beta.2", "1.10.0", "1.9.0"],
            versions);
    }

    [Fact]
    public async Task GetAvailableVersions_UsesNpmRegistryCommand()
    {
        var runner = new FakeRunner(command =>
            new CliCommandResult(0, "[\"2.1.300\",\"2.1.299\"]", ""));
        var manager = CreateManager("@anthropic-ai/claude-code", runner);

        var versions = await manager.GetAvailableVersionsAsync();

        Assert.Equal(["2.1.300", "2.1.299"], versions);
        var command = Assert.Single(runner.Commands);
        Assert.Equal("npm.cmd", command.FileName);
        Assert.Equal(["view", "@anthropic-ai/claude-code", "versions", "--json"], command.Arguments);
    }

    [Fact]
    public async Task Install_UsesFixedGlobalNpmCommandAndReportsInstalledVersion()
    {
        var runner = new FakeRunner(command =>
        {
            if (command.Arguments.SequenceEqual(["install", "--global", "@openai/codex@0.163.0"]))
                return new CliCommandResult(0, "changed 2 packages", "");
            if (command.Arguments.SequenceEqual(["--version"]))
                return new CliCommandResult(0, "codex-cli 0.163.0", "");
            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', command.Arguments)}");
        });
        var manager = CreateManager("@openai/codex", runner);

        var started = manager.StartInstall("0.163.0");
        var completed = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal("0.163.0", completed.InstalledVersion);
        Assert.Contains(runner.Commands, command =>
            command.FileName == "npm.cmd"
            && command.Arguments.SequenceEqual(["install", "--global", "@openai/codex@0.163.0"]));
    }

    [Fact]
    public void Install_IsRefusedWhileSessionsAreActive()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("runner must not be called"));
        var manager = CreateManager("@openai/codex", runner, () => 3);

        var error = Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest"));

        Assert.Equal("sessions_running", error.Code);
        Assert.Equal(3, error.ActiveSessionCount);
        Assert.Equal("3 sessions running", error.Message);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Install_RechecksSessionsImmediatelyBeforeNpmRuns()
    {
        var checks = 0;
        var runner = new FakeRunner(_ => throw new InvalidOperationException("runner must not be called"));
        var manager = CreateManager("@openai/codex", runner,
            () => Interlocked.Increment(ref checks) == 1 ? 0 : 2);

        var started = manager.StartInstall("latest");
        var refused = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("refused", refused.Status);
        Assert.Equal("sessions_running", refused.ErrorCode);
        Assert.Equal("2 sessions running", refused.Error);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Install_PreservesNpmErrorText()
    {
        var runner = new FakeRunner(command =>
            command.Arguments.FirstOrDefault() == "install"
                ? new CliCommandResult(1, "", "npm ERR! version not found")
                : new CliCommandResult(0, "codex-cli 0.162.0", ""));
        var manager = CreateManager("@openai/codex", runner);

        var started = manager.StartInstall("9.9.9");
        var failed = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("npm_install_failed", failed.ErrorCode);
        Assert.Equal("npm ERR! version not found", failed.Error);
    }

    private static NpmCliVersionManager CreateManager(
        string packageName,
        ICliCommandRunner runner,
        Func<int>? activeSessions = null)
        => new(packageName, () => "provider-cli.exe", activeSessions ?? (() => 0), runner, () => "npm.cmd");

    private static async Task<CliInstallOperation> WaitForTerminalAsync(
        NpmCliVersionManager manager, Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var operation = Assert.IsType<CliInstallOperation>(manager.GetOperation(operationId));
            if (operation.Status is not ("queued" or "running")) return operation;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeRunner(Func<CliCommand, CliCommandResult> handler) : ICliCommandRunner
    {
        private readonly object _gate = new();
        public List<CliCommand> Commands { get; } = [];

        public Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
        {
            lock (_gate) Commands.Add(command);
            return Task.FromResult(handler(command));
        }
    }
}
