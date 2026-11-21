using System.Threading.Channels;

namespace RedCompute.NodeLink.Forward;

/// <summary>
/// A bounded queue of byte chunks with a <see cref="Stream"/> on its read side: how a channel's deliveries become a
/// body stream. The writer (a channel handler) waits while it is full, so the link commits a frame only once the
/// reader made room, and backpressure reaches the peer's credit window. At most <c>capacity</c> chunks wait here.
/// </summary>
internal sealed class ChunkPipe
{
    private readonly Channel<ReadOnlyMemory<byte>> _chunks;
    private readonly CancellationTokenSource _discard = new();
    private volatile bool _discarding;

    public ChunkPipe(int capacity = 16, Action? onReaderGone = null)
    {
        _chunks = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Stream = new ReadStream(this, onReaderGone);
    }

    public Stream Stream { get; }

    /// <summary>Queues a copy of <paramref name="data"/>; a discarding pipe drops it.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_discarding || data.IsEmpty) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _discard.Token);
        try
        {
            await _chunks.Writer.WriteAsync(data.ToArray(), linked.Token);
        }
        catch (OperationCanceledException) when (_discarding && !ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
            // the reader completed or failed the pipe: nothing more is wanted
        }
    }

    /// <summary>Ends the stream: the reader sees end-of-stream, or <paramref name="error"/> once it read what is queued.</summary>
    public void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

    /// <summary>The reader is done for good: what comes later is dropped and a waiting writer goes on.</summary>
    public void Discard()
    {
        _discarding = true;
        _chunks.Writer.TryComplete();
        try
        {
            _discard.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        while (_chunks.Reader.TryRead(out _))
        {
        }
    }

    public bool Completed => _chunks.Reader.Completion.IsCompleted;

    private sealed class ReadStream(ChunkPipe pipe, Action? onReaderGone) : Stream
    {
        private ReadOnlyMemory<byte> _current;
        private bool _ended;
        private int _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            while (_current.IsEmpty)
            {
                if (_ended) return 0;
                try
                {
                    if (!await pipe._chunks.Reader.WaitToReadAsync(ct))
                    {
                        await pipe._chunks.Reader.Completion; // throws the writer's error, if it failed the pipe
                        _ended = true;
                        return 0;
                    }
                }
                catch (ChannelClosedException closed) when (closed.InnerException is { } inner)
                {
                    throw inner;
                }
                if (pipe._chunks.Reader.TryRead(out var chunk)) _current = chunk;
            }
            var take = Math.Min(buffer.Length, _current.Length);
            _current[..take].CopyTo(buffer);
            _current = _current[take..];
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                var early = !_ended;
                pipe.Discard();
                if (early) onReaderGone?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
