using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.Core.Discovery;
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

    public static (IBackendProvider? provider, IResult? error) Resolve(
        CapabilityEntry entry, string? requestedProvider, string capabilityName)
    {
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
