using System.Globalization;

namespace RedCompute.App.Services.Hardware;

/// <summary>Parsers for the Linux /proc files the hardware monitor reads. Pure, so tests run anywhere.</summary>
internal static class LinuxProc
{
    /// <summary>
    /// The aggregate "cpu" line of /proc/stat: user nice system idle iowait irq softirq steal
    /// [guest guest_nice], in clock ticks. Idle is idle + iowait; total is the sum of the first
    /// eight fields (guest time is already included in user and nice).
    /// </summary>
    public static bool TryParseCpuTimes(string? line, out long idle, out long total)
    {
        idle = total = 0;
        if (line is null) return false;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") return false;

        var values = new long[8];
        for (var i = 0; i < values.Length && i + 1 < fields.Length; i++)
            if (!long.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                return false;

        idle = values[3] + values[4];
        total = values.Sum();
        return true;
    }

    /// <summary>MemTotal and MemAvailable from /proc/meminfo, converted from kB to bytes.</summary>
    public static bool TryParseMemInfo(string text, out ulong totalBytes, out ulong availableBytes)
    {
        ulong? total = null, available = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kilobytes(line);
        }

        totalBytes = total ?? 0;
        availableBytes = Math.Min(available ?? 0, totalBytes);
        return total is > 0 && available is not null;

        static ulong? Kilobytes(string line)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb)
                ? kb * 1024
                : null;
        }
    }

    /// <summary>
    /// The parent PID from /proc/&lt;pid&gt;/stat: "pid (comm) state ppid ...". The command name can
    /// contain spaces and parentheses, so fields are read after its last closing parenthesis.
    /// </summary>
    public static bool TryParseParentPid(string stat, out int parentPid)
    {
        parentPid = 0;
        var close = stat.LastIndexOf(')');
        if (close < 0) return false;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2
               && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid)
               && parentPid > 0;
    }
}
