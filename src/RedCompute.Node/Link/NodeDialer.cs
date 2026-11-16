using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using RedCompute.Node.Pairing;
using RedCompute.NodeLink;

namespace RedCompute.Node.Link;

/// <summary>
/// Opens one link connection (design 5.1): a WebSocket to <c>&lt;origin&gt;&lt;linkPath&gt;</c> with
/// <c>Authorization: LeafNode &lt;assertion&gt;</c>, the sub-protocol <c>leaf-node.v1</c> and no
/// compression. A fresh assertion per dial; its clock follows the offset the server last reported.
/// <para>
/// A refused upgrade becomes a <see cref="LinkDialException"/> the link client classifies (design 4.7).
/// The kernel names the reason in the <c>X-Leaf-Node-Error</c> response header (<c>assertion_invalid</c>,
/// <c>assertion_replayed</c>, <c>node_revoked</c>, <c>node_unknown</c>, <c>compute_nodes_unsupported</c>, …)
/// and its clock in <c>Date</c>. A 403 or 404 stops the node and wipes its key <b>only</b> with
/// <c>node_revoked</c> or <c>node_unknown</c>: anything else (a proxy's 403, a kernel without the
/// ingress) is retried, so a misconfigured path never destroys a paired node.
/// </para>
/// </summary>
public sealed class NodeDialer
{
    public const string ErrorHeader = "X-Leaf-Node-Error";
    public const string AuthorizationScheme = "LeafNode";

    private readonly Func<ECDsa> _key;
    private readonly string _nodeId;
    private readonly Uri _uri;
    private readonly string _audience;
    private readonly TimeProvider _time;
    private readonly Action<ClientWebSocketOptions>? _configure;
    private readonly Func<ClientWebSocket, Uri, CancellationToken, Task>? _connect;
    private long _clockOffsetTicks;

    /// <param name="key">Opens the node key for one signature (CNG keeps no handle open between dials).</param>
    /// <param name="configure">Tests: trust a test certificate, use an in-process handler.</param>
    public NodeDialer(NodePairing pairing, Func<ECDsa> key, TimeProvider? time = null,
        Action<ClientWebSocketOptions>? configure = null, Func<ClientWebSocket, Uri, CancellationToken, Task>? connect = null)
    {
        _key = key;
        _nodeId = pairing.NodeId;
        _uri = ServerOrigin.LinkUri(pairing.ServerOrigin, pairing.LinkPath);
        _audience = pairing.ServerOrigin + pairing.LinkPath;
        _time = time ?? TimeProvider.System;
        _configure = configure;
        _connect = connect;
    }

    /// <summary>Server clock minus ours, from the last refusal that carried the server's time.</summary>
    public TimeSpan ClockOffset => TimeSpan.FromTicks(Interlocked.Read(ref _clockOffsetTicks));

    public Uri Uri => _uri;

    public async Task<ILinkTransport> DialAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.AddSubProtocol(LinkProtocol.SubProtocol);
            socket.Options.DangerousDeflateOptions = null;
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan; // the link has its own heartbeat
            string assertion;
            using (var key = _key())
                assertion = NodeCrypto.Assertion(key, _nodeId, _audience, _time.GetUtcNow() + ClockOffset);
            socket.Options.SetRequestHeader("Authorization", $"{AuthorizationScheme} {assertion}");
            _configure?.Invoke(socket.Options);
            try
            {
                if (_connect is not null) await _connect(socket, _uri, ct);
                else await socket.ConnectAsync(_uri, ct);
            }
            catch (WebSocketException error) when (socket.HttpStatusCode is not 0 and not HttpStatusCode.SwitchingProtocols)
            {
                throw Refusal(socket, error);
            }
            catch (WebSocketException error)
            {
                throw new LinkClosedException(null, $"Could not open the link to {_uri.Host}: {error.Message}");
            }
            catch (HttpRequestException error)
            {
                throw new LinkClosedException(null, $"Could not reach {_uri.Host}: {error.Message}");
            }
            if (socket.SubProtocol != LinkProtocol.SubProtocol)
            {
                await new WebSocketLinkTransport(socket).CloseAsync(LinkCloseCodes.ProtocolError, "sub-protocol not accepted", ct);
                throw new LinkDialException(null, "subprotocol_refused", "The server did not accept the leaf-node.v1 sub-protocol.");
            }
            return new WebSocketLinkTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private LinkDialException Refusal(ClientWebSocket socket, Exception error)
    {
        var status = (int)socket.HttpStatusCode;
        var headers = socket.HttpResponseHeaders;
        string? Header(string name) => headers is not null && headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

        var code = Header(ErrorHeader);
        DateTimeOffset? serverTime = DateTimeOffset.TryParse(Header("Date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        TimeSpan? retryAfter = int.TryParse(Header("Retry-After"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

        if (status == 401 && serverTime is { } now)
            Interlocked.Exchange(ref _clockOffsetTicks, (now - _time.GetUtcNow()).Ticks);

        // Only an explicit verdict stops the node (see the class remarks).
        var stopping = (status == 403 && code == "node_revoked") || (status == 404 && code == "node_unknown");
        var reported = status switch
        {
            403 or 404 when !stopping => (int?)null,
            401 when code is "assertion_replayed" => null,
            _ => status,
        };
        var reason = code ?? $"http_{status}";
        var message = status switch
        {
            404 when !stopping => $"{_uri.Host} does not accept nodes at {_uri.AbsolutePath} (404). Is Leaf up to date?",
            503 when code == "compute_nodes_unsupported" => $"{_uri.Host}'s compute service does not support nodes yet.",
            _ => $"{_uri.Host} refused the link ({status} {reason}).",
        };
        return new LinkDialException(reported, reason, message, retryAfter, serverTime);
    }
}
