using System.Text.Json.Nodes;
using RedCompute.Node.Control;
using RedCompute.Node.Install;
using RedCompute.Node.Pairing;
using RedCompute.Node.Runtimes;
using RedCompute.PluginSdk.Hosting;

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
          leaf-node repin --code ABCD-EFGH   (the re-pin code the node's owner made in Leaf)
          leaf-node unpair
          leaf-node install [--keep-running-when-logged-out]
          leaf-node uninstall
          leaf-node run              (the background agent; started by install)
          leaf-node stop | kill-all | logs [--lines N] | version

        Runtimes the server may start, stop and reach (only you add them, on this machine):
          leaf-node runtime add comfyui --path <ComfyUI folder> [--venv <venv>] [--wsl <distro>] [--port 8188]
                [--gpu cuda:0|cpu] [--models-dir <folder>] [--extra-args="<args>"] [--id <id>] [--label <label>]
                --accept-graphs-run-installed-nodes
          leaf-node runtime add comfyui --attach http://127.0.0.1:8188 [--id <id>] [--label <label>] [--gpu …]
                --accept-graphs-run-installed-nodes
          leaf-node runtime list | test <id> | enable <id> | disable <id> | remove <id> | start <id> | stop <id>
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? NodeExitCodes.Usage : NodeExitCodes.Ok;
        }
        var verb = args[0];
        var runtimeVerb = verb == "runtime";
        Dictionary<string, string?> options = [];
        try
        {
            if (!runtimeVerb) options = ParseOptions(args.Skip(1).ToArray());
            if (verb is not ("version" or "--version") && !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
                throw new NodeCommandException("root", "leaf-node runs as your own user, not as root. Run it without sudo.",
                    NodeExitCodes.Refused);
            return verb switch
            {
                "version" or "--version" => Print(AgentIdentity.Version),
                "run" => await RunAgentAsync(ct),
                "pair" => await PairAsync(options, ct),
                "unpair" => await UnpairAsync(ct),
                "repin" => await RepinAsync(options, ct),
                "status" => await StatusAsync(options, ct),
                "stop" => await SimpleAsync("stop", "Stopped.", ct),
                "kill-all" => await KillAllAsync(ct),
                "runtime" => await RuntimeAsync(args.Skip(1).ToArray(), ct),
                "logs" => await LogsAsync(options, ct),
                "install" => await InstallAsync(options, ct),
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
            {
                var (channels, runtimes) = await agent.KillAllAsync();
                return new JsonObject { ["ok"] = true, ["killed"] = channels, ["runtimes"] = runtimes };
            }
            case "runtimes":
                await agent.Runtimes.ReloadAsync();
                return new JsonObject { ["ok"] = true };
            case "runtime-start":
            case "runtime-stop":
            case "runtime-test":
            {
                var id = request["id"]?.GetValue<string>() ?? "";
                try
                {
                    if (verb == "runtime-test")
                    {
                        var seconds = request["timeoutSeconds"]?.GetValue<int>() ?? 120;
                        var (passed, tested) = await agent.Runtimes.TestAsync(id, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 900)), ct);
                        return new JsonObject { ["ok"] = true, ["passed"] = passed, ["runtime"] = RuntimeManager.Json(tested) };
                    }
                    var runtime = verb == "runtime-start" ? await agent.Runtimes.StartAsync(id, ct) : await agent.Runtimes.StopAsync(id, null, ct);
                    return new JsonObject { ["ok"] = true, ["runtime"] = RuntimeManager.Json(runtime) };
                }
                catch (HostRuntimeException error)
                {
                    return new JsonObject { ["ok"] = false, ["error"] = error.Code, ["message"] = error.Message };
                }
            }
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
        output.WriteLine(pairing.LinkPin is { } pinned
            ? $"Server link key {pinned.Fingerprint}: the link is end-to-end encrypted. Leaf shows the same fingerprint in Add a node."
            : "This server does not offer encrypted node links yet; the link is not end-to-end encrypted.");

        output.WriteLine($"Paired as {pairing.NodeId} — fingerprint {pairing.Fingerprint}. Confirm it in Leaf.");
        if (pairing.CredentialStore == "file") output.WriteLine($"The node key is kept in {env.Paths.KeyFile} (readable only by you).");
        var reloaded = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "reload" }, ct: ct);
        if (reloaded is null)
            output.WriteLine("The agent is not running yet: it connects when it starts (leaf-node install starts it at logon).");
        return NodeExitCodes.Ok;
    }

    /// <summary>
    /// <c>leaf-node repin --code</c> (design 1b.7): the owner's single-use re-pin code buys the server's current link
    /// key, authenticated by this node's existing identity key; the node pins it with a new link key, confirms it
    /// with a signed proof, and the running agent reconnects encrypted. The node id stays the same.
    /// </summary>
    private async Task<int> RepinAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        var code = PairingCode.Normalize(Require(options, "code"));
        var state = env.StateStore.Load();
        var pairing = state.Pairing ?? throw new NodeCommandException("not_paired", "This node is not paired. Pair it first: leaf-node pair.");
        var store = env.KeyStores.ForKeyRef(pairing.KeyRef);
        using var identity = store.Open(pairing.KeyRef, pairing.ServerOrigin)
                             ?? throw new NodeCommandException("key_missing", "The node key is gone. Pair again: leaf-node unpair, then leaf-node pair.");
        using var server = env.ServerClient();
        var now = env.Time.GetUtcNow();
        var info = await server.RepinAsync(pairing.ServerOrigin, pairing.NodeId, identity, code, now, ct);
        if (pairing.LinkPin is { } old && old.Fingerprint == info.Fingerprint)
            env.Log.Info($"Re-pin: the server's link key is still {info.Fingerprint}.");
        store.CreateLinkKey(pairing.KeyRef, pairing.ServerOrigin, pairing.NodeId).Dispose();
        await server.PinAsync(pairing.ServerOrigin, pairing.NodeId, identity, code, info, now, ct);
        env.StateStore.Save(state with { Pairing = pairing with { LinkPin = new NodeLinkPin(info.PublicKey, info.Fingerprint, info.KeyId, now, info.Protocol) } });
        env.Audit.Record("local", "repin", info.Fingerprint, pairing.NodeId);
        env.Log.Info($"Pinned the server link key {info.Fingerprint} ({info.KeyId}).");
        output.WriteLine($"Pinned the server's link key {info.Fingerprint}. The link is end-to-end encrypted from now on.");
        if (await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "reload" }, ct: ct) is null)
            output.WriteLine("The agent is not running: it connects encrypted when it starts.");
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
            lines.Add(status["linkEncryption"]?.GetValue<string>() == "on"
                ? $"Link encryption: on (server link key {status["serverLinkFingerprint"]})."
                : "Link encryption: off. This node pinned no server link key; ask its owner for a re-pin code: leaf-node repin --code <code>.");
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
        {
            lines.Add($"Link: {link["status"]}{(link["reason"] is { } reason ? $" ({reason})" : "")}.");
            if (link["status"]?.GetValue<string>() == "needs-repin") lines.Add(NodeAgent.RepinAdvice(link["reason"]?.GetValue<string>()));
            if (link["status"]?.GetValue<string>() == "revoked-unverified") lines.Add(NodeAgent.UnverifiedAdvice(link["reason"]?.GetValue<string>()));
        }
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
        output.WriteLine($"Stopped everything the server started here ({answer["killed"]} channel(s), {answer["runtimes"]} runtime(s)).");
        return NodeExitCodes.Ok;
    }

    private Task<int> LogsAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        var lines = options.TryGetValue("lines", out var text) && int.TryParse(text, out var count) && count > 0 ? count : 50;
        foreach (var line in new NodeLog(env.Paths.LogFile).Tail(lines)) output.WriteLine(line);
        return Task.FromResult(NodeExitCodes.Ok);
    }

    // ---- runtimes ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>leaf-node runtime …</c> (Phase 4 design 2.4): the owner registers, lists, enables, disables, removes, starts
    /// and stops the runtimes of this machine. The server can only start, stop and reach what is registered here.
    /// </summary>
    private async Task<int> RuntimeAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0) throw new NodeCommandException("usage", Usage, NodeExitCodes.Usage);
        var sub = args[0];
        var positional = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var options = ParseOptions(args.Skip(positional is null ? 1 : 2).ToArray());
        var registry = new RuntimeRegistry(env.Host, env.Paths);
        switch (sub)
        {
            case "add":
            {
                var kind = positional ?? throw new NodeCommandException("usage", "Which kind? leaf-node runtime add comfyui …", NodeExitCodes.Usage);
                var runtime = registry.Add(RuntimeToAdd(kind, options, registry));
                var attach = runtime.Managed == HostRuntimeManagement.Attach;
                env.Audit.Record("local", "runtime-add", runtime.Id, $"{runtime.Kind} {(attach ? "attach" : "spawn")} port {runtime.Port}");
                output.WriteLine($"Registered {runtime.Kind} runtime '{runtime.Id}' at 127.0.0.1:{runtime.Port}.");
                output.WriteLine(attach
                    ? "You run it; the server can reach it on the ComfyUI allowlist (no Manager, install or user-data routes), never start or stop it."
                    : "The server can start and stop it, as you, with what you set here (never a program, argument or folder of its own), "
                      + "and reach it on the ComfyUI allowlist (no Manager, install or user-data routes).");
                output.WriteLine("A workflow runs every node type installed in it. leaf-node runtime disable "
                    + runtime.Id + " stops the server using it; kill-all, a revoke and an uninstall stop what leaf-node started.");
                await NotifyAgentAsync(ct);
                return NodeExitCodes.Ok;
            }
            case "test":
            {
                var id = positional ?? throw new NodeCommandException("usage", "Which runtime? leaf-node runtime test <id>", NodeExitCodes.Usage);
                var seconds = int.TryParse(options.GetValueOrDefault("timeout"), out var t) ? t : 120;
                var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "runtime-test", ["id"] = id, ["timeoutSeconds"] = seconds },
                    TimeSpan.FromSeconds(seconds + 30), ct);
                bool passed;
                string? status, detail;
                if (answer is null)
                {
                    // No agent: test here, with what an earlier agent may have left adopted.
                    await using var runtimes = new RuntimeManager(env, env.RuntimeSpec, env.RuntimeProbe);
                    runtimes.Load();
                    try
                    {
                        var (ok, runtime) = await runtimes.TestAsync(id, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 900)), ct);
                        (passed, status, detail) = (ok, runtime.Status.ToString().ToLowerInvariant(), runtime.StatusDetail);
                    }
                    catch (HostRuntimeException error)
                    {
                        throw new NodeCommandException(error.Code, error.Message);
                    }
                }
                else
                {
                    ThrowIfFailed(answer);
                    passed = answer["passed"]?.GetValue<bool>() == true;
                    status = answer["runtime"]?["status"]?.GetValue<string>();
                    detail = answer["runtime"]?["statusDetail"]?.GetValue<string>();
                }
                output.WriteLine(passed
                    ? $"'{id}' answered GET /system_stats."
                    : $"'{id}' did not answer GET /system_stats ({status}{(detail is null ? "" : ": " + detail)}). leaf-node logs and its log file say more.");
                return passed ? NodeExitCodes.Ok : NodeExitCodes.Failed;
            }
            case "list":
            {
                var status = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "status" }, ct: ct);
                var live = (status?["runtimes"] as JsonArray)?.OfType<JsonObject>().ToDictionary(r => r["id"]!.GetValue<string>()) ?? [];
                var all = registry.Load();
                if (all.Count == 0) output.WriteLine("No runtimes registered. Add one: leaf-node runtime add comfyui …");
                foreach (var runtime in all)
                {
                    var state = live.TryGetValue(runtime.Id, out var entry) ? entry["status"]?.GetValue<string>() : status is null ? "agent not running" : "?";
                    output.WriteLine($"{runtime.Id}  {runtime.Kind}  {(runtime.Managed == HostRuntimeManagement.Spawn ? "spawn" : "attach")}  "
                        + $"127.0.0.1:{runtime.Port}  {(runtime.Enabled ? "enabled" : "disabled")}  {state}");
                }
                return NodeExitCodes.Ok;
            }
            case "remove" or "enable" or "disable":
            {
                var id = positional ?? throw new NodeCommandException("usage", $"Which runtime? leaf-node runtime {sub} <id>", NodeExitCodes.Usage);
                if (sub == "remove")
                {
                    if (!registry.Remove(id)) throw new NodeCommandException("runtime_unknown", $"No runtime '{id}' is registered here.");
                }
                else
                {
                    registry.SetEnabled(id, sub == "enable");
                }
                env.Audit.Record("local", "runtime-" + sub, id, null);
                output.WriteLine(sub switch
                {
                    "remove" => $"Removed '{id}'. If leaf-node had started it, it is stopped.",
                    "enable" => $"Enabled '{id}'.",
                    _ => $"Disabled '{id}': the server can no longer start or reach it. If leaf-node had started it, it is stopped.",
                });
                await NotifyAgentAsync(ct);
                return NodeExitCodes.Ok;
            }
            case "start" or "stop":
            {
                var id = positional ?? throw new NodeCommandException("usage", $"Which runtime? leaf-node runtime {sub} <id>", NodeExitCodes.Usage);
                var answer = await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "runtime-" + sub, ["id"] = id }, ct: ct)
                             ?? throw new NodeCommandException("not_running", "leaf-node is not running.", NodeExitCodes.NotRunning);
                ThrowIfFailed(answer);
                output.WriteLine($"'{id}' is {answer["runtime"]?["status"]}.");
                return NodeExitCodes.Ok;
            }
            default:
                throw new NodeCommandException("usage", $"Unknown runtime command '{sub}'.\n\n{Usage}", NodeExitCodes.Usage);
        }
    }

    /// <summary>
    /// The registration <c>runtime add</c> asks for, checked here on the owner's machine (design 4): a spawned ComfyUI's
    /// folder holds <c>main.py</c> (inside WSL it is the distribution's path, checked when it starts) and its port is
    /// free; an attached one is a loopback URL. The design's flag names, and the earlier ones as aliases.
    /// </summary>
    private RuntimeRegistration RuntimeToAdd(string kind, IReadOnlyDictionary<string, string?> options, RuntimeRegistry registry)
    {
        string? Option(params string[] names) => names.Select(options.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var attachUrl = options.TryGetValue("attach", out var url) ? url : null;
        var attach = options.ContainsKey("attach");
        int port;
        if (attach && attachUrl is not null)
        {
            if (!Uri.TryCreate(attachUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http"
                || !(uri.Host is "127.0.0.1" or "localhost" or "[::1]") || uri.AbsolutePath != "/" || uri.Query.Length > 0)
                throw new NodeCommandException("usage", "--attach takes this machine's runtime, http://127.0.0.1:<port>.", NodeExitCodes.Usage);
            port = uri.Port;
        }
        else if (Option("port") is { } text)
        {
            if (!int.TryParse(text, out port)) throw new NodeCommandException("usage", "--port is a number.", NodeExitCodes.Usage);
        }
        else
        {
            port = 8188;
        }
        var wsl = Option("wsl", "wsl-distro");
        var path = Option("path", "server-path");
        if (!attach && path is not null && wsl is null)
        {
            if (!env.Host.Files.FileExists(env.Host.Path(Path.Combine(path, "main.py"))))
                throw new NodeCommandException("bad_runtime", $"{path} has no main.py: --path is the ComfyUI folder.", NodeExitCodes.Usage);
            if (!PortFree(port))
                throw new NodeCommandException("port_taken", $"Something already listens on 127.0.0.1:{port}. Pick another --port, or --attach to it.",
                    NodeExitCodes.Usage);
        }
        var existing = registry.Load();
        var id = Option("id") ?? Enumerable.Range(1, 99).Select(n => $"{kind}-{n}").First(candidate => existing.All(r => r.Id != candidate));
        return new RuntimeRegistration
        {
            Id = id,
            Kind = kind,
            Label = Option("label"),
            Managed = attach ? HostRuntimeManagement.Attach : HostRuntimeManagement.Spawn,
            Port = port,
            Accelerator = Option("gpu", "accelerator") ?? "cpu",
            ServerPath = attach ? null : path,
            VenvPath = attach ? null : Option("venv"),
            WslDistro = attach ? null : wsl,
            ServerArgs = attach ? null : Option("extra-args", "server-args"),
            ModelsDirectory = attach ? null : Option("models-dir"),
            AcceptedGraphsRunInstalledNodes = options.ContainsKey("accept-graphs-run-installed-nodes"),
            AddedAt = env.Time.GetUtcNow(),
        };
    }

    private static bool PortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private async Task NotifyAgentAsync(CancellationToken ct)
    {
        if (await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "runtimes" }, ct: ct) is null)
            output.WriteLine("The agent is not running: it reads the change when it starts.");
    }

    // ---- install / uninstall -----------------------------------------------------------------------

    private async Task<int> InstallAsync(IReadOnlyDictionary<string, string?> options, CancellationToken ct)
    {
        // An upgrade (or a reinstall): the running agent stops first, so the new one can take the lock
        // and the version folder it runs from can be replaced.
        if (await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "stop" }, ct: ct) is not null)
        {
            output.WriteLine("Stopping the running leaf-node...");
            await WaitForStopAsync(ct);
        }
        var installer = new NodeInstaller(env.Paths, env.Host, Platform(), env.Log);
        var executable = installer.Install(AppContext.BaseDirectory, AgentIdentity.Version, options.ContainsKey("keep-running-when-logged-out"));
        output.WriteLine($"Installed leaf-node {AgentIdentity.Version} ({executable}); it starts at logon and is running now.");
        if (env.StateStore.Load().Pairing is null)
            output.WriteLine("Next: leaf-node pair --server https://<leaf address> --code <code from Leaf>");
        return NodeExitCodes.Ok;
    }

    private async Task<int> UninstallAsync(CancellationToken ct)
    {
        // Stop the runtimes the agent started and the agent (it may hold the key open), then unpair and delete the
        // key, then remove the rest. Without a running agent, what an earlier one started is adopted and stopped here.
        if (await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "kill-all" }, ct: ct) is not null)
        {
            await ControlClient.SendAsync(env.Paths, new JsonObject { ["verb"] = "stop" }, ct: ct);
            await WaitForStopAsync(ct);
        }
        else
        {
            await using var runtimes = new RuntimeManager(env, env.RuntimeSpec, env.RuntimeProbe);
            runtimes.Load();
            var stopped = await runtimes.StopAllManagedAsync("uninstall");
            if (stopped > 0) output.WriteLine($"Stopped {stopped} runtime(s) leaf-node had started.");
        }
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

    /// <summary>
    /// The user's real registrations, except for an agent that LEAF_NODE_HOME isolates: it keeps them
    /// in its own home and never touches the Run value, the Uninstall key or the systemd unit.
    /// </summary>
    internal INodeInstallPlatform Platform()
        => installPlatform?.Invoke()
           ?? (env.Paths.IsolatedHome is not null
               ? new IsolatedInstallPlatform(env.Paths, env.Host)
               : OperatingSystem.IsWindows()
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
