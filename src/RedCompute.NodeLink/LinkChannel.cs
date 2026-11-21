using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace RedCompute.NodeLink;

internal enum ChannelPhase
{
    /// <summary>We sent OPEN and wait for the peer's accept.</summary>
    Opening,
    Open,
    Retired,
}

/// <summary>One frame on its way out, kept until the peer acks it.</summary>
internal sealed class OutFrame(FrameKind kind, FrameFlags flags, ulong seq, byte[] payload)
{
    public FrameKind Kind { get; } = kind;
    public FrameFlags Flags { get; } = flags;
    public ulong Seq { get; } = seq;
    public byte[] Payload { get; } = payload;
    public bool Sent { get; set; }
    public bool Retransmitted { get; set; }
    public long SentAt { get; set; }
}

/// <summary>What a channel hands its consumer: a message (or a frame), or the peer's half-close.</summary>
internal readonly record struct Delivery(ulong Seq, byte[] Payload, bool Fin, bool IsClose);

/// <summary>
/// The per-channel state machine (design 4.3, 4.4), with no I/O: the session calls it under its lock.
/// <para>Sender: each DATA/CLOSE gets the next seq and stays in the replay buffer until the peer's
/// cumulative ack covers it. At most the peer's window of bytes is outstanding. On RTO it goes back to
/// the first frame the peer has not received (go-back-N) or, when the peer has everything but not yet
/// committed it, probes with the last frame so the peer re-acks.</para>
/// <para>Receiver: frames are delivered in order; duplicates are dropped and re-acked; frames ahead of
/// a gap wait in a reorder buffer bounded by the window. The ack is "committed": it covers a frame only
/// once the consumer returned from it (for message channels, fragments count as committed when they
/// join the in-memory reassembly, and the message when its consumer returns).</para>
/// </summary>
internal sealed class LinkChannel
{
    private readonly LinkOptions _options;
    private readonly List<OutFrame> _out = [];
    private readonly SortedDictionary<ulong, LinkFrame> _reorder = [];
    private readonly Queue<ulong> _deliveryPoints = new();
    private readonly Queue<(ulong Seq, int Bytes)> _uncommitted = new();
    private MemoryStream? _assembly;

    // ---- send side
    private ulong _nextSeq;
    private ulong _highestSent;
    private ulong _peerReceived;
    private long _windowUsed;
    private int _firstUnsent;
    private double _srttMs = -1;
    private TimeSpan _rto;
    private long _rtoArmedAt;
    private bool _rtoArmed;
    private ulong _closeSeq;

    // ---- receive side
    private ulong _received;
    private ulong _committed;
    private long _reorderBytes;
    private long _uncommittedBytes;
    private int _ackFrames;
    private long _ackBytes;
    private long _ackDueAt;
    private ulong _lastAckSent;
    private bool _receivedAny;

    public LinkChannel(uint id, ChannelClass @class, bool local, LinkOptions options, int priority, string? streamKey,
        JsonObject? meta, int localWindow, int peerWindow, ulong sendBase, ulong receiveBase)
    {
        Id = id;
        Class = @class;
        Local = local;
        _options = options;
        Priority = Math.Clamp(priority, 0, ChannelClass.MaxPriority);
        StreamKey = streamKey;
        Meta = meta;
        LocalWindow = localWindow;
        PeerWindow = peerWindow;
        SendBase = sendBase;
        _nextSeq = sendBase + 1;
        AckedThrough = sendBase;
        _highestSent = sendBase;
        _peerReceived = sendBase;
        _received = receiveBase;
        _committed = receiveBase;
        _lastAckSent = receiveBase;
        _rto = options.RtoMin;
        Phase = local && id != 0 ? ChannelPhase.Opening : ChannelPhase.Open;
    }

    public uint Id { get; }
    public ChannelClass Class { get; }
    public bool Local { get; }
    public int Priority { get; }
    public string? StreamKey { get; }
    public bool Durable => StreamKey is not null;
    public JsonObject? Meta { get; }
    public ChannelPhase Phase { get; set; }
    public long LocalWindow { get; private set; }
    public long PeerWindow { get; set; }
    public ulong SendBase { get; }
    public ulong AckedThrough { get; private set; }
    public ulong NextSeq => _nextSeq;
    public ulong Received => _received;
    public ulong Committed => _committed;

    /// <summary>Bytes enqueued and not yet acked (sent or not): what a writer waits on.</summary>
    public long QueuedBytes { get; private set; }

    public bool LocalCloseQueued => _closeSeq != 0;
    public bool LocalCloseAcked => _closeSeq != 0 && AckedThrough >= _closeSeq;
    public bool RemoteCloseReceived { get; private set; }
    public bool RemoteCloseCommitted => RemoteCloseReceived && _deliveryPoints.Count == 0 && _committed == _received;

    /// <summary>OPEN (opener) or its accept (acceptor) must go out.</summary>
    public bool OpenDue { get; set; }
    public byte[]? OpenPayload { get; set; }
    public long OpenSentAt { get; set; }
    public bool WindowDue { get; set; }
    public bool AckNow { get; private set; }

    /// <summary>The durable writer that feeds this channel from its spool, if any.</summary>
    public DurableStreamWriter? Writer { get; set; }

    public LinkChannelHandle? Handle { get; set; }
    public IChannelHandler? Handler { get; set; }
    public System.Threading.Channels.Channel<Delivery>? Deliveries { get; set; }
    public TaskCompletionSource? Opened { get; set; }
    public TaskCompletionSource<bool> SpaceSignal { get; private set; } = NewSignal();

    public bool CanRetire => LocalCloseAcked && RemoteCloseCommitted && _lastAckSent == _committed;

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ================================================================= send side

    public int FrameBytesFor => Priority >= 2 ? Math.Min(_options.LowPriorityFrameBytes, FrameCodec.MaxSendPayload) : FrameCodec.MaxSendPayload;

    /// <summary>Splits a message into frames, FIN on the last (an empty message is one empty FIN frame).</summary>
    public void EnqueueMessage(ReadOnlySpan<byte> message)
    {
        var size = FrameBytesFor;
        var offset = 0;
        do
        {
            var take = Math.Min(size, message.Length - offset);
            var last = offset + take >= message.Length;
            Enqueue(FrameKind.Data, last ? FrameFlags.Fin : FrameFlags.None, message.Slice(offset, take).ToArray());
            offset += take;
        } while (offset < message.Length);
    }

    /// <summary>A durable writer's frame, with the seq its spool gave it.</summary>
    public void EnqueueSpooled(ulong seq, FrameKind kind, FrameFlags flags, byte[] payload)
    {
        if (seq != _nextSeq)
            throw new InvalidOperationException($"Channel {Id} expected seq {_nextSeq}, the spool gave {seq}.");
        Enqueue(kind, flags, payload);
    }

    /// <summary>A durable writer's spooled CLOSE, the stream's last frame.</summary>
    public void EnqueueSpooledClose(ulong seq)
    {
        if (seq != _nextSeq)
            throw new InvalidOperationException($"Channel {Id} expected seq {_nextSeq}, the spool gave {seq}.");
        EnqueueClose();
    }

    /// <summary>Why the channel is gone, once reset.</summary>
    public Exception? ResetError { get; set; }

    public void EnqueueClose()
    {
        if (_closeSeq != 0) return;
        _closeSeq = _nextSeq;
        Enqueue(FrameKind.Close, FrameFlags.Fin, []);
    }

    private void Enqueue(FrameKind kind, FrameFlags flags, byte[] payload)
    {
        _out.Add(new OutFrame(kind, flags, _nextSeq++, payload));
        QueuedBytes += payload.Length;
    }

    /// <summary>The size of the frame that would go next, or 0 when none may go now.</summary>
    public int NextSendableBytes()
    {
        if (Phase != ChannelPhase.Open || _firstUnsent >= _out.Count) return 0;
        var frame = _out[_firstUnsent];
        var size = frame.Payload.Length + FrameCodec.HeaderSize;
        if (frame.Seq <= _highestSent) return size; // a retransmission: already inside the window
        return _windowUsed == 0 || _windowUsed + frame.Payload.Length <= PeerWindow ? size : 0;
    }

    /// <summary>Marks the next frame sent and encodes it, with our receive ack piggybacked.</summary>
    public byte[] TakeNext(long now)
    {
        var frame = _out[_firstUnsent];
        frame.Sent = true;
        frame.SentAt = now;
        if (frame.Seq > _highestSent)
        {
            _windowUsed += frame.Payload.Length;
            _highestSent = frame.Seq;
        }
        AdvanceFirstUnsent();
        if (!_rtoArmed)
        {
            _rtoArmed = true;
            _rtoArmedAt = now;
        }
        var flags = frame.Flags | FrameFlags.AckValid;
        TookAck();
        return FrameCodec.Encode(new LinkFrame(frame.Kind, flags, Id, frame.Seq, _committed, frame.Payload));
    }

    private void AdvanceFirstUnsent()
    {
        while (_firstUnsent < _out.Count && _out[_firstUnsent].Sent) _firstUnsent++;
    }

    /// <summary>
    /// The peer's cumulative (committed) ack and, from a standalone ACK, how far it has received.
    /// Returns whether anything new was acked (writers may proceed).
    /// </summary>
    public bool OnAck(ulong ack, ulong receivedHint, long now)
    {
        if (ack >= _nextSeq)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel {Id}: ack {ack} for a frame never sent (next {_nextSeq}).");
        if (receivedHint >= _nextSeq) receivedHint = _nextSeq - 1;
        _peerReceived = Math.Max(_peerReceived, Math.Max(ack, receivedHint));
        if (ack <= AckedThrough) return false;

        OutFrame? newest = null;
        var removed = 0;
        foreach (var frame in _out)
        {
            if (frame.Seq > ack) break;
            newest = frame;
            removed++;
            QueuedBytes -= frame.Payload.Length;
            if (frame.Seq <= _highestSent) _windowUsed -= frame.Payload.Length;
        }
        _out.RemoveRange(0, removed);
        _firstUnsent = Math.Max(0, _firstUnsent - removed);
        AckedThrough = ack;
        if (_highestSent < ack) _highestSent = ack;

        // Karn: only a frame sent once gives an RTT sample.
        if (newest is { Sent: true, Retransmitted: false })
        {
            var sample = _options.Time.GetElapsedTime(newest.SentAt, now).TotalMilliseconds;
            _srttMs = _srttMs < 0 ? sample : 0.875 * _srttMs + 0.125 * sample;
        }
        _rto = BaseRto();
        _rtoArmed = _out.Any(frame => frame.Sent);
        _rtoArmedAt = now;
        Writer?.OnAcked(ack);
        Signal();
        return true;
    }

    /// <summary>A durable acceptor already committed through <paramref name="ack"/>: start the stream after it.</summary>
    public void SkipCommitted(ulong ack)
    {
        if (ack <= AckedThrough) return;
        if (_out.Count > 0)
            throw new InvalidOperationException("SkipCommitted runs before the first frame is enqueued.");
        AckedThrough = ack;
        _highestSent = ack;
        _peerReceived = ack;
        _nextSeq = ack + 1;
        Writer?.OnAcked(ack);
    }

    private TimeSpan BaseRto()
    {
        var rto = _srttMs < 0 ? _options.RtoMin : TimeSpan.FromMilliseconds(Math.Max(_options.RtoMin.TotalMilliseconds, 2 * _srttMs));
        return rto > _options.RtoMax ? _options.RtoMax : rto;
    }

    public void OnPeerWindow(ulong window)
    {
        PeerWindow = (long)Math.Min(window, int.MaxValue);
        Signal();
    }

    /// <summary>The retransmission timer. Returns true when frames were queued again.</summary>
    public bool OnTimer(long now)
    {
        var requeued = false;
        if (_rtoArmed && _options.Time.GetElapsedTime(_rtoArmedAt, now) >= _rto)
        {
            var goBack = _out.FindIndex(frame => frame.Sent && frame.Seq > _peerReceived);
            if (goBack >= 0)
            {
                for (var i = goBack; i < _out.Count; i++)
                {
                    if (!_out[i].Sent) continue;
                    _out[i].Sent = false;
                    _out[i].Retransmitted = true;
                }
                _firstUnsent = Math.Min(_firstUnsent, goBack);
                requeued = true;
            }
            else
            {
                // The peer has everything but has not committed it: probe with the last frame so it re-acks.
                var last = _out.FindLastIndex(frame => frame.Sent);
                if (last >= 0)
                {
                    _out[last].Sent = false;
                    _out[last].Retransmitted = true;
                    _firstUnsent = Math.Min(_firstUnsent, last);
                    requeued = true;
                }
            }
            _rto = _rto * 2 > _options.RtoMax ? _options.RtoMax : _rto * 2;
            _rtoArmedAt = now;
            _rtoArmed = requeued;
        }
        if (Phase == ChannelPhase.Opening && OpenSentAt != 0 && _options.Time.GetElapsedTime(OpenSentAt, now) >= _rto)
        {
            OpenDue = true;
            requeued = true;
        }
        if (_ackDueAt != 0 && now >= _ackDueAt)
        {
            AckNow = true;
            requeued = true;
        }
        return requeued;
    }

    /// <summary>A new connection of the same link session: resend what the peer has not received.</summary>
    public void OnResume(ulong peerReceived, long now)
    {
        if (peerReceived >= _nextSeq) peerReceived = _nextSeq - 1;
        _peerReceived = Math.Max(_peerReceived, peerReceived);
        for (var i = 0; i < _out.Count; i++)
        {
            var frame = _out[i];
            if (frame.Sent && frame.Seq > _peerReceived)
            {
                frame.Sent = false;
                frame.Retransmitted = true;
                _firstUnsent = Math.Min(_firstUnsent, i);
            }
        }
        _rtoArmed = _out.Any(frame => frame.Sent);
        _rtoArmedAt = now;
        _rto = BaseRto();
        if (Phase == ChannelPhase.Opening) OpenDue = true;
        if (_receivedAny) AckNow = true;
    }

    public void Signal()
    {
        var signal = SpaceSignal;
        SpaceSignal = NewSignal();
        signal.TrySetResult(true);
    }

    // ================================================================= receive side

    /// <summary>
    /// A DATA or CLOSE frame. In-order frames become deliveries (added to <paramref name="deliveries"/>);
    /// returns false when the channel must be reset because a message outgrew its reassembly cap.
    /// </summary>
    public bool Receive(LinkFrame frame, List<Delivery> deliveries, long now)
    {
        _receivedAny = true;
        if (RemoteCloseReceived && frame.Seq > _received)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel {Id}: a frame after the peer's CLOSE.");
        if (frame.Seq <= _received)
        {
            AckNow = true; // a duplicate: the peer missed our ack
            return true;
        }
        if (frame.Seq != _received + 1)
        {
            if (!_reorder.ContainsKey(frame.Seq) && _reorderBytes + frame.Payload.Length <= LocalWindow)
            {
                _reorder[frame.Seq] = frame with { Payload = frame.Payload.ToArray() };
                _reorderBytes += frame.Payload.Length;
            }
            return true;
        }
        if (!Accept(frame, deliveries)) return false;
        while (_reorder.Remove(_received + 1, out var next))
        {
            _reorderBytes -= next.Payload.Length;
            if (!Accept(next, deliveries)) return false;
        }
        Recommit(now);
        return true;
    }

    private bool Accept(LinkFrame frame, List<Delivery> deliveries)
    {
        _received = frame.Seq;
        _uncommitted.Enqueue((frame.Seq, frame.Payload.Length));
        _uncommittedBytes += frame.Payload.Length;
        if (_uncommittedBytes + _reorderBytes > LocalWindow + 2L * FrameCodec.MaxFrame)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel {Id}: the peer overran the {LocalWindow}-byte window.");

        if (frame.Kind == FrameKind.Close)
        {
            RemoteCloseReceived = true;
            _deliveryPoints.Enqueue(frame.Seq);
            deliveries.Add(new Delivery(frame.Seq, [], true, true));
            return true;
        }
        if (Class.Messages && !Durable)
        {
            if (!frame.IsFin || _assembly is not null)
            {
                _assembly ??= new MemoryStream();
                if (_assembly.Length + frame.Payload.Length > Class.ReassemblyCap) return false;
                _assembly.Write(frame.Payload.Span);
            }
            if (!frame.IsFin) return true;
            var message = _assembly?.ToArray() ?? frame.Payload.ToArray();
            _assembly = null;
            _deliveryPoints.Enqueue(frame.Seq);
            deliveries.Add(new Delivery(frame.Seq, message, true, false));
            return true;
        }
        _deliveryPoints.Enqueue(frame.Seq);
        deliveries.Add(new Delivery(frame.Seq, frame.Payload.ToArray(), frame.IsFin, false));
        return true;
    }

    /// <summary>The consumer returned from the delivery ending at <paramref name="seq"/>.</summary>
    public void MarkDelivered(ulong seq, long now)
    {
        if (_deliveryPoints.Count == 0 || _deliveryPoints.Peek() != seq)
            throw new InvalidOperationException($"Channel {Id}: delivery {seq} completed out of order.");
        _deliveryPoints.Dequeue();
        Recommit(now);
    }

    private void Recommit(long now)
    {
        var committed = _deliveryPoints.Count == 0 ? _received : _deliveryPoints.Peek() - 1;
        if (committed <= _committed) return;
        _committed = committed;
        while (_uncommitted.Count > 0 && _uncommitted.Peek().Seq <= _committed)
        {
            var (_, bytes) = _uncommitted.Dequeue();
            _uncommittedBytes -= bytes;
            _ackFrames++;
            _ackBytes += bytes;
        }
        if (_ackFrames >= _options.AckFrames || _ackBytes >= _options.AckBytes || RemoteCloseReceived)
            AckNow = true;
        else if (_ackDueAt == 0)
            _ackDueAt = now + (long)(_options.AckDelay.TotalSeconds * _options.Time.TimestampFrequency);
    }

    /// <summary>A standalone ACK: committed in the header, received-contiguous as the payload hint.</summary>
    public byte[] TakeAck()
    {
        var hint = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(hint, _received);
        TookAck();
        return FrameCodec.Encode(new LinkFrame(FrameKind.Ack, FrameFlags.AckValid, Id, 0, _committed, hint));
    }

    private void TookAck()
    {
        _lastAckSent = _committed;
        _ackFrames = 0;
        _ackBytes = 0;
        _ackDueAt = 0;
        AckNow = false;
    }

    public void SetLocalWindow(long window)
    {
        LocalWindow = window;
        WindowDue = true;
    }

    public byte[] TakeWindow()
    {
        WindowDue = false;
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)LocalWindow);
        return FrameCodec.Encode(new LinkFrame(FrameKind.Window, FrameFlags.None, Id, 0, 0, payload));
    }

    public static ulong ReadU64(ReadOnlyMemory<byte> payload)
        => payload.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(payload.Span) : 0;
}
