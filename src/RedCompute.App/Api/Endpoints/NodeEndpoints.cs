using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Hardware;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public sealed record NodeListView(IReadOnlyList<NodeView> Nodes);

/// <summary>A computer RedCompute runs work on, with what it has and what runs on it right now.</summary>
public sealed record NodeView(
    string Id,
    string Name,
    string Kind,
    string Status,
    bool IsLocal,
    NodePlatformView Platform,
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    NodeIdentityView Identity,
    NodeHardwareView Hardware,
    IReadOnlyList<NodeCapabilityView> Capabilities);

public sealed record NodePlatformView(string Os, string OsDescription, string Architecture, string MachineName,
    string? ProcessorName);

/// <summary>Persisted is false when node.json could not be read or written; Problem then says why.</summary>
public sealed record NodeIdentityView(bool Persisted, string? Problem);

public sealed record NodeHardwareView(bool Available, DateTimeOffset? Timestamp, NodeCpuView? Cpu, NodeRamView? Ram,
    IReadOnlyList<NodeGpuView> Gpus);

public sealed record NodeCpuView(int CoreCount, double UsagePercent);

public sealed record NodeRamView(ulong TotalBytes, ulong UsedBytes);

public sealed record NodeGpuView(int Index, string Name, ulong MemoryTotalBytes, ulong MemoryUsedBytes);

public sealed record NodeCapabilityView(
    string Slug,
    string DisplayName,
    string? Icon,
    string? Color,
    string? Category,
    string Status,
    string ExecutionMode,
    bool Sleeping,
    bool Disabled,
    IReadOnlyList<NodeProviderView> Providers);

public sealed record NodeProviderView(string Name, string? Type, string Status, string? StatusDetail, bool IsDefault);

/// <summary>
/// Read-only node API: <c>GET /nodes</c> and <c>GET /nodes/{id}</c>, with <c>local</c> accepted as the
/// local node's id. In Phase 1 every capability and provider runs on node zero.
/// </summary>
public static class NodeEndpoints
{
    public static void Map(EndpointRegistry endpoints, NodeRegistry nodes, CapabilityRegistry registry,
        HardwareMonitorService hardwareMonitor)
        => Map(endpoints, nodes, registry, hardwareMonitor.GetSnapshot);

    public static void Map(EndpointRegistry endpoints, NodeRegistry nodes, CapabilityRegistry registry,
        Func<HardwareSnapshot?> hardware)
    {
        endpoints.MapGet("/nodes",
            "The computers RedCompute runs work on, each with its platform, hardware summary, and the capabilities and providers on it with their current status. Phase 1: exactly one, this machine (isLocal).",
            async Task<Ok<NodeListView>> () =>
            {
                var view = await BuildAsync(nodes, nodes.Local, registry, hardware());
                return TypedResults.Ok(new NodeListView([view]));
            })
            .WithResponse(SchemaOf<NodeListView>());

        endpoints.MapGet("/nodes/{id}",
            "One computer by id; 'local' is accepted for this machine. 404 problem details for an unknown id.",
            async Task<Results<Ok<NodeView>, ProblemHttpResult>> (string id) =>
            {
                var node = nodes.Find(id);
                if (node is null)
                    return TypedResults.Problem(
                        title: "Unknown node",
                        detail: $"No node has the id '{id}'. Known: {nodes.LocalNodeId} (alias '{INodeRegistry.LocalAlias}').",
                        statusCode: StatusCodes.Status404NotFound);
                return TypedResults.Ok(await BuildAsync(nodes, node, registry, hardware()));
            })
            .WithResponse(SchemaOf<NodeView>());
    }

    /// <summary>
    /// The response schema, exported from the record itself. Discovery reads the first 200 response type,
    /// which for a typed result is the result wrapper, so the schema is declared here instead.
    /// </summary>
    private static JsonNode SchemaOf<T>() => JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Web, typeof(T),
        new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });

    internal static async Task<NodeView> BuildAsync(NodeRegistry nodes, NodeDescriptor node,
        CapabilityRegistry registry, HardwareSnapshot? snapshot)
    {
        var isLocal = node.Id == nodes.LocalNodeId;
        return new NodeView(
            node.Id,
            node.Name,
            node.Kind.ToString().ToLowerInvariant(),
            node.Status.ToString().ToLowerInvariant(),
            isLocal,
            new NodePlatformView(node.Os, node.OsDescription, node.Architecture, node.MachineName, node.ProcessorName),
            node.AgentVersion,
            node.StartedAt,
            node.LastSeenAt,
            new NodeIdentityView(nodes.Identity.Persisted, nodes.Identity.Problem),
            Hardware(snapshot),
            // Phase 1: every capability runs on node zero.
            isLocal ? await CapabilitiesAsync(registry) : []);
    }

    private static NodeHardwareView Hardware(HardwareSnapshot? snapshot) => snapshot is null
        ? new NodeHardwareView(false, null, null, null, [])
        : new NodeHardwareView(
            true,
            snapshot.Timestamp,
            new NodeCpuView(snapshot.Cpu.CoreCount, snapshot.Cpu.UsagePercent),
            new NodeRamView(snapshot.Ram.TotalBytes, snapshot.Ram.UsedBytes),
            snapshot.Gpus.Select(gpu => new NodeGpuView(gpu.Index, gpu.Name, gpu.Memory.TotalBytes, gpu.Memory.UsedBytes))
                .ToList());

    /// <summary>The same status rules as <c>/status</c>: each provider is asked once, concurrently.</summary>
    private static async Task<IReadOnlyList<NodeCapabilityView>> CapabilitiesAsync(CapabilityRegistry registry)
    {
        var capabilities = registry.Capabilities.ToArray();
        var statuses = new Dictionary<IBackendProvider, Task<BackendStatus>>(ReferenceEqualityComparer.Instance);
        foreach (var provider in capabilities.SelectMany(capability => capability.Value.Providers.Values))
            if (!statuses.ContainsKey(provider))
                statuses.Add(provider, StatusOf(provider));
        await Task.WhenAll(statuses.Values);

        return capabilities
            .OrderBy(capability => capability.Value.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(capability =>
            {
                var (slug, entry) = capability;
                var active = entry.ActiveProvider;
                var status = entry.IsExternal ? BackendStatus.Running
                    : active is null ? BackendStatus.Stopped
                    : statuses[active].Result;
                var providers = entry.Providers
                    .OrderBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(provider => new NodeProviderView(
                        provider.Key,
                        entry.Config.Providers.TryGetValue(provider.Key, out var config) ? config.Type : null,
                        statuses[provider.Value].Result.ToString(),
                        provider.Value.StatusDetail,
                        provider.Key == entry.DefaultProviderName))
                    .ToList();
                return new NodeCapabilityView(
                    slug,
                    entry.Definition.DisplayName,
                    entry.Definition.Icon,
                    entry.Definition.Color,
                    entry.Definition.Category,
                    status.ToString(),
                    entry.Definition.ExecutionMode.ToString().ToLowerInvariant(),
                    entry.IsSleeping,
                    entry.IsManuallyDisabled,
                    providers);
            })
            .ToList();
    }

    private static async Task<BackendStatus> StatusOf(IBackendProvider provider)
    {
        try { return await provider.GetStatusAsync(); }
        catch { return BackendStatus.Error; }
    }
}
