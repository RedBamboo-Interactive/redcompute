using RedCompute.NodeLink.Spool;

namespace RedCompute.NodeLink;

/// <summary>
/// The sending end of a durable stream (design 4.3): keyed by a stream key, numbered in its own seq
/// space for the stream's whole life, and kept in a spool until the receiver's committed ack covers it.
/// Writes never wait for the link. Whenever a link session starts, the owner attaches the writer
/// (<see cref="LinkSession.AttachDurableAsync"/>); the channel opens at the spool's oldest frame, the
/// receiver answers with what it already committed, and the stream continues after that, so nothing is
/// lost or delivered twice across reconnects and restarts of either side.
/// </summary>
public sealed class DurableStreamWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _closeSeq;
    private Action? _wake;

    public DurableStreamWriter(string streamKey, IStreamSpool spool, ChannelClass? @class = null, int? priority = null,
        LinkOptions? options = null)
    {
        StreamKey = streamKey;
        Spool = spool;
        Class = @class ?? ChannelClasses.TestStream;
        if (!Class.MayBeDurable) throw new ArgumentException($"Class {Class.Name} is never durable.", nameof(@class));
        Priority = priority ?? Class.Priority;
        var low = (options ?? LinkOptions.Default).LowPriorityFrameBytes;
        FrameBytes = Priority >= 2 ? Math.Min(low, FrameCodec.MaxPayload) : FrameCodec.MaxPayload;
        // A spool reopened after a restart may already hold the stream's CLOSE, or have trimmed it:
        // then the receiver committed everything and the stream is finished.
        _closeSeq = spool.CloseSeq;
        if (_closeSeq == 0)
            foreach (var record in spool.Read(spool.FirstSeq, long.MaxValue))
                if (record.Kind == FrameKind.Close) _closeSeq = record.Seq;
        if (_closeSeq != 0 && _closeSeq < spool.FirstSeq) _finished.TrySetResult();
    }

    public string StreamKey { get; }
    public IStreamSpool Spool { get; }
    public ChannelClass Class { get; }
    public int Priority { get; }
    public int FrameBytes { get; }

    public bool Completed { get { lock (_lock) return _closeSeq != 0; } }

    /// <summary>Completes when the stream's CLOSE is acked: the receiver committed everything.</summary>
    public Task Finished => _finished.Task;

    public long SpooledBytes { get { lock (_lock) return Spool.Bytes; } }

    /// <summary>Spools one message (split into frames, FIN on the last). Throws <see cref="SpoolOverflowException"/> past the cap.</summary>
    public void Write(ReadOnlySpan<byte> message)
    {
        lock (_lock)
        {
            if (_closeSeq != 0) throw new InvalidOperationException($"Stream {StreamKey} is complete.");
            var offset = 0;
            do
            {
                var take = Math.Min(FrameBytes, message.Length - offset);
                var last = offset + take >= message.Length;
                Spool.Append(FrameKind.Data, last ? FrameFlags.Fin : FrameFlags.None, message.Slice(offset, take));
                offset += take;
            } while (offset < message.Length);
        }
        Volatile.Read(ref _wake)?.Invoke();
    }

    /// <summary>Ends the stream: its CLOSE is the last spooled frame.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (_closeSeq != 0) return;
            _closeSeq = Spool.Append(FrameKind.Close, FrameFlags.Fin, []);
            Spool.SetCloseSeq(_closeSeq);
        }
        Volatile.Read(ref _wake)?.Invoke();
    }

    public void MaybeSync()
    {
        lock (_lock) Spool.MaybeSync();
    }

    internal void Attach(Action wake) => Volatile.Write(ref _wake, wake);

    internal void Detach(Action wake) => Interlocked.CompareExchange(ref _wake, null, wake);

    internal ulong FirstSeq { get { lock (_lock) return Spool.FirstSeq; } }

    internal ulong NextSeq { get { lock (_lock) return Spool.NextSeq; } }

    internal IReadOnlyList<SpoolRecord> Read(ulong fromSeq, long maxBytes)
    {
        lock (_lock) return Spool.Read(fromSeq, maxBytes);
    }

    internal void OnAcked(ulong ack)
    {
        bool finished;
        lock (_lock)
        {
            Spool.Trim(ack);
            finished = _closeSeq != 0 && ack >= _closeSeq;
        }
        if (finished) _finished.TrySetResult();
    }

    public void Dispose()
    {
        lock (_lock) Spool.Dispose();
    }
}
