namespace RedCompute.NodeLink;

/// <summary>The link's timings and limits. Defaults are the design's; tests shorten the clocks.</summary>
public sealed class LinkOptions
{
    /// <summary>PING every interval from each side (design 4.5).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The link is dead after this many intervals without a frame (45 s).</summary>
    public int MissedHeartbeats { get; init; } = 3;

    /// <summary>A standalone ACK goes after this delay, <see cref="AckFrames"/> or <see cref="AckBytes"/>.</summary>
    public TimeSpan AckDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public int AckFrames { get; init; } = 32;
    public int AckBytes { get; init; } = 64 * 1024;

    /// <summary>Retransmission timeout: max(min, 2 x smoothed RTT), doubling to max.</summary>
    public TimeSpan RtoMin { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RtoMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a side waits for the other's handshake frame.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The timer granularity for acks, RTO and heartbeats.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long NodeHub keeps a detached link session for a resume (design 4.3).</summary>
    public TimeSpan SessionRetention { get; init; } = TimeSpan.FromMinutes(15);

    public int MaxChannels { get; init; } = 256;

    /// <summary>Frames of priority 2 and lower are cut to this size, so they never sit long in front of interactive ones.</summary>
    public int LowPriorityFrameBytes { get; init; } = 16 * 1024;

    /// <summary>Retired channel ids remembered, to re-ack or re-reset late duplicates.</summary>
    public int Tombstones { get; init; } = 1024;

    /// <summary>
    /// Window autotune (Phase 4 2.3): the receiver of an <see cref="ChannelClass.AutoTune"/> channel grows its window
    /// toward 2 x measured rate x RTT, up to <see cref="AutoTuneChannelCap"/> per channel and
    /// <see cref="AutoTuneLinkCap"/> for all such windows of the link together, and shrinks it back (never below the
    /// class's window) when the consumer slows down.
    /// </summary>
    public bool AutoTune { get; init; } = true;
    public long AutoTuneChannelCap { get; init; } = 8L * 1024 * 1024;
    public long AutoTuneLinkCap { get; init; } = 32L * 1024 * 1024;

    /// <summary>The RTT autotune assumes before the heartbeat measured one.</summary>
    public TimeSpan AutoTuneDefaultRtt { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The tuner's interval is 2 x RTT, kept between these bounds.</summary>
    public TimeSpan AutoTuneMinInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan AutoTuneMaxInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan DeadAfter => HeartbeatInterval * MissedHeartbeats;

    public static LinkOptions Default { get; } = new();
}

/// <summary>
/// A channel class (design 4.2): its scheduling priority (0 highest), the receive window it starts
/// with, whether it may be durable (keyed by a stream key, replayed from a spool) and whether its
/// consumer receives reassembled messages or each frame as it commits.
/// </summary>
public sealed record ChannelClass(string Name, int Priority, int Window, bool MayBeDurable, bool Messages, int ReassemblyCap)
{
    public const int MaxPriority = 3;

    /// <summary>The receiver tunes this class's window to the measured rate (<see cref="LinkOptions.AutoTune"/>).</summary>
    public bool AutoTune { get; init; }
}

public static class ChannelClasses
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;

    public static ChannelClass Control { get; } = new("control", 0, 256 * KiB, false, true, 1 * MiB);
    public static ChannelClass DiagEcho { get; } = new("diag.echo", 3, 256 * KiB, true, false, 0);
    public static ChannelClass Stdio { get; } = new("stdio", 1, 1 * MiB, true, false, 0);
    /// <summary>One forwarded HTTP exchange (<see cref="LinkFeatures.ForwardHttpV1"/>).</summary>
    public static ChannelClass Port { get; } = new("port", 2, 256 * KiB, false, false, 0) { AutoTune = true };

    /// <summary>One forwarded WebSocket (<see cref="LinkFeatures.ForwardWsV1"/>): a link message per WS message.</summary>
    public static ChannelClass PortWs { get; } = new("port.ws", 2, 256 * KiB, false, true, 16 * MiB) { AutoTune = true };
    public static ChannelClass File { get; } = new("file", 2, 256 * KiB, false, true, 4 * MiB);
    public static ChannelClass Blob { get; } = new("blob", 3, 4 * MiB, false, false, 0);

    /// <summary>The harness's class: any priority, durable or not, messages up to 1 MiB.</summary>
    public static ChannelClass TestStream { get; } = new("test.stream", 3, 256 * KiB, true, true, 1 * MiB);

    public static IReadOnlyDictionary<string, ChannelClass> All { get; } =
        new[] { Control, DiagEcho, Stdio, Port, PortWs, File, Blob, TestStream }.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public static ChannelClass? Find(string? name) => name is not null && All.TryGetValue(name, out var found) ? found : null;
}
