using System.Text.Json.Nodes;
using RedCompute.Node.Install;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// Packaging (Phase 3 R7): the systemd user unit comes from its template, and an agent that
/// LEAF_NODE_HOME isolates never writes the user's real registrations.
/// </summary>
public sealed class PackagingTests : IDisposable
{
    private readonly NodeHome _home = new("pkg");

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_unit_template_renders_exactly_the_unit_install_wrote_before()
    {
        // The unit as LinuxInstallPlatform.Unit wrote it before the template existed.
        const string before = "[Unit]\nDescription=Leaf node agent\nAfter=network-online.target\nWants=network-online.target\n\n"
                              + "[Service]\nType=simple\nExecStart=/home/u/.local/bin/leaf-node run\nRestart=on-failure\nRestartSec=5\n"
                              + "KillMode=mixed\nTimeoutStopSec=90\n\n[Install]\nWantedBy=default.target\n";

        Assert.Equal(before, LinuxInstallPlatform.Unit("/home/u/.local/bin/leaf-node"));
    }

    [Fact]
    public void The_unit_template_is_the_file_in_the_repository()
    {
        var template = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "RedCompute.Node", "Install", "leaf-node.service"));
        Assert.DoesNotContain('\r', template);
        Assert.Contains("ExecStart=@EXECUTABLE@ run", template);
        Assert.Equal(1, template.Split('\n').Where(line => !line.StartsWith('#')).Count(line => line.Contains('@')));
    }

    [Fact]
    public void An_isolated_home_keeps_its_registrations_to_itself()
    {
        var commands = new NodeCommands(_home.Environment(), TextWriter.Null);
        Assert.IsType<IsolatedInstallPlatform>(commands.Platform());

        var started = new List<string>();
        var platform = new IsolatedInstallPlatform(_home.Paths, _home.Host, started.Add);
        platform.RegisterApp("/x/leaf-node", _home.Paths.Programs, "0.1.2");
        platform.EnableAutostart("/x/leaf-node", keepRunningWhenLoggedOut: true);

        var folder = Path.Combine(_home.Root, "registrations");
        var app = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "app.json")))!;
        Assert.Equal("0.1.2", app["displayVersion"]!.GetValue<string>());
        Assert.Equal("\"/x/leaf-node\" uninstall", app["uninstallString"]!.GetValue<string>());
        var autostart = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "autostart.json")))!;
        Assert.Equal(["/x/leaf-node", "run"], autostart["command"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.True(autostart["keepRunningWhenLoggedOut"]!.GetValue<bool>());
        Assert.Equal(["/x/leaf-node"], started);
        Assert.Equal(2, platform.Leftovers().Count);

        platform.DisableAutostart();
        platform.UnregisterApp();
        Assert.Empty(platform.Leftovers());
    }

    [Fact]
    public void Without_an_isolated_home_the_real_registrations_are_used()
    {
        var paths = OperatingSystem.IsWindows()
            ? NodePaths.Resolve(_ => null, _ => @"C:\Users\u\AppData\Local", windows: true)
            : NodePaths.Resolve(_ => null, _ => "/home/u", windows: false);
        var env = _home.Environment();
        var commands = new NodeCommands(new NodeEnvironment
        {
            Paths = paths, Host = env.Host, KeyStores = env.KeyStores, Log = env.Log,
        }, TextWriter.Null);

        var platform = commands.Platform();

        if (OperatingSystem.IsWindows()) Assert.IsType<WindowsInstallPlatform>(platform);
        else Assert.IsType<LinuxInstallPlatform>(platform);
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "RedCompute.sln"))) return folder.FullName;
        // Built with --artifacts-path outside the checkout: the source file's own location.
        return Path.GetFullPath(Path.Combine(SourceFolder(), "..", ".."));
    }

    private static string SourceFolder([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)!;
}
