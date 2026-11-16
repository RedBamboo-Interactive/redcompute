using RedCompute.NodeLink;
using RedCompute.NodeLink.Spool;
using Xunit;

namespace RedCompute.NodeLink.Tests;

public sealed class SpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nodelink-spool-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SegmentFileSpoolOptions Options(long segment = 4096) => new() { SegmentBytes = segment, Time = _time };

    private static byte[] Payload(ulong seq, int length)
    {
        var bytes = new byte[length];
        new Random((int)seq).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Records_roll_over_segments_read_back_in_order_and_trim_deletes_whole_segments()
    {
        using var spool = new SegmentFileSpool(_root, Options());
        for (ulong seq = 1; seq <= 50; seq++)
            Assert.Equal(seq, spool.Append(FrameKind.Data, seq % 5 == 0 ? FrameFlags.Fin : FrameFlags.None, Payload(seq, 300)));
        Assert.True(Directory.GetFiles(_root, "*.seg").Length > 3);

        var all = spool.Read(1, long.MaxValue);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (ulong)i), all.Select(r => r.Seq));
        Assert.All(all, r => Assert.Equal(Payload(r.Seq, 300), r.Payload));
        Assert.Equal(FrameFlags.Fin, all[4].Flags);

        var files = Directory.GetFiles(_root, "*.seg").Length;
        spool.Trim(30);
        Assert.Equal(31UL, spool.FirstSeq);
        Assert.True(Directory.GetFiles(_root, "*.seg").Length < files);
        Assert.Equal(20 * 300, spool.Bytes);
        Assert.Equal(2, spool.Read(31, 600).Count);
    }

    [Fact]
    public void A_reopened_spool_continues_after_its_last_record_and_never_replays_below_the_trim()
    {
        using (var spool = new SegmentFileSpool(_root, Options()))
        {
            for (ulong seq = 1; seq <= 40; seq++) spool.Append(FrameKind.Data, FrameFlags.Fin, Payload(seq, 200));
            spool.Trim(25);
        }
        using (var reopened = new SegmentFileSpool(_root, Options()))
        {
            Assert.Equal(26UL, reopened.FirstSeq);
            Assert.Equal(41UL, reopened.NextSeq);
            Assert.Equal(Payload(26, 200), reopened.Read(26, 1)[0].Payload);
            Assert.Equal(41UL, reopened.Append(FrameKind.Close, FrameFlags.Fin, []));
        }
    }

    [Fact]
    public void A_torn_tail_is_cut_at_the_last_whole_record_with_no_duplicate_and_no_loss_before_it()
    {
        using (var spool = new SegmentFileSpool(_root, Options(segment: 1 << 20)))
            for (ulong seq = 1; seq <= 10; seq++) spool.Append(FrameKind.Data, FrameFlags.Fin, Payload(seq, 1000));
        var file = Directory.GetFiles(_root, "*.seg").Single();
        var length = new FileInfo(file).Length;
        using (var stream = new FileStream(file, FileMode.Open)) stream.SetLength(length - 500); // mid-record

        using var reopened = new SegmentFileSpool(_root, Options(segment: 1 << 20));
        Assert.Equal(Enumerable.Range(1, 9).Select(i => (ulong)i), reopened.Read(1, long.MaxValue).Select(r => r.Seq));
        Assert.True(reopened.RecoveredTruncatedBytes > 0);
        Assert.Equal(10UL, reopened.Append(FrameKind.Data, FrameFlags.Fin, Payload(10, 1000)));
        Assert.Equal(10, reopened.Read(1, long.MaxValue).Count);
    }

    [Fact]
    public void A_corrupt_record_ends_the_valid_spool_there()
    {
        using (var spool = new SegmentFileSpool(_root, Options(segment: 1 << 20)))
            for (ulong seq = 1; seq <= 6; seq++) spool.Append(FrameKind.Data, FrameFlags.Fin, Payload(seq, 100));
        var file = Directory.GetFiles(_root, "*.seg").Single();
        var bytes = File.ReadAllBytes(file);
        bytes[3 * 119 + 19 + 5] ^= 0xFF; // a payload byte of the fourth record
        File.WriteAllBytes(file, bytes);

        using var reopened = new SegmentFileSpool(_root, Options(segment: 1 << 20));
        Assert.Equal([1UL, 2, 3], reopened.Read(1, long.MaxValue).Select(r => r.Seq));
        Assert.Equal(4UL, reopened.NextSeq);
    }

    [Fact]
    public void Appends_sync_every_64_KiB_or_200_ms()
    {
        using var spool = new SegmentFileSpool(_root, new SegmentFileSpoolOptions { Time = _time });
        var file = () => Directory.GetFiles(_root, "*.seg").Single();
        spool.Append(FrameKind.Data, FrameFlags.Fin, new byte[1000]);
        // Not yet synced: the bytes may still be in the process. Past 200 ms the next check syncs.
        _time.Advance(TimeSpan.FromMilliseconds(199));
        spool.MaybeSync();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        spool.MaybeSync();
        Assert.Equal(1000 + 19, new FileInfo(file()).Length);
        for (var i = 0; i < 70; i++) spool.Append(FrameKind.Data, FrameFlags.Fin, new byte[1000]);
        Assert.True(new FileInfo(file()).Length >= 64 * 1024);
    }

    [Fact]
    public void The_budget_refuses_past_its_cap_and_pressure_parks_at_90_percent()
    {
        var budget = new SpoolBudget(10_000);
        using var spool = new MemoryStreamSpool(budget: budget);
        for (var i = 0; i < 9; i++) spool.Append(FrameKind.Data, FrameFlags.Fin, new byte[1000]);
        Assert.Equal(SpoolPressure.Park, SpoolBudget.Evaluate(spool.Bytes, budget.CapBytes));
        Assert.Equal(SpoolPressure.Normal, SpoolBudget.Evaluate(8_999, 10_000));
        spool.Append(FrameKind.Data, FrameFlags.Fin, new byte[1000]);
        Assert.Throws<SpoolOverflowException>(() => spool.Append(FrameKind.Data, FrameFlags.Fin, new byte[1]));
        spool.Trim(5);
        Assert.Equal(5000, budget.UsedBytes);
        Assert.Equal(SpoolPressure.Overflow, SpoolBudget.Evaluate(10_001, 10_000));
    }

    [Fact]
    public void The_link_loss_clock_parks_after_the_policy_time_or_at_90_percent_and_kills_on_overflow()
    {
        var policy = LinkLossPolicy.Default;
        Assert.Equal(TimeSpan.FromMinutes(15), policy.KeepRunningFor);
        var clock = new LinkLossClock(_time);
        Assert.Equal(LinkLossVerdict.KeepRunning, clock.Evaluate(policy, 0));
        clock.OnLinkLost();
        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(LinkLossVerdict.KeepRunning, clock.Evaluate(policy, 1000));
        Assert.Equal(LinkLossVerdict.Park, clock.Evaluate(policy, (policy.SpoolCapBytes * 9 + 9) / 10));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LinkLossVerdict.Park, clock.Evaluate(policy, 0));
        clock.OnLinkRestored();
        Assert.Equal(LinkLossVerdict.KeepRunning, clock.Evaluate(policy, 0));
        Assert.Equal(LinkLossVerdict.Kill, clock.Evaluate(policy, policy.SpoolCapBytes + 1));

        var json = System.Text.Json.JsonSerializer.Serialize(policy);
        Assert.Equal("""{"keepRunning":"PT15M","spoolCapBytes":268435456,"onLimit":{"action":"graceful-stop","closeStdin":true,"graceSeconds":60}}""", json);
    }

    [Fact]
    public void A_finished_stream_reopened_from_a_fully_trimmed_spool_is_still_finished()
    {
        using (var writer = new DurableStreamWriter("proc/2/stdout", new SegmentFileSpool(_root, Options())))
        {
            writer.Write([1, 2, 3]);
            writer.Complete();
            writer.Spool.Trim(writer.Spool.NextSeq - 1);
        }
        using var again = new DurableStreamWriter("proc/2/stdout", new SegmentFileSpool(_root, Options()));
        Assert.True(again.Completed);
        Assert.True(again.Finished.IsCompleted);
        Assert.Equal(0, again.SpooledBytes);
    }

    [Fact]
    public void A_durable_writer_reopened_from_disk_knows_its_close()
    {
        using (var writer = new DurableStreamWriter("proc/1/stdout", new SegmentFileSpool(_root, Options())))
        {
            writer.Write(new byte[100_000]);
            writer.Complete();
        }
        using var again = new DurableStreamWriter("proc/1/stdout", new SegmentFileSpool(_root, Options()));
        Assert.True(again.Completed);
        Assert.Throws<InvalidOperationException>(() => again.Write([1]));
        var records = again.Spool.Read(1, long.MaxValue);
        Assert.Equal(FrameKind.Close, records[^1].Kind);
        Assert.Equal(100_000, records.Where(r => r.Kind == FrameKind.Data).Sum(r => r.Payload.Length));
        Assert.All(records.Where(r => r.Kind == FrameKind.Data), r => Assert.True(r.Payload.Length <= 16 * 1024)); // test.stream is priority 3
    }
}
