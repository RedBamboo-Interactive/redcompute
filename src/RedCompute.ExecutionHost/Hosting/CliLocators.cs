namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// Where the provider CLIs are on a host: the search orders the Claude Code, Codex and OpenCode
/// plugins used, moved here unchanged so that RedCompute (for node zero, later for a remote host)
/// and the node agent (for its CLI report) find the same binary. Every file, folder and variable
/// is read through <paramref name="host"/>.
/// </summary>
public static class CliLocators
{
    /// <summary>The Claude Code CLI: the configured path, else the managed install, the SDK, Claude Desktop, PATH.</summary>
    public static string? Claude(IExecutionHost host, string? configuredPath)
        => Claude(
            host,
            configuredPath,
            host.Platform.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            host.Platform.GetEnvironmentVariable("PATH"));

    /// <summary><see cref="Claude(IExecutionHost, string?)"/> with the application-data folder and PATH given.</summary>
    public static string? Claude(
        IExecutionHost host,
        string? configuredPath,
        string applicationData,
        string? pathEnvironment)
    {
        bool FileExists(string path) => host.Files.FileExists(host.Path(path));

        if (configuredPath != null)
            return FileExists(configuredPath) ? configuredPath : null;

        // Prefer the native binary from the globally installed Claude Code package. This is the
        // package RedCompute exposes for version management, so the version we report, install,
        // and launch must all refer to the same bytes.
        var globalCliExe = host.PathRules.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-code", "bin", "claude.exe");
        if (FileExists(globalCliExe))
            return globalCliExe;

        // The Agent SDK also bundles a native CLI that supports stream-json IPC. Keep it as a
        // compatibility fallback for machines without a global Claude Code package.
        var sdkExe = host.PathRules.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        if (FileExists(sdkExe))
            return sdkExe;

        // Claude Desktop bundles versioned native CLIs outside PATH.
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

        // Fallback: search PATH for claude.cmd / claude.exe
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

    /// <summary>The Codex CLI: see <see cref="CodexCliLocator"/>.</summary>
    public static string? Codex(IExecutionHost host, string? configuredPath) => CodexCliLocator.Resolve(host, configuredPath);

    /// <summary>
    /// The OpenCode CLI: the configured path, npm's global package, PATH, then npm's shim. The PATH
    /// separator is this process's (the host's on node zero).
    /// </summary>
    public static string? OpenCode(IExecutionHost host, string? configuredPath)
    {
        bool FileExists(string path) => host.Files.FileExists(host.Path(path));

        if (configuredPath != null)
            return FileExists(configuredPath) ? configuredPath : null;

        // Try npm global node_modules directly first — avoids .cmd wrapper
        var appData = host.Platform.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmBinDirect = host.PathRules.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe");
        if (FileExists(npmBinDirect))
            return npmBinDirect;

        var pathDirs = host.Platform.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            // Prefer .exe over .cmd; skip .cmd wrappers in npm dirs when possible
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = host.PathRules.Combine(dir, $"opencode{ext}");
                if (!FileExists(candidate)) continue;

                // If we found a .cmd in an npm directory, try to resolve through to the real exe
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
