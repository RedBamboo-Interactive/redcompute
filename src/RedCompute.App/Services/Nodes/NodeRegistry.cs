using System.IO;
using System.Runtime.InteropServices;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Nodes;

/// <summary>The machine facts a node reports, read once at boot.</summary>
public sealed record NodePlatform(string Os, string OsDescription, string Architecture, string MachineName,
    string? ProcessorName)
{
    public static NodePlatform Detect() => new(
        OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown",
        RuntimeInformation.OSDescription.Trim(),
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        Environment.MachineName,
        ReadProcessorName());

    /// <summary>The CPU's marketing name, best effort: null when the OS does not say.</summary>
    private static string? ReadProcessorName()
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
/// Phase 1 node registry: exactly one node, the machine RedCompute runs on. Read-only; built once in
/// <see cref="RedComputeHost"/> before providers are created.
/// </summary>
public sealed class NodeRegistry : INodeRegistry
{
    private readonly NodePlatform _platform;
    private readonly string _version;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<DateTimeOffset> _clock;

    public NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock = null)
    {
        Identity = identity;
        _platform = platform;
        _version = version;
        _startedAt = startedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Opens node.json in <paramref name="directory"/> (creating it on first boot) and detects the platform.</summary>
    public static NodeRegistry Open(string directory, string version, Action<string> log)
        => new(NodeIdentityStore.LoadOrCreate(directory, log), NodePlatform.Detect(), version, DateTimeOffset.UtcNow);

    /// <summary>The identity this run uses, and whether it is the persisted one.</summary>
    public NodeIdentityResult Identity { get; }

    public string LocalNodeId => Identity.Identity.Id;
    public string LegacyNodeId => Identity.Identity.LegacyRowsNodeId;

    public NodeDescriptor Local => new(
        LocalNodeId,
        _platform.MachineName,
        NodeKind.Local,
        NodeStatus.Online,
        _platform.Os,
        _platform.OsDescription,
        _platform.Architecture,
        _platform.MachineName,
        _platform.ProcessorName,
        _version,
        _startedAt,
        _clock());

    public IReadOnlyList<NodeDescriptor> Nodes => [Local];

    public NodeDescriptor? Find(string nodeId)
        => string.Equals(nodeId, INodeRegistry.LocalAlias, StringComparison.OrdinalIgnoreCase)
           || string.Equals(nodeId, LocalNodeId, StringComparison.Ordinal)
            ? Local
            : null;
}
