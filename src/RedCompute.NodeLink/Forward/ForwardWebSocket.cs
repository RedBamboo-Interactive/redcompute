using System.Net.WebSockets;
using System.Threading.Channels;

namespace RedCompute.NodeLink.Forward;

/// <summary>
/// The server's WebSocket to a node's runtime, over one <c>port.ws</c> channel (Phase 4 design 2.2): each WS message
/// is one link message (a type byte, then the payload), and a close on either side is the channel's CLOSE. A lost
/// link session aborts it (<see cref="WebSocketError.ConnectionClosedPrematurely"/>).
/// </summary>
public sealed class ForwardWebSocket : WebSocket, IChannelHandler
{
    private readonly int _maxMessage;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _remoteClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(byte Type, byte[] Payload)> _incoming =
        Channel.CreateBounded<(byte, byte[])>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _lock = new();
    private MemoryStream? _sending;
    private WebSocketMessageType _sendingType;
    private LinkChannelHandle? _handle;
    private (byte Type, byte[] Payload)? _current;
    private int _currentOffset;
    private WebSocketState _state = WebSocketState.Connecting;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;
    private string? _resetCode;

    internal ForwardWebSocket(int maxMessage) => _maxMessage = maxMessage;

    internal Task Ready => _ready.Task;

    internal void Attach(LinkChannelHandle handle) => _handle = handle;

    public override WebSocketCloseStatus? CloseStatus { get { lock (_lock) return _closeStatus; } }
    public override string? CloseStatusDescription { get { lock (_lock) return _closeDescription; } }
    public override WebSocketState State { get { lock (_lock) return _state; } }
    public override string? SubProtocol => null;

    /// <summary>The code the channel was reset with, when it was.</summary>
    public string? ResetCode { get { lock (_lock) return _resetCode; } }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        if (_current is null)
        {
            ThrowIfNotReadable();
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(ct) || !_incoming.Reader.TryRead(out var next))
                {
                    lock (_lock)
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, _closeDescription);
                }
                _current = next;
                _currentOffset = 0;
            }
            catch (ChannelClosedException closed)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, closed.InnerException?.Message ?? "The link channel was reset.");
            }
            catch (ForwardStreamException reset)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, reset.Message);
            }
        }
        var (type, payload) = _current.Value;
        var take = Math.Min(buffer.Count, payload.Length - _currentOffset);
        payload.AsSpan(_currentOffset, take).CopyTo(buffer.AsSpan());
        _currentOffset += take;
        var end = _currentOffset >= payload.Length;
        if (end) _current = null;
        return new WebSocketReceiveResult(take, type == ForwardWsMessage.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, end);
    }

    private void ThrowIfNotReadable()
    {
        lock (_lock)
            if (_state is WebSocketState.Aborted)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, $"The forward was reset ({_resetCode}).");
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        if (messageType == WebSocketMessageType.Close) throw new ArgumentException("Close through CloseAsync or CloseOutputAsync.", nameof(messageType));
        await _sendGate.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_state == WebSocketState.Aborted && _resetCode is not null)
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, $"The forward was reset ({_resetCode}).");
                if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived))
                    throw new WebSocketException(WebSocketError.InvalidState, $"The WebSocket is {_state}.");
            }
            if (_sending is not null && _sendingType != messageType)
                throw new ArgumentException("A message's fragments share one type.", nameof(messageType));
            if (_sending is null && endOfMessage)
            {
                await SendMessageAsync(messageType, buffer, ct);
                return;
            }
            _sending ??= new MemoryStream();
            _sendingType = messageType;
            if (_sending.Length + buffer.Count + 1 > _maxMessage)
                throw new WebSocketException(WebSocketError.Faulted, $"A forwarded WS message is at most {_maxMessage - 1} bytes.");
            _sending.Write(buffer);
            if (!endOfMessage) return;
            var whole = _sending.ToArray();
            _sending = null;
            await SendMessageAsync(messageType, whole, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendMessageAsync(WebSocketMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length + 1 > _maxMessage)
            throw new WebSocketException(WebSocketError.Faulted, $"A forwarded WS message is at most {_maxMessage - 1} bytes.");
        var message = ForwardWsMessage.Encode(type == WebSocketMessageType.Text ? ForwardWsMessage.Text : ForwardWsMessage.Binary, payload.Span);
        try
        {
            await _handle!.SendAsync(message, ct);
        }
        catch (LinkChannelResetException reset)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, reset.Message);
        }
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        lock (_lock)
        {
            switch (_state)
            {
                case WebSocketState.Open:
                    _state = WebSocketState.CloseSent;
                    break;
                case WebSocketState.CloseReceived:
                    _state = WebSocketState.Closed;
                    break;
                case WebSocketState.CloseSent or WebSocketState.Closed:
                    return Task.CompletedTask;
                default:
                    throw new WebSocketException(WebSocketError.InvalidState, $"The WebSocket is {_state}.");
            }
        }
        _handle!.Close(ForwardWsMessage.EncodeClose((int)closeStatus, statusDescription));
        return Task.CompletedTask;
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        await CloseOutputAsync(closeStatus, statusDescription, ct);
        // Messages that arrive before the peer's close are dropped, as a WebSocket's CloseAsync does.
        try
        {
            while (await _incoming.Reader.WaitToReadAsync(ct)) _incoming.Reader.TryRead(out _);
        }
        catch (Exception error) when (error is ChannelClosedException or ForwardStreamException)
        {
        }
        await _remoteClosed.Task.WaitAsync(ct);
    }

    public override void Abort()
    {
        lock (_lock)
        {
            if (_state is WebSocketState.Closed or WebSocketState.Aborted) return;
            _state = WebSocketState.Aborted;
        }
        _handle?.Reset(LinkErrorCodes.Closed, "The WebSocket was aborted.");
    }

    public override void Dispose()
    {
        lock (_lock)
            if (_state is WebSocketState.Closed or WebSocketState.Aborted) return;
        Abort();
    }

    // ---------------------------------------------------------------- the channel

    async ValueTask IChannelHandler.OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
    {
        var message = data.Payload;
        if (message.IsEmpty) return;
        var type = message.Span[0];
        if (!_ready.Task.IsCompleted)
        {
            if (type != ForwardWsMessage.Ready || message.Length != 1)
            {
                channel.Reset(ForwardErrorCodes.BadForward, "The node's first WS message is its 'connected'.");
                return;
            }
            lock (_lock) _state = WebSocketState.Open;
            _ready.TrySetResult();
            return;
        }
        if (type is not (ForwardWsMessage.Text or ForwardWsMessage.Binary))
        {
            channel.Reset(ForwardErrorCodes.BadForward, $"A WS message of type {type}.");
            return;
        }
        try
        {
            await _incoming.Writer.WriteAsync((type, message[1..].ToArray()), ct);
        }
        catch (ChannelClosedException)
        {
        }
    }

    ValueTask IChannelHandler.OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
    {
        (int? Code, string? Reason) close;
        try
        {
            close = ForwardWsMessage.ParseClose(channel.RemoteClosePayload.Span);
        }
        catch (ForwardStreamException error)
        {
            channel.Reset(error.Code, error.Message);
            return ValueTask.CompletedTask;
        }
        if (!_ready.Task.IsCompleted)
        {
            _ready.TrySetException(new ForwardException(ForwardErrorCodes.UpstreamFailed, "The node closed the WebSocket before it connected."));
            channel.Reset(LinkErrorCodes.Closed, "Closed before it connected.");
            return ValueTask.CompletedTask;
        }
        lock (_lock)
        {
            _closeStatus = close.Code is { } code ? (WebSocketCloseStatus)code : WebSocketCloseStatus.Empty;
            _closeDescription = close.Reason;
            _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
        }
        _incoming.Writer.TryComplete();
        _remoteClosed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    void IChannelHandler.OnReset(LinkChannelHandle channel, string code, string message)
    {
        lock (_lock)
        {
            _resetCode = code;
            if (_state != WebSocketState.Closed) _state = WebSocketState.Aborted;
        }
        _ready.TrySetException(new ForwardException(code, message));
        _incoming.Writer.TryComplete(new ForwardStreamException(code, message));
        _remoteClosed.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely, message));
        _ = _remoteClosed.Task.Exception;
    }
}
