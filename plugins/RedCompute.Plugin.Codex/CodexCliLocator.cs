using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Resolves the Codex CLI executable.
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
/// </summary>
public static class CodexCliLocator
{
    /// <summary>
    /// Returns the best available path to a runnable Codex CLI, or null if none is found.
    /// An explicit configured path always wins and is never second-guessed.
    /// </summary>
    public static string? Resolve(string? configuredPath) => Resolve(configuredPath, CodexCliEnvironment.Current());

    internal static string? Resolve(string? configuredPath, CodexCliEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

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
        var prefixNative = NativeUnder(PrefixPackageRoot(prefix), env);
        if (prefixNative != null) return prefixNative;
        var prefixShim = Path.Combine(prefix, "bin", "codex");
        if (!File.Exists(prefixShim)) return null;
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

        var candidate = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Locates vendor/&lt;triple&gt;/bin/codex[.exe] beneath an @openai/codex package root.</summary>
    private static string? NativeUnder(string packageRoot, CodexCliEnvironment env)
    {
        var triple = VendorTriple(env);
        if (triple == null || !Directory.Exists(packageRoot)) return null;

        var candidate = Path.Combine(
            packageRoot, "node_modules", "@openai", PlatformPackage(env),
            "vendor", triple, "bin", ExeName(env));

        return File.Exists(candidate) ? candidate : null;
    }

    private static string PrefixPackageRoot(string prefix)
        => Path.Combine(prefix, "lib", "node_modules", "@openai", "codex");

    private static IEnumerable<string> NpmGlobalRoots(CodexCliEnvironment env)
    {
        if (!env.IsWindows && env.ConfiguredNpmPrefix is { } configured)
            yield return PrefixPackageRoot(configured);

        if (!string.IsNullOrEmpty(env.AppData))
            yield return Path.Combine(env.AppData, "npm", "node_modules", "@openai", "codex");

        if (!string.IsNullOrEmpty(env.Home))
        {
            yield return Path.Combine(env.Home, ".npm-global", "lib", "node_modules", "@openai", "codex");
            yield return Path.Combine(env.Home, ".bun", "install", "global", "node_modules", "@openai", "codex");
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
            yield return Path.Combine(directory, "node_modules", "@openai", "codex");
            yield break;
        }

        yield return Path.GetFullPath(Path.Combine(directory, "..", "lib", "node_modules", "@openai", "codex"));
        if (LinkedPackageRoot(shim) is { } linked)
            yield return linked;
    }

    /// <summary>The @openai/codex directory above a link's final target, or null.</summary>
    private static string? LinkedPackageRoot(string shim)
    {
        try
        {
            var target = new FileInfo(shim).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
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
                try { candidate = Path.Combine(dir, $"codex{ext}"); }
                catch { continue; }
                if (File.Exists(candidate)) yield return candidate;
            }
        }
    }
}

/// <summary>What <see cref="CodexCliLocator"/> reads from the machine; tests supply their own.</summary>
internal sealed record CodexCliEnvironment
{
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

    public static CodexCliEnvironment Current() => new()
    {
        IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
        IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        Architecture = RuntimeInformation.ProcessArchitecture,
        PathVariable = Environment.GetEnvironmentVariable("PATH"),
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ConfiguredNpmPrefix = OperatingSystem.IsWindows() ? null : NonEmpty(
            Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX")
            ?? Environment.GetEnvironmentVariable("npm_config_prefix")),
        AskNpmPrefix = () => SystemNpmPrefix.Value,
    };

    private static readonly Lazy<string?> SystemNpmPrefix = new(() => AskNpm("npm"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <c>npm prefix -g</c> (5 s at most); null when npm is missing or fails.</summary>
    internal static string? AskNpm(string npmExecutable)
    {
        try
        {
            using var npm = Process.Start(new ProcessStartInfo(npmExecutable, ["prefix", "-g"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
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
