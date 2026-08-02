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
    /// Resolve the user an upstream suite service is acting for. Forwarded identity wins
    /// only for the authenticated RedLeaf service with explicit delegation authority;
    /// loopback transport alone conveys no trust.
    /// </summary>
    public static string? ResolveUserId(HttpContext ctx)
    {
        if (CanDelegateUser(ctx) &&
            ctx.Request.Headers.TryGetValue("X-User-Id", out var forwarded) &&
            !string.IsNullOrWhiteSpace(forwarded))
            return forwarded.ToString();

        return ctx.User?.FindFirst("sub")?.Value;
    }

    private static bool CanDelegateUser(HttpContext ctx)
        => string.Equals(ctx.User?.FindFirst("client_id")?.Value, "redleaf", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(ctx.User?.FindFirst("compute_delegate_user")?.Value, "true", StringComparison.OrdinalIgnoreCase);
}
