using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// Resolves the Codex CLI executable (moved from the Codex plugin unchanged, so the server and the
/// node agent find the same binary; <see cref="CliLocators.Codex"/> is the entry point).
///
/// The npm package installs shims (codex, codex.cmd, codex.ps1) that re-exec a JS launcher,
/// which in turn exec's a vendored native binary. Spawning the shim is unreliable — on Windows
/// a .cmd cannot be started with UseShellExecute=false, elsewhere the shim needs node on the PATH
/// RedCompute was started with — and it costs an extra Node process on every call. So we resolve
/// through to the native binary and fall back to the shim only if the vendored layout is not what
/// we expect.
///
/// Outside Windows, npm installs globally under its prefix: the package in
/// {prefix}/lib/node_modules/@openai/codex and the shim in {prefix}/bin/codex, a link to the
/// package's bin/codex.js. A user prefix (~/.npm-global, ~/.local) is often not on the PATH a
/// desktop-started RedLeaf inherits, so the prefix itself is probed: NPM_CONFIG_PREFIX, else
/// <c>npm prefix -g</c>, asked once per process and only when neither the fixed locations nor
/// PATH hold the CLI. Windows never asks npm; its lookup is what it always was.
///
/// Everything is read on the host the CLI runs on: its environment, folders, files and links, and
/// <c>npm prefix -g</c> runs there.
/// </summary>
public static class CodexCliLocator
{
    /// <summary>
    /// Returns the best available path to a runnable Codex CLI, or null if none is found.
    /// An explicit configured path always wins and is never second-guessed.
    /// </summary>
    public static string? Resolve(string? configuredPath) => Resolve(LocalExecutionHost.Shared, configuredPath);

    /// <summary>The CLI on <paramref name="host"/>; see <see cref="Resolve(string?)"/>.</summary>
    public static string? Resolve(IExecutionHost host, string? configuredPath)
        => Resolve(configuredPath, CodexCliEnvironment.Current(host));

    internal static string? Resolve(string? configuredPath, CodexCliEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return env.FileExists(configuredPath) ? configuredPath : null;

        var standalone = OfficialStandalonePath(env);
        if (standalone is not null) return standalone;

        foreach (var root in NpmGlobalRoots(env))
        {
            var native = NativeUnder(root, env);
            if (native != null) return native;
        }

        // Shim on PATH — resolve through it to the native binary where we can.
        var shims = PathCandidates(env).ToList();
        foreach (var shim in shims)
        {
            var native = NativeThroughShim(shim, env);
            if (native != null) return native;
            if (shim.EndsWith(ExeName(env), StringComparison.OrdinalIgnoreCase)) return shim;
        }
        if (shims.Count > 0 || env.IsWindows) return shims.FirstOrDefault();

        // Unix, nowhere on PATH: the npm prefix itself. NPM_CONFIG_PREFIX, else npm prefix -g.
        var prefix = env.ConfiguredNpmPrefix ?? env.AskNpmPrefix();
        if (prefix is null) return null;
        var prefixNative = NativeUnder(PrefixPackageRoot(env, prefix), env);
        if (prefixNative != null) return prefixNative;
        var prefixShim = env.Combine(prefix, "bin", "codex");
        if (!env.FileExists(prefixShim)) return null;
        return NativeThroughShim(prefixShim, env) ?? prefixShim;
    }

    private static string? NativeThroughShim(string shim, CodexCliEnvironment env)
    {
        foreach (var pkgRoot in ShimPackageRoots(shim, env))
        {
            var native = NativeUnder(pkgRoot, env);
            if (native != null) return native;
        }
        return null;
    }

    private static string ExeName(CodexCliEnvironment env) => env.IsWindows ? "codex.exe" : "codex";

    /// <summary>Vendor triple used by the @openai/codex-{platform} companion package.</summary>
    private static string? VendorTriple(CodexCliEnvironment env) =>
        env.Architecture switch
        {
            Architecture.X64 when env.IsWindows => "x86_64-pc-windows-msvc",
            Architecture.X64 when env.IsLinux => "x86_64-unknown-linux-musl",
            Architecture.X64 when env.IsMacOS => "x86_64-apple-darwin",
            Architecture.Arm64 when env.IsLinux => "aarch64-unknown-linux-musl",
            Architecture.Arm64 when env.IsMacOS => "aarch64-apple-darwin",
            _ => null,
        };

    private static string PlatformPackage(CodexCliEnvironment env) =>
        env.IsWindows
            ? (env.Architecture == Architecture.Arm64 ? "codex-win32-arm64" : "codex-win32-x64")
            : env.IsMacOS
                ? (env.Architecture == Architecture.Arm64 ? "codex-darwin-arm64" : "codex-darwin-x64")
                : (env.Architecture == Architecture.Arm64 ? "codex-linux-arm64" : "codex-linux-x64");

    /// <summary>Official Codex standalone installer location on Windows.</summary>
    private static string? OfficialStandalonePath(CodexCliEnvironment env)
    {
        if (!env.IsWindows) return null;

        var localAppData = env.LocalAppData;
        if (string.IsNullOrWhiteSpace(localAppData)) return null;

        var candidate = env.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        return env.FileExists(candidate) ? candidate : null;
    }

    /// <summary>Locates vendor/&lt;triple&gt;/bin/codex[.exe] beneath an @openai/codex package root.</summary>
    private static string? NativeUnder(string packageRoot, CodexCliEnvironment env)
    {
        var triple = VendorTriple(env);
        if (triple == null || !env.DirectoryExists(packageRoot)) return null;

        var candidate = env.Combine(
            packageRoot, "node_modules", "@openai", PlatformPackage(env),
            "vendor", triple, "bin", ExeName(env));

        return env.FileExists(candidate) ? candidate : null;
    }

    private static string PrefixPackageRoot(CodexCliEnvironment env, string prefix)
        => env.Combine(prefix, "lib", "node_modules", "@openai", "codex");

    private static IEnumerable<string> NpmGlobalRoots(CodexCliEnvironment env)
    {
        if (!env.IsWindows && env.ConfiguredNpmPrefix is { } configured)
            yield return PrefixPackageRoot(env, configured);

        if (!string.IsNullOrEmpty(env.AppData))
            yield return env.Combine(env.AppData, "npm", "node_modules", "@openai", "codex");

        if (!string.IsNullOrEmpty(env.Home))
        {
            yield return env.Combine(env.Home, ".npm-global", "lib", "node_modules", "@openai", "codex");
            yield return env.Combine(env.Home, ".bun", "install", "global", "node_modules", "@openai", "codex");
        }

        foreach (var root in env.SystemPackageRoots)
            yield return root;
    }

    /// <summary>
    /// Where the @openai/codex package of a shim lives: next to it on Windows
    /// ({dir}/node_modules/@openai/codex); elsewhere {dir}/../lib/node_modules/@openai/codex, and
    /// the package the shim links to (its final target is the package's bin/codex.js).
    /// </summary>
    private static IEnumerable<string> ShimPackageRoots(string shim, CodexCliEnvironment env)
    {
        var directory = Path.GetDirectoryName(shim)!;
        if (env.IsWindows)
        {
            yield return env.Combine(directory, "node_modules", "@openai", "codex");
            yield break;
        }

        yield return env.Host.PathRules.GetFullPath(env.Combine(directory, "..", "lib", "node_modules", "@openai", "codex"));
        if (LinkedPackageRoot(shim, env) is { } linked)
            yield return linked;
    }

    /// <summary>The @openai/codex directory above a link's final target, or null.</summary>
    private static string? LinkedPackageRoot(string shim, CodexCliEnvironment env)
    {
        try
        {
            var target = env.Host.Files.ResolveLinkTarget(env.Host.Path(shim), returnFinalTarget: true);
            for (var directory = target is null ? null : Path.GetDirectoryName(target);
                 directory is not null;
                 directory = Path.GetDirectoryName(directory))
            {
                if (Path.GetFileName(directory) == "codex"
                    && Path.GetFileName(Path.GetDirectoryName(directory)) == "@openai")
                    return directory;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static IEnumerable<string> PathCandidates(CodexCliEnvironment env)
    {
        var dirs = env.PathVariable?.Split(Path.PathSeparator) ?? [];
        var exts = env.IsWindows
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = env.Combine(dir, $"codex{ext}"); }
                catch { continue; }
                if (env.FileExists(candidate)) yield return candidate;
            }
        }
    }
}

/// <summary>What <see cref="CodexCliLocator"/> reads from the machine; tests supply their own.</summary>
internal sealed record CodexCliEnvironment
{
    /// <summary>The host whose files, folders and links are probed (node zero's local host by default).</summary>
    public IExecutionHost Host { get; init; } = LocalExecutionHost.Shared;

    public required bool IsWindows { get; init; }
    public bool IsLinux { get; init; }
    public bool IsMacOS { get; init; }
    public required Architecture Architecture { get; init; }
    public string? PathVariable { get; init; }
    public string? Home { get; init; }
    public string? AppData { get; init; }
    public string? LocalAppData { get; init; }
    /// <summary>The system-wide npm package folders, probed last among the fixed locations.</summary>
    public IReadOnlyList<string> SystemPackageRoots { get; init; } =
        ["/usr/local/lib/node_modules/@openai/codex", "/usr/lib/node_modules/@openai/codex"];
    /// <summary>NPM_CONFIG_PREFIX (npm also reads npm_config_prefix); never read on Windows.</summary>
    public string? ConfiguredNpmPrefix { get; init; }
    /// <summary><c>npm prefix -g</c>; asked only off Windows, when nothing else was found.</summary>
    public Func<string?> AskNpmPrefix { get; init; } = () => null;

    internal bool FileExists(string path) => Host.Files.FileExists(Host.Path(path));
    internal bool DirectoryExists(string path) => Host.Files.DirectoryExists(Host.Path(path));
    internal string Combine(params string[] parts) => Host.PathRules.Combine(parts);

    /// <summary>
    /// The host's environment, folders and platform. The OS family comes from the host;
    /// macOS and the process architecture have no host member yet and are this process's own,
    /// which is the host's on node zero.
    /// </summary>
    public static CodexCliEnvironment Current(IExecutionHost host) => new()
    {
        Host = host,
        IsWindows = host.Platform.IsWindows,
        IsLinux = host.Platform.IsLinux,
        IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        Architecture = RuntimeInformation.ProcessArchitecture,
        PathVariable = host.Platform.GetEnvironmentVariable("PATH"),
        Home = host.Platform.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppData = host.Platform.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LocalAppData = host.Platform.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ConfiguredNpmPrefix = host.Platform.IsWindows ? null : NonEmpty(
            host.Platform.GetEnvironmentVariable("NPM_CONFIG_PREFIX")
            ?? host.Platform.GetEnvironmentVariable("npm_config_prefix")),
        AskNpmPrefix = () => SystemNpmPrefixes.GetOrAdd(host.NodeId,
            _ => new Lazy<string?>(() => AskNpm(host, "npm"), LazyThreadSafetyMode.ExecutionAndPublication)).Value,
    };

    // npm is asked once per process for each host (losing GetOrAdd races never evaluate their Lazy).
    private static readonly ConcurrentDictionary<string, Lazy<string?>> SystemNpmPrefixes = new(StringComparer.Ordinal);

    /// <summary>Containment owner label of <c>npm prefix -g</c>, which runs uncontained as it always has.</summary>
    internal const string NpmPrefixOwner = "codex-npm-prefix";

    /// <summary>Runs <c>npm prefix -g</c> on the host (5 s at most); null when npm is missing or fails.</summary>
    internal static string? AskNpm(IExecutionHost host, string npmExecutable)
    {
        try
        {
            // The start info this always used: npm and its two arguments, stdout and stderr piped,
            // no shell, uncontained.
            using var npm = host.Processes.Start(new HostSpawnSpec
            {
                FileName = npmExecutable,
                ArgumentList = ["prefix", "-g"],
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                UseShellExecute = false,
                Containment = ContainmentMode.None,
                OwnerLabel = NpmPrefixOwner,
            });
            if (npm is null) return null;
            var output = npm.StandardOutput.ReadToEndAsync();
            _ = npm.StandardError.ReadToEndAsync();
            if (!npm.WaitForExit(5000))
            {
                try { npm.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            var prefix = NonEmpty(output.Result.Trim());
            return npm.ExitCode == 0 && prefix is not null && Path.IsPathRooted(prefix) ? prefix : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException)
        {
            return null;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
