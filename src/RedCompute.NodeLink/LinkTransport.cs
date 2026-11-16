using System.Net.WebSockets;
using System.Threading.Channels;

namespace RedCompute.NodeLink;

/// <summary>
/// One connection that carries whole binary messages (one frame each). A WebSocket in production,
/// an in-memory pipe in tests, the fault proxy in the harness.
/// </summary>
public interface ILinkTransport : IAsyncDisposable
{
    /// <summary>Sends one message. Callers never overlap two sends.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);

    /// <summary>
    /// The next message. Throws <see cref="LinkClosedException"/> when the connection closed, and
    /// <see cref="LinkProtocolException"/> for a message the link refuses (text, oversized).
    /// </summary>
    ValueTask<byte[]> ReceiveAsync(CancellationToken ct);

    /// <summary>Closes with a code the peer sees; never throws.</summary>
    ValueTask CloseAsync(int code, string? reason, CancellationToken ct);
}

/// <summary>A WebSocket as a link transport: binary messages only, at most one frame each.</summary>
public sealed class WebSocketLinkTransport(WebSocket socket) : ILinkTransport
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocket Socket { get; } = socket;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch (WebSocketException error)
        {
            throw new LinkClosedException((int?)Socket.CloseStatus, Socket.CloseStatusDescription ?? error.Message);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[FrameCodec.MaxFrame + 1];
        var length = 0;
        try
        {
            while (true)
            {
                var result = await Socket.ReceiveAsync(buffer.AsMemory(length), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new LinkClosedException((int?)Socket.CloseStatus ?? LinkCloseCodes.Abnormal, Socket.CloseStatusDescription);
                if (result.MessageType == WebSocketMessageType.Text)
                    throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The link carries binary messages only.");
                length += result.Count;
                if (length > FrameCodec.MaxFrame)
                    throw new LinkProtocolException(LinkCloseCodes.MessageTooBig, $"A message is at most {FrameCodec.MaxFrame} bytes.");
                if (result.EndOfMessage) return buffer.AsSpan(0, length).ToArray();
            }
        }
        catch (WebSocketException error)
        {
            throw new LinkClosedException((int?)Socket.CloseStatus ?? LinkCloseCodes.Abnormal, Socket.CloseStatusDescription ?? error.Message);
        }
    }

    public async ValueTask CloseAsync(int code, string? reason, CancellationToken ct)
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await Socket.CloseOutputAsync((WebSocketCloseStatus)code, Clip(reason), timeout.Token);
            }
        }
        catch
        {
            // Closing is best effort: the peer may already be gone.
        }
        finally
        {
            Socket.Abort();
        }
    }

    // A close reason is at most 123 UTF-8 bytes.
    private static string? Clip(string? reason) => reason is { Length: > 100 } ? reason[..100] : reason;

    public ValueTask DisposeAsync()
    {
        Socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Two connected in-memory transports. A bounded queue per direction gives backpressure; a close
/// on one end is the next thing the other end receives.
/// </summary>
public static class LinkPipe
{
    public static (ILinkTransport A, ILinkTransport B) Create(int capacity = 64)
    {
        var ab = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false });
        var ba = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false });
        var a = new End(ab, ba.Reader);
        var b = new End(ba, ab.Reader);
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    private sealed class End(Channel<byte[]> outbound, ChannelReader<byte[]> inbound) : ILinkTransport
    {
        private int _closed;

        public End? Peer { get; set; }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            if (Volatile.Read(ref _closed) != 0) throw new LinkClosedException(LinkCloseCodes.Abnormal, "closed locally");
            try
            {
                await outbound.Writer.WriteAsync(message.ToArray(), ct);
            }
            catch (ChannelClosedException error)
            {
                throw error.InnerException as LinkClosedException ?? new LinkClosedException(LinkCloseCodes.Abnormal, "the pipe closed");
            }
        }

        public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _closed) != 0) throw new LinkClosedException(LinkCloseCodes.Abnormal, "closed locally");
            try
            {
                return await inbound.ReadAsync(ct);
            }
            catch (ChannelClosedException error)
            {
                throw error.InnerException as LinkClosedException ?? new LinkClosedException(LinkCloseCodes.Abnormal, "the pipe closed");
            }
        }

        /// <summary>
        /// The peer reads what was already sent, then the close. Our own pending receive ends too, and
        /// a peer still sending gets the close as well.
        /// </summary>
        public ValueTask CloseAsync(int code, string? reason, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                var closed = new LinkClosedException(code, reason);
                outbound.Writer.TryComplete(closed);
                Peer?.CompleteInbound(closed);
            }
            return ValueTask.CompletedTask;
        }

        // The peer's outbound queue is our inbound one: completing it ends our receive and the peer's sends.
        private void CompleteInbound(LinkClosedException closed) => outbound.Writer.TryComplete(closed);

        public ValueTask DisposeAsync() => CloseAsync(LinkCloseCodes.Abnormal, "disposed", CancellationToken.None);
    }
}
