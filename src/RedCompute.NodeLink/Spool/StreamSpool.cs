namespace RedCompute.NodeLink.Spool;

/// <summary>One spooled frame of a durable stream: its stream seq, kind (DATA or CLOSE), flags and bytes.</summary>
public readonly record struct SpoolRecord(ulong Seq, FrameKind Kind, FrameFlags Flags, byte[] Payload);

/// <summary>
/// The unacked frames of one durable stream (design 4.6), kept until the receiver's committed ack
/// covers them. Seqs start at 1 and never repeat. Implementations are not thread-safe; the
/// <see cref="DurableStreamWriter"/> serialises access.
/// </summary>
public interface IStreamSpool : IDisposable
{
    /// <summary>The oldest seq still held, or <see cref="NextSeq"/> when empty.</summary>
    ulong FirstSeq { get; }

    /// <summary>The seq the next append gets.</summary>
    ulong NextSeq { get; }

    /// <summary>Payload bytes held.</summary>
    long Bytes { get; }

    /// <summary>Appends one frame and returns its seq. Throws <see cref="SpoolOverflowException"/> past a cap.</summary>
    ulong Append(FrameKind kind, FrameFlags flags, ReadOnlySpan<byte> payload);

    /// <summary>Records from <paramref name="fromSeq"/> on, at least one when any, at most about <paramref name="maxBytes"/>.</summary>
    IReadOnlyList<SpoolRecord> Read(ulong fromSeq, long maxBytes);

    /// <summary>Forgets every record up to and including <paramref name="throughSeq"/>.</summary>
    void Trim(ulong throughSeq);

    /// <summary>The seq of the stream's CLOSE once appended (0 before), kept even after it is trimmed.</summary>
    ulong CloseSeq { get; }

    /// <summary>Records the CLOSE's seq durably, so a reopened, fully trimmed spool still knows the stream ended.</summary>
    void SetCloseSeq(ulong seq);

    /// <summary>Makes every appended record durable now (a no-op in memory).</summary>
    void Sync();

    /// <summary>Syncs when the time-based cadence is due.</summary>
    void MaybeSync();
}

/// <summary>The spool would pass its cap: the owner must stop the producer (design 4.6: never drop silently).</summary>
public sealed class SpoolOverflowException(string message) : Exception(message);

/// <summary>How full a stream's spool is against its policy cap.</summary>
public enum SpoolPressure
{
    Normal,
    /// <summary>At 90% of the cap: run the policy's onLimit (park).</summary>
    Park,
    /// <summary>Past the cap: kill at once.</summary>
    Overflow,
}

/// <summary>A cap shared by every spool of one node (1 GiB by default).</summary>
public sealed class SpoolBudget(long capBytes = SpoolBudget.DefaultCap)
{
    public const long DefaultCap = 1L << 30;
    private long _used;

    public long CapBytes { get; } = capBytes;
    public long UsedBytes => Interlocked.Read(ref _used);

    public bool TryReserve(long bytes)
    {
        while (true)
        {
            var used = Interlocked.Read(ref _used);
            if (used + bytes > CapBytes) return false;
            if (Interlocked.CompareExchange(ref _used, used + bytes, used) == used) return true;
        }
    }

    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);

    public static SpoolPressure Evaluate(long bytes, long capBytes)
        => bytes > capBytes ? SpoolPressure.Overflow : bytes * 10 >= capBytes * 9 ? SpoolPressure.Park : SpoolPressure.Normal;
}

/// <summary>A spool in memory: ephemeral producers, tests, and a node without a disk spool.</summary>
public sealed class MemoryStreamSpool(ulong firstSeq = 1, SpoolBudget? budget = null) : IStreamSpool
{
    private readonly LinkedList<SpoolRecord> _records = new();

    public ulong FirstSeq => _records.First?.Value.Seq ?? NextSeq;
    public ulong NextSeq { get; private set; } = firstSeq;
    public long Bytes { get; private set; }

    public ulong Append(FrameKind kind, FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        if (budget is not null && !budget.TryReserve(payload.Length))
            throw new SpoolOverflowException($"The node's spool budget of {budget.CapBytes} bytes is full.");
        var seq = NextSeq++;
        _records.AddLast(new SpoolRecord(seq, kind, flags, payload.ToArray()));
        Bytes += payload.Length;
        return seq;
    }

    public IReadOnlyList<SpoolRecord> Read(ulong fromSeq, long maxBytes)
    {
        var result = new List<SpoolRecord>();
        long bytes = 0;
        foreach (var record in _records)
        {
            if (record.Seq < fromSeq) continue;
            if (result.Count > 0 && bytes + record.Payload.Length > maxBytes) break;
            result.Add(record);
            bytes += record.Payload.Length;
        }
        return result;
    }

    public void Trim(ulong throughSeq)
    {
        while (_records.First is { } first && first.Value.Seq <= throughSeq)
        {
            Bytes -= first.Value.Payload.Length;
            budget?.Release(first.Value.Payload.Length);
            _records.RemoveFirst();
        }
    }

    public ulong CloseSeq { get; private set; }

    public void SetCloseSeq(ulong seq) => CloseSeq = seq;

    public void Sync()
    {
    }

    public void MaybeSync()
    {
    }

    public void Dispose()
    {
        budget?.Release(Bytes);
        Bytes = 0;
        _records.Clear();
    }
}
