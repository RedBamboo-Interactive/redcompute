using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The CLI search orders moved from the Claude Code and OpenCode plugins into
/// <see cref="CliLocators"/> unchanged. Each case builds one combination of candidate files in a temp
/// folder and runs the plugins' former code (kept below, verbatim) and the shared locator over the
/// same host; they must answer the same path for every combination and every configured path.
/// (Codex's locator moved file and all; CodexCliLocatorTests guard it.)
/// </summary>
public sealed class CliLocatorParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-parity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly string[] ClaudeCandidates =
    [
        "appdata/npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe",
        "appdata/npm/node_modules/@anthropic-ai/claude-agent-sdk/node_modules/@anthropic-ai/claude-agent-sdk-win32-x64/claude.exe",
        "appdata/Claude/claude-code/1.2.3/claude.exe",
        "appdata/Claude/claude-code/1.10.0/claude.exe",
        "appdata/Claude/claude-code/not-a-version/claude.exe",
        "path1/claude.exe",
        "path1/claude.cmd",
        "path2/claude",
    ];

    private static readonly string[] OpenCodeCandidates =
    [
        "appdata/npm/node_modules/opencode-ai/bin/opencode.exe",
        "tools/npm/opencode.cmd",
        "tools/npm/node_modules/opencode-ai/bin/opencode.exe",
        "path1/opencode.exe",
        "path1/opencode",
        "path2/opencode.cmd",
        "appdata/npm/opencode.cmd",
    ];

    [Fact]
    public void Claude_finds_what_the_plugin_found_for_every_layout()
    {
        var checkedCases = 0;
        for (var mask = 0; mask < 1 << ClaudeCandidates.Length; mask++)
        {
            var (host, root) = Layout(ClaudeCandidates, mask, "claude");
            foreach (var configured in Configured(root, ClaudeCandidates))
            {
                var appData = Path.Combine(root, "appdata");
                var path = PathVariable(root, "path0", "\"" + Path.Combine(root, "path1") + "\"", " ", "path2");
                Assert.Equal(LegacyClaude(host, configured, appData, path), CliLocators.Claude(host, configured, appData, path));
                Assert.Equal(LegacyClaude(host, configured, appData, host.Platform.GetEnvironmentVariable("PATH")),
                    CliLocators.Claude(host, configured));
                checkedCases++;
            }
        }
        Assert.Equal((1 << ClaudeCandidates.Length) * 3, checkedCases);
    }

    [Fact]
    public void OpenCode_finds_what_the_plugin_found_for_every_layout()
    {
        for (var mask = 0; mask < 1 << OpenCodeCandidates.Length; mask++)
        {
            var (host, root) = Layout(OpenCodeCandidates, mask, "opencode");
            foreach (var configured in Configured(root, OpenCodeCandidates))
                Assert.Equal(LegacyOpenCode(host, configured), CliLocators.OpenCode(host, configured));
        }
    }

    [Fact]
    public void The_plugins_resolve_through_the_shared_locators()
    {
        var (host, root) = Layout(ClaudeCandidates, 0b1000_0001, "claude");
        var appData = Path.Combine(root, "appdata");
        Assert.Equal(CliLocators.Claude(host, null, appData, null),
            RedCompute.Plugin.ClaudeCode.ClaudeSessionService.ResolveClaudePath(host, null, appData, null));
        Assert.EndsWith(Path.Combine("claude-code", "bin", "claude.exe"), CliLocators.Claude(host, null, appData, null));
    }

    private (IExecutionHost Host, string Root) Layout(string[] candidates, int mask, string name)
    {
        var root = Path.Combine(_root, $"{name}-{mask}");
        for (var i = 0; i < candidates.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var file = Path.Combine(root, candidates[i].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
        }
        Directory.CreateDirectory(root);
        var path = PathVariable(root, "path0", Path.Combine(root, "tools", "npm"), "path1", "", "path2");
        return (new ControlledHost(Path.Combine(root, "appdata"), path), root);
    }

    private static string PathVariable(string root, params string[] entries)
        => string.Join(Path.PathSeparator, entries.Select(e => e.Length == 0 || e.Trim().Length == 0 || Path.IsPathRooted(e.Trim('"'))
            ? e
            : Path.Combine(root, e)));

    /// <summary>No configured path, a configured file that exists (the first candidate), one that does not.</summary>
    private static IEnumerable<string?> Configured(string root, string[] candidates)
    {
        yield return null;
        yield return Path.Combine(root, candidates[0].Replace('/', Path.DirectorySeparatorChar));
        yield return Path.Combine(root, "missing", "cli.exe");
    }

    /// <summary>The local host with its application-data folder and PATH set by the test.</summary>
    private sealed class ControlledHost(string appData, string path) : IExecutionHost
    {
        private readonly LocalExecutionHost _local = new("node_parity");
        public string NodeId => _local.NodeId;
        public HostPath Path(string value) => _local.Path(value);
        public IHostPathRules PathRules => _local.PathRules;
        public IHostProcesses Processes => _local.Processes;
        public IHostContainment Containment => _local.Containment;
        public IHostFileSystem Files => _local.Files;
        public IHostNetwork Network => _local.Network;
        public IHostPlatform Platform { get; } = new ControlledPlatform(new LocalExecutionHost("node_parity").Platform, appData, path);
        public IHostHardware Hardware => _local.Hardware;
        public IHostQueries Queries => _local.Queries;
    }

    private sealed class ControlledPlatform(IHostPlatform inner, string appData, string path) : IHostPlatform
    {
        public HostPlatformInfo Info => inner.Info;
        public bool IsWindows => inner.IsWindows;
        public bool IsLinux => inner.IsLinux;
        public bool CanLaunchLocalBackends => inner.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? path : inner.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.ApplicationData ? appData : inner.GetFolderPath(folder);
        public string ComputeDataRoot => inner.ComputeDataRoot;
    }

    // ---- the plugins' code before the move (ClaudeSessionService.ResolveClaudePath, OpenCodeSessionService.ResolveOpenCodePath)

    private static string? LegacyClaude(IExecutionHost host, string? configuredPath, string applicationData, string? pathEnvironment)
    {
        bool FileExists(string path) => host.Files.FileExists(host.Path(path));

        if (configuredPath != null)
            return FileExists(configuredPath) ? configuredPath : null;

        var globalCliExe = host.PathRules.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-code", "bin", "claude.exe");
        if (FileExists(globalCliExe))
            return globalCliExe;

        var sdkExe = host.PathRules.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        if (FileExists(sdkExe))
            return sdkExe;

        var desktopRoot = host.PathRules.Combine(applicationData, "Claude", "claude-code");
        if (host.Files.DirectoryExists(host.Path(desktopRoot)))
        {
            var desktopExe = host.Files.EnumerateDirectories(host.Path(desktopRoot), "*", SearchOption.TopDirectoryOnly)
                .Select(directory => new
                {
                    Path = host.PathRules.Combine(directory.Value, "claude.exe"),
                    Version = Version.TryParse(Path.GetFileName(directory.Value), out var version)
                        ? version
                        : new Version(0, 0),
                })
                .Where(candidate => FileExists(candidate.Path))
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (desktopExe != null)
                return desktopExe;
        }

        var pathDirs = pathEnvironment?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = host.PathRules.Combine(dir.Trim().Trim('"'), $"claude{ext}");
                if (FileExists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? LegacyOpenCode(IExecutionHost host, string? configuredPath)
    {
        bool FileExists(string path) => host.Files.FileExists(host.Path(path));

        if (configuredPath != null)
            return FileExists(configuredPath) ? configuredPath : null;

        var appData = host.Platform.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmBinDirect = host.PathRules.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe");
        if (FileExists(npmBinDirect))
            return npmBinDirect;

        var pathDirs = host.Platform.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = host.PathRules.Combine(dir, $"opencode{ext}");
                if (!FileExists(candidate)) continue;

                if (ext == ".cmd" && dir.Replace('\\', '/').Contains("/npm", StringComparison.OrdinalIgnoreCase))
                {
                    var resolvedExe = host.PathRules.Combine(dir, "node_modules", "opencode-ai", "bin", "opencode.exe");
                    if (FileExists(resolvedExe))
                        return resolvedExe;
                }

                return candidate;
            }
        }

        var npmFallback = host.PathRules.Combine(appData, "npm", "opencode.cmd");
        if (FileExists(npmFallback))
            return npmFallback;

        return null;
    }
}
