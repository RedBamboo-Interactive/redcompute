using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Extensions;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.Node;
using RedCompute.Node.Control;
using RedCompute.Node.Credentials;
using RedCompute.Node.Runtimes;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

// The RedCompute side of the kernel's end-to-end node tests, in one process:
//
//   RedCompute    NodeHub (/nodes/hub), the node endpoints (/nodes, /nodes/{id}/revoked, /nodes/changed),
//                 /nodes/link-key and the kernel sync, wired as RelayServer.MapNodes wires them, behind AppHost's
//                 JWT auth with the kernel's signing key, on 127.0.0.1:<port>.
//   the node      the leaf-node agent, run and driven through its own commands (pair, run, runtime add|start,
//                 status, unpair) with an isolated LEAF_NODE_HOME, file keys and one spawned runtime that is a
//                 sleeping copy of this process (so "its runtimes stopped" is a real process that exits).
//
//   RedCompute.KernelLinkHarness --work <dir> --kernel http://127.0.0.1:<kernel port>
//   env LEAF_HARNESS_SIGNING_KEY   the kernel test host's JWT signing key (a test key, never a real one)
//
// It prints {"event":"listening","port":N} once, then answers one JSON line per command read from stdin:
//   {"cmd":"pair","server":"http://...","code":"XXXX-XXXX"}  {"cmd":"run"}  {"cmd":"runtime-add"}
//   {"cmd":"runtime-start"}  {"cmd":"status"}  {"cmd":"status-text"}  {"cmd":"log"}  {"cmd":"unpair"}  {"cmd":"quit"}
// Everything it logs goes to <work>/harness.log, never to stdout.

if (args is ["--sleep", var seconds])
{
    Thread.Sleep(TimeSpan.FromSeconds(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
    return 0;
}

var work = Arg("--work") ?? throw new ArgumentException("--work <dir>");
var kernel = Arg("--kernel") ?? throw new ArgumentException("--kernel <url>");
var signingKey = Environment.GetEnvironmentVariable("LEAF_HARNESS_SIGNING_KEY")
                 ?? throw new ArgumentException("LEAF_HARNESS_SIGNING_KEY");
Directory.CreateDirectory(work);
var logFile = new StreamWriter(Path.Combine(work, "harness.log"), append: true) { AutoFlush = true };
var logGate = new object();
void Log(string message)
{
    lock (logGate) logFile.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {message}");
}

// ---- the node's paths first: leaf-node's Program sets the data root to its own compute folder before anything reads it.
var nodeHome = Path.Combine(work, "node");
Environment.SetEnvironmentVariable(NodePaths.HomeVariable, nodeHome);
var bootstrap = new LocalExecutionHost("leaf-node");
var paths = NodePaths.Resolve(bootstrap.Platform.GetEnvironmentVariable, bootstrap.Platform.GetFolderPath, OperatingSystem.IsWindows());
Environment.SetEnvironmentVariable(RedCompute.Core.Storage.ComputeStorage.DataRootVariable, paths.Compute);

// ---- RedCompute: NodeHub, the node endpoints and the kernel sync (RelayServer.MapNodes), its stores in their own folder.
var server = Path.Combine(work, "redcompute");
var jwtOptions = new JwtOptions { SigningKey = signingKey };
var jwt = new JwtService(jwtOptions);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
builder.Services.AddAppHostAuth(new AuthOptions { Jwt = jwtOptions, Mode = AuthMode.LocalDefault });
var app = builder.Build();
app.UseWebSockets();
app.UseAppHostJwtAuth();
var zero = new NodeIdentityResult(new NodeIdentity("node_01JCZ0000000000000000000AB", DateTimeOffset.UtcNow.AddDays(-1),
    "node_01JCZ0000000000000000000AB"), true, false, null);
var nodes = new NodeRegistry(zero, new NodePlatform(OperatingSystem.IsWindows() ? "windows" : "linux", "harness", "x64",
    "HARNESS", "Harness CPU"), "0.0.0-harness", DateTimeOffset.UtcNow, remoteCacheDirectory: Path.Combine(server, "nodes"),
    log: m => Log("[registry] " + m), trustDirectory: Path.Combine(server, "node-trust"));
var sync = new KernelNodeSync(nodes, new KernelNodeClient(kernel, jwt), m => Log("[sync] " + m));
var linkKeys = new NodeLinkKeyStore(Path.Combine(server, "node-link-key"), m => Log("[link-key] " + m));
var hub = new NodeHub(nodes, m => Log("[NodeHub] " + m), new NodeHubOptions { Resync = sync.RefreshAsync, LinkKeys = linkKeys });
NodeEndpoints.Map(new EndpointRegistry(app), nodes, new CapabilityRegistry(), () => null, hub, sync);
NodeHub.MapRoute(app, hub);
NodeHub.MapLinkKeyRoute(app, linkKeys);
await app.StartAsync();
sync.Start();
var port = new Uri(app.Urls.First()).Port;

// ---- the node: leaf-node's own environment, with file keys and a runtime that is this process sleeping.
var nodeLog = new NodeLog(paths.LogFile);
var env = new NodeEnvironment
{
    Paths = paths,
    Host = new LocalExecutionHost("leaf-node"),
    KeyStores = new FileKeyStores(paths),
    Log = nodeLog,
    RuntimeSpec = _ => new HostSpawnSpec
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = ["--sleep", "600"],
        Containment = ContainmentMode.None,
        OwnerLabel = "comfyui",
    },
    RuntimeProbe = (_, _) => Task.FromResult(true),
    RuntimeProbeInterval = TimeSpan.FromMilliseconds(200),
};
const string RuntimeId = "comfy-e2e";
var runtimePids = new HashSet<int>();
using var stop = new CancellationTokenSource();
Task<int>? agent = null;

var stdout = Console.Out;
void Say(JsonObject message)
{
    lock (logGate) stdout.WriteLine(message.ToJsonString());
    stdout.Flush();
}

Say(new JsonObject { ["event"] = "listening", ["port"] = port });

while (await Console.In.ReadLineAsync() is { } line)
{
    JsonObject answer;
    try
    {
        var command = JsonNode.Parse(line)!.AsObject();
        answer = await RunAsync(command["cmd"]!.GetValue<string>(), command);
    }
    catch (Exception error)
    {
        answer = new JsonObject { ["error"] = error.GetType().Name + ": " + error.Message };
    }
    Say(answer);
    if (answer["quit"] is not null) break;
}

stop.Cancel();
if (agent is not null) await agent.WaitAsync(TimeSpan.FromSeconds(20)).ContinueWith(_ => { });
await sync.DisposeAsync();
await hub.DisposeAsync();
await app.StopAsync();
return 0;

async Task<JsonObject> RunAsync(string cmd, JsonObject command)
{
    switch (cmd)
    {
        case "pair":
            return await CommandAsync(["pair", "--server", command["server"]!.GetValue<string>(), "--code", command["code"]!.GetValue<string>(),
                "--name", "harness-node"]);
        case "run":
            agent = Task.Run(() => new NodeCommands(env, TextWriter.Null).RunAsync(["run"], stop.Token));
            return new JsonObject { ["ok"] = true };
        case "runtime-add":
        {
            var comfy = Path.Combine(work, "ComfyUI");
            Directory.CreateDirectory(comfy);
            File.WriteAllText(Path.Combine(comfy, "main.py"), "# a stand-in: the harness spawns itself, sleeping\n");
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var free = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return await CommandAsync(["runtime", "add", "comfyui", "--id", RuntimeId, "--path", comfy, "--port", free.ToString(),
                "--accept-graphs-run-installed-nodes"]);
        }
        case "runtime-start":
            return await CommandAsync(["runtime", "start", RuntimeId]);
        case "unpair":
            return await CommandAsync(["unpair"]);
        case "status":
            return await StatusAsync();
        case "status-text":
            return await CommandAsync(["status"]);
        case "log":
        {
            // The agent's own log, read while it keeps writing.
            using var stream = new FileStream(paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new JsonObject { ["log"] = await new StreamReader(stream).ReadToEndAsync() };
        }
        case "quit":
            return new JsonObject { ["quit"] = true };
        default:
            throw new ArgumentException($"unknown command {cmd}");
    }
}

async Task<JsonObject> CommandAsync(string[] verb)
{
    var output = new StringWriter();
    var exit = await new NodeCommands(env, output).RunAsync(verb, stop.Token);
    return new JsonObject { ["exit"] = exit, ["output"] = output.ToString() };
}

async Task<JsonObject> StatusAsync()
{
    JsonNode? agentStatus = null;
    if (agent is { IsCompleted: false })
    {
        try
        {
            agentStatus = await ControlClient.SendAsync(paths, new JsonObject { ["verb"] = "status" }, ct: stop.Token);
        }
        catch (Exception error)
        {
            agentStatus = new JsonObject { ["error"] = error.Message };
        }
    }
    int? pid = null;
    var state = Path.Combine(paths.RuntimeState, RuntimeId + ".json");
    if (File.Exists(state))
    {
        try
        {
            pid = JsonNode.Parse(File.ReadAllText(state))!.AsObject()
                .FirstOrDefault(p => p.Key.Equals("pid", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<int>();
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
        }
    }
    if (pid is { } seen) runtimePids.Add(seen);
    return new JsonObject
    {
        ["nodeJson"] = File.Exists(paths.StateFile),
        // node.json as the agent reads it (design 3.2): the pairing, or once it ended only how it ended.
        ["pairing"] = env.StateStore.Load().Pairing?.NodeId,
        ["ended"] = env.StateStore.Load().Ended?.Reason,
        ["keyFile"] = File.Exists(paths.KeyFile),
        ["linkKeyFile"] = File.Exists(paths.LinkKeyFile),
        ["agentRunning"] = agent is { IsCompleted: false },
        ["agent"] = agentStatus?.DeepClone(),
        ["runtimePid"] = pid,
        // Every runtime pid this harness has seen that is still alive: empty once the agent stopped its runtime.
        ["runtimeAlive"] = new JsonArray(runtimePids.Where(Alive).Select(id => (JsonNode)id).ToArray()),
    };
}

static bool Alive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

string? Arg(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

/// <summary>The node's keys in 0600 files under its isolated home (leaf-node's --allow-file-key store), never the OS stores.</summary>
internal sealed class FileKeyStores(NodePaths paths) : INodeKeyStores
{
    public INodeKeyStore ForNewKey(bool allowFileKey) => new FileKeyStore(paths);
    public INodeKeyStore ForKeyRef(string keyRef) => new FileKeyStore(paths);
}
