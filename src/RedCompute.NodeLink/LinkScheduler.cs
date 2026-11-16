namespace RedCompute.NodeLink;

/// <summary>One sender with a frame ready: its class priority (0 highest) and the frame's size.</summary>
public readonly record struct SchedulerEntry(int Priority, int NextFrameBytes);

/// <summary>
/// Picks the next frame to send (design 4.4): strict priority between classes, round-robin inside a
/// class, and an aging boost so a lower class with data gets at least a tenth of the link: once the
/// classes above it have sent nine times its next frame since it was last served, it goes next.
/// Pure and deterministic; the session calls it under its lock.
/// </summary>
public sealed class LinkScheduler
{
    private const int Classes = ChannelClass.MaxPriority + 1;

    /// <summary>A starved class is served after others sent this many times its next frame (10% floor).</summary>
    public const int StarvationRatio = 9;

    private readonly long[] _waitedBytes = new long[Classes];
    private readonly int[] _cursor = new int[Classes];
    private readonly bool[] _hadWork = new bool[Classes];

    /// <summary>The index of the entry to send next, or -1 when none has a frame.</summary>
    public int Pick(ReadOnlySpan<SchedulerEntry> entries)
    {
        Array.Clear(_hadWork);
        Span<int> nextSize = stackalloc int[Classes];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.NextFrameBytes <= 0) continue;
            var p = Math.Clamp(entry.Priority, 0, Classes - 1);
            if (!_hadWork[p]) nextSize[p] = entry.NextFrameBytes;
            _hadWork[p] = true;
        }

        var top = Array.IndexOf(_hadWork, true);
        if (top < 0) return -1;
        var chosen = top;
        _agedPick = false;
        for (var p = top + 1; p < Classes; p++)
        {
            if (_hadWork[p] && _waitedBytes[p] >= (long)StarvationRatio * nextSize[p])
            {
                chosen = p;
                _agedPick = true;
                break;
            }
        }
        return RoundRobin(entries, chosen);
    }

    private bool _agedPick;

    /// <summary>
    /// Records that the entry of <paramref name="priority"/> sent <paramref name="bytes"/>. A class
    /// served by the aging boost pays nine times its frame out of what it waited, keeping any surplus,
    /// so over time it gets a tenth of the bytes even when the classes above send larger frames.
    /// </summary>
    public void OnSent(int priority, int bytes)
    {
        var served = Math.Clamp(priority, 0, Classes - 1);
        for (var p = 0; p < Classes; p++)
        {
            if (p == served) _waitedBytes[p] = _agedPick ? Math.Max(0, _waitedBytes[p] - (long)StarvationRatio * bytes) : 0;
            else if (_hadWork[p]) _waitedBytes[p] += bytes;
            else _waitedBytes[p] = 0;
        }
    }

    private int RoundRobin(ReadOnlySpan<SchedulerEntry> entries, int priority)
    {
        // The first entry of this class after the one served last time, wrapping around.
        var start = _cursor[priority];
        var first = -1;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.NextFrameBytes <= 0 || Math.Clamp(entry.Priority, 0, Classes - 1) != priority) continue;
            if (first < 0) first = i;
            if (i >= start)
            {
                _cursor[priority] = i + 1;
                return i;
            }
        }
        _cursor[priority] = first + 1;
        return first;
    }
}
