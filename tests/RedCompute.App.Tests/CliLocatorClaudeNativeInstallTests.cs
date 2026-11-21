using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Anthropic's native installer (install.sh on Linux, install.ps1 on Windows) puts Claude Code in
/// ~/.local/bin for the current user, a folder RedCompute's PATH often lacks. The Claude locator
/// looks there after the npm package, the SDK and Claude Desktop, and before PATH.
/// </summary>
public sealed class CliLocatorClaudeNativeInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-native-" + Guid.NewGuid().ToString("N"));
    private readonly LocalExecutionHost _host = new("node_native");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string AppData => Path.Combine(_root, "appdata");
    private string Home => Path.Combine(_root, "home");
    private static string NativeName => OperatingSystem.IsWindows() ? "claude.exe" : "claude";
    private string NativeExe => Path.Combine(Home, ".local", "bin", NativeName);

    private string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Finds_the_native_install_when_it_is_not_on_PATH()
    {
        Touch(NativeExe);
        Directory.CreateDirectory(AppData);

        Assert.Equal(NativeExe, CliLocators.Claude(_host, null, AppData, pathEnvironment: "", userProfile: Home));
    }

    [Fact]
    public void The_native_install_comes_before_PATH()
    {
        Touch(NativeExe);
        var onPath = Path.Combine(_root, "tools");
        Touch(Path.Combine(onPath, NativeName));

        Assert.Equal(NativeExe, CliLocators.Claude(_host, null, AppData, onPath, Home));
    }

    [Fact]
    public void The_managed_npm_package_still_wins()
    {
        Touch(NativeExe);
        var npmExe = Touch(Path.Combine(AppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));

        Assert.Equal(npmExe, CliLocators.Claude(_host, null, AppData, "", Home));
    }

    [Fact]
    public void Without_a_user_profile_the_search_is_the_plugins_old_order()
    {
        Touch(NativeExe);
        Directory.CreateDirectory(AppData);

        Assert.Null(CliLocators.Claude(_host, null, AppData, "", userProfile: null));
        Assert.Null(CliLocators.Claude(_host, null, AppData, ""));
    }

    [Fact]
    public void A_configured_path_is_still_the_only_answer()
    {
        Touch(NativeExe);
        var missing = Path.Combine(_root, "missing", "claude.exe");

        Assert.Null(CliLocators.Claude(_host, missing, AppData, "", Home));
    }
}
