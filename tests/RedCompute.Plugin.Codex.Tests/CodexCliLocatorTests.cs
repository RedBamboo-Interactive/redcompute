using System.Runtime.InteropServices;
using RedCompute.Plugin.Codex;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Where the Codex CLI is found. Every case builds a real file layout in a temp folder and
/// hands the locator an environment describing a machine, so the Linux layouts are checked on
/// Windows too; only symlinks and spawning a fake npm need a Unix host.
/// </summary>
public sealed class CodexCliLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"redcompute-codex-locator-{Guid.NewGuid():N}");

    public CodexCliLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_cli_under_a_custom_npm_prefix_that_is_not_on_path_is_found()
    {
        var prefix = Path.Combine(_root, "custom-prefix");
        var native = LinuxNative(PrefixPackage(prefix));

        var found = CodexCliLocator.Resolve(null, Linux(prefix: prefix, ask: NeverAsked));

        Assert.Equal(native, found);
    }

    [Fact]
    public void Npm_is_asked_for_its_prefix_once_when_the_fixed_locations_hold_nothing()
    {
        var prefix = Path.Combine(_root, "npm-reported");
        var native = LinuxNative(PrefixPackage(prefix));
        var asked = 0;

        var found = CodexCliLocator.Resolve(null, Linux(ask: () => { asked++; return prefix; }));

        Assert.Equal(native, found);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Npm_is_not_asked_when_a_fixed_location_already_has_the_cli()
    {
        var home = Path.Combine(_root, "home");
        var native = LinuxNative(Path.Combine(home, ".npm-global", "lib", "node_modules", "@openai", "codex"));

        var found = CodexCliLocator.Resolve(null, Linux(home: home, ask: NeverAsked));

        Assert.Equal(native, found);
    }

    [Fact]
    public void Npm_is_not_asked_when_npm_config_prefix_is_set()
    {
        var found = CodexCliLocator.Resolve(null, Linux(prefix: Path.Combine(_root, "empty-prefix"), ask: NeverAsked));

        Assert.Null(found);
    }

    [Fact]
    public void A_unix_shim_on_path_resolves_through_its_lib_node_modules_package()
    {
        var prefix = Path.Combine(_root, "usr-local");
        var shim = Touch(Path.Combine(prefix, "bin", "codex"));
        var native = LinuxNative(PrefixPackage(prefix));

        var found = CodexCliLocator.Resolve(null, Linux(path: Path.GetDirectoryName(shim), ask: NeverAsked));

        Assert.Equal(native, found);
    }

    [Fact]
    public void The_prefix_bin_shim_is_used_when_its_package_has_no_vendored_binary()
    {
        var prefix = Path.Combine(_root, "shim-only");
        var shim = Touch(Path.Combine(prefix, "bin", "codex"));
        Directory.CreateDirectory(PrefixPackage(prefix));

        Assert.Equal(shim, CodexCliLocator.Resolve(null, Linux(prefix: prefix, ask: NeverAsked)));

        var asked = Path.Combine(_root, "asked-shim-only");
        var askedShim = Touch(Path.Combine(asked, "bin", "codex"));
        Assert.Equal(askedShim, CodexCliLocator.Resolve(null, Linux(ask: () => asked)));
    }

    [Fact]
    public void Nothing_installed_anywhere_is_null()
    {
        Assert.Null(CodexCliLocator.Resolve(null, Linux(ask: () => null)));
    }

    [Fact]
    public void A_configured_path_wins_and_is_never_second_guessed()
    {
        var prefix = Path.Combine(_root, "custom-prefix");
        LinuxNative(PrefixPackage(prefix));
        var configured = Touch(Path.Combine(_root, "elsewhere", "codex"));

        Assert.Equal(configured, CodexCliLocator.Resolve(configured, Linux(prefix: prefix, ask: NeverAsked)));
        Assert.Null(CodexCliLocator.Resolve(Path.Combine(_root, "missing", "codex"), Linux(prefix: prefix, ask: NeverAsked)));
    }

    [Fact]
    public void Windows_lookup_is_unchanged_appdata_npm_package()
    {
        var appData = Path.Combine(_root, "AppData", "Roaming");
        var native = WindowsNative(Path.Combine(appData, "npm", "node_modules", "@openai", "codex"));

        Assert.Equal(native, CodexCliLocator.Resolve(null, Windows(appData: appData)));
    }

    [Fact]
    public void Windows_lookup_is_unchanged_standalone_installer_wins_over_npm()
    {
        var localAppData = Path.Combine(_root, "AppData", "Local");
        var standalone = Touch(Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        var appData = Path.Combine(_root, "AppData", "Roaming");
        WindowsNative(Path.Combine(appData, "npm", "node_modules", "@openai", "codex"));

        Assert.Equal(standalone, CodexCliLocator.Resolve(null, Windows(appData: appData, localAppData: localAppData)));
    }

    [Fact]
    public void Windows_lookup_is_unchanged_shim_resolves_through_its_sibling_node_modules()
    {
        var npmDir = Path.Combine(_root, "npm-shims");
        Touch(Path.Combine(npmDir, "codex.cmd"));
        var native = WindowsNative(Path.Combine(npmDir, "node_modules", "@openai", "codex"));

        Assert.Equal(native, CodexCliLocator.Resolve(null, Windows(path: npmDir)));
    }

    [Fact]
    public void Windows_lookup_is_unchanged_no_npm_prefix_and_no_unix_layout()
    {
        // A prefix and a ../lib layout that only the Unix lookup reads; Windows falls back to
        // the bare .cmd shim exactly as before, and never runs npm.
        var prefix = Path.Combine(_root, "prefix");
        LinuxNative(PrefixPackage(prefix));
        WindowsNative(PrefixPackage(prefix));
        var shim = Touch(Path.Combine(prefix, "bin", "codex.cmd"));

        var found = CodexCliLocator.Resolve(null, Windows(path: Path.GetDirectoryName(shim)) with
        {
            ConfiguredNpmPrefix = prefix,
            AskNpmPrefix = NeverAsked,
        });

        Assert.Equal(shim, found);
    }

    [NonWindowsFact("the shim is a symlink, which Windows only creates with Developer Mode or elevation")]
    public void A_linked_shim_resolves_through_the_package_it_links_to()
    {
        var package = Path.Combine(_root, "store", "node_modules", "@openai", "codex");
        var launcher = Touch(Path.Combine(package, "bin", "codex.js"));
        var native = LinuxNative(package);
        var shims = Path.Combine(_root, "shims");
        Directory.CreateDirectory(shims);
        File.CreateSymbolicLink(Path.Combine(shims, "codex"), launcher);

        Assert.Equal(native, CodexCliLocator.Resolve(null, Linux(path: shims, ask: NeverAsked)));
    }

    [NonWindowsFact("spawns a POSIX shell script standing in for npm")]
    public void Asking_npm_reads_its_global_prefix_and_ignores_failures()
    {
        var prefix = Path.Combine(_root, "npm-global");
        Assert.Equal(prefix, CodexCliEnvironment.AskNpm(Script("npm-ok", $"echo '{prefix}'")));
        Assert.Null(CodexCliEnvironment.AskNpm(Script("npm-fails", $"echo '{prefix}'; exit 1")));
        Assert.Null(CodexCliEnvironment.AskNpm(Script("npm-relative", "echo 'not/rooted'")));
        Assert.Null(CodexCliEnvironment.AskNpm(Path.Combine(_root, "no-such-npm")));
    }

    [NonWindowsFact("NPM_CONFIG_PREFIX is read only off Windows")]
    public void The_real_environment_finds_a_cli_under_npm_config_prefix_not_on_path()
    {
        var prefix = Path.Combine(_root, "real-prefix");
        var native = NativeFor(PrefixPackage(prefix), RuntimeInformation.ProcessArchitecture);
        var before = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        try
        {
            Environment.SetEnvironmentVariable("NPM_CONFIG_PREFIX", prefix);
            Assert.DoesNotContain(Path.Combine(prefix, "bin"),
                (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));

            Assert.Equal(native, CodexCliLocator.Resolve(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPM_CONFIG_PREFIX", before);
        }
    }

    private static string? NeverAsked() => throw new Xunit.Sdk.XunitException("npm prefix -g must not be asked");

    private CodexCliEnvironment Linux(string? path = null, string? home = null, string? prefix = null, Func<string?>? ask = null) => new()
    {
        IsWindows = false,
        IsLinux = true,
        Architecture = Architecture.X64,
        PathVariable = path ?? "",
        Home = home ?? Path.Combine(_root, "empty-home"),
        ConfiguredNpmPrefix = prefix,
        AskNpmPrefix = ask ?? NeverAsked,
        SystemPackageRoots = [],
    };

    private CodexCliEnvironment Windows(string? path = null, string? appData = null, string? localAppData = null) => new()
    {
        IsWindows = true,
        Architecture = Architecture.X64,
        PathVariable = path ?? "",
        Home = Path.Combine(_root, "empty-profile"),
        AppData = appData ?? Path.Combine(_root, "empty-roaming"),
        LocalAppData = localAppData ?? Path.Combine(_root, "empty-local"),
        SystemPackageRoots = [],
    };

    private static string PrefixPackage(string prefix) => Path.Combine(prefix, "lib", "node_modules", "@openai", "codex");

    private static string LinuxNative(string package) => NativeFor(package, Architecture.X64, windows: false);

    private static string WindowsNative(string package) => Touch(Path.Combine(
        package, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"));

    private static string NativeFor(string package, Architecture architecture, bool? windows = null)
    {
        if (windows ?? OperatingSystem.IsWindows())
            return WindowsNative(package);
        var macOS = windows is null && OperatingSystem.IsMacOS();
        var (platformPackage, triple) = (macOS, architecture) switch
        {
            (false, Architecture.Arm64) => ("codex-linux-arm64", "aarch64-unknown-linux-musl"),
            (false, _) => ("codex-linux-x64", "x86_64-unknown-linux-musl"),
            (true, Architecture.Arm64) => ("codex-darwin-arm64", "aarch64-apple-darwin"),
            (true, _) => ("codex-darwin-x64", "x86_64-apple-darwin"),
        };
        return Touch(Path.Combine(package, "node_modules", "@openai", platformPackage, "vendor", triple, "bin", "codex"));
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test seam only");
        return path;
    }

    private string Script(string name, string body)
    {
        var path = Touch(Path.Combine(_root, "bin", name));
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
