using System.Net;
using System.Text.Json.Nodes;
using RedCompute.Core.Storage;
using RedCompute.Node.Control;
using RedCompute.Node.Install;
using RedCompute.Node.Pairing;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>The verbs, the control channel, the single-instance lock, the paths and install/uninstall.</summary>
public sealed class ControlAndCommandTests : IDisposable
{
    private readonly NodeHome _home = new("cmd");

    public void Dispose() => _home.Dispose();

    // ---- paths and the data root --------------------------------------------------------------------

    [Fact]
    public void Windows_keeps_everything_under_local_app_data_leafnode()
    {
        var paths = NodePaths.Resolve(_ => null, folder => folder == Environment.SpecialFolder.LocalApplicationData
            ? @"C:\Users\u\AppData\Local" : throw new InvalidOperationException(folder.ToString()), windows: true);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "LeafNode"), paths.Data);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "LeafNode", "compute"), paths.Compute);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "LeafNode", "audit"), paths.Audit);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "Programs", "LeafNode"), paths.Programs);
        Assert.Null(paths.Bin);
    }

    [NonWindowsFact("Linux paths: Path.IsPathFullyQualified follows the OS the test runs on")]
    public void Linux_follows_xdg()
    {
        var variables = new Dictionary<string, string?>
        {
            ["XDG_STATE_HOME"] = "/home/u/.state",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
        };
        var paths = NodePaths.Resolve(name => variables.GetValueOrDefault(name),
            folder => folder == Environment.SpecialFolder.UserProfile ? "/home/u" : throw new InvalidOperationException(), windows: false);
        Assert.Equal(Path.Combine("/home/u", ".local", "share", "leaf-node", "data"), paths.Data);
        Assert.Equal(Path.Combine("/home/u/.state", "leaf-node", "logs"), paths.Logs);
        Assert.Equal(Path.Combine("/home/u/.state", "leaf-node", "spool"), paths.Spool);
        Assert.Equal(Path.Combine("/run/user/1000", "leaf-node.sock"), paths.ControlSocket);
        Assert.Equal(Path.Combine("/home/u", ".local", "bin"), paths.Bin);
        Assert.Equal(Path.Combine("/home/u", ".config", "systemd", "user"), paths.SystemdUnits);
    }

    [Fact]
    public void The_execution_hosts_data_root_is_the_nodes_own_compute_folder()
    {
        var variables = new Dictionary<string, string?>();
        var paths = _home.Paths;
        // What Program.ConfigureDataRoot sets, as ComputeStorage resolves it.
        variables[ComputeStorage.DataRootVariable] = paths.Compute;
        var storage = ComputeStorage.FromEnvironment(name => variables.GetValueOrDefault(name), _ => "/never/RedCompute");
        Assert.Equal(paths.Compute, storage.DataRoot);
        Assert.StartsWith(_home.Root, storage.SessionProcesses);
    }

    [Fact]
    public void A_relative_home_is_refused()
        => Assert.Equal("bad_home", Assert.Throws<NodeCommandException>(() =>
            NodePaths.Resolve(name => name == NodePaths.HomeVariable ? "relative/home" : null, _ => "", OperatingSystem.IsWindows())).Code);

    // ---- the control channel and the lock -----------------------------------------------------------

    [Fact]
    public async Task The_control_channel_answers_the_same_user_and_reports_no_agent_otherwise()
    {
        Assert.Null(await ControlClient.SendAsync(_home.Paths, new JsonObject { ["verb"] = "status" }, TimeSpan.FromSeconds(5)));

        await using var server = new ControlServer(_home.Paths, (request, _) => Task.FromResult(request["verb"]?.GetValue<string>() == "ping"
            ? new JsonObject { ["ok"] = true, ["pong"] = true }
            : throw new NodeCommandException("unknown_verb", "no")), NodeLog.None);
        server.Start();

        var answer = await ControlClient.SendAsync(_home.Paths, new JsonObject { ["verb"] = "ping" });
        Assert.True(answer!["pong"]!.GetValue<bool>());
        var refused = await ControlClient.SendAsync(_home.Paths, new JsonObject { ["verb"] = "other" });
        Assert.False(refused!["ok"]!.GetValue<bool>());
        Assert.Equal("unknown_verb", refused["error"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_home.Paths.ControlSocket));
    }

    [Fact]
    public async Task One_agent_per_user()
    {
        using (var first = InstanceLock.TryAcquire(_home.Paths))
        {
            Assert.NotNull(first);
            Assert.Null(await Task.Run(() => InstanceLock.TryAcquire(_home.Paths)));
        }
        using var again = InstanceLock.TryAcquire(_home.Paths);
        Assert.NotNull(again);
    }

    // ---- the verbs ----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_without_an_agent_says_not_paired_and_not_running()
    {
        var output = new StringWriter();
        Assert.Equal(0, await new NodeCommands(_home.Environment(), output).RunAsync(["status"], CancellationToken.None));
        Assert.Contains("Not paired", output.ToString());
        Assert.Contains("not running", output.ToString());
    }

    [Fact]
    public async Task Pair_prints_the_fingerprint_and_refuses_a_second_pairing()
    {
        var origin = "https://leaf.example.test";
        var http = new StubHttp((request, body) => PairingTests.Json(HttpStatusCode.Created, new JsonObject
        {
            ["nodeId"] = NodeAgentLinkTests.NodeId,
            ["fingerprint"] = NodeCrypto.Fingerprint(Convert.FromBase64String(JsonNode.Parse(body)!["publicKey"]!.GetValue<string>())),
        }));
        var env = _home.Environment(server: () => new NodeServerClient(http));
        var output = new StringWriter();

        var exit = await new NodeCommands(env, output).RunAsync(
            ["pair", "--server", "https://Leaf.Example.Test/", "--code", "abcd efgh", "--name", "lab box"], CancellationToken.None);

        Assert.Equal(0, exit);
        var pairing = env.StateStore.Load().Pairing!;
        Assert.Equal(origin, pairing.ServerOrigin);
        Assert.Contains($"Paired as {NodeAgentLinkTests.NodeId} — fingerprint {pairing.Fingerprint}. Confirm it in Leaf.", output.ToString());
        Assert.Equal("lab box", JsonNode.Parse(http.Requests[0].Body)!["machine"]!["name"]!.GetValue<string>());
        Assert.Contains(env.Audit.Read(), line => line["name"]!.GetValue<string>() == "pair");

        output.GetStringBuilder().Clear();
        Assert.Equal(1, await new NodeCommands(env, output).RunAsync(
            ["pair", "--server", origin, "--code", "ABCD-EFGH"], CancellationToken.None));
        Assert.Contains("already paired", output.ToString());
    }

    [Theory]
    [InlineData(new[] { "pair", "--code", "ABCD-EFGH" }, "--server is required")]
    [InlineData(new[] { "pair", "--server", "https://leaf.example.test", "--code", "nope" }, "not a pairing code")]
    [InlineData(new[] { "pair", "--server", "http://leaf.example.test", "--code", "ABCD-EFGH" }, "not https")]
    [InlineData(new[] { "frobnicate" }, "Unknown command")]
    public async Task Bad_usage_is_explained(string[] args, string says)
    {
        var output = new StringWriter();
        Assert.Equal(NodeExitCodes.Usage, await new NodeCommands(_home.Environment(), output).RunAsync(args, CancellationToken.None));
        Assert.Contains(says, output.ToString());
    }

    [Fact]
    public async Task Unpair_without_an_agent_tells_the_server_and_deletes_the_key()
    {
        await LinkedAgent.PairLocallyAsync(_home);
        var http = new StubHttp((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var env = _home.Environment(server: () => new NodeServerClient(http));
        var output = new StringWriter();

        Assert.Equal(0, await new NodeCommands(env, output).RunAsync(["unpair"], CancellationToken.None));

        Assert.Contains("Unpaired", output.ToString());
        Assert.Equal($"/api/nodes/{NodeAgentLinkTests.NodeId}/unpair", Assert.Single(http.Requests).Request.RequestUri!.AbsolutePath);
        Assert.False(File.Exists(_home.Paths.KeyFile));
        Assert.Equal("unpaired", env.StateStore.Load().Ended!.Reason);
    }

    [Fact]
    public async Task Status_kill_all_and_stop_reach_the_running_agent()
    {
        var env = _home.Environment();
        var output = new StringWriter();
        var run = new NodeCommands(env, output).RunAsync(["run"], CancellationToken.None);
        await Wait.UntilAsync(() => ControlClient.SendAsync(_home.Paths, new JsonObject { ["verb"] = "status" }).Result is not null,
            "the agent's control channel");

        var status = new StringWriter();
        await new NodeCommands(env, status).RunAsync(["status", "--json"], CancellationToken.None);
        var json = JsonNode.Parse(status.ToString())!;
        Assert.True(json["running"]!.GetValue<bool>());
        Assert.Equal(Environment.ProcessId, json["pid"]!.GetValue<int>());

        var kill = new StringWriter();
        Assert.Equal(0, await new NodeCommands(env, kill).RunAsync(["kill-all"], CancellationToken.None));
        Assert.Contains("0 channel(s)", kill.ToString());

        // A second agent for the same user refuses to start.
        var second = new StringWriter();
        Assert.Equal(NodeExitCodes.AlreadyRunning, await new NodeCommands(env, second).RunAsync(["run"], CancellationToken.None));

        Assert.Equal(0, await new NodeCommands(env, new StringWriter()).RunAsync(["stop"], CancellationToken.None));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(NodeExitCodes.NotRunning, await new NodeCommands(env, new StringWriter()).RunAsync(["stop"], CancellationToken.None));
    }

    // ---- install / uninstall ------------------------------------------------------------------------

    [Fact]
    public void Install_places_the_program_side_by_side_and_registers_autostart()
    {
        var platform = new FakeInstallPlatform();
        var source = FakeBuild("0.1.0");
        var installer = new NodeInstaller(_home.Paths, _home.Host, platform, NodeLog.None);

        var executable = installer.Install(source, "0.1.0", keepRunningWhenLoggedOut: true);

        Assert.Equal(Path.Combine(_home.Paths.Programs, "0.1.0", NodeInstaller.ExecutableName), executable);
        Assert.True(File.Exists(Path.Combine(_home.Paths.Programs, "0.1.0", "leaf-node.dll")));
        Assert.Equal("0.1.0", JsonNode.Parse(File.ReadAllText(Path.Combine(_home.Paths.Programs, "current.json")))!["version"]!.GetValue<string>());
        Assert.True(platform.Autostart);
        Assert.True(platform.Linger);
        Assert.True(platform.Registered);
        if (_home.Paths.Bin is { } bin)
        {
            var shim = Path.Combine(bin, NodeInstaller.ShimName);
            Assert.Equal(Path.Combine(_home.Paths.Programs, NodeInstaller.ShimName), new FileInfo(shim).LinkTarget);
            Assert.Equal(shim, platform.AutostartCommand);
        }

        // Two versions are kept; an update prunes the oldest.
        installer.Install(FakeBuild("0.2.0"), "0.2.0", false);
        installer.Install(FakeBuild("0.3.0"), "0.3.0", false);
        Assert.Equal(["0.2.0", "0.3.0"], Directory.EnumerateDirectories(_home.Paths.Programs).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Uninstall_unpairs_and_leaves_nothing_behind()
    {
        var platform = new FakeInstallPlatform();
        new NodeInstaller(_home.Paths, _home.Host, platform, NodeLog.None).Install(FakeBuild("0.1.0"), "0.1.0", false);
        await LinkedAgent.PairLocallyAsync(_home);
        var env = _home.Environment(server: () => new NodeServerClient(new StubHttp((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))));
        env.Audit.Record("local", "test", "ok");
        var output = new StringWriter();

        var exit = await new NodeCommands(env, output, () => platform).RunAsync(["uninstall"], CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("leaf-node is uninstalled.", output.ToString());
        Assert.False(platform.Autostart);
        Assert.False(platform.Registered);
        Assert.False(Directory.Exists(_home.Root), "the node home is gone: " + string.Join(", ",
            Directory.Exists(_home.Root) ? Directory.EnumerateFileSystemEntries(_home.Root, "*", SearchOption.AllDirectories) : []));
    }

    private string FakeBuild(string version)
    {
        var folder = Path.Combine(_home.Root, "build-" + Guid.NewGuid().ToString("N")[..8], version);
        Directory.CreateDirectory(Path.Combine(folder, "runtimes"));
        File.WriteAllText(Path.Combine(folder, NodeInstaller.ExecutableName), "exe");
        File.WriteAllText(Path.Combine(folder, "leaf-node.dll"), "dll");
        File.WriteAllText(Path.Combine(folder, "runtimes", "native.so"), "so");
        return folder;
    }
}

internal sealed class FakeInstallPlatform : INodeInstallPlatform
{
    public bool Autostart { get; private set; }
    public string? AutostartCommand { get; private set; }
    public bool Linger { get; private set; }
    public bool Registered { get; private set; }

    public void EnableAutostart(string executable, bool keepRunningWhenLoggedOut)
    {
        Autostart = true;
        AutostartCommand = executable;
        Linger = keepRunningWhenLoggedOut;
    }

    public void DisableAutostart() => Autostart = false;
    public void RegisterApp(string executable, string installLocation, string version) => Registered = true;
    public void UnregisterApp() => Registered = false;

    public IReadOnlyList<string> Leftovers()
        => [.. Autostart ? new[] { "autostart" } : [], .. Registered ? new[] { "app" } : []];
}
