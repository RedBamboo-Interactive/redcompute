using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Api.Endpoints;

public static class UserInfoHelper
{
    public static async Task<(string? UserId, string? UserName, string? AvatarUrl)> ResolveFromContext(HttpContext ctx)
    {
        var claimUserId = ctx.User?.FindFirst("sub")?.Value;
        var userId = ResolveUserId(ctx);
        var userName = userId == claimUserId ? ctx.User?.FindFirst("name")?.Value : null;
        var avatarUrl = userId == claimUserId ? ctx.User?.FindFirst("picture")?.Value : null;

        if (userId != null && (avatarUrl == null || userName == null))
        {
            var userStore = ctx.RequestServices.GetService<IUserStore>();
            if (userStore != null)
            {
                var user = await userStore.FindByIdAsync(userId);
                userName ??= user?.Name;
                avatarUrl ??= user?.AvatarUrl;
            }
        }

        return (userId, userName, avatarUrl);
    }

    /// <summary>
    /// Resolve the user an upstream suite service is acting for. A loopback-only
    /// <c>X-User-Id</c> must win over the transport principal: Nova authenticates to
    /// RedCompute as a service while forwarding the actual discussion owner here.
    /// </summary>
    public static string? ResolveUserId(HttpContext ctx)
    {
        if (IsLocalRequest(ctx) &&
            ctx.Request.Headers.TryGetValue("X-User-Id", out var forwarded) &&
            !string.IsNullOrWhiteSpace(forwarded))
            return forwarded.ToString();

        return ctx.User?.FindFirst("sub")?.Value;
    }

    private static bool IsLocalRequest(HttpContext ctx)
    {
        if (ctx.Request.Headers.ContainsKey("Cf-Connecting-Ip") ||
            ctx.Request.Headers.ContainsKey("Cf-Ray"))
            return false;

        var remote = ctx.Connection.RemoteIpAddress;
        return remote == null || System.Net.IPAddress.IsLoopback(remote) ||
               remote.Equals(ctx.Connection.LocalIpAddress);
    }
}
