using System.Buffers.Binary;

namespace RedCompute.NodeLink;

/// <summary>The frame kinds of format 1 (design 4.1).</summary>
public enum FrameKind : byte
{
    Data = 1,
    Ack = 2,
    Window = 3,
    Open = 4,
    Close = 5,
    Reset = 6,
    Ping = 7,
    Pong = 8,
}

[Flags]
public enum FrameFlags : ushort
{
    None = 0,
    /// <summary>The last frame of a logical message.</summary>
    Fin = 1,
    /// <summary>The <see cref="LinkFrame.Ack"/> field carries a cumulative ack.</summary>
    AckValid = 2,
    /// <summary>Reserved for interactive frames; carried, not interpreted, in protocol 1.</summary>
    Urgent = 4,
}

/// <summary>
/// One link frame: one binary WebSocket message. <see cref="Seq"/> numbers DATA and CLOSE frames per
/// channel and direction from 1; an OPEN carries the base its direction's numbering continues from;
/// ACK, WINDOW, RESET, PING and PONG are unsequenced (0). The handshake (hello and welcome) is the one
/// DATA frame with seq 0, on channel 0, first in each direction.
/// </summary>
public readonly record struct LinkFrame(FrameKind Kind, FrameFlags Flags, uint Channel, ulong Seq, ulong Ack,
    ReadOnlyMemory<byte> Payload)
{
    public bool IsFin => (Flags & FrameFlags.Fin) != 0;
    public bool HasAck => (Flags & FrameFlags.AckValid) != 0;
    public bool IsHandshake => Kind == FrameKind.Data && Channel == 0 && Seq == 0;
    public bool IsSequenced => Kind is FrameKind.Data or FrameKind.Close && !IsHandshake;
}

/// <summary>
/// The 24-byte little-endian header: u8 format, u8 kind, u16 flags, u32 channel, u64 seq, u64 ack,
/// then 0..65 536 payload bytes (the message length gives the size).
/// </summary>
public static class FrameCodec
{
    public const byte Format = 1;
    public const int HeaderSize = 24;
    public const int MaxPayload = 64 * 1024;
    public const int MaxFrame = HeaderSize + MaxPayload;

    /// <summary>
    /// What a sender puts in one frame: 17 bytes under <see cref="MaxPayload"/>, so a frame sealed for an
    /// end-to-end encrypted link (record type + AEAD tag, <see cref="Noise.NoiseLink.RecordOverhead"/>) still
    /// fits the <see cref="MaxFrame"/> every relay accepts. Receivers keep accepting <see cref="MaxPayload"/>.
    /// </summary>
    public const int MaxSendPayload = MaxPayload - Noise.NoiseLink.RecordOverhead;
    private const ushort KnownFlags = (ushort)(FrameFlags.Fin | FrameFlags.AckValid | FrameFlags.Urgent);

    public static byte[] Encode(in LinkFrame frame)
    {
        if (frame.Payload.Length > MaxPayload)
            throw new ArgumentException($"A frame payload is at most {MaxPayload} bytes.", nameof(frame));
        var bytes = new byte[HeaderSize + frame.Payload.Length];
        WriteHeader(bytes, frame.Kind, frame.Flags, frame.Channel, frame.Seq, frame.Ack);
        frame.Payload.Span.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public static void WriteHeader(Span<byte> header, FrameKind kind, FrameFlags flags, uint channel, ulong seq, ulong ack)
    {
        header[0] = Format;
        header[1] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], channel);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], seq);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], ack);
    }

    /// <summary>
    /// Reads one message as a frame. Anything malformed throws <see cref="LinkProtocolException"/>
    /// (close 4400, or 1009 for an oversized message) and nothing else.
    /// </summary>
    public static LinkFrame Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length > MaxFrame)
            throw new LinkProtocolException(LinkCloseCodes.MessageTooBig, $"A frame is at most {MaxFrame} bytes; got {message.Length}.");
        if (message.Length < HeaderSize)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"A frame has a {HeaderSize}-byte header; got {message.Length} bytes.");
        var span = message.Span;
        if (span[0] != Format)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Unknown frame format {span[0]}.");
        var kind = (FrameKind)span[1];
        if (kind is < FrameKind.Data or > FrameKind.Pong)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Unknown frame kind {span[1]}.");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        if ((flags & ~KnownFlags) != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Reserved frame flags set (0x{flags:x4}).");
        var channel = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        var ack = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
        var frame = new LinkFrame(kind, (FrameFlags)flags, channel, seq, ack, message[HeaderSize..]);

        if (kind is FrameKind.Ack or FrameKind.Window or FrameKind.Reset or FrameKind.Ping or FrameKind.Pong && seq != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"A {kind} frame is unsequenced; got seq {seq}.");
        if (kind == FrameKind.Ack && !frame.HasAck)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "An ACK frame must carry ACK_VALID.");
        if (kind == FrameKind.Close && seq == 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "A CLOSE frame is sequenced from 1.");
        if (kind == FrameKind.Data && seq == 0 && channel != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "Only channel 0 carries the unsequenced handshake.");
        if (kind is FrameKind.Ping or FrameKind.Pong && channel != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"A {kind} frame belongs on channel 0.");
        if (kind is FrameKind.Open or FrameKind.Close or FrameKind.Reset && channel == 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Channel 0 is never opened, closed or reset.");
        if (kind == FrameKind.Window && frame.Payload.Length != 8)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "A WINDOW frame carries one u64.");
        if (!frame.HasAck && ack != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The ack field is set without ACK_VALID.");
        return frame;
    }
}
