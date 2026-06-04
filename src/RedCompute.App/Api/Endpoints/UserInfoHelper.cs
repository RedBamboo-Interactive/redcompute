using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Api.Endpoints;

public static class UserInfoHelper
{
    public static async Task<(string? UserId, string? UserName, string? AvatarUrl)> ResolveFromContext(HttpContext ctx)
    {
        var userId = ctx.User?.FindFirst("sub")?.Value;
        var userName = ctx.User?.FindFirst("name")?.Value;
        var avatarUrl = ctx.User?.FindFirst("picture")?.Value;

        if (userId != null && avatarUrl == null)
        {
            var userStore = ctx.RequestServices.GetService<IUserStore>();
            if (userStore != null)
            {
                var user = await userStore.FindByIdAsync(userId);
                avatarUrl = user?.AvatarUrl;
            }
        }

        return (userId, userName, avatarUrl);
    }
}
