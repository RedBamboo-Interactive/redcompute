using System.Text.Json.Nodes;
using RedCompute.Node.Control;
using RedCompute.Node.Install;
using RedCompute.Node.Pairing;

namespace RedCompute.Node;

/// <summary>
/// The <c>leaf-node</c> verbs (design 2.2, 2.3, 3.2, 3.5): <c>run</c>, <c>pair</c>, <c>unpair</c>,
/// <c>status</c>, <c>stop</c>, <c>kill-all</c>, <c>logs</c>, <c>install</c>, <c>uninstall</c>, <c>version</c>.
/// Verbs that act on the running agent go over the control channel; <c>pair</c>, <c>unpair</c> and
/// <c>status</c> also work while no agent runs.
/// </summary>
public sealed class NodeCommands(NodeEnvironment env, TextWriter output, Func<INodeInstallPlatform>? installPlatform = null)
{
    public const string Usage = """
        leaf-node: the Leaf node agent

          leaf-node pair --server https://<leaf address> --code ABCD-EFGH [--name <name>] [--allow-file-key]
          leaf-node status
          leaf-node unpair
          leaf-node install [--keep-running-when-logged-out]
          leaf-node uninstall
          leaf-node run              (the background agent; started by install)
          leaf-node stop | kill-all | logs [--lines N] | version
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? NodeExitCodes.Usage : NodeExitCodes.Ok;
        }
        var verb = args[0];
        var options = ParseOptions(args.Skip(1).ToArray());
        try
        {
            if (verb is not ("version" or "--version") && !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
                throw new NodeCommandException("root", "leaf-node runs as your own user, not as root. Run it without sudo.",
                    NodeExitCodes.Refused);
            return verb switch
            {
                "version" or "--version" => Print(AgentIdentity.Version),
                "run" => await RunAgentAsync(ct),
                "pair" => await PairAsync(options, ct),
                "unpair" => await UnpairAsync(ct),
                "status" => await StatusAsync(options, ct),
                "stop" => await SimpleAsync("stop", "Stopped.", ct),
                "kill-all" => await KillAllAsync(ct),
                "logs" => await LogsAsync(options, ct),
                "install" => Install(options),
                "uninstall" => await UninstallAsync(ct),
                "update" => throw new NodeCommandException("not_available",
                    "Updates are installed by downloading the new version for now (leaf-node update comes later)."),
                _ => throw new NodeCommandException("usage", $"Unknown command '{verb}'.\n\n{Usage}", NodeExitCodes.Usage),
            };
        }
        catch (NodeCommandException error)
        {
            output.WriteLine(error.Message);
            return error.ExitCode;
        }
    }

    // ---- run ----------------------------------------------------------------------------------------

    private async Task<int> RunAgentAsync(CancellationToken ct)
    {
        using var instance = InstanceLock.TryAcquire(env.Paths);
        if (instance is null)
            throw new NodeCommandException("already_running", "leaf-node is already running for this user.", NodeExitCodes.AlreadyRunning);
        await using var agent = new NodeAgent(env);
        // Stopping (the stop verb, SIGTERM, Ctrl+C) says link.bye first, then ends the link.
        using var stop = new CancellationTokenSource();
        var stopping = 0;
        async Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopping, 1) == 1) return;
            await agent.SayByeAsync("stop");
            stop.Cancel();
        }
        using var onSignal = ct.Register(() => _ = StopAsync());
        await using var control = new ControlServer(env.Paths, (request, token) => HandleControlAsync(agent, request, StopAsync, token), env.Log);
        control.Start();
        await agent.RunAsync(stop.Token);
        env.Log.Info("leaf-node stopped.");
        return NodeExitCodes.Ok;
    }

    /// <summary>The running agent's side of the control channel.</summary>
    internal static async Task<JsonObject> HandleControlAsync(NodeAgent agent, JsonObject request, Func<Task> stop,
        CancellationToken ct)
    {
        var verb = request["verb"]?.GetValue<string>();
        switch (verb)
        {
            case "status":
                return agent.Status();
            case "reload":
                agent.Reload();
                return new JsonObject { ["ok"] = true };
            case "unpair":
                return await agent.UnpairAsync(ct);
            case "stop":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100, CancellationToken.None); // answer first
                    await stop();
                }, CancellationToken.None);
                return new JsonObject { ["ok"] = true };
            case "kill-all":
                return new JsonObject { ["ok"] = true, ["killed"] = agent.KillAll() };
            default:
                throw new NodeCommandException("unknown_verb", $"Unknown control request '{verb}'.");
        }
    }

    // ---- pair / unpair ------------------------------------------------------------------------------

    private async Task<int> PairAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        var origin = ServerOrigin.Normalize(Require(options, "server"));
        var code = PairingCode.Normalize(Require(options, "code"));
        var state = env.StateStore.Load();
        if (state.Pairing is { } existing)
            throw new NodeCommandException("already_paired",
                $"This node is already paired as {existing.NodeId} with {existing.ServerOrigin}. Run leaf-node unpair first.");

        var store = env.KeyStores.ForNewKey(options.ContainsKey("allow-file-key"));
        var info = env.Host.Platform.Info;
        var machine = new MachineDescription(
            options.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name) ? name! : info.MachineName,
            MachineIdentity.MachineIdHash(env.Host), AgentIdentity.Os, AgentIdentity.Arch, info.OsDescription, info.Chassis);
        using var server = env.ServerClient();
        var pairing = await server.PairAsync(origin, code, machine.Name, store, machine, AgentIdentity.Info, env.Time.GetUtcNow(), ct);
        env.StateStore.Save(new NodeState { Pairing = pairing });
        env.Audit.Record("local", "pair", "paired", pairing.NodeId);
        env.Log.Info($"Paired as {pairing.NodeId} with {origin} (key in {pairing.CredentialStore}).");

        output.WriteLine($"Paired as {pairing.NodeId} — fingerprint {pairing.Fingerprint}. Confirm it in Leaf.");
        if (pairing.CredentialStore == "file") output.WriteLine($"The node key is kept in {env.Paths.KeyFile} (readable only by you).");
        var reloaded = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "reload" }, ct: ct);
        if (reloaded is null)
            output.WriteLine("The agent is not running yet: it connects when it starts (leaf-node install starts it at logon).");
        return NodeExitCodes.Ok;
    }

    private async Task<int> UnpairAsync(CancellationToken ct)
    {
        var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "unpair" }, ct: ct);
        bool confirmed;
        string nodeId;
        if (answer is not null)
        {
            ThrowIfFailed(answer);
            confirmed = answer["serverConfirmed"]?.GetValue<bool>() == true;
            nodeId = answer["nodeId"]?.GetValue<string>() ?? "";
        }
        else
        {
            var pairing = env.StateStore.Load().Pairing
                          ?? throw new NodeCommandException("not_paired", "This node is not paired.");
            confirmed = await NodeLifecycle.ForgetAsync(env, pairing, "unpaired", callServer: true, ct);
            nodeId = pairing.NodeId;
        }
        output.WriteLine(confirmed
            ? $"Unpaired {nodeId}. Its key is deleted."
            : $"Unpaired {nodeId} here and deleted its key. The server could not be told: revoke the node in Leaf.");
        return NodeExitCodes.Ok;
    }

    // ---- status, stop, kill-all, logs ---------------------------------------------------------------

    private async Task<int> StatusAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "status" }, ct: ct);
        if (answer is null)
        {
            answer = NodeLifecycle.StateJson(env.StateStore.Load());
            answer["running"] = false;
        }
        if (options.ContainsKey("json"))
        {
            output.WriteLine(answer.ToJsonString(NodeStateStore.Json));
            return NodeExitCodes.Ok;
        }
        output.WriteLine(DescribeStatus(answer));
        return NodeExitCodes.Ok;
    }

    internal static string DescribeStatus(JsonObject status)
    {
        var lines = new List<string>();
        var running = status["running"]?.GetValue<bool>() == true;
        if (status["paired"]?.GetValue<bool>() == true)
        {
            lines.Add($"Paired as {status["nodeId"]} with {status["serverOrigin"]}.");
            lines.Add($"Key fingerprint {status["fingerprint"]} ({status["credentialStore"]}).");
        }
        else if (status["ended"] is JsonObject ended)
        {
            var at = DateTimeOffset.TryParse(ended["at"]?.GetValue<string>(), out var when) ? when.ToString("yyyy-MM-dd HH:mm") : "?";
            lines.Add(ended["reason"]?.GetValue<string>() switch
            {
                "revoked" => $"Revoked by the server on {at}. Pair again to reconnect.",
                "unknown" => $"The server no longer knows this node (since {at}). Pair again to reconnect.",
                _ => $"Unpaired on {at}. Pair again to reconnect.",
            });
        }
        else
        {
            lines.Add("Not paired. Pair with: leaf-node pair --server https://<leaf address> --code <code from Leaf>");
        }
        if (!running) lines.Add("The agent is not running.");
        else if (status["link"] is JsonObject link)
            lines.Add($"Link: {link["status"]}{(link["reason"] is { } reason ? $" ({reason})" : "")}.");
        lines.Add($"leaf-node {status["agent"]?["version"]}");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<int> SimpleAsync(string verb, string done, CancellationToken ct)
    {
        var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = verb }, ct: ct)
                     ?? throw new NodeCommandException("not_running", "leaf-node is not running.", NodeExitCodes.NotRunning);
        ThrowIfFailed(answer);
        output.WriteLine(done);
        return NodeExitCodes.Ok;
    }

    private async Task<int> KillAllAsync(CancellationToken ct)
    {
        var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "kill-all" }, ct: ct)
                     ?? throw new NodeCommandException("not_running", "leaf-node is not running.", NodeExitCodes.NotRunning);
        ThrowIfFailed(answer);
        output.WriteLine($"Stopped everything the server started here ({answer["killed"]} channel(s)).");
        return NodeExitCodes.Ok;
    }

    private Task<int> LogsAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        var lines = options.TryGetValue("lines", out var text) && int.TryParse(text, out var count) && count > 0 ? count : 50;
        foreach (var line in new NodeLog(env.Paths.LogFile).Tail(lines)) output.WriteLine(line);
        return Task.FromResult(NodeExitCodes.Ok);
    }

    // ---- install / uninstall -----------------------------------------------------------------------

    private int Install(IReadOnlyDictionary<string, string?> options)
    {
        var installer = new NodeInstaller(env.Paths, env.Host, Platform(), env.Log);
        var executable = installer.Install(AppContext.BaseDirectory, AgentIdentity.Version, options.ContainsKey("keep-running-when-logged-out"));
        output.WriteLine($"Installed leaf-node {AgentIdentity.Version} ({executable}); it starts at logon and is running now.");
        if (env.StateStore.Load().Pairing is null)
            output.WriteLine("Next: leaf-node pair --server https://<leaf address> --code <code from Leaf>");
        return NodeExitCodes.Ok;
    }

    private async Task<int> UninstallAsync(CancellationToken ct)
    {
        // Stop the agent (it may hold the key open), then unpair and delete the key, then remove the rest.
        if (await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "stop" }, ct: ct) is not null)
            await WaitForStopAsync(ct);
        if (env.StateStore.Load().Pairing is { } pairing)
        {
            var confirmed = await NodeLifecycle.ForgetAsync(env, pairing, "unpaired", callServer: true, ct);
            output.WriteLine(confirmed ? $"Unpaired {pairing.NodeId}." : $"Unpaired {pairing.NodeId} here; revoke it in Leaf too.");
        }
        env.Log.Dispose();
        var result = new NodeInstaller(env.Paths, env.Host, Platform(), NodeLog.None).Uninstall(AppContext.BaseDirectory);
        foreach (var removed in result.Removed) output.WriteLine($"Removed {removed}");
        foreach (var later in result.ScheduledAfterExit) output.WriteLine($"Removing {later} once leaf-node exits");
        if (result.Leftovers.Count > 0)
        {
            foreach (var left in result.Leftovers) output.WriteLine($"Still present: {left}");
            return NodeExitCodes.Failed;
        }
        output.WriteLine("leaf-node is uninstalled.");
        return NodeExitCodes.Ok;
    }

    private async Task WaitForStopAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            using var probe = InstanceLock.TryAcquire(env.Paths);
            if (probe is not null) return;
            await Task.Delay(100, ct);
        }
        throw new NodeCommandException("still_running", "The agent did not stop; try again.");
    }

    private INodeInstallPlatform Platform()
        => installPlatform?.Invoke()
           ?? (OperatingSystem.IsWindows()
               ? new WindowsInstallPlatform(env.Host)
               : new LinuxInstallPlatform(env.Paths, env.Host, env.Log));

    // ---- helpers -----------------------------------------------------------------------------------

    private int Print(string text)
    {
        output.WriteLine(text);
        return NodeExitCodes.Ok;
    }

    private static void ThrowIfFailed(JsonObject answer)
    {
        if (answer["ok"]?.GetValue<bool>() == true) return;
        throw new NodeCommandException(answer["error"]?.GetValue<string>() ?? "failed",
            answer["message"]?.GetValue<string>() ?? "The agent refused the request.");
    }

    private static string Require(IReadOnlyDictionary<string, string?> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value!
            : throw new NodeCommandException("usage", $"--{name} is required.\n\n{Usage}", NodeExitCodes.Usage);

    /// <summary><c>--name value</c>, <c>--name=value</c> and bare flags.</summary>
    internal static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new NodeCommandException("usage", $"Unexpected argument '{arg}'.\n\n{Usage}", NodeExitCodes.Usage);
            var name = arg[2..];
            string? value = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            options[name] = value;
        }
        return options;
    }
}
