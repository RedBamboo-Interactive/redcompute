namespace RedCompute.Core.Hardware;

public class HardwareSnapshot
{
    public DateTimeOffset Timestamp { get; set; }
    public CpuInfo Cpu { get; set; } = new();
    public RamInfo Ram { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
}

public class CpuInfo
{
    public double UsagePercent { get; set; }
    public int CoreCount { get; set; }
}

public class RamInfo
{
    public ulong TotalBytes { get; set; }
    public ulong UsedBytes { get; set; }
    public ulong AvailableBytes { get; set; }
    public double UsagePercent { get; set; }
}

public class GpuInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public GpuMemoryInfo Memory { get; set; } = new();
    public double UtilizationPercent { get; set; }
    public double MemoryUtilizationPercent { get; set; }
    public double PowerWatts { get; set; }
    public double PowerLimitWatts { get; set; }
    public double TemperatureCelsius { get; set; }
    public int GraphicsClockMHz { get; set; }
    public int MemoryClockMHz { get; set; }
    public List<GpuProcessInfo> Processes { get; set; } = new();
}

public class GpuMemoryInfo
{
    public ulong TotalBytes { get; set; }
    public ulong UsedBytes { get; set; }
    public ulong FreeBytes { get; set; }
}

public class GpuProcessInfo
{
    public uint Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public ulong UsedMemoryBytes { get; set; }
}
