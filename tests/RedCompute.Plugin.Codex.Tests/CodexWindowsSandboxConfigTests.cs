using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

[Collection(CodexLaunchGoldenCollection.Name)]
public sealed class CodexWindowsSandboxConfigTests
{
    [Theory]
    [InlineData("[windows]\nsandbox = \"elevated\"\n")]
    [InlineData("model = \"x\"\n[windows]\n# note\nsandbox='unelevated'\n")]
    [InlineData("windows.sandbox = \"elevated\"\n")]
    [InlineData("[ windows ]\n\"sandbox\" = \"elevated\"\n")]
    [InlineData("windows = { sandbox = \"elevated\" }\n")]
    [InlineData("[windows]\nsandbox = \"off\" # disabled on purpose\n")]
    public void A_config_that_sets_windows_sandbox_is_detected(string toml)
        => Assert.True(CodexWindowsSandboxConfig.SetsWindowsSandbox(toml));

    [Theory]
    [InlineData("")]
    [InlineData("model = \"x\"\n")]
    [InlineData("sandbox = \"elevated\"\n")]
    [InlineData("[windows]\nother = 1\n[projects.'c:/x']\nsandbox = \"elevated\"\n")]
    [InlineData("[windows_extra]\nsandbox = \"elevated\"\n")]
    [InlineData("# windows.sandbox = \"elevated\"\n")]
    [InlineData("note = \"[windows] sandbox = elevated\"\n")]
    [InlineData("text = \"\"\"\nwindows.sandbox = 1\n\"\"\"\n")]
    [InlineData("[[windows]]\nsandbox = \"elevated\"\n")]
    [InlineData("[mcp_servers.x]\nwindows = { other = 1 }\n")]
    public void A_config_that_does_not_is_not(string toml)
        => Assert.False(CodexWindowsSandboxConfig.SetsWindowsSandbox(toml));

    [WindowsTheory("the override is Windows-only")]
    [InlineData(null, true)]
    [InlineData("model = \"x\"\n", true)]
    [InlineData("[windows]\nsandbox = \"elevated\"\n", false)]
    [InlineData("windows.sandbox = \"elevated\"\n", false)]
    public void The_override_is_added_unless_the_user_set_the_key(string? toml, bool added)
    {
        CodexWindowsSandboxConfig.ConfigTextOverride = (_, _) => toml;
        try
        {
            var args = CodexWindowsSandboxConfig.AppServerArguments(LocalExecutionHost.Shared);
            Assert.Equal(added ? ["-c", "windows.sandbox=\"unelevated\"", "app-server"] : ["app-server"], args);
        }
        finally { CodexWindowsSandboxConfig.ConfigTextOverride = null; }
    }

    [WindowsFact("the override is Windows-only")]
    public void The_real_file_is_read_from_CODEX_HOME_and_never_written()
    {
        var home = Path.Combine(Path.GetTempPath(), "codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var file = Path.Combine(home, "config.toml");
        try
        {
            var overlay = new Dictionary<string, string> { ["CODEX_HOME"] = home };
            Assert.Equal(["-c", "windows.sandbox=\"unelevated\"", "app-server"],
                CodexWindowsSandboxConfig.AppServerArguments(LocalExecutionHost.Shared, overlay));
            File.WriteAllText(file, "[windows]\nsandbox = \"elevated\"\n");
            var before = File.ReadAllBytes(file);
            Assert.Equal(["app-server"], CodexWindowsSandboxConfig.AppServerArguments(LocalExecutionHost.Shared, overlay));
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(home, true); }
    }
}
