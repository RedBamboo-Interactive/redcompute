using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Node;
using RedCompute.Node.Credentials;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// A stand-in ComfyUI on 127.0.0.1 (Kestrel) that does what the provider needs: <c>/system_stats</c>, <c>/prompt</c>
/// (the prompt then "runs" and finishes on the client's <c>/ws</c>), <c>/history/{id}</c>, <c>/view</c>,
/// <c>/upload/image</c> and <c>/queue</c>. It records every request as it arrived (method, target, headers, body),
/// and can stream <c>/view</c>, <c>/history</c> or <c>/queue</c> without end to test the server's caps.
/// </summary>
internal sealed class FakeComfyUi : IAsyncDisposable
{
    public sealed record Seen(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        /// <summary>Everything the runtime saw of this request, as text (for "never contains" checks).</summary>
        public string Text => $"{Method} {Target}\n{string.Join("\n", Headers.Select(h => h.Key + ": " + h.Value))}\n{Encoding.Latin1.GetString(Body)}";
    }

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new(StringComparer.Ordinal);
    public ConcurrentQueue<Seen> Requests { get; } = new();
    public ConcurrentQueue<JsonObject> Prompts { get; } = new();

    /// <summary>The prompt ids this ComfyUI assigned, in order.</summary>
    public ConcurrentQueue<string> PromptIds { get; } = new();
    public int Port { get; private set; }

    /// <summary>Stream this path's answer without end ("/view", "/history" or "/queue").</summary>
    public string? Endless { get; set; }

    /// <summary>Set once an endless answer stopped because the server went away.</summary>
    public TaskCompletionSource EndlessAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public byte[] Output { get; set; } = Encoding.ASCII.GetBytes("\x89PNG fake output");

    /// <summary>What <c>GET /queue</c> answers: ComfyUI's own shape, the owner's prompts and the server's.</summary>
    public string QueueJson { get; set; } = """{"queue_running":[],"queue_pending":[]}""";

    private FakeComfyUi(WebApplication app) => _app = app;

    public static async Task<FakeComfyUi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new FakeComfyUi(app);
        app.UseWebSockets();
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        fake.Port = new Uri(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return fake;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        var target = request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        if (ctx.WebSockets.IsWebSocketRequest)
        {
            Requests.Enqueue(new Seen("WS", target, headers, []));
            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            _sockets[request.Query["clientId"].ToString()] = socket;
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                    if ((await socket.ReceiveAsync(buffer, ctx.RequestAborted)).MessageType == WebSocketMessageType.Close) break;
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
            return;
        }

        var body = new MemoryStream();
        await request.Body.CopyToAsync(body);
        Requests.Enqueue(new Seen(request.Method, target, headers, body.ToArray()));
        var path = request.Path.Value!;

        if (Endless is { } endless && path.StartsWith(endless, StringComparison.Ordinal))
        {
            ctx.Response.ContentType = endless == "/view" ? "image/png" : "application/json";
            var chunk = new byte[64 * 1024];
            if (endless != "/view") chunk.AsSpan().Fill((byte)' ');
            try
            {
                if (endless != "/view") await ctx.Response.Body.WriteAsync("{\"x\":\""u8.ToArray());
                // Not a measure of what left: once the connection is gone Kestrel's writes complete without writing until
                // RequestAborted fires. What the node really forwarded is in its audit (ComfyNodeSide.Forwarded).
                while (!ctx.RequestAborted.IsCancellationRequested)
                    await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            EndlessAborted.TrySetResult();
            return;
        }

        switch (path)
        {
            case "/system_stats":
                await ctx.Response.WriteAsJsonAsync(new { system = new { comfyui_version = "0.3.40" } });
                return;
            case "/prompt":
            {
                var prompt = JsonNode.Parse(body.ToArray())!.AsObject();
                Prompts.Enqueue(prompt);
                var id = Guid.NewGuid().ToString();
                PromptIds.Enqueue(id);
                var clientId = (string?)prompt["client_id"] ?? "";
                await ctx.Response.WriteAsJsonAsync(new { prompt_id = id, number = 1, node_errors = new { } });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    if (!_sockets.TryGetValue(clientId, out var socket)) return;
                    foreach (var node in new string?[] { "9", null })
                    {
                        var message = JsonSerializer.SerializeToUtf8Bytes(new { type = "executing", data = new { node, prompt_id = id } });
                        await socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                });
                return;
            }
            case var history when history.StartsWith("/history/", StringComparison.Ordinal):
            {
                var id = history["/history/".Length..];
                await ctx.Response.WriteAsync("{\"" + id + "\":" + """{"outputs":{"9":{"images":[{"filename":"out_00001_.png","subfolder":"","type":"output"}]}},"status":{"completed":true}}""" + "}");
                return;
            }
            case "/view":
                ctx.Response.ContentType = "image/png";
                await ctx.Response.Body.WriteAsync(Output);
                return;
            case "/queue" when request.Method == "GET":
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(QueueJson);
                return;
            case "/queue":
                await ctx.Response.WriteAsync("""{"queue_running":[],"queue_pending":[]}""");
                return;
            case "/upload/image":
            {
                var text = Encoding.Latin1.GetString(body.ToArray());
                var name = System.Text.RegularExpressions.Regex.Match(text, "filename=\"?([^\";\r\n]+)").Groups[1].Value;
                await ctx.Response.WriteAsJsonAsync(new { name, subfolder = "", type = "input" });
                return;
            }
            default:
                ctx.Response.StatusCode = 404;
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets.Values)
            try { socket.Abort(); }
            catch (ObjectDisposedException) { } // its request already ended
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// The node side, as the agent wires it (NodeAgent): the owner's attach-mode registration of <see cref="FakeComfyUi"/>,
/// the real <see cref="RuntimeManager"/>, the real <see cref="RuntimeForwardTarget"/> behind a <see cref="ForwardHost"/>
/// whose audit goes to the node's audit log, and <c>runtime.v1</c> through <see cref="RuntimeRpc"/>.
/// </summary>
internal sealed class ComfyNodeSide : LinkHandler, IAsyncDisposable
{
    public const string RuntimeId = "comfyui-1";

    private readonly FakeNodeHandler _phase3 = new();
    private readonly ForwardHost _forward;

    public string Root { get; }
    public NodePaths Paths { get; }
    public NodeEnvironment Environment { get; }
    public RuntimeManager Runtimes { get; }

    /// <summary>Every exchange the node's forward finished, as its audit saw it (bytes really read from the runtime and sent).</summary>
    public ConcurrentQueue<ForwardAudit> Forwarded { get; } = new();

    private ComfyNodeSide(int port)
    {
        Root = Path.Combine(Path.GetTempPath(), "leaf-node-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        Paths = NodePaths.Resolve(variable => variable == NodePaths.HomeVariable ? Root : null,
            _ => throw new InvalidOperationException("an isolated home asks for no OS folder"), OperatingSystem.IsWindows());
        var host = new LocalExecutionHost("node_e2e");
        new RuntimeRegistry(host, Paths).Add(new RuntimeRegistration
        {
            Id = RuntimeId, Kind = RuntimeKinds.ComfyUi, Label = "ComfyUI on the lab box", Managed = HostRuntimeManagement.Attach,
            Port = port, Accelerator = "cuda:0", AcceptedGraphsRunInstalledNodes = true,
        });
        Environment = new NodeEnvironment
        {
            Paths = Paths, Host = host, KeyStores = new NoKeys(), Log = new NodeLog(Paths.LogFile), SweepContainmentAtStart = false,
        };
        Runtimes = new RuntimeManager(Environment);
        var env = Environment;
        var forwarded = Forwarded;
        _forward = new ForwardHost(new RuntimeForwardTarget(Runtimes), audit: entry =>
        {
            forwarded.Enqueue(entry);
            env.Audit.Record("forward", entry.Path, entry.Outcome,
                $"{entry.Runtime} {entry.Mode}{(entry.Method is null ? "" : " " + entry.Method)} status {entry.Status?.ToString() ?? "-"}"
                + $" request {entry.RequestBytes} B response {entry.ResponseBytes} B");
        });
    }

    public static async Task<ComfyNodeSide> StartAsync(int port)
    {
        var node = new ComfyNodeSide(port);
        node.Runtimes.Load();
        await node.Runtimes.ProbeOnceAsync();
        return node;
    }

    /// <summary>The node's audit log (a folder of daily files: Paths.Audit), every record as JSON text.</summary>
    public string AuditText() => string.Join('\n', Environment.Audit.Read().Select(record => record.ToJsonString()));

    /// <summary>The node's log, as text (read while the agent still holds it open).</summary>
    public string LogText()
    {
        if (!File.Exists(Paths.LogFile)) return "";
        using var stream = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
        => ForwardHost.IsForwardClass(info.Class) ? _forward.Accept(session, info) : _phase3.AcceptChannel(session, info);

    public override async ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
        => RuntimeRpc.Handles(method)
            ? await RuntimeRpc.AnswerAsync(Runtimes, method, parameters, ct)
            : await _phase3.OnRequestAsync(session, method, parameters, ct);

    public async ValueTask DisposeAsync()
    {
        await Runtimes.DisposeAsync();
        Environment.Log.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class NoKeys : INodeKeyStores
    {
        public INodeKeyStore ForNewKey(bool allowFileKey) => throw new InvalidOperationException("no keys in this test");
        public INodeKeyStore ForKeyRef(string keyRef) => throw new InvalidOperationException("no keys in this test");
    }
}

/// <summary>Everything at once: the fake ComfyUI, the node side, a NodeHub with the node linked, and its host for the runtime.</summary>
internal sealed class ComfyNodeKit : IAsyncDisposable
{
    public static readonly string NodeId = RemoteId(1);

    public FakeComfyUi Comfy { get; private init; } = null!;
    public ComfyNodeSide Node { get; private init; } = null!;
    public NodeRegistry Registry { get; private init; } = null!;
    public NodeHub Hub { get; private init; } = null!;
    public PipedNode Link { get; private init; } = null!;

    public static async Task<ComfyNodeKit> StartAsync()
    {
        var comfy = await FakeComfyUi.StartAsync();
        var node = await ComfyNodeSide.StartAsync(comfy.Port);
        var registry = NodeTestKit.Registry();
        registry.ApplyKernelNodes([Kernel(NodeId, "Lab box")]);
        var hub = new NodeHub(registry, _ => { });
        var link = new PipedNode(hub, NodeId, nodeHandler: node, features: RuntimeNodeHandler.Phase4Features);
        await WaitAsync(() => registry.RuntimesOf(NodeId).Any(r => r.Status == Core.Providers.BackendStatus.Running), "the runtime report");
        return new ComfyNodeKit { Comfy = comfy, Node = node, Registry = registry, Hub = hub, Link = link };
    }

    public HostRuntime Runtime => Registry.RuntimesOf(NodeId).Single();

    public RemoteExecutionHost Host => Hub.RemoteHost(NodeId).ForRuntime(ComfyNodeSide.RuntimeId);

    /// <summary>A text-to-image request, or an image-to-image one with <paramref name="image"/> as its <c>image</c> input.</summary>
    public static JobRequest Request(object? image = null)
    {
        var workflow = new Dictionary<string, object>
        {
            ["3"] = new { class_type = "KSampler", inputs = new { seed = 1, steps = 4 } },
            ["6"] = new { class_type = "CLIPTextEncode", inputs = new { text = "" } },
            ["9"] = new { class_type = "SaveImage", inputs = new { filename_prefix = "out" } },
        };
        var parameters = new List<object> { new { name = "prompt", node_id = "6", field = "text", type = "string" } };
        if (image is not null)
        {
            workflow["10"] = new { class_type = "LoadImage", inputs = new { image = "" } };
            parameters.Add(new { name = "image", node_id = "10", field = "image", type = "image" });
        }
        var values = new Dictionary<string, object?>
        {
            ["workflow_json"] = JsonSerializer.Serialize(workflow),
            ["workflow_entity"] = new { slug = "test-workflow" },
            // As the kernel freezes it: the effective inputs, the asset reference among them (never sent to ComfyUI).
            ["workflow_inputs"] = image is null ? (object)new { prompt = "a red leaf" } : new { prompt = "a red leaf", image },
            ["workflow_parameters"] = parameters,
            ["output_node"] = "9",
            ["media_type"] = "image",
            ["prompt"] = "a red leaf",
        };
        if (image is not null) values["image"] = image;
        return new JobRequest
        {
            CapabilitySlug = "image-gen",
            Parameters = values.ToDictionary(pair => pair.Key, pair => (object?)JsonSerializer.SerializeToElement(pair.Value)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync();
        await Hub.DisposeAsync();
        await Node.DisposeAsync();
        await Comfy.DisposeAsync();
    }
}
