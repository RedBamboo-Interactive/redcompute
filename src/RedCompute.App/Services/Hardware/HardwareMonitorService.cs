using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
    private CapabilityRegistry? _registry;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    public void Start(CapabilityRegistry? registry = null)
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

        _registry = registry;
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
        var perProcessVram = CollectPerProcessGpuMemory();
        var pidToSlug = BuildPidToCapabilityMap();

        foreach (var proc in perProcessVram)
        {
            if (pidToSlug.TryGetValue(proc.Pid, out var slug))
                proc.CapabilitySlug = slug;
        }

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

            var capVram = new Dictionary<string, ulong>();
            foreach (var p in processes.Where(p => p.CapabilitySlug != null))
            {
                if (capVram.TryGetValue(p.CapabilitySlug!, out var existing))
                    capVram[p.CapabilitySlug!] = existing + p.UsedMemoryBytes;
                else
                    capVram[p.CapabilitySlug!] = p.UsedMemoryBytes;
            }

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
                CapabilityVram = capVram
            };

            gpus.Add(gpu);
        }

        return gpus;
    }

    private Dictionary<uint, string> BuildPidToCapabilityMap()
    {
        var map = new Dictionary<uint, string>();
        if (_registry == null) return map;

        var providerPids = new Dictionary<int, string>();
        foreach (var (slug, entry) in _registry.Capabilities)
        {
            foreach (var (_, provider) in entry.Providers)
            {
                var pid = provider.ProcessId;
                if (pid.HasValue)
                    providerPids[pid.Value] = slug;
            }
        }

        if (providerPids.Count == 0) return map;

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

        // For each process, walk up the tree to find a provider ancestor
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var pid = proc.Id;
                var current = pid;
                for (int depth = 0; depth < 10; depth++)
                {
                    if (providerPids.TryGetValue(current, out var slug))
                    {
                        map[(uint)pid] = slug;
                        break;
                    }
                    if (!childToParent.TryGetValue(current, out var parent) || parent == current)
                        break;
                    current = parent;
                }
            }
            catch { }
        }

        return map;
    }

    private static int GetParentProcessId(int pid)
    {
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

    private static readonly Regex PidPattern = new(@"^pid_(\d+)_", RegexOptions.Compiled);

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
