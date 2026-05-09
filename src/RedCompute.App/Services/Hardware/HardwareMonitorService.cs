using System.Diagnostics;
using System.Text;
using RedCompute.Core.Hardware;
using static RedCompute.App.Services.Hardware.NvmlInterop;
using static RedCompute.App.Services.Hardware.SystemInterop;

namespace RedCompute.App.Services.Hardware;

public class HardwareMonitorService : IDisposable
{
    private Timer? _timer;
    private bool _nvmlAvailable;
    private uint _gpuCount;
    private HardwareSnapshot? _lastSnapshot;
    private long _prevIdleTime, _prevKernelTime, _prevUserTime;
    private bool _hasPrevCpuSample;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    public void Start()
    {
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
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public HardwareSnapshot? GetSnapshot() => _lastSnapshot;

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        if (_nvmlAvailable)
        {
            try { nvmlShutdown(); } catch { }
            _nvmlAvailable = false;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Poll()
    {
        try
        {
            var snapshot = new HardwareSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                Cpu = CollectCpu(),
                Ram = CollectRam(),
                Gpus = _nvmlAvailable ? CollectGpus() : new()
            };

            _lastSnapshot = snapshot;
            SnapshotUpdated?.Invoke(snapshot);
        }
        catch { }
    }

    private CpuInfo CollectCpu()
    {
        double usage = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_hasPrevCpuSample)
            {
                var idleDelta = idle - _prevIdleTime;
                var totalDelta = (kernel - _prevKernelTime) + (user - _prevUserTime);
                if (totalDelta > 0)
                    usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
            }

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasPrevCpuSample = true;
        }

        return new CpuInfo
        {
            UsagePercent = Math.Round(Math.Max(0, Math.Min(100, usage)), 1),
            CoreCount = Environment.ProcessorCount
        };
    }

    private static RamInfo CollectRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            var used = mem.ullTotalPhys - mem.ullAvailPhys;
            return new RamInfo
            {
                TotalBytes = mem.ullTotalPhys,
                UsedBytes = used,
                AvailableBytes = mem.ullAvailPhys,
                UsagePercent = mem.ullTotalPhys > 0 ? Math.Round(used * 100.0 / mem.ullTotalPhys, 1) : 0
            };
        }
        return new RamInfo();
    }

    private List<GpuInfo> CollectGpus()
    {
        var gpus = new List<GpuInfo>();

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
                Processes = CollectGpuProcesses(device)
            };

            gpus.Add(gpu);
        }

        return gpus;
    }

    private static List<GpuProcessInfo> CollectGpuProcesses(IntPtr device)
    {
        var result = new List<GpuProcessInfo>();
        var seen = new HashSet<uint>();

        CollectProcessList(device, nvmlDeviceGetComputeRunningProcesses_v3, result, seen);
        CollectProcessList(device, nvmlDeviceGetGraphicsRunningProcesses_v3, result, seen);

        result.Sort((a, b) => b.UsedMemoryBytes.CompareTo(a.UsedMemoryBytes));
        return result;
    }

    private delegate NvmlReturn GetProcessesFn(IntPtr device, ref uint count, NvmlProcessInfo[]? infos);

    private static void CollectProcessList(IntPtr device, GetProcessesFn fn, List<GpuProcessInfo> result, HashSet<uint> seen)
    {
        uint count = 0;
        var ret = fn(device, ref count, null);
        if (ret != NvmlReturn.Success && ret != NvmlReturn.InsufficientSize)
            return;
        if (count == 0)
            return;

        var infos = new NvmlProcessInfo[count];
        if (fn(device, ref count, infos) != NvmlReturn.Success)
            return;

        for (int i = 0; i < count; i++)
        {
            var pid = infos[i].Pid;
            if (!seen.Add(pid))
                continue;

            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { procName = $"PID {pid}"; }

            // NVML returns ULONG_MAX when per-process memory is unavailable (common for graphics processes)
            var memBytes = infos[i].UsedGpuMemory == ulong.MaxValue ? 0UL : infos[i].UsedGpuMemory;

            result.Add(new GpuProcessInfo
            {
                Pid = pid,
                ProcessName = procName,
                UsedMemoryBytes = memBytes
            });
        }
    }

    private void SampleCpuTimes()
    {
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasPrevCpuSample = true;
        }
    }
}
