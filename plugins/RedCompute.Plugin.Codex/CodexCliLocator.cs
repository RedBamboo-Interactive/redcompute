using System.IO;
using System.Runtime.InteropServices;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Resolves the Codex CLI executable.
///
/// The npm package installs shims (codex, codex.cmd, codex.ps1) that re-exec a JS launcher,
/// which in turn exec's a vendored native binary. Spawning the shim is unreliable — on Windows
/// a .cmd cannot be started with UseShellExecute=false — and it costs an extra Node process on
/// every call. So we resolve through to the native binary and fall back to the shim only if the
/// vendored layout is not what we expect.
/// </summary>
public static class CodexCliLocator
{
    private static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "codex.exe" : "codex";

    /// <summary>Vendor triple used by the @openai/codex-{platform} companion package.</summary>
    private static string? VendorTriple =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) => "x86_64-pc-windows-msvc",
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => "x86_64-unknown-linux-musl",
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => "x86_64-apple-darwin",
            Architecture.Arm64 when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => "aarch64-unknown-linux-musl",
            Architecture.Arm64 when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => "aarch64-apple-darwin",
            _ => null,
        };

    private static string PlatformPackage =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "codex-win32-arm64" : "codex-win32-x64")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "codex-darwin-arm64" : "codex-darwin-x64")
                : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "codex-linux-arm64" : "codex-linux-x64");

    /// <summary>
    /// Returns the best available path to a runnable Codex CLI, or null if none is found.
    /// An explicit configured path always wins and is never second-guessed.
    /// </summary>
    public static string? Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        foreach (var root in NpmGlobalRoots())
        {
            var native = NativeUnder(root);
            if (native != null) return native;
        }

        // Shim on PATH — resolve through it to the native binary where we can.
        foreach (var shim in PathCandidates())
        {
            var pkgRoot = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules", "@openai", "codex");
            var native = NativeUnder(pkgRoot);
            if (native != null) return native;
            if (shim.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase)) return shim;
        }

        return PathCandidates().FirstOrDefault();
    }

    /// <summary>Locates vendor/&lt;triple&gt;/bin/codex[.exe] beneath an @openai/codex package root.</summary>
    private static string? NativeUnder(string packageRoot)
    {
        var triple = VendorTriple;
        if (triple == null || !Directory.Exists(packageRoot)) return null;

        var candidate = Path.Combine(
            packageRoot, "node_modules", "@openai", PlatformPackage,
            "vendor", triple, "bin", ExeName);

        return File.Exists(candidate) ? candidate : null;
    }

    private static IEnumerable<string> NpmGlobalRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            yield return Path.Combine(appData, "npm", "node_modules", "@openai", "codex");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".npm-global", "lib", "node_modules", "@openai", "codex");
            yield return Path.Combine(home, ".bun", "install", "global", "node_modules", "@openai", "codex");
        }

        yield return "/usr/local/lib/node_modules/@openai/codex";
        yield return "/usr/lib/node_modules/@openai/codex";
    }

    private static IEnumerable<string> PathCandidates()
    {
        var dirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
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
