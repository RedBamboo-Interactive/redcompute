using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>The per-channel state machine, driven by hand: seq, ack, RTO, reorder, window, half-close.</summary>
public sealed class LinkChannelTests
{
    private readonly ManualTime _time = new();

    private LinkOptions Options => new()
    {
        Time = _time,
        RtoMin = TimeSpan.FromSeconds(1),
        RtoMax = TimeSpan.FromSeconds(30),
        AckDelay = TimeSpan.FromMilliseconds(50),
    };

    private LinkChannel Sender(int peerWindow = 256 * 1024, ChannelClass? @class = null)
    {
        var channel = new LinkChannel(1, @class ?? ChannelClasses.TestStream, local: true, Options, 1, null, null, 256 * 1024, peerWindow, 0, 0)
        {
            Phase = ChannelPhase.Open,
        };
        return channel;
    }

    private LinkChannel Receiver(ChannelClass? @class = null, int window = 256 * 1024, string? streamKey = null)
        => new(2, @class ?? ChannelClasses.TestStream, local: false, Options, 1, streamKey, null, window, 256 * 1024, 0, 0);

    private static LinkFrame Decode(byte[] bytes) => FrameCodec.Decode(bytes);

    private List<LinkFrame> Drain(LinkChannel channel)
    {
        var frames = new List<LinkFrame>();
        while (channel.NextSendableBytes() > 0) frames.Add(Decode(channel.TakeNext(_time.GetTimestamp())));
        return frames;
    }

    [Fact]
    public void Frames_are_numbered_from_one_split_at_the_frame_size_and_fin_on_the_last()
    {
        var channel = Sender();
        channel.EnqueueMessage(new byte[FrameCodec.MaxPayload * 2 + 10]);
        channel.EnqueueMessage([]);
        var frames = Drain(channel);
        Assert.Equal([1UL, 2, 3, 4], frames.Select(f => f.Seq));
        Assert.Equal([false, false, true, true], frames.Select(f => f.IsFin));
        Assert.Equal([FrameCodec.MaxPayload, FrameCodec.MaxPayload, 10, 0], frames.Select(f => f.Payload.Length));
        Assert.All(frames, f => Assert.True(f.HasAck)); // the receive ack rides on every DATA
    }

    [Fact]
    public void Low_priority_frames_are_cut_at_16_KiB()
    {
        var channel = new LinkChannel(1, ChannelClasses.Blob, true, Options, 3, null, null, 1 << 22, 1 << 22, 0, 0) { Phase = ChannelPhase.Open };
        channel.EnqueueMessage(new byte[40 * 1024]);
        Assert.Equal([16384, 16384, 8192], Drain(channel).Select(f => f.Payload.Length));
    }

    [Fact]
    public void The_window_bounds_what_is_outstanding_and_an_ack_opens_it_again()
    {
        var channel = Sender(peerWindow: 3 * 1000);
        for (var i = 0; i < 5; i++) channel.EnqueueMessage(new byte[1000]);
        Assert.Equal(3, Drain(channel).Count);
        Assert.Equal(0, channel.NextSendableBytes());
        Assert.True(channel.OnAck(2, 0, _time.GetTimestamp()));
        Assert.Equal([4UL, 5], Drain(channel).Select(f => f.Seq));
        Assert.Equal(3000, channel.QueuedBytes);
    }

    [Fact]
    public void One_frame_larger_than_the_window_still_goes_when_nothing_is_outstanding()
    {
        var channel = Sender(peerWindow: 100);
        channel.EnqueueMessage(new byte[5000]);
        Assert.Single(Drain(channel));
    }

    [Fact]
    public void An_ack_beyond_what_was_sent_is_a_protocol_error()
    {
        var channel = Sender();
        channel.EnqueueMessage([1]);
        Drain(channel);
        Assert.Throws<LinkProtocolException>(() => channel.OnAck(2, 0, _time.GetTimestamp()));
    }

    [Fact]
    public void The_RTO_goes_back_to_the_first_frame_the_peer_has_not_received_and_doubles()
    {
        var channel = Sender();
        for (var i = 0; i < 4; i++) channel.EnqueueMessage([(byte)i]);
        Drain(channel);
        channel.OnAck(1, 2, _time.GetTimestamp()); // committed 1, received through 2

        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(channel.OnTimer(_time.GetTimestamp()));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(channel.OnTimer(_time.GetTimestamp()));
        Assert.Equal([3UL, 4], Drain(channel).Select(f => f.Seq)); // go-back-N from received + 1

        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(channel.OnTimer(_time.GetTimestamp())); // doubled to 2 s
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(channel.OnTimer(_time.GetTimestamp()));
        Assert.Equal([3UL, 4], Drain(channel).Select(f => f.Seq));
    }

    [Fact]
    public void When_the_peer_has_everything_uncommitted_the_RTO_probes_with_the_last_frame()
    {
        var channel = Sender();
        for (var i = 0; i < 3; i++) channel.EnqueueMessage([(byte)i]);
        Drain(channel);
        channel.OnAck(0 + 1, 3, _time.GetTimestamp());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(channel.OnTimer(_time.GetTimestamp()));
        Assert.Equal([3UL], Drain(channel).Select(f => f.Seq));
    }

    [Fact]
    public void A_resume_resends_only_what_the_peer_did_not_receive()
    {
        var channel = Sender();
        for (var i = 0; i < 5; i++) channel.EnqueueMessage([(byte)i]);
        Drain(channel);
        channel.OnResume(peerReceived: 3, _time.GetTimestamp());
        Assert.Equal([4UL, 5], Drain(channel).Select(f => f.Seq));
    }

    [Fact]
    public void The_receiver_delivers_in_order_drops_duplicates_and_fills_gaps_from_the_reorder_buffer()
    {
        var channel = Receiver(ChannelClasses.DiagEcho);
        var deliveries = new List<Delivery>();
        LinkFrame Data(ulong seq) => new(FrameKind.Data, FrameFlags.Fin, 2, seq, 0, new[] { (byte)seq });
        var now = _time.GetTimestamp();

        Assert.True(channel.Receive(Data(1), deliveries, now));
        Assert.True(channel.Receive(Data(3), deliveries, now));
        Assert.True(channel.Receive(Data(4), deliveries, now));
        Assert.Equal([1UL], deliveries.Select(d => d.Seq));
        Assert.Equal(1UL, channel.Received);

        Assert.True(channel.Receive(Data(2), deliveries, now));
        Assert.Equal([1UL, 2, 3, 4], deliveries.Select(d => d.Seq));
        Assert.Equal(4UL, channel.Received);

        Assert.False(channel.AckNow);
        Assert.True(channel.Receive(Data(2), deliveries, now)); // a duplicate is dropped and re-acked
        Assert.Equal(4, deliveries.Count);
        Assert.True(channel.AckNow);
    }

    [Fact]
    public void The_ack_is_committed_only_after_the_consumer_returns()
    {
        var channel = Receiver(ChannelClasses.DiagEcho);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        for (ulong seq = 1; seq <= 3; seq++)
            channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, seq, 0, new byte[10]), deliveries, now);
        Assert.Equal(0UL, channel.Committed);

        channel.MarkDelivered(1, now);
        Assert.Equal(1UL, channel.Committed);
        channel.MarkDelivered(2, now);
        channel.MarkDelivered(3, now);
        Assert.Equal(3UL, channel.Committed);

        var ack = FrameCodec.Decode(channel.TakeAck());
        Assert.Equal(FrameKind.Ack, ack.Kind);
        Assert.Equal(3UL, ack.Ack);
        Assert.Equal(3UL, LinkChannel.ReadU64(ack.Payload));
    }

    [Fact]
    public void Message_fragments_count_as_committed_but_the_message_waits_for_its_consumer()
    {
        var channel = Receiver(ChannelClasses.TestStream);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 1, 0, new byte[] { 1, 2 }), deliveries, now);
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 2, 0, new byte[] { 3 }), deliveries, now);
        Assert.Empty(deliveries);
        Assert.Equal(2UL, channel.Committed);
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 2, 3, 0, new byte[] { 4 }), deliveries, now);
        var message = Assert.Single(deliveries);
        Assert.Equal([1, 2, 3, 4], message.Payload);
        Assert.Equal(2UL, channel.Committed);
        channel.MarkDelivered(3, now);
        Assert.Equal(3UL, channel.Committed);
    }

    [Fact]
    public void A_message_past_the_reassembly_cap_asks_for_a_reset()
    {
        var channel = Receiver(ChannelClasses.TestStream);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        ulong seq = 0;
        var ok = true;
        while (ok && seq < 40)
            ok = channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, ++seq, 0, new byte[FrameCodec.MaxPayload]), deliveries, now);
        Assert.False(ok);
        Assert.True(seq * FrameCodec.MaxPayload > (ulong)ChannelClasses.TestStream.ReassemblyCap);
    }

    [Fact]
    public void A_sender_overrunning_the_window_is_a_protocol_error()
    {
        var channel = Receiver(ChannelClasses.DiagEcho, window: 64 * 1024);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        Assert.Throws<LinkProtocolException>(() =>
        {
            for (ulong seq = 1; seq < 100; seq++)
                channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, seq, 0, new byte[FrameCodec.MaxPayload]), deliveries, now);
        });
    }

    [Fact]
    public void The_reorder_buffer_is_bounded_by_the_window()
    {
        var channel = Receiver(ChannelClasses.DiagEcho, window: 64 * 1024);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 2, 0, new byte[60 * 1024]), deliveries, now);
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 3, 0, new byte[10 * 1024]), deliveries, now); // dropped: no room
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 1, 0, new byte[1]), deliveries, now);
        Assert.Equal([1UL, 2], deliveries.Select(d => d.Seq));
        Assert.Equal(2UL, channel.Received);
    }

    [Fact]
    public void Acks_go_after_32_frames_64_KiB_or_50_ms()
    {
        var channel = Receiver(ChannelClasses.DiagEcho);
        var deliveries = new List<Delivery>();
        var now = _time.GetTimestamp();
        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 1, 0, new byte[10]), deliveries, now);
        channel.MarkDelivered(1, now);
        Assert.False(channel.AckNow);
        _time.Advance(TimeSpan.FromMilliseconds(49));
        channel.OnTimer(_time.GetTimestamp());
        Assert.False(channel.AckNow);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        channel.OnTimer(_time.GetTimestamp());
        Assert.True(channel.AckNow);
        channel.TakeAck();

        for (ulong seq = 2; seq <= 33; seq++)
        {
            channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, seq, 0, new byte[1]), deliveries, now);
            channel.MarkDelivered(seq, now);
        }
        Assert.True(channel.AckNow); // 32 frames
        channel.TakeAck();

        channel.Receive(new LinkFrame(FrameKind.Data, FrameFlags.None, 2, 34, 0, new byte[FrameCodec.MaxPayload]), deliveries, now);
        channel.MarkDelivered(34, now);
        Assert.True(channel.AckNow); // 64 KiB
    }

    [Fact]
    public void Half_closes_retire_the_channel_once_both_are_done_and_acked()
    {
        var a = Sender();
        var b = Receiver(ChannelClasses.DiagEcho);
        var now = _time.GetTimestamp();
        a.EnqueueMessage([1]);
        a.EnqueueClose();
        var deliveries = new List<Delivery>();
        foreach (var frame in Drain(a)) b.Receive(frame with { Channel = 2 }, deliveries, now);
        Assert.True(b.RemoteCloseReceived);
        Assert.Equal([false, true], deliveries.Select(d => d.IsClose));
        foreach (var delivery in deliveries) b.MarkDelivered(delivery.Seq, now);
        Assert.True(b.RemoteCloseCommitted);
        Assert.False(b.CanRetire); // b has not closed its own direction

        Assert.Throws<LinkProtocolException>(() =>
            b.Receive(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 2, 3, 0, new byte[1]), deliveries, now));

        a.OnAck(2, 2, now);
        Assert.True(a.LocalCloseAcked);
    }

    [Fact]
    public void A_durable_acceptor_that_committed_ahead_skips_the_sender_forward()
    {
        var channel = new LinkChannel(1, ChannelClasses.TestStream, true, Options, 1, "proc/1/stdout", null, 1 << 18, 1 << 18, 10, 0);
        channel.SkipCommitted(15);
        Assert.Equal(16UL, channel.NextSeq);
        Assert.Equal(15UL, channel.AckedThrough);
    }
}
