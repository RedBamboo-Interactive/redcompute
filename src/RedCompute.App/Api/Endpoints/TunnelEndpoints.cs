using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;

namespace RedCompute.App.Api.Endpoints;

public static class TunnelEndpoints
{
    public static void Map(WebApplication app, CloudflareTunnelService tunnelService)
    {
        app.MapGet("/tunnel/status", () => Results.Ok(new
        {
            status = tunnelService.Status.ToString(),
            hostname = App.ConfigManager.Config.Tunnel.Hostname,
            error = tunnelService.ErrorMessage
        }));

        app.MapPost("/tunnel/start", async () =>
        {
            var config = App.ConfigManager.Config;
            var started = await tunnelService.StartAsync(config.ApiPort, config.Tunnel);
            return Results.Ok(new
            {
                status = tunnelService.Status.ToString(),
                hostname = config.Tunnel.Hostname,
                error = tunnelService.ErrorMessage,
                started
            });
        });

        app.MapPost("/tunnel/stop", async () =>
        {
            await tunnelService.StopAsync();
            return Results.Ok(new
            {
                status = tunnelService.Status.ToString(),
                stopped = true
            });
        });
    }
}
