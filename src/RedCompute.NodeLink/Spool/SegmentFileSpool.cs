using System.Buffers.Binary;
using System.Globalization;

namespace RedCompute.NodeLink.Spool;

public sealed class SegmentFileSpoolOptions
{
    /// <summary>A segment file rolls over past this size.</summary>
    public long SegmentBytes { get; init; } = 4L << 20;

    /// <summary>fsync after this many unsynced bytes ...</summary>
    public long SyncBytes { get; init; } = 64 * 1024;

    /// <summary>... or this long after the first unsynced append (design 4.6: 64 KiB or 200 ms).</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Payloads kept in memory too, so a replay rarely reads the disk (8 MiB per stream).</summary>
    public long MemoryCacheBytes { get; init; } = 8L << 20;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// A durable stream's spool on the node's disk (design 4.6): append-only segment files
/// <c>{firstSeq:D20}.seg</c> in one directory, each record <c>u32 length, u64 seq, u8 kind, u16 flags,
/// u32 crc32, payload</c>. Records are fsynced every <see cref="SegmentFileSpoolOptions.SyncBytes"/> or
/// <see cref="SegmentFileSpoolOptions.SyncInterval"/>, so a crash loses nothing synced. On open, a torn
/// or corrupt tail is truncated at the last whole record; seqs continue after it. Trimming deletes whole
/// segments and records the trim point in <c>trim</c>, so a reopened spool never replays below it.
/// </summary>
public sealed class SegmentFileSpool : IStreamSpool
{
    private const int HeaderBytes = 19;
    private const string TrimFile = "trim";
    private const string CloseFile = "close";

    private readonly string _directory;
    private readonly SegmentFileSpoolOptions _options;
    private readonly SpoolBudget? _budget;
    private readonly List<Segment> _segments = [];
    private readonly LinkedList<Entry> _entries = new();
    private FileStream? _writer;
    private long _unsyncedBytes;
    private long _firstUnsyncedAt;
    private long _cachedBytes;
    private ulong _trimmedThrough;

    private sealed class Segment(string path, ulong firstSeq)
    {
        public string Path { get; } = path;
        public ulong FirstSeq { get; } = firstSeq;
        public ulong LastSeq { get; set; }
        public long Length { get; set; }
    }

    private sealed class Entry(ulong seq, FrameKind kind, FrameFlags flags, Segment segment, long offset, int length)
    {
        public ulong Seq { get; } = seq;
        public FrameKind Kind { get; } = kind;
        public FrameFlags Flags { get; } = flags;
        public Segment Segment { get; } = segment;
        public long Offset { get; } = offset;
        public int Length { get; } = length;
        public byte[]? Cached { get; set; }
    }

    public SegmentFileSpool(string directory, SegmentFileSpoolOptions? options = null, SpoolBudget? budget = null)
    {
        _directory = directory;
        _options = options ?? new SegmentFileSpoolOptions();
        _budget = budget;
        Directory.CreateDirectory(directory);
        Recover();
    }

    public ulong FirstSeq => _entries.First?.Value.Seq ?? NextSeq;
    public ulong NextSeq { get; private set; } = 1;
    public long Bytes { get; private set; }

    /// <summary>Records dropped from a torn or corrupt tail at open (for diagnostics and tests).</summary>
    public long RecoveredTruncatedBytes { get; private set; }

    private void Recover()
    {
        var trimPath = Path.Combine(_directory, TrimFile);
        if (File.Exists(trimPath) && ulong.TryParse(File.ReadAllText(trimPath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var trimmed))
            _trimmedThrough = trimmed;
        NextSeq = _trimmedThrough + 1;
        var closePath = Path.Combine(_directory, CloseFile);
        if (File.Exists(closePath) && ulong.TryParse(File.ReadAllText(closePath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var close))
            CloseSeq = close;

        var files = Directory.GetFiles(_directory, "*.seg")
            .Select(path => (Path: path, First: ulong.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var first) ? first : 0))
            .Where(file => file.First > 0)
            .OrderBy(file => file.First)
            .ToList();
        var broken = false;
        foreach (var (path, first) in files)
        {
            // Segments continue each other; a gap (a lost or foreign file) ends the valid spool.
            if (broken || (_segments.Count > 0 && first != _segments[^1].LastSeq + 1))
            {
                broken = true;
                File.Delete(path);
                continue;
            }
            var segment = new Segment(path, first);
            var valid = ReadSegment(segment);
            var length = new FileInfo(path).Length;
            if (valid < length)
            {
                RecoveredTruncatedBytes += length - valid;
                using (var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    truncate.SetLength(valid);
                broken = true;
            }
            segment.Length = valid;
            if (segment.LastSeq == 0 && segment.Length == 0) File.Delete(path);
            else if (segment.LastSeq <= _trimmedThrough && segment.LastSeq != 0) File.Delete(path);
            else _segments.Add(segment);
        }
    }

    /// <summary>Indexes a segment's whole records; returns the length of its valid prefix.</summary>
    private long ReadSegment(Segment segment)
    {
        using var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[HeaderBytes];
        long offset = 0;
        var expectedSeq = segment.FirstSeq;
        while (true)
        {
            if (stream.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false) < HeaderBytes) return offset;
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var seq = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4));
            var kind = (FrameKind)header[12];
            var flags = (FrameFlags)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(13));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(15));
            if (length is < 0 or > FrameCodec.MaxPayload || kind is not (FrameKind.Data or FrameKind.Close)) return offset;
            // Seqs are contiguous across segments; anything else is a corrupt tail.
            // A segment holds contiguous seqs from the one in its name; anything else is a corrupt tail.
            if (seq != expectedSeq) return offset;
            expectedSeq++;
            var payload = new byte[length];
            if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length) return offset;
            if (Crc(header.AsSpan(4, 11), payload) != crc) return offset;

            segment.LastSeq = seq;
            if (seq > _trimmedThrough)
            {
                var entry = new Entry(seq, kind, flags, segment, offset + HeaderBytes, length);
                Cache(entry, payload);
                _entries.AddLast(entry);
                Bytes += length;
                _budget?.TryReserve(length);
                NextSeq = seq + 1;
            }
            offset += HeaderBytes + length;
        }
    }

    public ulong Append(FrameKind kind, FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > FrameCodec.MaxPayload) throw new ArgumentException("A spooled frame is at most one frame payload.");
        if (_budget is not null && !_budget.TryReserve(payload.Length))
            throw new SpoolOverflowException($"The node's spool budget of {_budget.CapBytes} bytes is full.");

        var seq = NextSeq;
        var segment = WritableSegment(seq);
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), seq);
        header[12] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(13), (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(15), Crc(header.AsSpan(4, 11), payload));
        _writer!.Write(header);
        _writer.Write(payload);

        var entry = new Entry(seq, kind, flags, segment, segment.Length + HeaderBytes, payload.Length);
        Cache(entry, payload.ToArray());
        _entries.AddLast(entry);
        segment.Length += HeaderBytes + payload.Length;
        segment.LastSeq = seq;
        NextSeq = seq + 1;
        Bytes += payload.Length;

        if (_unsyncedBytes == 0) _firstUnsyncedAt = _options.Time.GetTimestamp();
        _unsyncedBytes += HeaderBytes + payload.Length;
        if (_unsyncedBytes >= _options.SyncBytes) Sync();
        else MaybeSync();
        return seq;
    }

    private Segment WritableSegment(ulong seq)
    {
        var current = _segments.Count > 0 ? _segments[^1] : null;
        if (current is not null && current.Length < _options.SegmentBytes)
        {
            _writer ??= new FileStream(current.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return current;
        }
        if (_writer is not null)
        {
            Sync();
            _writer.Dispose();
            _writer = null;
        }
        var segment = new Segment(Path.Combine(_directory, seq.ToString("D20", CultureInfo.InvariantCulture) + ".seg"), seq);
        _segments.Add(segment);
        _writer = new FileStream(segment.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return segment;
    }

    private void Cache(Entry entry, byte[] payload)
    {
        if (_cachedBytes + payload.Length > _options.MemoryCacheBytes) return;
        entry.Cached = payload;
        _cachedBytes += payload.Length;
    }

    public IReadOnlyList<SpoolRecord> Read(ulong fromSeq, long maxBytes)
    {
        var result = new List<SpoolRecord>();
        long bytes = 0;
        FileStream? reader = null;
        try
        {
            foreach (var entry in _entries)
            {
                if (entry.Seq < fromSeq) continue;
                if (result.Count > 0 && bytes + entry.Length > maxBytes) break;
                var payload = entry.Cached;
                if (payload is null)
                {
                    _writer?.Flush();
                    if (reader is null || reader.Name != entry.Segment.Path)
                    {
                        reader?.Dispose();
                        reader = new FileStream(entry.Segment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    payload = new byte[entry.Length];
                    reader.Position = entry.Offset;
                    reader.ReadExactly(payload);
                }
                result.Add(new SpoolRecord(entry.Seq, entry.Kind, entry.Flags, payload));
                bytes += entry.Length;
            }
        }
        finally
        {
            reader?.Dispose();
        }
        return result;
    }

    public void Trim(ulong throughSeq)
    {
        if (throughSeq <= _trimmedThrough) return;
        while (_entries.First is { } first && first.Value.Seq <= throughSeq)
        {
            Bytes -= first.Value.Length;
            _budget?.Release(first.Value.Length);
            if (first.Value.Cached is not null) _cachedBytes -= first.Value.Length;
            _entries.RemoveFirst();
        }
        _trimmedThrough = Math.Min(throughSeq, NextSeq - 1);

        var deleted = false;
        while (_segments.Count > 1 && _segments[0].LastSeq <= _trimmedThrough)
        {
            File.Delete(_segments[0].Path);
            _segments.RemoveAt(0);
            deleted = true;
        }
        if (deleted) WriteTrimMarker();
    }

    private void WriteTrimMarker() => WriteMarker(TrimFile, _trimmedThrough);

    public ulong CloseSeq { get; private set; }

    public void SetCloseSeq(ulong seq)
    {
        Sync();
        CloseSeq = seq;
        WriteMarker(CloseFile, seq);
    }

    private void WriteMarker(string name, ulong value)
    {
        var path = Path.Combine(_directory, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, value.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, path, overwrite: true);
    }

    public void Sync()
    {
        if (_writer is null || _unsyncedBytes == 0) return;
        _writer.Flush(flushToDisk: true);
        _unsyncedBytes = 0;
    }

    public void MaybeSync()
    {
        if (_unsyncedBytes > 0 && _options.Time.GetElapsedTime(_firstUnsyncedAt) >= _options.SyncInterval) Sync();
    }

    public void Dispose()
    {
        Sync();
        _writer?.Dispose();
        _writer = null;
        if (_trimmedThrough > 0) WriteTrimMarker();
        _budget?.Release(Bytes);
    }

    /// <summary>Deletes a stream's spool directory once the stream is finished.</summary>
    public static void Delete(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // CRC-32 (IEEE), so the spool needs no package.
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Crc(ReadOnlySpan<byte> head, ReadOnlySpan<byte> payload)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in head) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in payload) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
