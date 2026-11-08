using RedCompute.App.Services.Hardware;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class LinuxProcParsingTests
{
    [Fact]
    public void Cpu_times_count_iowait_as_idle_and_sum_the_first_eight_fields()
    {
        Assert.True(LinuxProc.TryParseCpuTimes("cpu  100 5 50 1000 20 3 2 1 40 0", out var idle, out var total));

        Assert.Equal(1020, idle);
        Assert.Equal(100 + 5 + 50 + 1000 + 20 + 3 + 2 + 1, total);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4 5")]
    [InlineData("cpu 1 2 x 4 5")]
    [InlineData("cpu 1 2")]
    public void Malformed_cpu_lines_are_rejected(string? line)
        => Assert.False(LinuxProc.TryParseCpuTimes(line, out _, out _));

    [Fact]
    public void Meminfo_reports_total_and_available_in_bytes()
    {
        const string text = "MemTotal:        8116224 kB\nMemFree:         3530000 kB\nMemAvailable:    6287000 kB\nBuffers: 1 kB\n";

        Assert.True(LinuxProc.TryParseMemInfo(text, out var total, out var available));

        Assert.Equal(8116224UL * 1024, total);
        Assert.Equal(6287000UL * 1024, available);
    }

    [Fact]
    public void Meminfo_without_available_memory_is_rejected()
        => Assert.False(LinuxProc.TryParseMemInfo("MemTotal: 1024 kB\n", out _, out _));

    [Theory]
    [InlineData("1234 (bash) S 1000 1234 1234 0 -1", 1000)]
    [InlineData("42 (weird) name) (x) R 7 42 42 0", 7)]
    [InlineData("9 (with space) S 1 9 9", 1)]
    public void Parent_pid_is_read_after_the_command_name(string stat, int parent)
    {
        Assert.True(LinuxProc.TryParseParentPid(stat, out var actual));
        Assert.Equal(parent, actual);
    }

    [Theory]
    [InlineData("1 (init) S 0 1 1")]
    [InlineData("garbage")]
    public void Missing_parents_are_rejected(string stat)
        => Assert.False(LinuxProc.TryParseParentPid(stat, out _));
}
