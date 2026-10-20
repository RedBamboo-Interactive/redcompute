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
    public void ParseAvailableVersions_FiltersRegistryNoiseAndOrdersStableVersionsNewestFirst()
    {
        const string registryFixture = """
            [
              "0.164.0-alpha.6-linux-arm64",
              "0.163.0-win32-x64",
              "0.164.0-alpha.6",
              "0.162.0",
              "0.163.0",
              "0.161.0+build.4",
              "0.161.0",
              "2.0.0-beta.2",
              "1.10.0"
            ]
            """;

        var versions = NpmCliVersionManager.ParseAvailableVersions(registryFixture);

        Assert.Equal(["1.10.0", "0.163.0", "0.162.0", "0.161.0"], versions);
    }

    [Fact]
    public async Task GetAvailableVersions_UsesRegistryVersionsAndLatestTag()
    {
        var runner = new FakeRunner(command => command.Arguments[2] == "versions"
            ? new CliCommandResult(0, "[\"2.1.300\",\"2.1.299-beta.1\",\"2.1.299\"]", "")
            : new CliCommandResult(0, "\"2.1.300\"", ""));
        var manager = CreateManager("@anthropic-ai/claude-code", runner);

        var catalog = await manager.GetAvailableVersionsAsync();

        Assert.Equal(["2.1.300", "2.1.299"], catalog.Versions);
        Assert.Equal("2.1.300", catalog.LatestVersion);
        Assert.Collection(runner.Commands,
            command => Assert.Equal(["view", "@anthropic-ai/claude-code", "versions", "--json"], command.Arguments),
            command => Assert.Equal(["view", "@anthropic-ai/claude-code", "dist-tags.latest", "--json"], command.Arguments));
    }

    [Theory]
    [InlineData("0.164.0-alpha.6")]
    [InlineData("0.163.0-win32-x64")]
    [InlineData("0.164.0-alpha.6-linux-arm64")]
    [InlineData("1.2.3+build.4")]
    public void Install_RejectsAnythingExceptLatestOrStableVersion(string version)
    {
        var manager = CreateManager("@openai/codex", new FakeRunner(_ =>
            throw new InvalidOperationException("runner must not be called")));

        var error = Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall(version));

        Assert.Equal("invalid_version", error.Code);
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
        var manager = CreateManager("@openai/codex", runner,
            sessionState: () => new CliProviderSessionState(3, [], []));

        var error = Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest"));

        Assert.Equal("sessions_running", error.Code);
        Assert.Equal(3, error.ActiveSessionCount);
        Assert.Equal("3 sessions running", error.Message);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Status_AllowsIdleLeafSessionsAndReportsParkingHintData()
    {
        var manager = CreateManager("@openai/codex",
            new FakeRunner(_ => new CliCommandResult(0, "codex-cli 0.163.0", "")),
            () => new CliProviderSessionState(0, ["idle-a", "idle-b"], [41, 42]));

        var status = await manager.GetStatusAsync();

        Assert.True(status.InstallAllowed);
        Assert.Equal(2, status.IdleSessionCount);
        Assert.Equal(0, status.ActiveSessionCount);
        Assert.Equal(0, status.ExternalProcessCount);
        Assert.Null(status.BlockedReason);
    }

    [Fact]
    public async Task Install_RechecksSessionsImmediatelyBeforeNpmRuns()
    {
        var checks = 0;
        var runner = new FakeRunner(_ => throw new InvalidOperationException("runner must not be called"));
        var manager = CreateManager("@openai/codex", runner,
            sessionState: () => Interlocked.Increment(ref checks) == 1
                ? CliProviderSessionState.Empty
                : new CliProviderSessionState(2, [], []));

        var started = manager.StartInstall("latest");
        var refused = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("refused", refused.Status);
        Assert.Equal("sessions_running", refused.ErrorCode);
        Assert.Equal("2 sessions running", refused.Error);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Install_ParksIdleSessionsBeforeNpmRuns()
    {
        var state = new CliProviderSessionState(0, ["idle-a", "idle-b"], [41, 42]);
        var parked = new List<string>();
        var runner = SuccessfulInstallRunner("0.163.0");
        var inspector = new FakeProcessInspector((_, owned) => owned.Count > 0
            ? []
            : state.OwnedProcessIds.Count > 0
                ? [new CliProcessInfo(41, "C:\\fake-package\\codex.exe")]
                : []);
        var manager = CreateManager("@openai/codex", runner,
            () => state,
            (ids, _) =>
            {
                parked.AddRange(ids);
                state = CliProviderSessionState.Empty;
                return Task.CompletedTask;
            },
            inspector);

        var started = manager.StartInstall("latest");
        var completed = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(["idle-a", "idle-b"], parked);
        Assert.Contains(runner.Commands, command => command.Arguments.FirstOrDefault() == "install");
    }

    [Fact]
    public void Install_IsRefusedWhenExternalPackageProcessExists()
    {
        var manager = CreateManager("@openai/codex",
            new FakeRunner(_ => throw new InvalidOperationException("runner must not be called")),
            processInspector: new FakeProcessInspector((_, _) =>
                [new CliProcessInfo(9001, "C:\\fake-package\\codex.exe")]));

        var error = Assert.Throws<CliInstallRejectedException>(() => manager.StartInstall("latest"));

        Assert.Equal("external_processes", error.Code);
        Assert.Equal("1 Codex process still running outside Leaf", error.Message);
    }

    [Fact]
    public async Task Install_FinalProcessCheckRefusesProcessLeftAfterIdleParking()
    {
        var state = new CliProviderSessionState(0, ["idle-a"], [41]);
        var manager = CreateManager("@openai/codex", SuccessfulInstallRunner("0.163.0"),
            () => state,
            (_, _) =>
            {
                state = CliProviderSessionState.Empty;
                return Task.CompletedTask;
            },
            new FakeProcessInspector((_, owned) => owned.Count > 0
                ? []
                : [new CliProcessInfo(9001, "C:\\fake-package\\codex.exe")]));

        var started = manager.StartInstall("latest");
        var refused = await WaitForTerminalAsync(manager, started.Id);

        Assert.Equal("refused", refused.Status);
        Assert.Equal("external_processes", refused.ErrorCode);
        Assert.Equal("1 Codex process still running outside Leaf", refused.Error);
    }

    [Fact]
    public async Task SessionLaunchDuringInstall_WaitsThenRuns()
    {
        var installStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (command, ct) =>
        {
            if (command.Arguments.FirstOrDefault() == "install")
            {
                installStarted.TrySetResult();
                await releaseInstall.Task.WaitAsync(ct);
                return new CliCommandResult(0, "installed", "");
            }
            return new CliCommandResult(0, "codex-cli 0.163.0", "");
        });
        var manager = CreateManager("@openai/codex", runner);
        var operation = manager.StartInstall("latest");
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var launchRan = false;
        var launch = manager.RunWhenInstallIdleAsync(() =>
        {
            launchRan = true;
            return Task.FromResult(42);
        });
        await Task.Delay(50);
        Assert.False(launchRan);

        releaseInstall.TrySetResult();
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, operation.Id)).Status);
        Assert.Equal(42, await launch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(launchRan);
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

    private static FakeRunner SuccessfulInstallRunner(string installedVersion)
        => new(command => command.Arguments.FirstOrDefault() == "install"
            ? new CliCommandResult(0, "installed", "")
            : new CliCommandResult(0, $"codex-cli {installedVersion}", ""));

    private static NpmCliVersionManager CreateManager(
        string packageName,
        ICliCommandRunner runner,
        Func<CliProviderSessionState>? sessionState = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? parkIdleSessions = null,
        ICliProcessInspector? processInspector = null)
        => new(
            packageName,
            packageName.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "Claude Code" : "Codex",
            () => "provider-cli.exe",
            sessionState ?? (() => CliProviderSessionState.Empty),
            parkIdleSessions ?? ((_, _) => Task.CompletedTask),
            runner,
            () => "npm.cmd",
            () => "C:\\fake-package",
            processInspector ?? new FakeProcessInspector((_, _) => []),
            TimeSpan.FromSeconds(5));

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

    private sealed class FakeRunner : ICliCommandRunner
    {
        private readonly Func<CliCommand, CancellationToken, Task<CliCommandResult>> _handler;
        private readonly object _gate = new();
        public List<CliCommand> Commands { get; } = [];

        public FakeRunner(Func<CliCommand, CliCommandResult> handler)
            : this((command, _) => Task.FromResult(handler(command))) { }

        public FakeRunner(Func<CliCommand, CancellationToken, Task<CliCommandResult>> handler)
            => _handler = handler;

        public Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
        {
            lock (_gate) Commands.Add(command);
            return _handler(command, ct);
        }
    }

    private sealed class FakeProcessInspector(
        Func<string, IReadOnlyCollection<int>, IReadOnlyList<CliProcessInfo>> inspect)
        : ICliProcessInspector
    {
        public IReadOnlyList<CliProcessInfo> FindPackageProcesses(
            string packageDirectory,
            IReadOnlyCollection<int> ownedRootProcessIds)
            => inspect(packageDirectory, ownedRootProcessIds);
    }
}
