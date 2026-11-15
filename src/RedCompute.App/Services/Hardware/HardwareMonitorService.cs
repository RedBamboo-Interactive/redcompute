using RedCompute.Core.Hardware;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.Hardware;

/// <summary>
/// Samples a node's hardware every two seconds and attributes GPU memory to capabilities. The
/// sampling itself is the node's (<see cref="IHostHardware"/>); the loop and the attribution, which
/// knows RedCompute's providers, are RedCompute's.
/// </summary>
public class HardwareMonitorService : IDisposable
{
    private Timer? _timer;
    private IExecutionHost _host = LocalExecutionHost.Shared;
    private IHostHardwareSampler? _sampler;
    private HardwareSnapshot? _lastSnapshot;
    private CapabilityRegistry? _registry;

    public event Action<HardwareSnapshot>? SnapshotUpdated;

    /// <summary>The node this monitor samples; set at boot before <see cref="Start"/>.</summary>
    public string NodeId { get; set; } = "";

    /// <param name="host">The node whose hardware is sampled; the shared local host when none is given.</param>
    public void Start(CapabilityRegistry? registry = null, IExecutionHost? host = null)
    {
        _host = host ?? LocalExecutionHost.Shared;
        _sampler = _host.Hardware.OpenSampler();
        _registry = registry;
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public HardwareSnapshot? GetSnapshot() => _lastSnapshot;

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        _sampler?.Dispose();
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
            var sampler = _sampler!;
            var snapshot = sampler.Sample();
            snapshot.NodeId = NodeId;
            if (sampler.SamplesGpus) Attribute(snapshot.Gpus);

            _lastSnapshot = snapshot;
            SnapshotUpdated?.Invoke(snapshot);
        }
        catch { }
    }

    /// <summary>
    /// Each sampled GPU process gets the capability whose provider process it descends from, and each
    /// GPU the VRAM per capability of its own processes.
    /// </summary>
    private void Attribute(List<GpuInfo> gpus)
    {
        var pidToSlug = BuildPidToCapabilityMap();

        foreach (var proc in gpus.SelectMany(gpu => gpu.Processes))
        {
            if (pidToSlug.TryGetValue(proc.Pid, out var slug))
                proc.CapabilitySlug = slug;
        }

        foreach (var gpu in gpus)
        {
            var capVram = new Dictionary<string, ulong>();
            foreach (var p in gpu.Processes.Where(p => p.CapabilitySlug != null))
            {
                if (capVram.TryGetValue(p.CapabilitySlug!, out var existing))
                    capVram[p.CapabilitySlug!] = existing + p.UsedMemoryBytes;
                else
                    capVram[p.CapabilitySlug!] = p.UsedMemoryBytes;
            }
            gpu.CapabilityVram = capVram;
        }
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

        var parentage = _host.Hardware.ProcessParentage();
        var childToParent = parentage.ChildToParent;

        // For each process, walk up the tree to find a provider ancestor
        foreach (var pid in parentage.ProcessIds)
        {
            try
            {
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
}
