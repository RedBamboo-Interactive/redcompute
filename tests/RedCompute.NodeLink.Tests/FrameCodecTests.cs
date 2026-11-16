using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.NodeLink.Tests;

public sealed class FrameCodecTests
{
    public static TheoryData<FrameKind, ushort, uint, ulong, ulong, string, string> Goldens => new()
    {
        { FrameKind.Data, 3, 3, 7, 5, "6869", "010103000300000007000000000000000500000000000000" + "6869" },
        { FrameKind.Data, 1, 0, 0, 0, "7b7d", "010101000000000000000000000000000000000000000000" + "7b7d" },
        { FrameKind.Ack, 2, 5, 0, 0x0102030405060708, "0900000000000000", "010202000500000000000000000000000807060504030201" + "0900000000000000" },
        { FrameKind.Window, 0, 4, 0, 0, "0000040000000000", "010300000400000000000000000000000000000000000000" + "0000040000000000" },
        { FrameKind.Open, 0, 1, 0, 0, "", "010400000100000000000000000000000000000000000000" },
        { FrameKind.Open, 2, 2, 41, 9, "", "010402000200000029000000000000000900000000000000" },
        { FrameKind.Close, 3, 7, 12, 4, "", "01050300070000000c000000000000000400000000000000" },
        { FrameKind.Reset, 0, 9, 0, 0, "7b7d", "010600000900000000000000000000000000000000000000" + "7b7d" },
        { FrameKind.Ping, 0, 0, 0, 0, "", "010700000000000000000000000000000000000000000000" },
        { FrameKind.Pong, 4, 0, 0, 0, "", "010804000000000000000000000000000000000000000000" },
        { FrameKind.Data, 0, 0xFFFFFFFF, ulong.MaxValue, 0, "", "01010000ffffffffffffffffffffffff0000000000000000" },
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Every_kind_encodes_to_its_golden_bytes_and_back(FrameKind kind, ushort flags, uint channel, ulong seq, ulong ack,
        string payloadHex, string golden)
    {
        var frame = new LinkFrame(kind, (FrameFlags)flags, channel, seq, ack, Convert.FromHexString(payloadHex));
        var bytes = FrameCodec.Encode(frame);
        Assert.Equal(golden, Convert.ToHexString(bytes).ToLowerInvariant());

        var decoded = FrameCodec.Decode(bytes);
        Assert.Equal(kind, decoded.Kind);
        Assert.Equal((FrameFlags)flags, decoded.Flags);
        Assert.Equal(channel, decoded.Channel);
        Assert.Equal(seq, decoded.Seq);
        Assert.Equal(ack, decoded.Ack);
        Assert.Equal(payloadHex, Convert.ToHexString(decoded.Payload.Span).ToLowerInvariant());
    }

    [Fact]
    public void The_payload_cap_is_64_KiB_and_a_larger_message_is_1009()
    {
        var full = FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 1, 1, 0, new byte[FrameCodec.MaxPayload]));
        Assert.Equal(FrameCodec.MaxFrame, full.Length);
        Assert.Equal(FrameCodec.MaxPayload, FrameCodec.Decode(full).Payload.Length);

        Assert.Throws<ArgumentException>(() =>
            FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 1, 1, 0, new byte[FrameCodec.MaxPayload + 1])));
        var oversized = new byte[FrameCodec.MaxFrame + 1];
        full.CopyTo(oversized, 0);
        var error = Assert.Throws<LinkProtocolException>(() => FrameCodec.Decode(oversized));
        Assert.Equal(LinkCloseCodes.MessageTooBig, error.CloseCode);
    }

    [Theory]
    [InlineData("", "short")]
    [InlineData("02010000010000000100000000000000000000000000000000", "format")]
    [InlineData("01090000010000000100000000000000000000000000000000", "kind")]
    [InlineData("01000000010000000100000000000000000000000000000000", "kind 0")]
    [InlineData("01010800010000000100000000000000000000000000000000", "reserved flag")]
    [InlineData("01020000010000000000000000000000050000000000000000", "ack without ACK_VALID")]
    [InlineData("01020200010000000100000000000000050000000000000000", "sequenced ack")]
    [InlineData("01050000010000000000000000000000000000000000000000", "close seq 0")]
    [InlineData("01010000010000000000000000000000000000000000000000", "handshake off channel 0")]
    [InlineData("01070000010000000000000000000000000000000000000000", "ping off channel 0")]
    [InlineData("01040000000000000000000000000000000000000000000000", "open channel 0")]
    [InlineData("01030000010000000000000000000000000000000000000000", "window without its u64")]
    [InlineData("01010000010000000100000000000000050000000000000000", "ack field without flag")]
    public void A_malformed_header_is_a_4400(string hex, string why)
    {
        var error = Assert.Throws<LinkProtocolException>(() => FrameCodec.Decode(Convert.FromHexString(hex)));
        Assert.True(error.CloseCode == LinkCloseCodes.ProtocolError, why);
    }

    [Fact]
    public void Random_bytes_never_throw_anything_but_a_protocol_error()
    {
        var random = new Random(20261116);
        var buffer = new byte[200];
        for (var i = 0; i < 100_000; i++)
        {
            var length = random.Next(0, buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            // Bias half of them towards a plausible header so the deeper checks run too.
            if (length >= 2 && i % 2 == 0)
            {
                buffer[0] = 1;
                buffer[1] = (byte)random.Next(0, 10);
            }
            try
            {
                FrameCodec.Decode(buffer.AsMemory(0, length));
            }
            catch (LinkProtocolException)
            {
            }
        }
    }
}
