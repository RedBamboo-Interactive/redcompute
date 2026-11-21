using RedCompute.Node.Reports;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>Design 2.5: the fixed probes, found with the shared locators, read like the provider plugins read them.</summary>
public sealed class CliProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leaf-node-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Each_cli_is_located_probed_with_its_fixed_argv_and_reported()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var name in new[] { "claude", "codex", "opencode" }) File.WriteAllText(Path.Combine(bin, name + ext), "");
        var probes = new ScriptedProbes();
        probes.Answers["claude --version"] = new ProbeOutput(true, 0, "2.1.3 (Claude Code)\n", "");
        probes.Answers["claude auth status --json"] = new ProbeOutput(true, 0, """{"loggedIn":true,"authMethod":"claude.ai"}""", "");
        probes.Answers["codex --version"] = new ProbeOutput(true, 0, "codex-cli 0.46.0\n", "");
        probes.Answers["codex login status"] = new ProbeOutput(true, 1, "", "Not logged in\n");
        probes.Answers["opencode --version"] = new ProbeOutput(true, 0, "\u001b[0m0.15.2\n", "");
        probes.Answers["opencode auth list"] = new ProbeOutput(true, 0, "\u001b[90m┌ Credentials\n│ ● Anthropic oauth\n└ \u001b[1m2\u001b[0m credentials\n", "");

        var clis = await new CliProber(new IsolatedHost(_root, bin), probes).ProbeAsync(CancellationToken.None);

        Assert.Equal(new CliReport("claude", true, "2.1.3 (Claude Code)", true, "claude.ai", Path.Combine(bin, "claude" + ext)), clis[0]);
        Assert.Equal(new CliReport("codex", true, "codex-cli 0.46.0", false, null, Path.Combine(bin, "codex" + ext)), clis[1]);
        Assert.Equal(new CliReport("opencode", true, "0.15.2", true, "opencode", Path.Combine(bin, "opencode" + ext)), clis[2]);
        // The argv the agent ran is exactly its own list.
        Assert.All(probes.Calls, call => Assert.Contains(CliProber.Commands[Path.GetFileNameWithoutExtension(call.File)],
            argv => argv.SequenceEqual(call.Arguments)));
        Assert.Equal(6, probes.Calls.Count);
    }

    [Fact]
    public async Task A_missing_cli_is_not_installed_and_is_not_run()
    {
        var probes = new ScriptedProbes();
        var clis = await new CliProber(new IsolatedHost(_root, Path.Combine(_root, "empty")), probes).ProbeAsync(CancellationToken.None);
        Assert.All(clis, cli => Assert.Equal(new CliReport(cli.Name, false, null, false, null, null), cli));
        Assert.Empty(probes.Calls);
    }

    [Theory]
    [InlineData("""{"loggedIn":true,"authMethod":"api-key"}""", "", 0, true, "api-key")]
    [InlineData("", """{"loggedIn":true}""", 0, true, null)]
    [InlineData("""{"loggedIn":false}""", "", 1, false, null)] // signed out: non-zero exit, same JSON
    [InlineData("not json", "", 0, false, null)]
    public void Claude_auth_status_reads_like_the_plugin(string stdout, string stderr, int exit, bool signedIn, string? method)
        => Assert.Equal((signedIn, method), CliProber.ReadClaudeAuth(new ProbeOutput(true, exit, stdout, stderr)));

    [Theory]
    [InlineData(0, "Logged in using ChatGPT", true, "chatgpt")]
    [InlineData(0, "Logged in using an API key - sk-***", true, "api-key")]
    [InlineData(1, "Not logged in", false, null)]
    public void Codex_login_status_reads_like_the_plugin(int exit, string text, bool signedIn, string? method)
        => Assert.Equal((signedIn, method), CliProber.ReadCodexAuth(new ProbeOutput(true, exit, "", text)));

    [Fact]
    public void The_search_path_adds_the_user_bins_after_the_users_own()
    {
        var host = new IsolatedHost(_root, "/opt/first");
        var path = CliProber.SearchPath(host).Split(Path.PathSeparator);
        Assert.Equal("/opt/first", path[0]);
        if (OperatingSystem.IsWindows())
            Assert.Contains(Path.Combine(_root, "appdata", "npm"), path);
        else
            Assert.Contains(Path.Combine(_root, "home", ".local", "bin"), path);
    }

    /// <summary>The local host with PATH, the profile and application data inside the test folder.</summary>
    private sealed class IsolatedHost(string root, string path) : IExecutionHost
    {
        private readonly LocalExecutionHost _local = new("node_cli_test");
        public string NodeId => _local.NodeId;
        public HostPath Path(string value) => _local.Path(value);
        public IHostPathRules PathRules => _local.PathRules;
        public IHostProcesses Processes => _local.Processes;
        public IHostContainment Containment => _local.Containment;
        public IHostFileSystem Files => _local.Files;
        public IHostNetwork Network => _local.Network;
        public IHostPlatform Platform { get; } = new IsolatedPlatform(new LocalExecutionHost("node_cli_test").Platform, root, path);
        public IHostHardware Hardware => _local.Hardware;
        public IHostQueries Queries => _local.Queries;
        public IHostRuntimes Runtimes => _local.Runtimes;
    }

    private sealed class IsolatedPlatform(IHostPlatform inner, string root, string path) : IHostPlatform
    {
        public HostPlatformInfo Info => inner.Info;
        public bool IsWindows => inner.IsWindows;
        public bool IsLinux => inner.IsLinux;
        public bool CanLaunchLocalBackends => inner.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? path : null;
        public string GetFolderPath(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.ApplicationData => System.IO.Path.Combine(root, "appdata"),
            Environment.SpecialFolder.LocalApplicationData => System.IO.Path.Combine(root, "localappdata"),
            Environment.SpecialFolder.UserProfile => System.IO.Path.Combine(root, "home"),
            _ => System.IO.Path.Combine(root, folder.ToString()),
        };
        public string ComputeDataRoot => inner.ComputeDataRoot;
    }
}
