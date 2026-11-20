using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Hardware;
using RedCompute.NodeLink;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Reports;

/// <summary>
/// What the node says about itself (design 2.5, 4.9): <c>node.describe</c>, <c>node.hardware</c> and
/// <c>node.cli</c>, with the rate limits the design sets: a hardware sample at most every 5 s on request,
/// a CLI re-probe at most once a minute on request.
/// </summary>
public sealed class NodeReporter : IDisposable
{
    public static readonly TimeSpan HardwareMinInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CliRefreshMinInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IExecutionHost _host;
    private readonly CliProber _prober;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _hardwareGate = new(1, 1);
    private readonly SemaphoreSlim _cliGate = new(1, 1);
    private IHostHardwareSampler? _sampler;
    private HardwareSnapshot? _lastSample;
    private long _lastSampleAt;
    private IReadOnlyList<CliReport>? _clis;
    private long _clisAt;
    private string? _processor;

    public NodeReporter(IExecutionHost host, IProbeRunner runner, TimeProvider? time = null)
    {
        _host = host;
        _prober = new CliProber(host, runner);
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>node.describe</c>: platform, processor, machine name, the hardware summary, WSL, agent, link, key store.</summary>
    public async Task<JsonObject> DescribeAsync(string nodeId, LinkSession? session, string? credentialStore, CancellationToken ct)
    {
        var info = _host.Platform.Info;
        var hardware = await SampleAsync(nodeId, force: false, ct);
        var describe = new JsonObject
        {
            ["nodeId"] = nodeId,
            ["platform"] = new JsonObject
            {
                ["os"] = info.Os,
                ["osDescription"] = info.OsDescription,
                ["arch"] = info.Architecture,
                ["chassis"] = info.Chassis,
            },
            ["machineName"] = info.MachineName,
            ["processor"] = _processor ??= _host.Hardware.ProcessorName(),
            ["hardware"] = Summary(hardware),
            ["wsl"] = _host.Platform.IsWindows ? WslPresent() : null,
            ["agent"] = new JsonObject
            {
                ["version"] = AgentIdentity.Version,
                ["os"] = AgentIdentity.Os,
                ["arch"] = AgentIdentity.Arch,
            },
            ["protocol"] = LinkProtocol.Current,
            ["features"] = new JsonArray((session?.Features ?? AgentIdentity.Features).Select(f => (JsonNode)f!).ToArray()),
            ["credentialStore"] = credentialStore,
        };
        return describe;
    }

    /// <summary><c>node.hardware</c>: one snapshot with the node's id, at most one fresh sample per 5 s.</summary>
    public async Task<JsonObject> HardwareAsync(string nodeId, CancellationToken ct)
        => (JsonObject)JsonSerializer.SerializeToNode(await SampleAsync(nodeId, force: false, ct), Json)!;

    /// <summary>The periodic report's hardware: a fresh sample.</summary>
    public async Task<JsonObject> HardwareReportAsync(string nodeId, CancellationToken ct)
        => (JsonObject)JsonSerializer.SerializeToNode(await SampleAsync(nodeId, force: true, ct), Json)!;

    /// <summary><c>node.cli</c>: the last probe; <paramref name="refresh"/> re-probes unless one ran in the last minute.</summary>
    public async Task<(JsonArray Clis, bool Changed)> CliAsync(bool refresh, CancellationToken ct)
    {
        await _cliGate.WaitAsync(ct);
        try
        {
            var due = _clis is null || (refresh && _time.GetElapsedTime(_clisAt) >= CliRefreshMinInterval);
            var changed = false;
            if (due)
            {
                var fresh = await _prober.ProbeAsync(ct);
                changed = _clis is null || !fresh.SequenceEqual(_clis);
                _clis = fresh;
                _clisAt = _time.GetTimestamp();
            }
            return (new JsonArray(_clis!.Select(cli => (JsonNode)cli.ToJson()).ToArray()), changed);
        }
        finally
        {
            _cliGate.Release();
        }
    }

    /// <summary>The scheduled re-probe (every 30 minutes): always probes, reports whether anything changed.</summary>
    public async Task<(JsonArray Clis, bool Changed)> ReprobeAsync(CancellationToken ct)
    {
        await _cliGate.WaitAsync(ct);
        try
        {
            var fresh = await _prober.ProbeAsync(ct);
            var changed = _clis is null || !fresh.SequenceEqual(_clis);
            _clis = fresh;
            _clisAt = _time.GetTimestamp();
            return (new JsonArray(_clis.Select(cli => (JsonNode)cli.ToJson()).ToArray()), changed);
        }
        finally
        {
            _cliGate.Release();
        }
    }

    private async Task<HardwareSnapshot> SampleAsync(string nodeId, bool force, CancellationToken ct)
    {
        await _hardwareGate.WaitAsync(ct);
        try
        {
            if (!force && _lastSample is not null && _time.GetElapsedTime(_lastSampleAt) < HardwareMinInterval)
                return _lastSample;
            var sample = await Task.Run(() =>
            {
                _sampler ??= _host.Hardware.OpenSampler();
                return _sampler.Sample();
            }, ct);
            sample.NodeId = nodeId;
            _lastSample = sample;
            _lastSampleAt = _time.GetTimestamp();
            return sample;
        }
        finally
        {
            _hardwareGate.Release();
        }
    }

    /// <summary>Names and totals only: the summary describe and the Nodes tab show, never live usage.</summary>
    internal static JsonObject Summary(HardwareSnapshot snapshot) => new()
    {
        ["cpuCores"] = snapshot.Cpu.CoreCount,
        ["ramTotalBytes"] = snapshot.Ram.TotalBytes,
        ["gpus"] = new JsonArray(snapshot.Gpus.Select(gpu => (JsonNode)new JsonObject
        {
            ["name"] = gpu.Name,
            ["vramTotalBytes"] = gpu.Memory.TotalBytes,
        }).ToArray()),
    };

    private bool WslPresent()
    {
        var root = _host.Platform.GetEnvironmentVariable("SystemRoot");
        return root is not null && _host.Files.FileExists(_host.Path(_host.PathRules.Combine(root, "System32", "wsl.exe")));
    }

    public void Dispose()
    {
        _sampler?.Dispose();
        _hardwareGate.Dispose();
        _cliGate.Dispose();
    }
}
