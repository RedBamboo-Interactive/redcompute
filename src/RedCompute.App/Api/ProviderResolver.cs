using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

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
    /// <see cref="CapabilityEntry.Providers"/>. A remote node has no provider instance yet (design 3.1,
    /// P7), so it is 422 <c>provider_unavailable_on_node</c>; it cannot be reached while
    /// <c>INodeRegistry.HostFor</c> refuses every remote node.
    /// </param>
    public static (IBackendProvider? provider, IResult? error) Resolve(
        CapabilityEntry entry, string? requestedProvider, string capabilityName, PlacementResolution? placement = null)
    {
        if (placement is { IsLocal: false })
            return (null, Results.Json(new
            {
                error = "provider_unavailable_on_node",
                message = $"'{capabilityName}' has no provider on node '{placement.NodeId}'",
                nodeId = placement.NodeId,
                availableOn = Array.Empty<string>(),
            }, statusCode: 422));

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

    public static void StripProviderFromBody(Dictionary<string, object?> body)
    {
        body.Remove("provider");
    }
}
