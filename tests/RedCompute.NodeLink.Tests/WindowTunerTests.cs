using System.Diagnostics;
using System.Threading.Channels;
using RedCompute.NodeLink;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.NodeLink.Tests;

/// <summary>Window autotune (Phase 4 design 2.3, 6.1): toward 2 x rate x RTT, within 8 MiB per channel and 32 MiB per link.</summary>
public sealed class WindowTunerTests(ITestOutputHelper output)
{
    private const long KiB = 1024;
    private const long MiB = 1024 * 1024;
    private static readonly TimeSpan Rtt = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void It_grows_toward_twice_rate_times_rtt()
    {
        // 10 MB/s over 50 ms: 1 MB, in whole 64 KiB steps.
        Assert.Equal(16 * 64 * KiB, WindowTuner.Next(256 * KiB, 256 * KiB, 8 * MiB, 32 * MiB, 10_000_000, Rtt));
    }

    [Fact]
    public void A_window_that_limits_the_rate_doubles_until_the_channel_cap()
    {
        var window = 256 * KiB;
        var steps = 0;
        while (true)
        {
            var rate = window / Rtt.TotalSeconds; // the window is the bottleneck
            var next = WindowTuner.Next(window, 256 * KiB, 8 * MiB, 32 * MiB, rate, Rtt);
            if (next == window) break;
            Assert.Equal(Math.Min(8 * MiB, window * 2), next);
            window = next;
            steps++;
        }
        Assert.Equal(8 * MiB, window);
        Assert.Equal(5, steps);
    }

    [Fact]
    public void The_link_budget_bounds_the_growth()
    {
        Assert.Equal(3 * MiB, WindowTuner.Next(1 * MiB, 256 * KiB, 8 * MiB, 3 * MiB, 100_000_000, Rtt));
        // A budget others already took leaves the class window, never less.
        Assert.Equal(256 * KiB, WindowTuner.Next(256 * KiB, 256 * KiB, 8 * MiB, 0, 100_000_000, Rtt));
        // A window over a budget that shrank gives the excess back.
        Assert.Equal(2 * MiB, WindowTuner.Next(4 * MiB, 256 * KiB, 8 * MiB, 2 * MiB, 4 * MiB / Rtt.TotalSeconds / 2, Rtt));
    }

    [Fact]
    public void A_slow_consumer_shrinks_it_back_but_never_below_the_class_window()
    {
        Assert.Equal(256 * KiB, WindowTuner.Next(8 * MiB, 256 * KiB, 8 * MiB, 32 * MiB, 1000, Rtt));
        Assert.Equal(256 * KiB, WindowTuner.Next(8 * MiB, 256 * KiB, 8 * MiB, 32 * MiB, 0, Rtt));
        Assert.Equal(2 * MiB, WindowTuner.Next(8 * MiB, 256 * KiB, 8 * MiB, 32 * MiB, 1 * MiB / Rtt.TotalSeconds, Rtt));
        Assert.Equal(256 * KiB, WindowTuner.Next(100 * KiB, 256 * KiB, 8 * MiB, 32 * MiB, 0, Rtt));
    }

    [Fact]
    public void Noise_around_the_window_changes_nothing()
    {
        var window = 2 * MiB;
        foreach (var factor in new[] { 0.55, 0.8, 1.0, 1.2 })
            Assert.Equal(window, WindowTuner.Next(window, 256 * KiB, 8 * MiB, 32 * MiB, window * factor / 2 / Rtt.TotalSeconds, Rtt));
        // A rate that is not a number measures nothing: back to the class window.
        Assert.Equal(256 * KiB, WindowTuner.Next(window, 256 * KiB, 8 * MiB, 32 * MiB, double.NaN, Rtt));
    }

    /// <summary>
    /// Over a 40 ms round trip a <c>port</c> channel's window grows past 1 MiB while a fast consumer drains it, and
    /// shrinks back to the class window when the consumer slows down; the link never closes (a shrink is never an
    /// overrun) and every byte arrives in order.
    /// </summary>
    [Fact]
    public async Task Over_a_slow_round_trip_the_window_grows_then_shrinks_with_the_consumer()
    {
        var sink = new PacedSink();
        var options = new LinkOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            MissedHeartbeats = 10,
            AckDelay = TimeSpan.FromMilliseconds(5),
            RtoMin = TimeSpan.FromMilliseconds(200),
            RtoMax = TimeSpan.FromSeconds(2),
            HandshakeTimeout = TimeSpan.FromSeconds(2),
            Tick = TimeSpan.FromMilliseconds(5),
        };
        LinkPair? pair = null;
        pair = new LinkPair(serverHandler: new TestLinkHandler { Accept = _ => new ChannelAcceptance(sink) }, options: options,
            dial: ct =>
            {
                var (node, server) = LinkPipe.Create(capacity: 4096);
                var hub = pair!.Server;
                lock (pair.ServerRuns) pair.ServerRuns.Add(Task.Run(() => hub.RunAsync(new LatencyTransport(server, TimeSpan.FromMilliseconds(20)), LinkPair.NodeId, CancellationToken.None)));
                return Task.FromResult<ILinkTransport>(new LatencyTransport(node, TimeSpan.FromMilliseconds(20)));
            });
        await using (pair)
        {
            pair.Start();
            var node = await pair.OnlineAsync();
            var channel = await node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.Port.Name), new RecordingHandler());
            var serverChannel = await sink.Channel.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(256 * KiB, serverChannel.ReceiveWindow);

            var chunk = new byte[16 * 1024];
            long sent = 0;
            long maxWindow = 0;
            var watch = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Fast phase: until the window passed 1 MiB.
            while (serverChannel.ReceiveWindow <= 1 * MiB)
            {
                stop.Token.ThrowIfCancellationRequested();
                Fill(chunk, sent);
                await channel.SendAsync(chunk, stop.Token);
                sent += chunk.Length;
            }
            maxWindow = serverChannel.ReceiveWindow;
            output.WriteLine($"window {maxWindow / KiB} KiB after {sent / KiB} KiB in {watch.ElapsedMilliseconds} ms");

            // Slow phase: the consumer takes a frame every 20 ms; the window comes back down.
            sink.Delay = TimeSpan.FromMilliseconds(20);
            while (serverChannel.ReceiveWindow > 256 * KiB)
            {
                stop.Token.ThrowIfCancellationRequested();
                Fill(chunk, sent);
                await channel.SendAsync(chunk, stop.Token);
                sent += chunk.Length;
            }
            output.WriteLine($"window back to {serverChannel.ReceiveWindow / KiB} KiB after {sent / KiB} KiB in {watch.ElapsedMilliseconds} ms");
            sink.Delay = TimeSpan.Zero;
            channel.Close();
            await sink.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(sent, sink.Bytes);
            Assert.Null(sink.Error);
            Assert.True(maxWindow <= 8 * MiB);
            Assert.Equal(LinkClientStatus.Online, pair.Client.Status);
            Assert.True(pair.ServerRuns.Count == 1, "one connection");
        }
    }

    private static void Fill(byte[] chunk, long offset)
    {
        for (var i = 0; i < chunk.Length; i++) chunk[i] = (byte)((offset + i) * 31 % 251);
    }

    private sealed class PacedSink : IChannelHandler, IChannelOpenedHandler
    {
        public TaskCompletionSource<LinkChannelHandle> Channel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Delay { get; set; }
        public long Bytes;
        public string? Error;

        public void OnOpened(LinkChannelHandle channel) => Channel.TrySetResult(channel);

        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            var span = data.Payload.Span;
            for (var i = 0; i < span.Length; i++)
                if (span[i] != (byte)((Bytes + i) * 31 % 251))
                {
                    Error ??= $"byte {Bytes + i} out of order";
                    break;
                }
            Bytes += span.Length;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            channel.Close();
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message) => Closed.TrySetException(new Exception(code + ": " + message));
    }
}

/// <summary>A transport whose messages arrive <c>delay</c> after they were sent, pipelined (a long fat pipe).</summary>
internal sealed class LatencyTransport : ILinkTransport
{
    private readonly ILinkTransport _inner;
    private readonly TimeSpan _delay;
    private readonly Channel<(long At, byte[]? Message, Exception? Error)> _arrivals = Channel.CreateUnbounded<(long, byte[]?, Exception?)>();
    private readonly CancellationTokenSource _stop = new();

    public LatencyTransport(ILinkTransport inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var message = await _inner.ReceiveAsync(_stop.Token);
                _arrivals.Writer.TryWrite((Stopwatch.GetTimestamp(), message, null));
            }
        }
        catch (Exception error)
        {
            _arrivals.Writer.TryWrite((Stopwatch.GetTimestamp(), null, error));
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => _inner.SendAsync(message, ct);

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var (at, message, error) = await _arrivals.Reader.ReadAsync(ct);
        var wait = _delay - Stopwatch.GetElapsedTime(at);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        if (error is not null) throw error;
        return message!;
    }

    public ValueTask CloseAsync(int code, string? reason, CancellationToken ct)
    {
        _stop.Cancel();
        return _inner.CloseAsync(code, reason, ct);
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        return _inner.DisposeAsync();
    }
}
