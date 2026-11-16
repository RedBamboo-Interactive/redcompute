using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>The scheduler in virtual time on a 10 MB/s link (design 8.1): deterministic, no clocks.</summary>
public sealed class SchedulerTests
{
    private const double BytesPerSecond = 10_000_000;
    private static double FrameTime(int bytes) => bytes / BytesPerSecond;

    [Fact]
    public void A_saturating_low_priority_stream_keeps_interactive_p99_under_one_64_KiB_frame_plus_5_ms()
    {
        var scheduler = new LinkScheduler();
        var chattyFrame = 16 * 1024 + FrameCodec.HeaderSize; // a priority-3 frame is cut at 16 KiB
        var arrivals = new Queue<double>();
        var latencies = new List<double>();
        var nextArrival = 0.0;
        var random = new Random(3);
        var now = 0.0;
        var interactiveBytes = 0L;
        var chattyBytes = 0L;
        while (now < 5.0)
        {
            while (nextArrival <= now)
            {
                arrivals.Enqueue(nextArrival);
                nextArrival += 0.001 + random.NextDouble() * 0.004; // a token every 1..5 ms
            }
            var interactiveSize = arrivals.Count > 0 ? 300 : 0;
            ReadOnlySpan<SchedulerEntry> entries = [new(1, interactiveSize), new(3, chattyFrame)];
            var pick = scheduler.Pick(entries);
            var size = entries[pick].NextFrameBytes;
            scheduler.OnSent(entries[pick].Priority, size);
            now += FrameTime(size);
            if (pick == 0)
            {
                latencies.Add(now - arrivals.Dequeue());
                interactiveBytes += size;
            }
            else
            {
                chattyBytes += size;
            }
        }
        latencies.Sort();
        var p99 = latencies[(int)(latencies.Count * 0.99)];
        Assert.True(p99 <= FrameTime(FrameCodec.MaxFrame) + 0.005, $"p99 {p99 * 1000:F2} ms");
        Assert.True(chattyBytes > interactiveBytes, "the chatty stream still uses the rest of the link");
    }

    [Theory]
    [InlineData(0, 64 * 1024, 3, 16 * 1024)]
    [InlineData(1, 64 * 1024, 2, 16 * 1024)]
    [InlineData(0, 300, 3, 16 * 1024)]
    [InlineData(2, 16 * 1024, 3, 16 * 1024)]
    public void A_backlogged_low_class_gets_at_least_a_tenth_of_the_bytes(int highPriority, int highFrame, int lowPriority, int lowFrame)
    {
        var scheduler = new LinkScheduler();
        long high = 0, low = 0;
        ReadOnlySpan<SchedulerEntry> entries = [new(highPriority, highFrame), new(lowPriority, lowFrame)];
        for (var i = 0; i < 400_000; i++)
        {
            var pick = scheduler.Pick(entries);
            scheduler.OnSent(entries[pick].Priority, entries[pick].NextFrameBytes);
            if (pick == 0) high += entries[pick].NextFrameBytes;
            else low += entries[pick].NextFrameBytes;
        }
        var share = (double)low / (high + low);
        Assert.True(share >= 0.099, $"low share {share:P2}");
        Assert.True(share <= 0.2, $"strict priority still rules: low share {share:P2}");
    }

    [Fact]
    public void Strict_priority_when_the_low_class_has_nothing_and_round_robin_inside_a_class()
    {
        var scheduler = new LinkScheduler();
        ReadOnlySpan<SchedulerEntry> entries = [new(2, 100), new(1, 100), new(2, 100), new(1, 100), new(3, 0)];
        var picks = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var pick = scheduler.Pick(entries);
            scheduler.OnSent(entries[pick].Priority, 100);
            picks.Add(pick);
        }
        Assert.Equal([1, 3, 1, 3], picks);
        Assert.Equal(-1, scheduler.Pick([new(0, 0), new(2, 0)]));
    }
}
