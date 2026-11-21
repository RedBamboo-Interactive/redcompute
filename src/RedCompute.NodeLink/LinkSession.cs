using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RedCompute.NodeLink;

/// <summary>
/// One link session (design 4.3): its channels, their sequence/ack state and the control RPCs in
/// flight. It outlives connections: <see cref="RunTransportAsync"/> pumps one connection, and when that
/// ends the session waits detached until the next connection resumes it (frames the peer has not
/// received are sent again) or until it is <see cref="ResetAsync"/> (<c>link_reset</c>: ephemeral channels
/// and calls in flight fail; durable streams re-attach in the next session).
/// </summary>
public sealed class LinkSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly LinkOptions _options;
    private readonly ILinkHandler _handler;
    private readonly SortedDictionary<uint, LinkChannel> _channels = [];
    private readonly Dictionary<uint, Tombstone> _tombstones = [];
    private readonly Queue<uint> _tombstoneOrder = new();
    private readonly Queue<byte[]> _urgent = new();
    private readonly LinkScheduler _scheduler = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly CancellationTokenSource _life = new();
    private readonly List<Task> _deliveryLoops = [];
    private readonly LinkChannel _control;
    private SchedulerEntry[] _entries = new SchedulerEntry[16];
    private LinkChannel[] _entryChannels = new LinkChannel[16];
    private TaskCompletionSource _sendSignal = NewSignal();
    private uint _nextLocalId;
    // Peer-opened ids: every id up to the floor was seen; above it, the ones in the set. OPENs may
    // arrive out of order (a lost or overtaken OPEN), so "highest seen" alone would drop a late one.
    private long _peerFloor;
    private readonly HashSet<uint> _peerSeen = [];
    private long _nextCallId;
    private bool _reset;

    // the current connection
    private ILinkTransport? _transport;
    private CancellationTokenSource? _transportCts;
    private TaskCompletionSource<LinkDisconnect>? _pumpDone;
    private (int Code, string? Reason)? _detach;
    private LinkHeartbeat? _heartbeat;

    private enum TombstoneKind { Retired, ResetByUs, ResetByPeer }

    private sealed record Tombstone(TombstoneKind Kind, ulong FinalCommitted, string? Code, string? Message);

    public LinkSession(LinkRole role, string linkSessionId, ILinkHandler handler, LinkOptions? options = null,
        IReadOnlyList<string>? features = null, string? epoch = null)
    {
        Role = role;
        LinkSessionId = linkSessionId;
        _handler = handler;
        _options = options ?? LinkOptions.Default;
        Features = features ?? [];
        Epoch = epoch;
        _nextLocalId = role == LinkRole.Node ? 1u : 2u;
        _peerFloor = role == LinkRole.Node ? 0 : -1;
        var control = ChannelClasses.Control;
        _control = new LinkChannel(0, control, local: true, _options, control.Priority, null, null, control.Window, control.Window, 0, 0);
        AddChannel(_control, new ControlHandler(this));
    }

    public LinkRole Role { get; }
    public string LinkSessionId { get; }

    /// <summary>The features both sides listed (design 4.8).</summary>
    public IReadOnlyList<string> Features { get; }

    /// <summary>The server's boot epoch (sent in its PINGs).</summary>
    public string? Epoch { get; }

    public bool IsAttached { get { lock (_gate) return _transport is not null; } }

    /// <summary>
    /// The server's <see cref="LinkVerdicts"/> verdict, when it sent one on this session (the node side only):
    /// in band, so sealed on an encrypted link. The first one counts.
    /// </summary>
    public string? PeerVerdict => Volatile.Read(ref _peerVerdict);
    private string? _peerVerdict;
    public bool IsReset { get { lock (_gate) return _reset; } }

    public TimeSpan? Rtt { get { lock (_gate) return _heartbeat?.Rtt; } }
    public TimeSpan? ClockOffset { get { lock (_gate) return _heartbeat?.ClockOffset; } }

    /// <summary>When the last connection ended (monotonic timestamp), or 0 while attached or never attached.</summary>
    public long DetachedAt { get; private set; }

    public int OpenChannelCount { get { lock (_gate) return _channels.Count - 1; } }

    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long Now => _options.Time.GetTimestamp();

    private void Signal()
    {
        var signal = Interlocked.Exchange(ref _sendSignal, NewSignal());
        signal.TrySetResult();
    }

    // ================================================================= resume info

    /// <summary>For the hello: how far we received on each channel.</summary>
    public LinkResume ResumeInfo()
    {
        lock (_gate) return new LinkResume(LinkSessionId, _channels.Values.Select(c => new ChannelResume(c.Id, c.Received)).ToList());
    }

    /// <summary>For the welcome of a resumed session.</summary>
    public IReadOnlyList<ChannelResume> ChannelReceipts()
    {
        lock (_gate) return _channels.Values.Select(c => new ChannelResume(c.Id, c.Received)).ToList();
    }

    // ================================================================= the pump

    /// <summary>
    /// Pumps one connection (after the handshake) until it ends, and returns how. With
    /// <paramref name="peerChannels"/> (a resume), frames the peer has not received go again.
    /// </summary>
    public async Task<LinkDisconnect> RunTransportAsync(ILinkTransport transport, IReadOnlyList<ChannelResume>? peerChannels,
        CancellationToken ct)
    {
        var started = Now;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var done = new TaskCompletionSource<LinkDisconnect>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_reset) throw new InvalidOperationException("The link session was reset.");
            if (_transport is not null) throw new InvalidOperationException("The link session already has a connection.");
            _transport = transport;
            _transportCts = cts;
            _pumpDone = done;
            _detach = null;
            _heartbeat = new LinkHeartbeat(_options);
            DetachedAt = 0;
            var peer = peerChannels?.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Max(c => c.Rx)) ?? [];
            var now = Now;
            foreach (var channel in _channels.Values)
                channel.OnResume(peer.GetValueOrDefault(channel.Id), now);
        }
        Signal();

        var receive = Task.Run(() => ReceiveLoopAsync(transport, cts.Token));
        var send = Task.Run(() => SendLoopAsync(transport, cts.Token));
        var timer = Task.Run(() => TimerLoopAsync(cts.Token));
        var first = await Task.WhenAny(receive, send, timer);
        cts.Cancel();
        try
        {
            await Task.WhenAll(receive, send, timer);
        }
        catch
        {
            // the first failure is the one that counts
        }

        var error = first.Exception?.GetBaseException();
        int code;
        string? reason;
        var byPeer = false;
        (int, string?)? detach;
        lock (_gate) detach = _detach;
        if (detach is { } chosen) (code, reason) = chosen;
        else if (error is LinkProtocolException protocol) (code, reason) = (protocol.CloseCode, protocol.Message);
        else if (error is LinkClosedException closed) (code, reason, byPeer) = (closed.Code ?? LinkCloseCodes.Abnormal, closed.Reason, true);
        else if (error is TimeoutException) (code, reason) = (LinkCloseCodes.Timeout, "missed heartbeats");
        else if (ct.IsCancellationRequested || _life.IsCancellationRequested) (code, reason) = (LinkCloseCodes.GoingAway, "stopping");
        else (code, reason) = (LinkCloseCodes.Abnormal, error?.Message);
        if (!byPeer) await transport.CloseAsync(code, reason, CancellationToken.None);

        var result = new LinkDisconnect(code, reason, byPeer, _options.Time.GetElapsedTime(started), error);
        lock (_gate)
        {
            _transport = null;
            _transportCts = null;
            _pumpDone = null;
            DetachedAt = Now;
        }
        done.TrySetResult(result);
        return result;
    }

    /// <summary>Ends the current connection with <paramref name="code"/> (4409 superseded, 4403 revoked, 1012 restart).</summary>
    public async Task DetachAsync(int code, string? reason)
    {
        Task? done;
        lock (_gate)
        {
            if (_transport is null) return;
            _detach = (code, reason);
            _transportCts?.Cancel();
            done = _pumpDone?.Task;
        }
        if (done is not null) await done;
    }

    private async Task ReceiveLoopAsync(ILinkTransport transport, CancellationToken ct)
    {
        while (true)
        {
            var message = await transport.ReceiveAsync(ct);
            var frame = FrameCodec.Decode(message);
            List<Action>? after = null;
            lock (_gate)
            {
                _heartbeat?.OnFrameReceived();
                Process(frame, ref after);
            }
            Run(after);
            Signal();
        }
    }

    private async Task SendLoopAsync(ILinkTransport transport, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wait = Volatile.Read(ref _sendSignal).Task;
            byte[]? next;
            List<Action>? after = null;
            lock (_gate) next = NextFrame(ref after);
            Run(after);
            if (next is null)
            {
                await wait.WaitAsync(ct);
                continue;
            }
            await transport.SendAsync(next, ct);
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(_options.Tick, _options.Time, ct);
            lock (_gate)
            {
                var now = Now;
                foreach (var channel in _channels.Values) channel.OnTimer(now);
                if (_heartbeat is { } heartbeat)
                {
                    if (heartbeat.IsDead()) throw new TimeoutException("No frame from the peer within the heartbeat window.");
                    if (heartbeat.PingDue())
                        _urgent.Enqueue(FrameCodec.Encode(new LinkFrame(FrameKind.Ping, FrameFlags.None, 0, 0, 0,
                            heartbeat.NextPing(Role == LinkRole.Server ? Epoch : null))));
                }
            }
            Signal();
        }
    }

    private static void Run(List<Action>? actions)
    {
        if (actions is null) return;
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch
            {
                // a handler's notification must not break the pump
            }
        }
    }

    // ================================================================= outgoing

    private byte[]? NextFrame(ref List<Action>? after)
    {
        if (_urgent.TryDequeue(out var urgent)) return urgent;
        var now = Now;
        foreach (var channel in _channels.Values)
        {
            if (!channel.OpenDue) continue;
            channel.OpenDue = false;
            if (channel.Local && channel.Phase == ChannelPhase.Opening)
            {
                channel.OpenSentAt = now;
                return FrameCodec.Encode(new LinkFrame(FrameKind.Open, FrameFlags.None, channel.Id, channel.SendBase, 0, channel.OpenPayload!));
            }
            if (!channel.Local)
            {
                var accept = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["window"] = channel.LocalWindow });
                return FrameCodec.Encode(new LinkFrame(FrameKind.Open, FrameFlags.AckValid, channel.Id, 0, channel.Committed, accept));
            }
        }
        foreach (var channel in _channels.Values)
        {
            if (channel.WindowDue) return channel.TakeWindow();
            if (channel.AckNow && channel.Phase == ChannelPhase.Open)
            {
                var ack = channel.TakeAck();
                MaybeRetire(channel, ref after);
                return ack;
            }
        }

        var count = 0;
        foreach (var channel in _channels.Values)
        {
            if (channel.Writer is not null) TopUp(channel);
            if (count == _entries.Length)
            {
                Array.Resize(ref _entries, count * 2);
                Array.Resize(ref _entryChannels, count * 2);
            }
            _entries[count] = new SchedulerEntry(channel.Priority, channel.NextSendableBytes());
            _entryChannels[count++] = channel;
        }
        var pick = _scheduler.Pick(_entries.AsSpan(0, count));
        if (pick < 0) return null;
        var chosen = _entryChannels[pick];
        var frame = chosen.TakeNext(now);
        _scheduler.OnSent(chosen.Priority, frame.Length);
        return frame;
    }

    /// <summary>Feeds a durable channel from its writer's spool, up to the peer's window.</summary>
    private static void TopUp(LinkChannel channel)
    {
        if (channel.Phase != ChannelPhase.Open || channel.LocalCloseQueued) return;
        var room = channel.PeerWindow - channel.QueuedBytes;
        if (room <= 0) return;
        foreach (var record in channel.Writer!.Read(channel.NextSeq, room))
        {
            if (record.Kind == FrameKind.Close) channel.EnqueueSpooledClose(record.Seq);
            else channel.EnqueueSpooled(record.Seq, record.Kind, record.Flags, record.Payload);
        }
    }

    // ================================================================= incoming

    private void Process(LinkFrame frame, ref List<Action>? after)
    {
        if (frame.IsHandshake) return; // a late duplicate of the hello or welcome
        switch (frame.Kind)
        {
            case FrameKind.Ping:
                _urgent.Enqueue(FrameCodec.Encode(new LinkFrame(FrameKind.Pong, FrameFlags.None, 0, 0, 0, _heartbeat!.PongFor(frame.Payload.Span))));
                return;
            case FrameKind.Pong:
                _heartbeat?.OnPong(frame.Payload.Span);
                return;
        }

        if (!_channels.TryGetValue(frame.Channel, out var channel))
        {
            Unknown(frame, ref after);
            return;
        }
        var now = Now;
        switch (frame.Kind)
        {
            case FrameKind.Open:
                if (channel.Local && channel.Phase == ChannelPhase.Opening) Accepted(channel, frame, ref after);
                else if (!channel.Local) channel.OpenDue = true; // our accept was lost: send it again
                return;
            case FrameKind.Data or FrameKind.Close:
                if (channel.Phase == ChannelPhase.Opening) return; // the accept was lost; RTO resends OPEN
                if (frame.HasAck && channel.OnAck(frame.Ack, 0, now)) MaybeRetire(channel, ref after);
                var deliveries = new List<Delivery>();
                if (!channel.Receive(frame, deliveries, now))
                {
                    ResetLocked(channel, LinkErrorCodes.MessageTooLarge, $"A message passed the {channel.Class.ReassemblyCap}-byte cap.", true, ref after);
                    return;
                }
                foreach (var delivery in deliveries) channel.Deliveries!.Writer.TryWrite(delivery);
                return;
            case FrameKind.Ack:
                if (channel.Phase == ChannelPhase.Opening) return;
                if (channel.OnAck(frame.Ack, LinkChannel.ReadU64(frame.Payload), now)) MaybeRetire(channel, ref after);
                return;
            case FrameKind.Window:
                channel.OnPeerWindow(LinkChannel.ReadU64(frame.Payload));
                return;
            case FrameKind.Reset:
                var (code, message) = ReadReset(frame.Payload);
                ResetLocked(channel, code, message, sendReset: false, ref after);
                return;
        }
    }

    private void Accepted(LinkChannel channel, LinkFrame frame, ref List<Action>? after)
    {
        if (frame.Seq != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel {channel.Id}: the acceptor's direction starts at 0.");
        channel.PeerWindow = ReadWindow(frame.Payload) ?? channel.Class.Window;
        channel.Phase = ChannelPhase.Open;
        if (frame.HasAck && frame.Ack > channel.AckedThrough)
        {
            if (!channel.Durable)
                throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel {channel.Id}: an ack in the accept of an ephemeral channel.");
            if (frame.Ack >= channel.Writer!.NextSeq)
            {
                ResetLocked(channel, LinkErrorCodes.StreamGap, $"The receiver committed {frame.Ack}, past the spool's end.", true, ref after);
                return;
            }
            channel.SkipCommitted(frame.Ack);
        }
        var opened = channel.Opened;
        after ??= [];
        after.Add(() => opened?.TrySetResult());
    }

    private void Unknown(LinkFrame frame, ref List<Action>? after)
    {
        var id = frame.Channel;
        if (_tombstones.TryGetValue(id, out var tomb))
        {
            switch (tomb.Kind)
            {
                case TombstoneKind.Retired when frame.Kind is FrameKind.Data or FrameKind.Close:
                    var hint = new byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(hint, tomb.FinalCommitted);
                    _urgent.Enqueue(FrameCodec.Encode(new LinkFrame(FrameKind.Ack, FrameFlags.AckValid, id, 0, tomb.FinalCommitted, hint)));
                    break;
                case TombstoneKind.ResetByUs when frame.Kind != FrameKind.Reset:
                    _urgent.Enqueue(ResetFrame(id, tomb.Code!, tomb.Message!));
                    break;
            }
            return;
        }
        var peerParity = IsPeerId(id);
        var unseen = peerParity && id > _peerFloor && !_peerSeen.Contains(id);
        if (frame.Kind == FrameKind.Open && unseen)
        {
            PeerOpen(frame, ref after);
            return;
        }
        if (unseen) return; // its OPEN is still on the way
        if (frame.Kind is FrameKind.Reset or FrameKind.Ack or FrameKind.Window) return;
        if (peerParity && frame.Kind == FrameKind.Open) return; // a stale duplicate OPEN
        _urgent.Enqueue(ResetFrame(id, LinkErrorCodes.ChannelUnknown, $"No channel {id}."));
    }

    private bool IsPeerId(uint id) => Role == LinkRole.Node ? id % 2 == 0 : id % 2 == 1;

    private void PeerOpen(LinkFrame frame, ref List<Action>? after)
    {
        var id = frame.Channel;
        _peerSeen.Add(id);
        while (_peerSeen.Remove((uint)(_peerFloor + 2))) _peerFloor += 2;
        JsonObject? request = null;
        try
        {
            request = JsonNode.Parse(frame.Payload.Span) as JsonObject;
        }
        catch (JsonException)
        {
        }
        var className = request?["class"]?.GetValue<string>();
        var @class = ChannelClasses.Find(className);
        var streamKey = request?["streamKey"] is JsonValue key && key.TryGetValue<string>(out var k) && k.Length > 0 ? k : null;
        if (_channels.Count - 1 >= _options.MaxChannels)
        {
            RefuseOpen(id, LinkErrorCodes.TooManyChannels, $"At most {_options.MaxChannels} channels per link.");
            return;
        }
        if (@class is null || @class == ChannelClasses.Control || (streamKey is not null && !@class.MayBeDurable))
        {
            RefuseOpen(id, LinkErrorCodes.ClassNotSupported, $"Channel class '{className}' is not supported.");
            return;
        }
        var priority = Math.Clamp(Int(request, "priority") ?? @class.Priority, 0, ChannelClass.MaxPriority);
        var meta = request?["meta"] as JsonObject;
        var info = new ChannelOpenInfo(id, @class.Name, streamKey, priority, frame.Seq, meta?.DeepClone() as JsonObject);
        ChannelAcceptance? acceptance;
        try
        {
            acceptance = _handler.AcceptChannel(this, info);
        }
        catch (Exception error)
        {
            RefuseOpen(id, LinkErrorCodes.Internal, error.Message);
            return;
        }
        if (acceptance is null)
        {
            RefuseOpen(id, LinkErrorCodes.ClassNotSupported, $"Channel class '{@class.Name}' is not accepted here.");
            return;
        }
        var receiveBase = frame.Seq;
        if (streamKey is not null)
        {
            if (acceptance.CommittedThrough < frame.Seq)
            {
                RefuseOpen(id, LinkErrorCodes.StreamGap,
                    $"Stream {streamKey} resumes after {frame.Seq} but only {acceptance.CommittedThrough} was committed.");
                return;
            }
            receiveBase = acceptance.CommittedThrough;
        }
        var window = acceptance.Window ?? @class.Window;
        var peerWindow = Int(request, "window") ?? @class.Window;
        var channel = new LinkChannel(id, @class, local: false, _options, priority, streamKey, info.Meta, window, peerWindow, 0, receiveBase)
        {
            OpenDue = true,
        };
        AddChannel(channel, acceptance.Handler);
    }

    private void RefuseOpen(uint id, string code, string message)
    {
        Bury(id, new Tombstone(TombstoneKind.ResetByUs, 0, code, message));
        _urgent.Enqueue(ResetFrame(id, code, message));
    }

    private static int? Int(JsonObject? body, string key)
        => body?[key] is JsonValue value && value.TryGetValue<long>(out var number) && number is > 0 and <= int.MaxValue ? (int)number : null;

    private static int? ReadWindow(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return Int(JsonNode.Parse(payload.Span) as JsonObject, "window");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Code, string Message) ReadReset(ReadOnlyMemory<byte> payload)
    {
        try
        {
            if (JsonNode.Parse(payload.Span) is JsonObject body)
                return (body["code"]?.GetValue<string>() ?? "reset", body["message"]?.GetValue<string>() ?? "The peer reset the channel.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
        }
        return ("reset", "The peer reset the channel.");
    }

    private static byte[] ResetFrame(uint id, string code, string message)
        => FrameCodec.Encode(new LinkFrame(FrameKind.Reset, FrameFlags.None, id, 0, 0,
            JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["code"] = code, ["message"] = message })));

    // ================================================================= channel lifecycle

    private void AddChannel(LinkChannel channel, IChannelHandler handler)
    {
        channel.Handler = handler;
        channel.Handle = new LinkChannelHandle(this, channel);
        channel.Deliveries = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _channels[channel.Id] = channel;
        _deliveryLoops.Add(Task.Run(() => DeliverLoopAsync(channel)));
        _deliveryLoops.RemoveAll(loop => loop.IsCompleted);
    }

    private async Task DeliverLoopAsync(LinkChannel channel)
    {
        var ct = _life.Token;
        try
        {
            await foreach (var delivery in channel.Deliveries!.Reader.ReadAllAsync(ct))
            {
                if (channel.Phase == ChannelPhase.Retired) return;
                try
                {
                    if (delivery.IsClose) await channel.Handler!.OnRemoteCloseAsync(channel.Handle!, ct);
                    else await channel.Handler!.OnDataAsync(channel.Handle!, new ChannelData(delivery.Seq, delivery.Payload, delivery.Fin), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    ResetChannel(channel, LinkErrorCodes.Internal, error.Message);
                    return;
                }
                List<Action>? after = null;
                lock (_gate)
                {
                    if (channel.Phase == ChannelPhase.Retired) return;
                    channel.MarkDelivered(delivery.Seq, Now);
                    MaybeRetire(channel, ref after);
                }
                Run(after);
                Signal();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MaybeRetire(LinkChannel channel, ref List<Action>? after)
    {
        if (channel.Id == 0 || channel.Phase == ChannelPhase.Retired || !channel.CanRetire) return;
        channel.Phase = ChannelPhase.Retired;
        _channels.Remove(channel.Id);
        Bury(channel.Id, new Tombstone(TombstoneKind.Retired, channel.Committed, null, null));
        channel.Deliveries!.Writer.TryComplete();
        channel.Writer?.Detach(Signal);
        channel.Signal();
        var handle = channel.Handle!;
        after ??= [];
        after.Add(handle.Retired);
    }

    private void ResetLocked(LinkChannel channel, string code, string message, bool sendReset, ref List<Action>? after)
    {
        if (channel.Phase == ChannelPhase.Retired || channel.Id == 0) return;
        channel.Phase = ChannelPhase.Retired;
        _channels.Remove(channel.Id);
        Bury(channel.Id, new Tombstone(sendReset ? TombstoneKind.ResetByUs : TombstoneKind.ResetByPeer, channel.Committed, code, message));
        if (sendReset) _urgent.Enqueue(ResetFrame(channel.Id, code, message));
        channel.Deliveries!.Writer.TryComplete();
        channel.Writer?.Detach(Signal);
        channel.ResetError = new LinkChannelResetException(code, message);
        channel.Signal();
        var opened = channel.Opened;
        var handle = channel.Handle!;
        var handler = channel.Handler!;
        var error = channel.ResetError;
        after ??= [];
        after.Add(() =>
        {
            if (opened is not null && opened.TrySetException(error)) _ = opened.Task.Exception;
            handle.Failed(error);
            handler.OnReset(handle, code, message);
        });
    }

    private void Bury(uint id, Tombstone tombstone)
    {
        _tombstones[id] = tombstone;
        _tombstoneOrder.Enqueue(id);
        while (_tombstoneOrder.Count > _options.Tombstones && _tombstoneOrder.TryDequeue(out var old)) _tombstones.Remove(old);
    }

    // ================================================================= the public channel API

    /// <summary>Opens a channel and waits for the peer to accept it (a reset throws <see cref="LinkChannelResetException"/>).</summary>
    public Task<LinkChannelHandle> OpenChannelAsync(ChannelOpenRequest request, IChannelHandler handler, CancellationToken ct = default)
        => OpenAsync(request, handler, null, ct);

    /// <summary>
    /// Opens the durable stream of <paramref name="writer"/> in this session: from the spool's oldest
    /// frame, continuing after whatever the receiver already committed.
    /// </summary>
    public Task<LinkChannelHandle> AttachDurableAsync(DurableStreamWriter writer, IChannelHandler? handler = null, CancellationToken ct = default)
        => OpenAsync(new ChannelOpenRequest(writer.Class.Name, writer.StreamKey, writer.Priority), handler ?? IgnoreHandler.Instance, writer, ct);

    private async Task<LinkChannelHandle> OpenAsync(ChannelOpenRequest request, IChannelHandler handler, DurableStreamWriter? writer,
        CancellationToken ct)
    {
        var @class = ChannelClasses.Find(request.Class) ?? throw new ArgumentException($"Unknown channel class '{request.Class}'.");
        if (@class == ChannelClasses.Control) throw new ArgumentException("Channel 0 is the only control channel.");
        if (request.StreamKey is not null && writer is null)
            throw new ArgumentException("A durable stream opens through AttachDurableAsync.");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkChannel channel;
        lock (_gate)
        {
            if (_reset) throw new LinkChannelResetException(LinkErrorCodes.LinkReset, "The link session was reset.");
            if (_channels.Count - 1 >= _options.MaxChannels)
                throw new LinkChannelResetException(LinkErrorCodes.TooManyChannels, $"At most {_options.MaxChannels} channels per link.");
            var id = _nextLocalId;
            _nextLocalId += 2;
            var priority = Math.Clamp(request.Priority ?? @class.Priority, 0, ChannelClass.MaxPriority);
            var window = request.Window ?? @class.Window;
            var sendBase = writer is null ? 0 : writer.FirstSeq - 1;
            channel = new LinkChannel(id, @class, local: true, _options, priority, request.StreamKey, request.Meta, window, @class.Window, sendBase, 0)
            {
                Writer = writer,
                Opened = opened,
                OpenDue = true,
            };
            var body = new JsonObject { ["class"] = @class.Name, ["priority"] = priority, ["window"] = window };
            if (request.StreamKey is not null) body["streamKey"] = request.StreamKey;
            if (request.Meta is not null) body["meta"] = request.Meta.DeepClone();
            channel.OpenPayload = JsonSerializer.SerializeToUtf8Bytes(body);
            AddChannel(channel, handler);
            writer?.Attach(Signal);
        }
        Signal();
        using (ct.Register(() => opened.TrySetCanceled(ct)))
        {
            try
            {
                await opened.Task;
            }
            catch (OperationCanceledException)
            {
                ResetChannel(channel, LinkErrorCodes.Closed, "The opener gave up.");
                throw;
            }
        }
        return channel.Handle!;
    }

    internal async ValueTask SendAsync(LinkChannel channel, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (channel.Writer is not null) throw new InvalidOperationException("A durable stream is written through its DurableStreamWriter.");
        if (channel.Class.Messages && channel.Class.ReassemblyCap > 0 && message.Length > channel.Class.ReassemblyCap)
            throw new ArgumentException($"A {channel.Class.Name} message is at most {channel.Class.ReassemblyCap} bytes.");
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (channel.Phase == ChannelPhase.Retired)
                    throw channel.ResetError ?? new LinkChannelResetException(LinkErrorCodes.Closed, "The channel is closed.");
                if (channel.LocalCloseQueued) throw new InvalidOperationException("The channel was closed for sending.");
                if (channel.QueuedBytes < channel.PeerWindow)
                {
                    channel.EnqueueMessage(message.Span);
                    break;
                }
                wait = channel.SpaceSignal.Task;
            }
            await wait.WaitAsync(ct);
        }
        Signal();
    }

    internal void CloseChannel(LinkChannel channel)
    {
        lock (_gate)
        {
            if (channel.Phase == ChannelPhase.Retired || channel.Writer is not null) return;
            channel.EnqueueClose();
        }
        Signal();
    }

    internal void ResetChannel(LinkChannel channel, string code, string message)
    {
        List<Action>? after = null;
        lock (_gate) ResetLocked(channel, code, message, sendReset: true, ref after);
        Run(after);
        Signal();
    }

    internal void SetReceiveWindow(LinkChannel channel, int bytes)
    {
        lock (_gate)
        {
            if (channel.Phase == ChannelPhase.Retired) return;
            channel.SetLocalWindow(Math.Max(FrameCodec.MaxPayload, bytes));
        }
        Signal();
    }

    // ================================================================= control RPC (channel 0)

    /// <summary>Calls <paramref name="method"/> on the peer and waits for its result.</summary>
    public async Task<JsonElement> CallAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextCallId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_reset) throw new LinkRpcException(LinkErrorCodes.LinkReset, "The link session was reset.");
            _calls[id] = pending;
        }
        try
        {
            await SendControlAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() }, ct);
            using (ct.Register(() => pending.TrySetCanceled(ct)))
                return await pending.Task;
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The server tells the node its verdict in band before it ends the session (<see cref="LinkVerdicts"/>), and
    /// waits for the answer at most <paramref name="timeout"/>, so the verdict is read before the close. A node that
    /// does not know the method answers an error, which also proves it read it. False when nothing came back.
    /// </summary>
    public async Task<bool> SendVerdictAsync(string code, TimeSpan timeout, int closeCode)
    {
        Task<LinkDisconnect>? ended;
        lock (_gate)
        {
            if (_transport is null) return false;
            // From now on, however this connection ends (an unresponsive node's heartbeat too), it closes closeCode.
            _detach = (closeCode, code);
            ended = _pumpDone?.Task;
        }
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var call = CallAsync(LinkVerdicts.Method, new JsonObject { ["code"] = code }, cts.Token);
            if (ended is not null && await Task.WhenAny(call, ended) != call)
            {
                cts.Cancel();
                _ = call.ContinueWith(t => t.Exception, TaskScheduler.Default);
                return false; // the connection ended first: nobody is left to read it
            }
            await call;
            return true;
        }
        catch (LinkRpcException error) when (error.Code != LinkErrorCodes.LinkReset)
        {
            return true;
        }
        catch (Exception error) when (error is LinkRpcException or LinkClosedException or OperationCanceledException
                                          or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
        => SendControlAsync(new JsonObject { ["method"] = method, ["params"] = parameters ?? new JsonObject() }, ct).AsTask();

    private ValueTask SendControlAsync(JsonObject message, CancellationToken ct)
        => SendAsync(_control, JsonSerializer.SerializeToUtf8Bytes(message), ct);

    private sealed class ControlHandler(LinkSession session) : IChannelHandler
    {
        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(data.Payload);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return; // unreadable control message: ignored, the link stays up
            }
            if (root.ValueKind != JsonValueKind.Object) return;
            var hasId = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out _);
            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;

            if (method is null)
            {
                if (!hasId || !session._calls.TryGetValue(idElement.GetInt64(), out var pending)) return;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : LinkErrorCodes.Internal;
                    var message = error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString()! : code;
                    pending.TrySetException(new LinkRpcException(code, message));
                }
                else
                {
                    pending.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
                }
                return;
            }
            if (method == LinkVerdicts.Method && session.Role == LinkRole.Node)
            {
                // The link's own verdict, never the application's: recorded here, answered here.
                var code = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("code", out var c)
                           && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (code is LinkVerdicts.Revoked or LinkVerdicts.Unknown)
                    Interlocked.CompareExchange(ref session._peerVerdict, code, null);
                if (hasId)
                {
                    var answer = new JsonObject { ["id"] = idElement.GetInt64(), ["result"] = new JsonObject { ["ok"] = true } };
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.SendControlAsync(answer, session._life.Token);
                        }
                        catch (Exception)
                        {
                            // the session ended; the server closes it anyway
                        }
                    }, CancellationToken.None);
                }
                return;
            }
            if (!hasId)
            {
                await session._handler.OnNotificationAsync(session, method, parameters, ct);
                return;
            }
            // Requests run beside the channel, so a slow one never holds up the answers to our own calls.
            var id = idElement.GetInt64();
            _ = Task.Run(() => session.AnswerAsync(id, method, parameters), CancellationToken.None);
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct) => ValueTask.CompletedTask;

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }

    private async Task AnswerAsync(long id, string method, JsonElement parameters)
    {
        JsonObject answer;
        try
        {
            var result = await _handler.OnRequestAsync(this, method, parameters, _life.Token);
            answer = new JsonObject { ["id"] = id, ["result"] = result };
        }
        catch (LinkRpcException error)
        {
            answer = new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message } };
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            answer = new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = LinkErrorCodes.Internal, ["message"] = error.Message } };
        }
        try
        {
            await SendControlAsync(answer, _life.Token);
        }
        catch (Exception)
        {
            // the session ended; the caller's side fails its call with link_reset
        }
    }

    // ================================================================= end of the session

    /// <summary>
    /// Ends the session for good: calls in flight fail and every channel is reset with
    /// <paramref name="code"/> (<c>link_reset</c> when a new session replaces it). A connection still
    /// attached is closed with <paramref name="closeCode"/>.
    /// </summary>
    public async Task ResetAsync(string code = LinkErrorCodes.LinkReset, int closeCode = LinkCloseCodes.Normal)
    {
        List<Action>? after = null;
        lock (_gate)
        {
            if (_reset) return;
            _reset = true;
            foreach (var channel in _channels.Values.Where(c => c.Id != 0).ToList())
                ResetLocked(channel, code, "The link session ended.", sendReset: false, ref after);
            _control.Deliveries!.Writer.TryComplete();
        }
        Run(after);
        foreach (var call in _calls.Values) call.TrySetException(new LinkRpcException(code, "The link session ended."));
        await DetachAsync(closeCode, code);
        _life.Cancel();
        // A delivery in flight finishes (or sees the cancellation) before the session counts as ended, so
        // a durable receiver's commit point is final before a new session reads it: nothing twice.
        Task[] loops;
        lock (_gate) loops = _deliveryLoops.ToArray();
        try
        {
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // a handler that ignores cancellation is its own problem; the session is over either way
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync(LinkErrorCodes.LinkReset, LinkCloseCodes.GoingAway);
        _life.Dispose();
    }

    private sealed class IgnoreHandler : IChannelHandler
    {
        public static IgnoreHandler Instance { get; } = new();
        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }
}
