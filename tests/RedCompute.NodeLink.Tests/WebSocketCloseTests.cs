using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// C8 (the Fedora acceptance revoke): the server ends a link with a code the node must read (4403 after the sealed
/// verdict), over a real WebSocket. .NET aborts a WebSocket whose pending receive or send is cancelled, so a session
/// that cancelled its pump before closing sent no close frame at all, and the kernel's relay reported the drop as 1012
/// compute_dropped. The node then ignored the verdict it had read and, redialling, met only the unsealed 403:
/// revoked-unverified, keys kept, runtime running. These tests run the link over real WebSockets (the same
/// ManagedWebSocket Kestrel and ClientWebSocket use) on loopback TCP.
/// </summary>
public sealed class WebSocketCloseTests
{
    private const string NodeId = LinkPair.NodeId;

    /// <summary>Two connected WebSockets over loopback TCP; each owns its socket, so an abort is a TCP close the peer sees.</summary>
    private static async Task<(WebSocket Client, WebSocket Server)> SocketPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connecting = client.ConnectAsync(listener.LocalEndPoint!);
        var server = await listener.AcceptAsync();
        await connecting;
        return (WebSocket.CreateFromStream(new NetworkStream(client, ownsSocket: true), Options(isServer: false)),
            WebSocket.CreateFromStream(new NetworkStream(server, ownsSocket: true), Options(isServer: true)));

        static WebSocketCreationOptions Options(bool isServer) => new() { IsServer = isServer, KeepAliveInterval = Timeout.InfiniteTimeSpan };
    }

    [Fact]
    public async Task A_cancelled_receive_leaves_the_socket_able_to_send_its_close_code()
    {
        var (a, b) = await SocketPairAsync();
        await using var closing = new WebSocketLinkTransport(a);
        await using var peer = new WebSocketLinkTransport(b);
        using var cancel = new CancellationTokenSource();
        var pending = closing.ReceiveAsync(cancel.Token).AsTask();

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(WebSocketState.Open, a.State);

        var reading = peer.ReceiveAsync(CancellationToken.None).AsTask();
        var close = closing.CloseAsync(LinkCloseCodes.Revoked, LinkVerdicts.Revoked, CancellationToken.None).AsTask();
        var closed = await Assert.ThrowsAsync<LinkClosedException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LinkCloseCodes.Revoked, closed.Code);
        Assert.Equal(LinkVerdicts.Revoked, closed.Reason);
        // The peer answers the close (as a link session does), and the closing side stops waiting for it at once.
        var started = DateTime.UtcNow;
        await peer.CloseAsync(LinkCloseCodes.Revoked, LinkVerdicts.Revoked, CancellationToken.None);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - started < WebSocketLinkTransport.CloseHandshake, "the close waited for its timeout");
    }

    [Fact]
    public async Task A_cancelled_receive_is_picked_up_by_the_next_one_and_no_message_is_lost()
    {
        var (a, b) = await SocketPairAsync();
        await using var reader = new WebSocketLinkTransport(a);
        await using var writer = new WebSocketLinkTransport(b);
        using var cancel = new CancellationTokenSource();
        var pending = reader.ReceiveAsync(cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await writer.SendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await writer.SendAsync(new byte[] { 4, 5 }, CancellationToken.None);

        Assert.Equal([1, 2, 3], await reader.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([4, 5], await reader.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_close_to_a_peer_that_never_answers_ends_after_the_handshake_timeout()
    {
        var (a, b) = await SocketPairAsync();
        await using var closing = new WebSocketLinkTransport(a);
        using var silent = b; // reads nothing, answers nothing

        var started = DateTime.UtcNow;
        await closing.CloseAsync(LinkCloseCodes.Revoked, LinkVerdicts.Revoked, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(DateTime.UtcNow - started, WebSocketLinkTransport.CloseHandshake - TimeSpan.FromMilliseconds(200),
            WebSocketLinkTransport.CloseHandshake + TimeSpan.FromSeconds(3));
        Assert.Equal(WebSocketState.Aborted, a.State);
    }

    /// <summary>
    /// The C8 path: node and server on real WebSockets, the server's link key pinned. After the revoke every new dial
    /// meets the kernel's unsealed HTTP 403, as it does once the node entity is revoked.
    /// </summary>
    private sealed class RevokedPath
    {
        public int Dials;
        public bool Revoked;
        public readonly List<string> Offline = [];

        public async Task<ILinkTransport> DialAsync(Func<ILinkTransport, Task> accept)
        {
            Interlocked.Increment(ref Dials);
            if (Volatile.Read(ref Revoked)) throw new LinkDialException(403, LinkVerdicts.Revoked, "the kernel refused the dial");
            var (client, server) = await SocketPairAsync();
            _ = Task.Run(() => accept(new WebSocketLinkTransport(server)));
            return new WebSocketLinkTransport(client);
        }
    }

    private static LinkPair Pair(Func<Func<ILinkTransport, Task>, Task<ILinkTransport>> dial, E2eKeys keys)
    {
        LinkPair? pair = null;
        pair = new LinkPair(
            clientSecure: keys.ClientSecure(NodeId),
            serverSecure: keys.ServerSecure,
            dial: _ => dial(t => pair!.Server.RunAsync(t, NodeId, CancellationToken.None)));
        return pair;
    }

    [Fact]
    public async Task The_servers_revocation_over_real_websockets_reaches_a_pinned_node_as_4403_and_stops_it()
    {
        using var keys = new E2eKeys();
        var path = new RevokedPath();
        await using var pair = Pair(path.DialAsync, keys);
        pair.Client.StatusChanged += (status, reason) =>
        {
            if (status == LinkClientStatus.Offline) lock (path.Offline) path.Offline.Add(reason ?? "");
        };
        pair.Start();
        await pair.OnlineAsync();

        path.Revoked = true;
        await pair.Server.RevokeAsync(NodeId);

        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Equal(LinkVerdicts.Revoked, pair.Client.StatusReason);
        lock (path.Offline) Assert.Equal([$"closed {LinkCloseCodes.Revoked}: {LinkVerdicts.Revoked}"], path.Offline);
        Assert.Equal(1, path.Dials);
    }

    /// <summary>
    /// A relay between the two (the kernel's ingress) that reports the server's side ending as <paramref name="code"/>
    /// to the node, whatever the server's close said: what the node sees when the server's close frame never arrives.
    /// </summary>
    private static Func<Func<ILinkTransport, Task>, Task<ILinkTransport>> RelayRewriting(RevokedPath path, int code)
        => accept =>
        {
            Interlocked.Increment(ref path.Dials);
            if (Volatile.Read(ref path.Revoked)) throw new LinkDialException(403, LinkVerdicts.Revoked, "the kernel refused the dial");
            var (node, towardNode) = LinkPipe.Create();
            var (towardServer, server) = LinkPipe.Create();
            _ = Task.Run(() => accept(server));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true) await towardServer.SendAsync(await towardNode.ReceiveAsync(CancellationToken.None), CancellationToken.None);
                }
                catch (LinkClosedException)
                {
                }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true) await towardNode.SendAsync(await towardServer.ReceiveAsync(CancellationToken.None), CancellationToken.None);
                }
                catch (LinkClosedException)
                {
                    await towardNode.CloseAsync(code, "compute_dropped", CancellationToken.None);
                }
            });
            return Task.FromResult(node);
        };

    [Fact]
    public async Task A_sealed_verdict_stops_a_pinned_node_even_when_the_close_after_it_arrives_as_1012()
    {
        using var keys = new E2eKeys();
        var path = new RevokedPath();
        await using var pair = Pair(RelayRewriting(path, LinkCloseCodes.ServiceRestart), keys).Start();
        await pair.OnlineAsync();

        path.Revoked = true;
        await pair.Server.RevokeAsync(NodeId);

        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Equal(LinkVerdicts.Revoked, pair.Client.StatusReason);
        Assert.Equal(1, path.Dials);
    }

    [Fact]
    public async Task A_1012_without_a_verdict_is_still_a_restart_and_the_node_redials()
    {
        using var keys = new E2eKeys();
        var path = new RevokedPath();
        await using var pair = Pair(RelayRewriting(path, LinkCloseCodes.ServiceRestart), keys).Start();
        await pair.OnlineAsync();

        await pair.Server.ShutdownAsync(LinkCloseCodes.ServiceRestart);

        await Wait.UntilAsync(() => path.Dials >= 2, what: "the redial after a restart");
        Assert.False(pair.ClientRun.IsCompleted);
        Assert.NotEqual(LinkClientStatus.Refused, pair.Client.Status);
    }
}
