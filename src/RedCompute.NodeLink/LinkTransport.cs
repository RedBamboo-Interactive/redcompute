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

/// <summary>
/// A WebSocket as a link transport: binary messages only, at most one frame each.
/// <para>
/// A cancelled send or receive only stops the caller's wait: the socket operation itself is never cancelled, because
/// .NET aborts a WebSocket whose send or receive is cancelled, and an aborted socket can no longer send the close frame
/// that tells the peer why the link ended (the C8 revoke: the session's own 4403 never left, and the kernel's relay
/// reported RedCompute's drop as 1012). A receive left pending is picked up by the next one, or drained by
/// <see cref="CloseAsync"/>.
/// </para>
/// </summary>
public sealed class WebSocketLinkTransport(WebSocket socket) : ILinkTransport
{
    /// <summary>How long a close waits for the close frame to go out and for the peer's answer.</summary>
    public static readonly TimeSpan CloseHandshake = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _buffer = new byte[FrameCodec.MaxFrame + 1];
    private int _length;
    // The socket's receive in flight, which a cancelled caller left behind; one at a time, as the socket allows.
    private Task<ValueWebSocketReceiveResult>? _receiving;

    public WebSocket Socket { get; } = socket;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        Task sending;
        try
        {
            sending = Socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).AsTask();
        }
        catch (WebSocketException error)
        {
            _sendGate.Release();
            throw new LinkClosedException((int?)Socket.CloseStatus, Socket.CloseStatusDescription ?? error.Message);
        }
        catch
        {
            _sendGate.Release();
            throw;
        }
        // The gate opens when the frame is out, not when a cancelled caller stops waiting: the next send, and the
        // close, go after it.
        _ = sending.ContinueWith(static (done, gate) =>
        {
            _ = done.Exception;
            ((SemaphoreSlim)gate!).Release();
        }, _sendGate, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await sending.WaitAsync(ct);
        }
        catch (WebSocketException error)
        {
            throw new LinkClosedException((int?)Socket.CloseStatus, Socket.CloseStatusDescription ?? error.Message);
        }
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var receiving = _receiving ??= Socket.ReceiveAsync(_buffer.AsMemory(_length), CancellationToken.None).AsTask();
                var result = await receiving.WaitAsync(ct);
                _receiving = null;
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new LinkClosedException((int?)Socket.CloseStatus ?? LinkCloseCodes.Abnormal, Socket.CloseStatusDescription);
                if (result.MessageType == WebSocketMessageType.Text)
                    throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The link carries binary messages only.");
                _length += result.Count;
                if (_length > FrameCodec.MaxFrame)
                    throw new LinkProtocolException(LinkCloseCodes.MessageTooBig, $"A message is at most {FrameCodec.MaxFrame} bytes.");
                if (!result.EndOfMessage) continue;
                var message = _buffer.AsSpan(0, _length).ToArray();
                _length = 0;
                return message;
            }
        }
        catch (WebSocketException error)
        {
            _receiving = null;
            throw new LinkClosedException((int?)Socket.CloseStatus ?? LinkCloseCodes.Abnormal, Socket.CloseStatusDescription ?? error.Message);
        }
    }

    /// <summary>
    /// Sends the close frame (after a send in flight), then reads until the peer's close or <see cref="CloseHandshake"/>,
    /// and only then aborts: aborting at once could drop the close frame before it left this machine.
    /// </summary>
    public async ValueTask CloseAsync(int code, string? reason, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CloseHandshake);
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _sendGate.WaitAsync(timeout.Token);
                try
                {
                    await Socket.CloseOutputAsync((WebSocketCloseStatus)code, Clip(reason), CancellationToken.None)
                        .WaitAsync(timeout.Token);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            // The peer's close (or its drop) says the frame arrived; what it still sends is not read any more.
            while (Socket.State == WebSocketState.CloseSent)
            {
                var receiving = _receiving ??= Socket.ReceiveAsync(_buffer.AsMemory(0), CancellationToken.None).AsTask();
                var result = await receiving.WaitAsync(timeout.Token);
                _receiving = null;
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // Closing is best effort: the peer may already be gone.
        }
        finally
        {
            if (_receiving is { } abandoned) _ = abandoned.ContinueWith(static t => t.Exception, TaskScheduler.Default);
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
