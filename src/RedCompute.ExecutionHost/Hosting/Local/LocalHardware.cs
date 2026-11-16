using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RedCompute.Core.Hardware;
using static RedCompute.PluginSdk.Hosting.Local.NvmlInterop;
using static RedCompute.PluginSdk.Hosting.Local.SystemInterop;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// Node zero's hardware: the sampling half of RedCompute's hardware monitor, moved here unchanged
/// (NVML, CPU and RAM counters, per-process VRAM, the parent reader VRAM attribution walks, the CPU
/// name). The monitor's loop and its capability attribution stay in RedCompute.
/// </summary>
internal sealed class LocalHardware : IHostHardware
{
    public IHostHardwareSampler OpenSampler() => new LocalHardwareSampler();

    /// <summary>
    /// Every process twice, as the monitor's attribution read them: first each one's parent (only
    /// where the OS answers, a failure skipping just that process), then the PIDs to walk.
    /// </summary>
    public HostProcessParentage ProcessParentage()
    {
        // Build child→parent map to attribute child process VRAM to capabilities
        var childToParent = new Dictionary<int, int>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // ParentId via interop — safe to fail per-process
                    var parentId = GetParentProcessId(proc.Id);
                    if (parentId > 0)
                        childToParent[proc.Id] = parentId;
                }
                catch { }
            }
        }
        catch { }

        var processIds = new List<int>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                processIds.Add(proc.Id);
            }
            catch { }
        }

        return new HostProcessParentage(childToParent, processIds);
    }

    private static int GetParentProcessId(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return OperatingSystem.IsLinux()
                       && LinuxProc.TryParseParentPid(File.ReadAllText($"/proc/{pid}/stat"), out var parent)
                    ? parent
                    : 0;
            }
            catch { return 0; }
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            var handle = proc.Handle;
            var pbi = new NtInterop.PROCESS_BASIC_INFORMATION();
            int status = NtInterop.NtQueryInformationProcess(handle, 0, ref pbi,
                System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : 0;
        }
        catch { return 0; }
    }

    /// <summary>The CPU's marketing name, best effort: null when the OS does not say.</summary>
    public string? ProcessorName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return Clean(key?.GetValue("ProcessorNameString") as string);
            }
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal) && line.IndexOf(':') is var colon and > 0)
                        return Clean(line[(colon + 1)..]);
            }
        }
        catch
        {
            // A missing name is not worth failing the node over.
        }
        return null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// One open hardware sampler: NVML initialised and the first CPU reading taken when it opens, NVML
/// shut down when it is disposed, every sample's CPU usage the delta since the previous one.
/// </summary>
internal sealed class LocalHardwareSampler : IHostHardwareSampler
{
    private bool _nvmlAvailable;
    private uint _gpuCount;
    private long _prevIdleTime, _prevTotalTime;
    private bool _hasPrevCpuSample;

    public LocalHardwareSampler()
    {
        // Windows loads nvml.dll by its DllImport name. Elsewhere the NVIDIA driver ships
        // libnvidia-ml.so.1; without it the init below throws DllNotFoundException and the
        // snapshot simply carries no GPUs.
        NvmlInterop.EnsureResolver();
        try
        {
            var result = nvmlInit_v2();
            if (result == NvmlReturn.Success)
            {
                nvmlDeviceGetCount_v2(out _gpuCount);
                _nvmlAvailable = _gpuCount > 0;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        SampleCpuTimes();
    }

    public bool SamplesGpus => _nvmlAvailable;

    public HardwareSnapshot Sample() => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Cpu = CollectCpu(),
        Ram = CollectRam(),
        Gpus = _nvmlAvailable ? CollectGpus() : new()
    };

    public void Dispose()
    {
        if (_nvmlAvailable)
        {
            try { nvmlShutdown(); } catch { }
            _nvmlAvailable = false;
        }
    }

    private CpuInfo CollectCpu()
    {
        double usage = 0;
        if (TryReadCpuTimes(out long idle, out long total))
        {
            if (_hasPrevCpuSample)
            {
                var idleDelta = idle - _prevIdleTime;
                var totalDelta = total - _prevTotalTime;
                if (totalDelta > 0)
                    usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
            }

            _prevIdleTime = idle;
            _prevTotalTime = total;
            _hasPrevCpuSample = true;
        }

        return new CpuInfo
        {
            UsagePercent = Math.Round(Math.Max(0, Math.Min(100, usage)), 1),
            CoreCount = Environment.ProcessorCount
        };
    }

    /// <summary>
    /// Cumulative idle and total CPU time, in platform units; only deltas are used. Windows:
    /// GetSystemTimes, whose kernel time already includes idle time. Linux: the aggregate line
    /// of /proc/stat. Any other platform reports no CPU usage.
    /// </summary>
    private static bool TryReadCpuTimes(out long idle, out long total)
    {
        idle = total = 0;
        if (OperatingSystem.IsWindows())
        {
            if (!GetSystemTimes(out idle, out long kernel, out long user))
                return false;
            total = kernel + user;
            return true;
        }

        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return LinuxProc.TryParseCpuTimes(File.ReadLines("/proc/stat").FirstOrDefault(), out idle, out total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RamInfo CollectRam()
    {
        if (OperatingSystem.IsWindows())
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                return Ram(mem.ullTotalPhys, mem.ullAvailPhys);
            return new RamInfo();
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (LinuxProc.TryParseMemInfo(File.ReadAllText("/proc/meminfo"), out var total, out var available))
                    return Ram(total, available);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new RamInfo();
    }

    private static RamInfo Ram(ulong total, ulong available)
    {
        var used = total - available;
        return new RamInfo
        {
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = available,
            UsagePercent = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0
        };
    }

    /// <summary>
    /// Every GPU, each with the per-process VRAM list. Capability attribution
    /// (<see cref="GpuProcessInfo.CapabilitySlug"/>, <see cref="GpuInfo.CapabilityVram"/>) is left
    /// empty for RedCompute to fill.
    /// </summary>
    private List<GpuInfo> CollectGpus()
    {
        var gpus = new List<GpuInfo>();
        var perProcessVram = OperatingSystem.IsWindows()
            ? CollectPerProcessGpuMemory()
            : CollectPerProcessGpuMemoryFromNvml();

        for (uint i = 0; i < _gpuCount; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != NvmlReturn.Success)
                continue;

            var name = new StringBuilder(256);
            nvmlDeviceGetName(device, name, 256);

            nvmlDeviceGetMemoryInfo(device, out var mem);
            nvmlDeviceGetUtilizationRates(device, out var util);
            nvmlDeviceGetPowerUsage(device, out var powerMw);
            nvmlDeviceGetEnforcedPowerLimit(device, out var limitMw);
            nvmlDeviceGetTemperature(device, NVML_TEMPERATURE_GPU, out var temp);
            nvmlDeviceGetClockInfo(device, NVML_CLOCK_GRAPHICS, out var gfxClock);
            nvmlDeviceGetClockInfo(device, NVML_CLOCK_MEM, out var memClock);

            var processes = perProcessVram
                .Where(p => p.UsedMemoryBytes > 0)
                .OrderByDescending(p => p.UsedMemoryBytes)
                .ToList();

            var gpu = new GpuInfo
            {
                Index = (int)i,
                Name = name.ToString(),
                Memory = new GpuMemoryInfo { TotalBytes = mem.Total, UsedBytes = mem.Used, FreeBytes = mem.Free },
                UtilizationPercent = util.Gpu,
                MemoryUtilizationPercent = util.Memory,
                PowerWatts = Math.Round(powerMw / 1000.0, 1),
                PowerLimitWatts = Math.Round(limitMw / 1000.0, 1),
                TemperatureCelsius = temp,
                GraphicsClockMHz = (int)gfxClock,
                MemoryClockMHz = (int)memClock,
                Processes = processes,
            };

            gpus.Add(gpu);
        }

        return gpus;
    }

    private static readonly Regex PidPattern = new(@"^pid_(\d+)_", RegexOptions.Compiled);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<GpuProcessInfo> CollectPerProcessGpuMemory()
    {
        var byPid = new Dictionary<uint, ulong>();

        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Process Memory"))
                return [];

            var category = new PerformanceCounterCategory("GPU Process Memory");
            var data = category.ReadCategory();

            if (!data.Contains("Dedicated Usage"))
                return [];

            foreach (InstanceData instance in data["Dedicated Usage"].Values)
            {
                var match = PidPattern.Match(instance.InstanceName);
                if (!match.Success) continue;

                var pid = uint.Parse(match.Groups[1].Value);
                var bytes = (ulong)instance.Sample.RawValue;

                if (byPid.TryGetValue(pid, out var existing))
                    byPid[pid] = existing + bytes;
                else
                    byPid[pid] = bytes;
            }
        }
        catch { return []; }

        var result = new List<GpuProcessInfo>();
        foreach (var (pid, bytes) in byPid)
        {
            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { continue; }

            result.Add(new GpuProcessInfo
            {
                Pid = pid,
                ProcessName = procName,
                UsedMemoryBytes = bytes
            });
        }

        return result;
    }

    /// <summary>
    /// Linux has no "GPU Process Memory" performance counters; NVML lists each device's compute
    /// and graphics processes instead. Memory is summed per PID across devices, as the Windows
    /// counter is, and the same list is attached to every GPU.
    /// </summary>
    private List<GpuProcessInfo> CollectPerProcessGpuMemoryFromNvml()
    {
        var byPid = new Dictionary<uint, ulong>();
        try
        {
            for (uint i = 0; i < _gpuCount; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != NvmlReturn.Success)
                    continue;
                // A process using a device for compute and graphics is listed twice with the
                // same allocation; count it once per device.
                var onDevice = new Dictionary<uint, ulong>();
                foreach (var info in ReadNvmlProcesses(device, compute: true)
                             .Concat(ReadNvmlProcesses(device, compute: false)))
                {
                    var bytes = info.UsedGpuMemory == NVML_VALUE_NOT_AVAILABLE ? 0UL : info.UsedGpuMemory;
                    onDevice[info.Pid] = onDevice.TryGetValue(info.Pid, out var seen) ? Math.Max(seen, bytes) : bytes;
                }
                foreach (var (pid, bytes) in onDevice)
                    byPid[pid] = byPid.TryGetValue(pid, out var existing) ? existing + bytes : bytes;
            }
        }
        catch { return []; }

        var result = new List<GpuProcessInfo>();
        foreach (var (pid, bytes) in byPid)
        {
            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { continue; }

            result.Add(new GpuProcessInfo
            {
                Pid = pid,
                ProcessName = procName,
                UsedMemoryBytes = bytes
            });
        }

        return result;
    }

    private static NvmlProcessInfo[] ReadNvmlProcesses(IntPtr device, bool compute)
    {
        // Ask for the count first, then read with headroom: processes can start in between.
        uint count = 0;
        var status = compute
            ? nvmlDeviceGetComputeRunningProcesses_v3(device, ref count, null)
            : nvmlDeviceGetGraphicsRunningProcesses_v3(device, ref count, null);
        if (status != NvmlReturn.InsufficientSize || count == 0)
            return [];

        var infos = new NvmlProcessInfo[count + 8];
        count = (uint)infos.Length;
        status = compute
            ? nvmlDeviceGetComputeRunningProcesses_v3(device, ref count, infos)
            : nvmlDeviceGetGraphicsRunningProcesses_v3(device, ref count, infos);
        return status == NvmlReturn.Success ? infos[..(int)Math.Min(count, (uint)infos.Length)] : [];
    }

    private void SampleCpuTimes()
    {
        if (TryReadCpuTimes(out long idle, out long total))
        {
            _prevIdleTime = idle;
            _prevTotalTime = total;
            _hasPrevCpuSample = true;
        }
    }
}
