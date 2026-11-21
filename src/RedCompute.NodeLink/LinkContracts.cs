using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.NodeLink;

public enum LinkRole
{
    /// <summary>The node: opens odd channel ids, dials, sends the hello.</summary>
    Node,
    /// <summary>NodeHub: opens even channel ids, accepts, answers the welcome.</summary>
    Server,
}

/// <summary>What the opener asks for: <c>OPEN {class, streamKey?, priority?, window?, meta}</c>.</summary>
public sealed record ChannelOpenRequest(string Class, string? StreamKey = null, int? Priority = null, int? Window = null,
    JsonObject? Meta = null);

/// <summary>A channel the peer opened, as the acceptor sees it.</summary>
public sealed record ChannelOpenInfo(uint Id, string Class, string? StreamKey, int Priority, ulong Base, JsonObject? Meta);

/// <summary>
/// Accepting a peer's channel. For a durable stream, <see cref="CommittedThrough"/> is the last seq the
/// acceptor already committed; the stream continues after it.
/// </summary>
public sealed record ChannelAcceptance(IChannelHandler Handler, ulong CommittedThrough = 0, int? Window = null);

/// <summary>One delivery: a message (message channels) or a frame (stream channels, durable streams).</summary>
public readonly record struct ChannelData(ulong Seq, ReadOnlyMemory<byte> Payload, bool Fin);

/// <summary>
/// The consumer of one channel. Calls are sequential per channel. The link acks a delivery only after
/// <see cref="OnDataAsync"/> returns, so return only once its effect is committed (design 4.3).
/// </summary>
public interface IChannelHandler
{
    ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct);

    /// <summary>
    /// The peer half-closed: nothing more will arrive. What it sent with its CLOSE (a forward's trailer) is
    /// <see cref="LinkChannelHandle.RemoteClosePayload"/>.
    /// </summary>
    ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct);

    /// <summary>The channel is gone: reset by either side, or its link session was lost (<c>link_reset</c>).</summary>
    void OnReset(LinkChannelHandle channel, string code, string message);
}

/// <summary>
/// A channel handler that wants to act as soon as the channel it accepted is open (a forward that must start its
/// exchange before the peer sends anything). Called once, outside the session's lock, after the accept is queued.
/// </summary>
public interface IChannelOpenedHandler
{
    void OnOpened(LinkChannelHandle channel);
}

/// <summary>
/// One side's application: accepts the peer's channels and answers its control calls on channel 0.
/// <see cref="AcceptChannel"/> runs under the session's lock: decide quickly, never call back into the session.
/// </summary>
public interface ILinkHandler
{
    /// <summary>
    /// A handler for the peer's channel, or null to refuse it (<c>class_not_supported</c>). Throw
    /// <see cref="LinkChannelResetException"/> to refuse it with that exception's code.
    /// </summary>
    ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info);

    /// <summary>A control call. Throw <see cref="LinkRpcException"/> to answer an error.</summary>
    ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct);

    /// <summary>A control notification; notifications are handled in order.</summary>
    ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct);
}

/// <summary>A handler that refuses every channel and knows no method; override what you need.</summary>
public class LinkHandler : ILinkHandler
{
    public virtual ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info) => null;

    public virtual ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
        => throw new LinkRpcException(LinkErrorCodes.MethodNotFound, $"Unknown method '{method}'.");

    public virtual ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
        => ValueTask.CompletedTask;
}

/// <summary>How one connection of a link session ended.</summary>
public sealed record LinkDisconnect(int Code, string? Reason, bool ByPeer, TimeSpan UpFor, Exception? Error);

/// <summary>An open channel, for its owner and its handler.</summary>
public sealed class LinkChannelHandle
{
    private readonly LinkSession _session;
    private readonly LinkChannel _channel;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LinkChannelHandle(LinkSession session, LinkChannel channel)
    {
        _session = session;
        _channel = channel;
    }

    public LinkSession Session => _session;
    public uint Id => _channel.Id;
    public string Class => _channel.Class.Name;
    public string? StreamKey => _channel.StreamKey;
    public int Priority => _channel.Priority;
    public JsonObject? Meta => _channel.Meta;

    /// <summary>Completes when the channel retires after both half-closes; faults when it is reset.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Queues one message; waits while the peer's window is full. Throws when the channel is gone.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) => _session.SendAsync(_channel, message, ct);

    /// <summary>Half-closes our direction once everything queued before it is sent.</summary>
    public void Close() => _session.CloseChannel(_channel, null);

    /// <summary>
    /// Half-closes with <paramref name="payload"/> on the CLOSE frame (at most <see cref="FrameCodec.MaxSendPayload"/>
    /// bytes): a forward's trailer. Not for durable streams.
    /// </summary>
    public void Close(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > FrameCodec.MaxSendPayload)
            throw new ArgumentException($"A close payload is at most {FrameCodec.MaxSendPayload} bytes.", nameof(payload));
        _session.CloseChannel(_channel, payload.ToArray());
    }

    /// <summary>The payload of the peer's CLOSE, once it arrived (empty when it sent none).</summary>
    public ReadOnlyMemory<byte> RemoteClosePayload { get; internal set; }

    /// <summary>Our current receive window (autotune moves it on <see cref="ChannelClass.AutoTune"/> classes).</summary>
    public long ReceiveWindow => _session.ReceiveWindowOf(_channel);

    public void Reset(string code, string message) => _session.ResetChannel(_channel, code, message);

    /// <summary>Grows (or shrinks) the bytes the peer may have outstanding to us.</summary>
    public void SetReceiveWindow(int bytes) => _session.SetReceiveWindow(_channel, bytes);

    internal void Retired() => _completion.TrySetResult();

    internal void Failed(Exception error)
    {
        if (_completion.TrySetException(error)) _ = _completion.Task.Exception; // observed
    }
}
