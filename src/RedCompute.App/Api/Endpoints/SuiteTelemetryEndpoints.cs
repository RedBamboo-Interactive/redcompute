using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;

namespace RedCompute.App.Api.Endpoints;

public static class SuiteTelemetryEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly (string Name, int Port, string Color)[] SuiteApps =
    [
        ("RedCompute", 18800, "#26A69A"),
        ("CodeRed", 18801, "#E55B5B"),
        ("RedMatter", 18802, "#7C4DFF"),
        ("Nova", 18803, "#C74B7A"),
        ("RedLeaf", 18804, "#66BB6A"),
    ];

    public static void Map(EndpointRegistry endpoints)
    {
        endpoints.MapGet("/api/telemetry/suite",
            "Aggregate telemetry stats from all running suite apps",
            async (string? since) =>
            {
                var tasks = SuiteApps.Select(async app =>
                {
                    try
                    {
                        var url = $"http://localhost:{app.Port}/api/telemetry/stats";
                        if (since is not null) url += $"?since={Uri.EscapeDataString(since)}";

                        var response = await Http.GetAsync(url);
                        if (!response.IsSuccessStatusCode)
                            return new
                            {
                                app.Name, app.Port, app.Color,
                                status = "error", stats = (JsonElement?)null,
                            };

                        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                        return new
                        {
                            app.Name, app.Port, app.Color,
                            status = "online", stats = (JsonElement?)json,
                        };
                    }
                    catch
                    {
                        return new
                        {
                            app.Name, app.Port, app.Color,
                            status = "offline", stats = (JsonElement?)null,
                        };
                    }
                });

                var results = await Task.WhenAll(tasks);
                return Results.Ok(new { apps = results });
            });
    }
}
