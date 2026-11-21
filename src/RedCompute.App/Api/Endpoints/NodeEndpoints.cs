using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Hardware;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

/// <param name="LegacyNodeId">
/// The node a record stored before node ids existed belongs to: every response already reports such a
/// record under this id. Equal to node zero's id unless its identity was recreated.
/// </param>
public sealed record NodeListView(IReadOnlyList<NodeView> Nodes, string LegacyNodeId);

/// <summary>A computer RedCompute runs work on, with what it has and what runs on it right now.</summary>
/// <param name="Kind"><c>local</c> (node zero, this machine) or <c>remote</c> (a paired machine running leaf-node).</param>
/// <param name="Status"><c>online</c>, <c>offline</c>, <c>revoked</c>, <c>needs-update</c> or <c>pending-confirmation</c> (online, not yet confirmed by its owner).</param>
/// <param name="Owner">The user id of the person the node belongs to; null for node zero.</param>
/// <param name="Confirmed">"This is my node" was confirmed; an unconfirmed node is never eligible for work. Always true for node zero.</param>
/// <param name="StatusReason">Why the node is in its status (<c>heartbeat_timeout</c>, <c>revoked_by_owner</c>, <c>unpaired_by_node</c>, …).</param>
/// <param name="Link">A remote node's live link while it is up; null otherwise.</param>
/// <param name="Clis">The CLIs a remote node's fixed probe found (never their output or credentials); empty for node zero.</param>
/// <param name="Placement">Whether placement may choose this node, and why not. Phase 2's Run-on reads only this.</param>
/// <param name="Icon">The node's icon, a curated Phosphor name, from its kernel entity; null from an older kernel (show the platform's).</param>
/// <param name="Color">The node's colour, a palette token, from its kernel entity; null from an older kernel (show it neutral).</param>
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
    IReadOnlyList<NodeCapabilityView> Capabilities,
    string? Owner,
    bool Confirmed,
    string? StatusReason,
    DateTimeOffset? PairedAt,
    NodeLinkView? Link,
    IReadOnlyList<NodeCliView> Clis,
    NodePlacementView Placement,
    string? Icon = null,
    string? Color = null);

/// <param name="Chassis">What kind of machine its firmware says it is (desktop, all-in-one, laptop, tablet, mini, server), when it says.</param>
public sealed record NodePlatformView(string Os, string OsDescription, string Architecture, string MachineName,
    string? ProcessorName, string? Chassis = null);

/// <summary>Persisted is false when node.json could not be read or written; Problem then says why.</summary>
public sealed record NodeIdentityView(bool Persisted, string? Problem);

public sealed record NodeHardwareView(bool Available, DateTimeOffset? Timestamp, NodeCpuView? Cpu, NodeRamView? Ram,
    IReadOnlyList<NodeGpuView> Gpus);

public sealed record NodeCpuView(int CoreCount, double UsagePercent);

public sealed record NodeRamView(ulong TotalBytes, ulong UsedBytes);

public sealed record NodeGpuView(int Index, string Name, ulong MemoryTotalBytes, ulong MemoryUsedBytes);

/// <summary>A remote node's link: when it came up, its last report, round trip and clock offset, what it speaks.</summary>
public sealed record NodeLinkView(
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastHeartbeatAt,
    double? RttMs,
    double? ClockOffsetMs,
    int Protocol,
    IReadOnlyList<string> Features,
    string? AgentVersion,
    bool UpdateAvailable,
    string? CredentialStore);

/// <summary>One CLI on a remote node: installed, version, signed in and how (never a token or an auth file).</summary>
public sealed record NodeCliView(string Name, bool Installed, string? Version, bool SignedIn, string? AuthMethod);

/// <param name="Reason">Null when eligible; else <c>remote_execution_not_available</c>, <c>offline</c>, <c>revoked</c>, <c>not_confirmed</c> or <c>needs_update</c>.</param>
public sealed record NodePlacementView(bool Eligible, string? Reason);

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

/// <summary>What <c>POST /nodes/{id}/revoked</c> did.</summary>
public sealed record NodeRevokedView(string NodeId, string Status, string? Reason);

/// <summary>
/// The node API: <c>GET /nodes</c> and <c>GET /nodes/{id}</c> (<c>local</c> is node zero's alias), node zero
/// and every paired remote node; the kernel's <c>POST /nodes/{id}/revoked</c> and <c>POST /nodes/changed</c>;
/// the admin diagnostics <c>POST /nodes/{id}/diag/echo</c>. In Phase 3 every capability and provider runs on
/// node zero, and no remote node is eligible for work. A remote node is visible only as the kernel's
/// <c>/api/nodes</c> shows it (<see cref="CanSee"/>).
/// </summary>
public static class NodeEndpoints
{
    /// <summary>The default and largest echo runs.</summary>
    public const long DefaultEchoBytes = 1L << 20;

    public static void Map(EndpointRegistry endpoints, NodeRegistry nodes, CapabilityRegistry registry,
        HardwareMonitorService hardwareMonitor, NodeHub? hub = null, KernelNodeSync? sync = null,
        NodeAgentRelease? agentRelease = null)
        => Map(endpoints, nodes, registry, hardwareMonitor.GetSnapshot, hub, sync, agentRelease);

    public static void Map(EndpointRegistry endpoints, NodeRegistry nodes, CapabilityRegistry registry,
        Func<HardwareSnapshot?> hardware, NodeHub? hub = null, KernelNodeSync? sync = null,
        NodeAgentRelease? agentRelease = null)
    {
        var release = agentRelease ?? new NodeAgentRelease();
        endpoints.MapGet("/nodes/agent",
            "Where to get leaf-node, the agent a machine runs to become a node (the \"Add a node\" dialog): the Nightly publishes it beside RedCompute's Windows release. Read on request from the signed release feed's current nightly RedCompute record (the pointer, then the record it names by SHA-256), then leaf-node-manifest.v1.json in that release, which must name the record's version and source commit: the chain the installers follow before installing. Returns {available, reason, channel, version, sourceCommit, releaseUrl, manifestUrl, windows, linux}; windows and linux are {archiveUrl, sha256, sizeBytes, installerUrl, installCommand}. installCommand only installs: pairing stays the separate `leaf-node pair --server <origin> --code <code>`. available false (with reason) when the feed cannot be read or names no leaf-node, as before the first Nightly that ships it. Cached 10 minutes (1 minute after a failure).",
            async Task<Ok<NodeAgentDownload>> (HttpContext context) => TypedResults.Ok(await release.GetAsync(context.RequestAborted)))
            .WithResponse(SchemaOf<NodeAgentDownload>());

        endpoints.MapGet("/nodes",
            "The computers RedCompute runs work on: node zero (kind local, isLocal, this machine) first, then every paired remote node (kind remote) by name. Each has its platform (with chassis when the firmware says), its icon and color from the kernel's node entity (null from an older kernel), hardware summary, status (online|offline|revoked|needs-update|pending-confirmation) with statusReason, owner, confirmed, the live link of a remote node, the CLIs it reported, and placement {eligible, reason}: node zero is eligible; a remote node is not in this release (reason remote_execution_not_available, offline, revoked, not_confirmed or needs_update). Capabilities and providers run on node zero only. legacyNodeId is the node records older than node ids belong to. Remote nodes are listed only to their owner and to admins (a signed identity; an Agent counts as the person it works for); anyone else, the anonymous local identity included, sees node zero only.",
            async Task<Ok<NodeListView>> (HttpContext context) =>
            {
                var views = new List<NodeView>();
                foreach (var node in nodes.Nodes.Where(node => CanSee(context.User, node)))
                    views.Add(await BuildAsync(nodes, node, registry, node.Kind == NodeKind.Local ? hardware() : node.Report?.Hardware));
                return TypedResults.Ok(new NodeListView(views, nodes.LegacyNodeId));
            })
            .WithResponse(SchemaOf<NodeListView>());

        endpoints.MapGet("/nodes/{id}",
            "One computer by id, the same shape as an item of GET /nodes; 'local' is accepted for this machine. 404 problem details for an unknown id, or a remote node the caller may not see (the same rule as GET /nodes).",
            async Task<Results<Ok<NodeView>, ProblemHttpResult>> (string id, HttpContext context) =>
            {
                var node = nodes.Find(id);
                if (node is null || !CanSee(context.User, node))
                    return TypedResults.Problem(
                        title: "Unknown node",
                        detail: $"No node has the id '{id}'. Known: {nodes.LocalNodeId} (alias '{INodeRegistry.LocalAlias}').",
                        statusCode: StatusCodes.Status404NotFound);
                return TypedResults.Ok(await BuildAsync(nodes, node, registry, node.Kind == NodeKind.Local ? hardware() : node.Report?.Hardware));
            })
            .WithResponse(SchemaOf<NodeView>());

        endpoints.MapPost("/nodes/{id}/revoked",
            "The kernel revoked a remote node (service:redleaf only; 403 otherwise). RedCompute marks it revoked for good, closes its link at once (close code 4403) and refuses every later link. Body {reason?}. Node zero cannot be revoked (409 node_zero_not_revocable).",
            async (string id, HttpContext context) =>
            {
                if (!SettingsEndpoints.IsRedLeafService(context.User))
                    return Error(StatusCodes.Status403Forbidden, "service_only", "Only the kernel reports a revocation.");
                if (!KernelNode.IsNodeId(id))
                    return Error(StatusCodes.Status400BadRequest, "invalid_node_id", "id must be node_ followed by a 26-character ULID.");
                if (id == nodes.LocalNodeId || id == nodes.LegacyNodeId)
                    return Error(StatusCodes.Status409Conflict, "node_zero_not_revocable", "Node zero is the machine RedCompute runs on.");
                var reason = await ReasonAsync(context) ?? "revoked_by_owner";
                var node = hub is not null ? await hub.RevokeAsync(id, reason) : nodes.MarkRevoked(id, reason);
                return Results.Json(new NodeRevokedView(node.Id, StatusText(node.Status), node.StatusReason));
            })
            .WithParam("reason", "string", location: ParamLocation.Body, description: "Why (revoked_by_owner, unpaired_by_node, …); default revoked_by_owner")
            .WithResponse(SchemaOf<NodeRevokedView>());

        endpoints.MapPost("/nodes/changed",
            "The kernel says a node entity changed (paired, confirmed, renamed, removed): RedCompute re-reads GET /api/nodes now instead of at its next 5-minute sync. service:redleaf only. 202.",
            (HttpContext context) =>
            {
                if (!SettingsEndpoints.IsRedLeafService(context.User))
                    return Error(StatusCodes.Status403Forbidden, "service_only", "Only the kernel reports a node change.");
                sync?.RequestSync();
                return Results.Json(new { accepted = sync is not null }, statusCode: StatusCodes.Status202Accepted);
            });

        endpoints.MapPost("/nodes/{id}/diag/echo",
            "Admin diagnostics, in person only: streams {bytes} (default 1 MiB, at most 1 GiB, rounded up to 8) through the remote node's diag.echo channel and checks every word comes back once and in order. Returns {bytes, framesSent, framesReceived, duplicates, outOfOrder, rttMs, durationMs, mebibytesPerSecond}. 403 for anyone else; 404 unknown node; 409 node_offline, node_revoked or node_zero (node zero has no link); 422 feature_not_supported; 504 echo_timeout.",
            async (string id, HttpContext context) =>
            {
                if (!ComputeResourceAccess.IsPersonPresent(context)
                    || !ExecutionIdentityClaims.ReadRoles(context.User).Contains("admin", StringComparer.OrdinalIgnoreCase))
                    return Error(StatusCodes.Status403Forbidden, "admin_person_required", "Only an admin, in person, runs node diagnostics.");
                var node = nodes.Find(id);
                if (node is null) return Error(StatusCodes.Status404NotFound, "node_not_found", $"No node has the id '{id}'.");
                if (node.Kind == NodeKind.Local) return Error(StatusCodes.Status409Conflict, "node_zero", "Node zero has no link to test.");
                if (node.Status == NodeStatus.Revoked) return Error(StatusCodes.Status409Conflict, "node_revoked", "This node was revoked.");
                if (hub is null) return Error(StatusCodes.Status503ServiceUnavailable, "node_hub_unavailable", "NodeHub is not running.");

                var bytes = DefaultEchoBytes;
                var body = await BodyAsync(context);
                if (body?["bytes"] is JsonValue value)
                    if (!value.TryGetValue<long>(out bytes) || bytes is <= 0 or > NodeHub.MaxEchoBytes)
                        return Error(StatusCodes.Status400BadRequest, "invalid_bytes", $"bytes must be between 1 and {NodeHub.MaxEchoBytes}.");
                // 30 s plus 1 s per MiB: the acceptance's 256 MiB over a tunnel fits.
                var timeout = TimeSpan.FromSeconds(30 + bytes / (double)(1 << 20));
                try
                {
                    return Results.Json(await hub.EchoAsync(node.Id, bytes, timeout, context.RequestAborted));
                }
                catch (NodeHubException error)
                {
                    var status = error.Code switch
                    {
                        "node_offline" => StatusCodes.Status409Conflict,
                        "feature_not_supported" => StatusCodes.Status422UnprocessableEntity,
                        "echo_timeout" => StatusCodes.Status504GatewayTimeout,
                        "invalid_bytes" => StatusCodes.Status400BadRequest,
                        _ => StatusCodes.Status502BadGateway,
                    };
                    return Error(status, error.Code, error.Message);
                }
            })
            .WithParam("bytes", "integer", location: ParamLocation.Body, description: "How many bytes to echo (default 1048576, at most 1073741824)")
            .WithResponse(SchemaOf<NodeEchoResult>());
    }

    /// <summary>
    /// Who sees a remote node: the kernel's read rule for <c>/api/nodes</c> (its <c>NodeAccess.CanRead</c>),
    /// which is <see cref="NodeAccess"/>'s <see cref="NodeUse.See"/>, the same rule placement uses.
    /// </summary>
    internal static bool CanSee(ClaimsPrincipal user, NodeDescriptor node) => NodeAccess.Allows(user, node, NodeUse.See);

    /// <summary>
    /// The response schema, exported from the record itself. Discovery reads the first 200 response type,
    /// which for a typed result is the result wrapper, so the schema is declared here instead.
    /// </summary>
    private static JsonNode SchemaOf<T>() => JsonSchemaExporter.GetJsonSchemaAsNode(JsonSerializerOptions.Web, typeof(T),
        new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);

    private static async Task<JsonObject?> BodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0 || context.Request.Body is null) return null;
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            return text.Length is 0 or > 16 * 1024 ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReasonAsync(HttpContext context)
        => (await BodyAsync(context))?["reason"] is JsonValue value && value.TryGetValue<string>(out var reason)
           && !string.IsNullOrWhiteSpace(reason)
            ? reason.Trim().Length <= 120 ? reason.Trim() : reason.Trim()[..120]
            : null;

    /// <summary>The status as the API spells it (the kernel's <c>needs-update</c>, not the enum name).</summary>
    public static string StatusText(NodeStatus status) => status switch
    {
        NodeStatus.Online => "online",
        NodeStatus.Offline => "offline",
        NodeStatus.Revoked => "revoked",
        NodeStatus.NeedsUpdate => "needs-update",
        NodeStatus.PendingConfirmation => "pending-confirmation",
        _ => status.ToString().ToLowerInvariant(),
    };

    internal static async Task<NodeView> BuildAsync(NodeRegistry nodes, NodeDescriptor node,
        CapabilityRegistry registry, HardwareSnapshot? snapshot)
    {
        var isLocal = node.Id == nodes.LocalNodeId;
        var placement = NodeRegistry.PlacementOf(node);
        return new NodeView(
            node.Id,
            node.Name,
            node.Kind.ToString().ToLowerInvariant(),
            StatusText(node.Status),
            isLocal,
            new NodePlatformView(node.Os, node.OsDescription, node.Architecture, node.MachineName, node.ProcessorName, node.Chassis),
            node.AgentVersion,
            node.StartedAt,
            node.LastSeenAt,
            isLocal ? new NodeIdentityView(nodes.Identity.Persisted, nodes.Identity.Problem) : new NodeIdentityView(true, null),
            Hardware(snapshot),
            // Phase 3: every capability runs on node zero.
            isLocal ? await CapabilitiesAsync(registry) : [],
            node.OwnerUserId,
            node.Confirmed,
            node.StatusReason,
            node.PairedAt,
            node.Link is { } link
                ? new NodeLinkView(link.ConnectedAt, link.LastHeartbeatAt, link.RttMs, link.ClockOffsetMs, link.Protocol, link.Features,
                    link.AgentVersion, link.UpdateAvailable, link.CredentialStore)
                : null,
            node.Report?.Clis.Select(cli => new NodeCliView(cli.Name, cli.Installed, cli.Version, cli.SignedIn, cli.AuthMethod)).ToList() ?? [],
            new NodePlacementView(placement.Eligible, placement.Reason),
            node.Icon,
            node.Color);
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
