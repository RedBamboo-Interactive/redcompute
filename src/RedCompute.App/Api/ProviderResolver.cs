using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api;

public static class ProviderResolver
{
    public static string? GetRequestedProvider(HttpContext ctx, Dictionary<string, object?>? body = null)
    {
        if (body != null && body.TryGetValue("provider", out var val))
        {
            if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            if (val is string s)
                return s;
        }

        return ctx.Request.Headers["X-Provider"].FirstOrDefault();
    }

    /// <param name="placement">
    /// The node <c>/{slug}/generate</c> resolved, or null for node zero. Node zero's providers are
    /// <see cref="CapabilityEntry.Providers"/>, resolved exactly as before. A remote node's are
    /// <see cref="CapabilityEntry.NodeProviders"/> (design 3.1, 3.3): the requested provider there, or the
    /// active provider's name, or the only one; none is 422 <c>provider_unavailable_on_node</c> with the nodes the
    /// caller may see that have one (<paramref name="nodes"/>, <paramref name="caller"/>).
    /// </param>
    public static (IBackendProvider? provider, IResult? error) Resolve(
        CapabilityEntry entry, string? requestedProvider, string capabilityName, PlacementResolution? placement = null,
        INodeRegistry? nodes = null, ClaimsPrincipal? caller = null)
    {
        if (placement is { IsLocal: false })
            return ResolveOnNode(entry, requestedProvider, capabilityName, placement.NodeId, nodes, caller);

        if (requestedProvider != null && !entry.Providers.ContainsKey(requestedProvider))
        {
            var available = string.Join(", ", entry.Providers.Keys);
            return (null, Results.Json(new ErrorResponse
            {
                Error = "provider_not_found",
                Message = $"Provider '{requestedProvider}' not found for {capabilityName}. Available: {available}"
            }, statusCode: 404));
        }

        var provider = entry.ResolveProvider(requestedProvider);
        return (provider, null);
    }

    private static (IBackendProvider? provider, IResult? error) ResolveOnNode(CapabilityEntry entry, string? requestedProvider,
        string capabilityName, string nodeId, INodeRegistry? nodes, ClaimsPrincipal? caller)
    {
        var onNode = entry.NodeProviders
            .Where(pair => pair.Key.NodeId == nodeId)
            .OrderBy(pair => pair.Key.Provider, StringComparer.Ordinal)
            .ToList();
        if (requestedProvider is not null)
        {
            if (onNode.FirstOrDefault(pair => pair.Key.Provider == requestedProvider).Value is { } named) return (named, null);
            // A name no node the caller may see has is the same 404 as on node zero (review P4-P7, item 7): whether it
            // is 404 or 422 is decided on node zero's providers, this node's, and those of the nodes the caller may see,
            // so the answer never tells that a node the caller may not see has it.
            var seen = nodes?.Nodes.Where(node => node.Kind == NodeKind.Remote && NodeAccess.Allows(caller, node, NodeUse.See))
                .Select(node => node.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            if (!entry.Providers.ContainsKey(requestedProvider)
                && !entry.NodeProviders.Keys.Any(key => key.Provider == requestedProvider && (key.NodeId == nodeId || seen.Contains(key.NodeId))))
                return (null, Results.Json(new ErrorResponse
                {
                    Error = "provider_not_found",
                    Message = $"Provider '{requestedProvider}' not found for {capabilityName}. Available: {string.Join(", ", entry.Providers.Keys)}"
                }, statusCode: 404));
        }
        else
        {
            if (entry.DefaultProviderName is { } active && onNode.FirstOrDefault(pair => pair.Key.Provider == active).Value is { } activeOnNode)
                return (activeOnNode, null);
            if (onNode.Count > 0) return (onNode[0].Value, null);
        }
        return (null, RemoteNodeControl.UnavailableOnNode(capabilityName, nodeId,
            nodes is null ? [] : RemoteNodeControl.AvailableOn(capabilityName, entry, nodes, caller)));
    }

    public static void StripProviderFromBody(Dictionary<string, object?> body)
    {
        body.Remove("provider");
    }
}
