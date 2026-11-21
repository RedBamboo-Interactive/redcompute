using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;

namespace RedCompute.App.Tests;

/// <summary>
/// A Phase 4 node for the server tests (P6): the Phase 3 answers of <see cref="FakeNodeHandler"/>, the <c>runtime.v1</c>
/// methods over a scripted runtime list (recording every call with its exact parameters), and the real
/// <see cref="ForwardHost"/> over an in-memory runtime (<see cref="MemoryRuntime"/>).
/// </summary>
internal sealed class RuntimeNodeHandler : LinkHandler
{
    public static IReadOnlyList<string> Phase4Features { get; } =
        [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho,
         LinkFeatures.RuntimeV1, LinkFeatures.ForwardHttpV1, LinkFeatures.ForwardWsV1];

    private readonly FakeNodeHandler _phase3 = new();

    public RuntimeNodeHandler()
    {
        Forward = new ForwardHost(Runtime);
    }

    /// <summary>What <c>runtime.list</c> answers, in the node's wire shape.</summary>
    public List<JsonObject> Runtimes { get; } = [ComfyUi()];

    /// <summary>Every <c>runtime.*</c> call: its method and its parameters as sent.</summary>
    public ConcurrentQueue<(string Method, string Parameters)> Calls { get; } = new();

    public MemoryRuntime Runtime { get; } = new();
    public ForwardHost Forward { get; }

    /// <summary>What <c>runtime.start</c> answers (the runtime then reports on its own).</summary>
    public string StartAnswer { get; set; } = "starting";

    public static JsonObject ComfyUi(string id = "comfyui-1", string managed = "spawn", string status = "stopped", bool enabled = true,
        int port = 8188, string accelerator = "cuda:0") => new()
    {
        ["id"] = id,
        ["kind"] = "comfyui",
        ["label"] = "ComfyUI on the lab box",
        ["managed"] = managed,
        ["enabled"] = enabled,
        ["status"] = status,
        ["statusDetail"] = null,
        ["version"] = "0.3.40",
        ["accelerator"] = accelerator,
        // A node may claim more than its kind serves: the server keeps only what its own table says.
        ["capabilities"] = new JsonArray("image-gen", "ai-session"),
        ["ports"] = new JsonArray(port),
        ["startedAt"] = null,
    };

    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
        => ForwardHost.IsForwardClass(info.Class) ? Forward.Accept(session, info) : _phase3.AcceptChannel(session, info);

    public override ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        if (!method.StartsWith("runtime.", StringComparison.Ordinal)) return _phase3.OnRequestAsync(session, method, parameters, ct);
        Calls.Enqueue((method, parameters.GetRawText()));
        string Id() => parameters.GetProperty("id").GetString()!;
        JsonObject Find(string id) => Runtimes.FirstOrDefault(r => (string?)r["id"] == id)
                                      ?? throw new LinkRpcException("runtime_unknown", $"No runtime '{id}' is registered on this node.");
        JsonNode answer = method switch
        {
            "runtime.list" => new JsonArray(Runtimes.Select(r => (JsonNode)r.DeepClone()).ToArray()),
            "runtime.start" => Find(Id()) switch
            {
                var r when (string?)r["managed"] == "attach" => throw new LinkRpcException("runtime_not_managed", "Its owner runs it."),
                var r when (bool?)r["enabled"] == false => throw new LinkRpcException("runtime_disabled", "Its owner disabled it."),
                _ => new JsonObject { ["status"] = StartAnswer },
            },
            "runtime.stop" => Find(Id()) is var stopped && (string?)stopped["managed"] == "attach"
                ? throw new LinkRpcException("runtime_not_managed", "Its owner runs it.")
                : new JsonObject { ["status"] = "stopped" },
            _ => throw new LinkRpcException(LinkErrorCodes.MethodNotAllowed, $"This node does not do '{method}'."),
        };
        return ValueTask.FromResult<JsonNode?>(answer);
    }
}

/// <summary>
/// The node's runtime, in memory: <c>GET /blob/{size}</c> streams seeded bytes, <c>POST /echo-hash</c> answers the
/// SHA-256 of what it got, <c>/ws</c> echoes every message. Records each OPEN it was asked to decide.
/// </summary>
internal sealed class MemoryRuntime : IForwardTarget
{
    public const string Id = "comfyui-1";

    public ConcurrentQueue<ForwardOpen> Opens { get; } = new();

    public static byte[] Blob(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    public ForwardDecision Decide(ForwardOpen open)
    {
        Opens.Enqueue(open);
        return open.Runtime == Id
            ? ForwardDecision.Accept(64L * 1024 * 1024)
            : ForwardDecision.Refuse(ForwardErrorCodes.RuntimeUnknown, $"No runtime {open.Runtime}.");
    }

    public async Task<ForwardUpstreamResponse> ExchangeAsync(ForwardOpen open, ForwardDecision decision, Stream requestBody, CancellationToken ct)
    {
        var parts = open.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["blob", var size] when open.Method == "GET":
                return new ForwardUpstreamResponse(200, [new("content-type", "application/octet-stream")], new MemoryStream(Blob(int.Parse(size))));
            case ["echo-hash"] when open.Method == "POST":
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await requestBody.ReadAsync(buffer, ct)) > 0) sha.AppendData(buffer, 0, read);
                return new ForwardUpstreamResponse(200, [new("content-type", "text/plain")],
                    new MemoryStream(Encoding.ASCII.GetBytes(Convert.ToHexString(sha.GetHashAndReset()))));
            }
            default:
                return new ForwardUpstreamResponse(404, [], new MemoryStream());
        }
    }

    public async Task<WebSocket> ConnectAsync(ForwardOpen open, ForwardDecision decision, CancellationToken ct)
    {
        var (client, server) = await SocketPair.CreateAsync(ct);
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1 << 20];
            try
            {
                while (true)
                {
                    var result = await server.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    await server.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
                }
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException)
            {
            }
        });
        return client;
    }

    /// <summary>Two connected WebSockets over a loopback TCP pair.</summary>
    private static class SocketPair
    {
        public static async Task<(WebSocket Client, WebSocket Server)> CreateAsync(CancellationToken ct)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync(ct).AsTask();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await accept;
            return (WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, TimeSpan.FromSeconds(30)),
                WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, TimeSpan.FromSeconds(30)));
        }
    }
}
