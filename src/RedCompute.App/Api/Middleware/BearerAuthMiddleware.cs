using System.Net;
using Microsoft.AspNetCore.Http;

namespace RedCompute.App.Api.Middleware;

public class BearerAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Func<string?> _getAccessToken;

    public BearerAuthMiddleware(RequestDelegate next, Func<string?> getAccessToken)
    {
        _next = next;
        _getAccessToken = getAccessToken;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var accessToken = _getAccessToken();
        if (string.IsNullOrEmpty(accessToken))
        {
            await _next(context);
            return;
        }

        if (IsLocalRequest(context))
        {
            await _next(context);
            return;
        }

        if (IsStaticAssetRequest(context))
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/ping"))
        {
            await _next(context);
            return;
        }

        string? provided = null;

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            provided = authHeader["Bearer ".Length..];

        provided ??= context.Request.Cookies["redcompute_token"];

        if (provided == null && context.WebSockets.IsWebSocketRequest)
            provided = context.Request.Query["token"].FirstOrDefault();

        if (provided == null || !string.Equals(provided, accessToken, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "unauthorized",
                message = "Valid access token required. Provide via Authorization: Bearer <token>"
            });
            return;
        }

        await _next(context);
    }

    private static bool IsStaticAssetRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path == "/" || path == "") return true;
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext);
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Cf-Connecting-Ip") ||
            context.Request.Headers.ContainsKey("Cf-Ray"))
            return false;

        var remote = context.Connection.RemoteIpAddress;
        if (remote == null) return true;
        if (IPAddress.IsLoopback(remote)) return true;
        if (remote.Equals(context.Connection.LocalIpAddress)) return true;
        return false;
    }
}
