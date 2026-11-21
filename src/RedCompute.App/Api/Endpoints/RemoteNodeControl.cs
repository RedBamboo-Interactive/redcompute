using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Nodes;
using RedCompute.NodeLink;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// <c>/control/start|stop/{slug}?nodeId=</c> for a remote node (Phase 4, design 3.3): the node's runtime serving the
/// capability is started or stopped through <c>runtime.start</c>/<c>runtime.stop</c> (an id and nothing else). Node zero's
/// providers and their manual-disable flags are never touched. The caller's access and the node's eligibility were
/// checked first (<see cref="GlobalEndpoints.ControlNode(HttpContext, INodeRegistry?, out string?)"/>); then, in the
/// design's order: a runtime for the capability on that node (422 <c>provider_unavailable_on_node</c> with
/// <c>availableOn</c>), then what the node answers.
/// </summary>
internal static class RemoteNodeControl
{
    public static async Task<IResult> RunAsync(bool start, string slug, string? providerName, CapabilityEntry entry,
        string nodeId, INodeRegistry nodes, HttpContext ctx)
    {
        IExecutionHost host;
        try
        {
            host = nodes.HostFor(nodeId);
        }
        catch (NodeUnavailableException error)
        {
            // Offline or revoked since the access check.
            return PlacementError.Unavailable(error).ToResult();
        }

        var runtime = Choose(host.Runtimes.List(), slug, providerName, entry);
        if (runtime is null) return UnavailableOnNode(slug, nodeId, AvailableOn(slug, entry, nodes, ctx.User));

        try
        {
            var answered = start
                ? await host.Runtimes.StartAsync(runtime.Id, ctx.RequestAborted)
                : await host.Runtimes.StopAsync(runtime.Id, null, ctx.RequestAborted);
            return Results.Ok(new
            {
                slug,
                provider = RemoteRuntimeKinds.ProviderName(entry, runtime.Kind),
                nodeId,
                runtime = runtime.Id,
                status = answered.Status.ToString(),
            });
        }
        catch (HostRuntimeException error) when (error.Code is HostRuntimeException.NotManaged or HostRuntimeException.Disabled
                                                     or HostRuntimeException.Stopping)
        {
            return Results.Json(new { error = error.Code, message = error.Message, nodeId, runtime = runtime.Id },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (HostRuntimeException)
        {
            // The node no longer has it (its next report drops it).
            return UnavailableOnNode(slug, nodeId, AvailableOn(slug, entry, nodes, ctx.User));
        }
        catch (NodeUnavailableException error)
        {
            return PlacementError.Unavailable(error).ToResult();
        }
        catch (TimeoutException error)
        {
            return Results.Json(new { error = "node_timeout", message = error.Message, nodeId }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (LinkRpcException error)
        {
            return Results.Json(new { error = start ? "start_failed" : "stop_failed", message = $"Node '{nodeId}' answered {error.Code}: {error.Message}", nodeId },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// The runtime a control call means on that node: one serving the capability (and, with a provider name, of that
    /// provider's kind); a managed, enabled one first, then an enabled one, then one of the active provider's kind, then by id.
    /// </summary>
    internal static HostRuntime? Choose(IReadOnlyList<HostRuntime> runtimes, string slug, string? providerName, CapabilityEntry entry)
    {
        var activeType = entry.DefaultProviderName is { } active && entry.Config.Providers.TryGetValue(active, out var activeConfig)
            ? activeConfig.Type
            : null;
        return runtimes
            .Where(runtime => runtime.Capabilities.Contains(slug, StringComparer.Ordinal))
            .Where(runtime => providerName is null || RemoteRuntimeKinds.ProviderName(entry, runtime.Kind) == providerName)
            .OrderByDescending(runtime => runtime.Managed == HostRuntimeManagement.Spawn && runtime.Enabled)
            .ThenByDescending(runtime => runtime.Enabled)
            .ThenByDescending(runtime => string.Equals(RemoteRuntimeKinds.ProviderType(runtime.Kind), activeType, StringComparison.OrdinalIgnoreCase))
            .ThenBy(runtime => runtime.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The nodes the caller may see where the capability has a provider: node zero when it has one, then the remote nodes
    /// whose reported runtimes serve it (design 1.2: <c>availableOn</c> is filtered by See).
    /// </summary>
    internal static IReadOnlyList<string> AvailableOn(string slug, CapabilityEntry? entry, INodeRegistry nodes, ClaimsPrincipal? caller)
    {
        var available = new List<string>();
        if (entry is { Providers.Count: > 0 }) available.Add(nodes.LocalNodeId);
        if (nodes is NodeRegistry registry)
            available.AddRange(registry.Nodes
                .Where(node => node.Kind == NodeKind.Remote && NodeAccess.Allows(caller, node, NodeUse.See)
                               && registry.RuntimesOf(node.Id).Any(runtime => runtime.Capabilities.Contains(slug, StringComparer.Ordinal)))
                .Select(node => node.Id));
        return available;
    }

    /// <summary>The design's 422 for a node that has no provider for the capability (the same shape generate answers).</summary>
    internal static IResult UnavailableOnNode(string capability, string nodeId, IReadOnlyList<string> availableOn)
        => Results.Json(new
        {
            error = "provider_unavailable_on_node",
            message = $"'{capability}' has no provider on node '{nodeId}'",
            nodeId,
            availableOn,
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
}
