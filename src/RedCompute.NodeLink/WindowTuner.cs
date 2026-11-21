namespace RedCompute.NodeLink;

/// <summary>
/// The receive-window autotune rule (Phase 4 design 2.3), pure so it can be tested on its own. The target is
/// 2 x measured rate x RTT: a window that limits the rate measures rate = window / RTT, so the target doubles it, and
/// the window grows by doubling while the window is the bottleneck. A consumer that commits slower than the window
/// allows measures a lower rate, and the window shrinks back toward its class's window (never below it).
/// </summary>
public static class WindowTuner
{
    /// <summary>Grow only when the target passes the current window by this factor (no churn on noise).</summary>
    public const double GrowFactor = 1.25;

    /// <summary>Shrink only when the target is below this fraction of the current window.</summary>
    public const double ShrinkFactor = 0.5;

    /// <param name="current">The window now.</param>
    /// <param name="floor">The class's window: never below it.</param>
    /// <param name="channelCap">At most this per channel (8 MiB).</param>
    /// <param name="linkRoom">What the link's budget leaves this channel: the link cap minus every other tuned window.</param>
    /// <param name="bytesPerSecond">What the consumer committed per second since the last sample.</param>
    /// <param name="rtt">The link's round trip.</param>
    public static long Next(long current, long floor, long channelCap, long linkRoom, double bytesPerSecond, TimeSpan rtt)
    {
        if (current < floor) return floor;
        var ceiling = Math.Max(floor, Math.Min(channelCap, linkRoom));
        var target = double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? 2 * bytesPerSecond * rtt.TotalSeconds : 0;
        var wanted = (long)Math.Min(target, long.MaxValue / 2);
        if (wanted > current * GrowFactor)
            return Math.Max(current, Math.Min(ceiling, Align(wanted)));
        if (wanted < current * ShrinkFactor)
            return Math.Max(floor, Math.Min(current, Align(wanted)));
        // Over a budget that shrank (other channels grew first): give back what passes it.
        return current > ceiling ? ceiling : current;
    }

    /// <summary>Windows move in whole 64 KiB steps, so a WINDOW frame goes only for a real change.</summary>
    private static long Align(long bytes)
    {
        const long step = 64 * 1024;
        return (bytes + step - 1) / step * step;
    }
}
