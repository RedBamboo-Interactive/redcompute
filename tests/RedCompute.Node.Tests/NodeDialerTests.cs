using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RedCompute.Node.Link;
using RedCompute.Node.Pairing;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The real dial (design 5.1, 4.7) against a stand-in kernel ingress on loopback: the assertion, the
/// sub-protocol, the clock fix after a 401, which refusals stop the node (only the server's explicit
/// verdict) and which are retried, and the link closing on a text or oversized message.
/// </summary>
public sealed class NodeDialerTests : IAsyncLifetime
{
    private const string NodeId = NodeAgentLinkTests.NodeId;
    private readonly NodeHome _home = new("dial");
    private readonly CancellationTokenSource _stop = new();
    private WebApplication _app = null!;
    private LinkServer _server = null!;
    private string _origin = "";
    private byte[] _spki = [];
    private readonly ConcurrentQueue<string> _assertions = new();
    private readonly ConcurrentQueue<int> _statuses = new();
    private readonly ConcurrentQueue<int?> _closes = new();

    /// <summary>What the ingress does with each upgrade; null = accept and run the link.</summary>
    private Func<HttpContext, Task<bool>>? _refuse;

    /// <summary>The ingress's clock minus the test's.</summary>
    private TimeSpan _serverSkew;

    /// <summary>After accepting, send this raw message instead of running the link.</summary>
    private (WebSocketMessageType Type, byte[] Bytes)? _misbehave;

    public async Task InitializeAsync()
    {
        _server = new LinkServer(new LinkServerOptions
        {
            ServerEpoch = "epoch-dial",
            HandlerFactory = _ => new ServerSide(),
            Features = [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho],
        });
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/api/nodes/link", IngressAsync);
        await _app.StartAsync();
        _origin = ServerOrigin.Normalize(_app.Urls.First());

        await LinkedAgent.PairLocallyAsync(_home, NodeId, _origin);
        using var key = new Credentials.FileKeyStore(_home.Paths).Open("file:key.p8", _origin)!;
        _spki = key.ExportSubjectPublicKeyInfo();
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _server.DisposeAsync();
        await _app.DisposeAsync();
        _home.Dispose();
    }

    private async Task IngressAsync(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow + _serverSkew;
        context.Response.Headers.Date = now.ToString("R");
        if (_refuse is { } refuse && await refuse(context)) return;

        var authorization = context.Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("LeafNode ", StringComparison.Ordinal) ? authorization["LeafNode ".Length..] : "";
        _assertions.Enqueue(token);
        var claims = NodeCrypto.VerifyAssertion(token, _spki);
        var iat = claims?["iat"]?.GetValue<long>() ?? 0;
        if (claims is null || claims["aud"]?.GetValue<string>() != _origin + "/api/nodes/link"
            || Math.Abs(iat - now.ToUnixTimeSeconds()) > 120)
        {
            await Refuse(context, 401, "assertion_invalid");
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(LinkProtocol.SubProtocol))
        {
            await Refuse(context, 400, "subprotocol_required");
            return;
        }
        _statuses.Enqueue(101);
        using var socket = await context.WebSockets.AcceptWebSocketAsync(LinkProtocol.SubProtocol);
        if (_misbehave is { } bad)
        {
            var buffer = new byte[70_000];
            await socket.ReceiveAsync(buffer, CancellationToken.None); // the hello
            await socket.SendAsync(bad.Bytes, bad.Type, true, CancellationToken.None);
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (WebSocketException)
            {
            }
            _closes.Enqueue((int?)socket.CloseStatus);
            return;
        }
        await _server.RunAsync(new WebSocketLinkTransport(socket), claims["iss"]!.GetValue<string>(), CancellationToken.None);
    }

    private async Task Refuse(HttpContext context, int status, string? code)
    {
        _statuses.Enqueue(status);
        context.Response.StatusCode = status;
        if (code is not null) context.Response.Headers[NodeDialer.ErrorHeader] = code;
        await context.Response.WriteAsync(code ?? "refused");
    }

    private (NodeAgent Agent, Task Run) StartAgent()
    {
        var agent = new NodeAgent(_home.Environment());
        return (agent, agent.RunAsync(_stop.Token));
    }

    [Fact]
    public async Task The_agent_links_over_a_websocket_with_a_signed_assertion()
    {
        var (agent, _) = StartAgent();
        await using (agent)
        {
            await Wait.UntilAsync(() => _server.IsOnline(NodeId), "the link");
            var describe = await _server.FindSession(NodeId)!.CallAsync("node.describe");
            Assert.Equal(NodeId, describe.GetProperty("nodeId").GetString());

            var claims = NodeCrypto.VerifyAssertion(Assert.Single(_assertions), _spki)!;
            Assert.Equal(NodeId, claims["iss"]!.GetValue<string>());
            Assert.Equal(_origin + "/api/nodes/link", claims["aud"]!.GetValue<string>());
            Assert.Equal("online", agent.Status()["link"]!["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_401_with_the_server_time_fixes_the_clock_and_retries_at_once()
    {
        _serverSkew = TimeSpan.FromMinutes(10); // the node's clock is 10 minutes behind
        var (agent, _) = StartAgent();
        await using (agent)
        {
            await Wait.UntilAsync(() => _server.IsOnline(NodeId), "the link after the clock fix");
            Assert.Equal([401, 101], _statuses.ToArray());
            var second = NodeCrypto.VerifyAssertion(_assertions.Last(), _spki)!;
            Assert.InRange(second["iat"]!.GetValue<long>() - (DateTimeOffset.UtcNow + _serverSkew).ToUnixTimeSeconds(), -5, 5);
        }
    }

    [Theory]
    [InlineData(403, "node_revoked", "revoked")]
    [InlineData(404, "node_unknown", "unknown")]
    public async Task The_servers_verdict_stops_the_node_and_wipes_its_key(int status, string code, string reason)
    {
        _refuse = async context =>
        {
            await Refuse(context, status, code);
            return true;
        };
        var (agent, _) = StartAgent();
        await using (agent)
        {
            await Wait.UntilAsync(() => _home.Environment().StateStore.Load().Ended is not null, "the cleanup");
            Assert.Equal(reason, _home.Environment().StateStore.Load().Ended!.Reason);
            Assert.False(File.Exists(_home.Paths.KeyFile));
            await Task.Delay(300);
            Assert.Single(_statuses); // never dialled again
        }
    }

    [Theory]
    [InlineData(403, null)] // a proxy or an access policy in front of Leaf
    [InlineData(404, null)] // a kernel without the ingress
    [InlineData(403, "forbidden")]
    [InlineData(503, "compute_nodes_unsupported")]
    public async Task Other_refusals_are_retried_and_never_wipe_the_key(int status, string? code)
    {
        _refuse = async context =>
        {
            await Refuse(context, status, code);
            return true;
        };
        var (agent, _) = StartAgent();
        await using (agent)
        {
            await Wait.UntilAsync(() => _statuses.Count >= 3, "three dials");
            Assert.NotNull(_home.Environment().StateStore.Load().Pairing);
            Assert.True(File.Exists(_home.Paths.KeyFile));
            Assert.NotEqual("refused", agent.Status()["link"]!["status"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(true, LinkCloseCodes.ProtocolError)]
    [InlineData(false, LinkCloseCodes.MessageTooBig)]
    public async Task A_text_or_oversized_message_closes_the_link(bool text, int expected)
    {
        _misbehave = text
            ? (WebSocketMessageType.Text, "{\"result\":{}}"u8.ToArray())
            : (WebSocketMessageType.Binary, new byte[FrameCodec.MaxFrame + 1]);
        var (agent, _) = StartAgent();
        await using (agent)
        {
            await Wait.UntilAsync(() => !_closes.IsEmpty, "the node's close");
            Assert.Equal(expected, _closes.First());
            Assert.False(_server.IsOnline(NodeId));
        }
    }
}
