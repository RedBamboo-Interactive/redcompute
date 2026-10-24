using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services.Jobs;

namespace RedCompute.App.Api.Endpoints;

public static class UsageEndpoints
{
    private static readonly string[] Statuses = ["succeeded", "failed", "cancelled", "other"];
    private static readonly string[] Media = ["image", "video", "animation", "audio", "none"];
    private static readonly string[] Initiators = ["you", "agent", "other"];
    private static readonly string[] Providers = ["comfyui", "codex", "claude-code", "opencode", "other"];

    public static void Map(EndpointRegistry endpoints, UsageCountsService usage)
    {
        endpoints.MapGet("/usage/daily-counts",
            "Content-free daily job counts for one beneficiary, for RedLeaf Usage Insights. RedLeaf service token only. Excludes confidential and child jobs; never returns names, inputs, results, outputs, errors or provenance.",
            (HttpContext context, string? since, string? until, string? beneficiary, string? capability) =>
            {
                if (!IsUsageCollector(context))
                    return Results.Json(new { error = "forbidden", message = "Usage counts require the RedLeaf service token" },
                        statusCode: StatusCodes.Status403Forbidden);

                var result = usage.GetDailyCounts(since, until, beneficiary, capability);
                return result.Counts is { } counts
                    ? Results.Ok(counts)
                    : Results.BadRequest(new { error = result.Error, message = result.Message });
            })
            .WithParam("since", "string", required: true, location: ParamLocation.Query,
                description: "First UTC day, inclusive, YYYY-MM-DD")
            .WithParam("until", "string", required: true, location: ParamLocation.Query,
                description: $"Last UTC day, inclusive, YYYY-MM-DD; before today and at most {UsageCountsService.MaxWindowDays} days after since")
            .WithParam("beneficiary", "string", required: true, location: ParamLocation.Query,
                description: "User id the jobs were created on behalf of")
            .WithParam("capability", "string", location: ParamLocation.Query,
                description: "Optional capability slug filter, such as image-gen")
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    days = new
                    {
                        type = "array",
                        description = "One entry per UTC day in the window, ascending, including days with no jobs",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                day = new { type = "string", format = "date" },
                                groups = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            capability = new { type = "string" },
                                            provider = new { type = "string", @enum = Providers },
                                            status = new { type = "string", @enum = Statuses },
                                            media = new { type = "string", @enum = Media },
                                            initiator = new { type = "string", @enum = Initiators },
                                            jobs = new { type = "integer", minimum = 1 },
                                            executionSeconds = new
                                            {
                                                type = "integer",
                                                minimum = 0,
                                                description = "Floored sum of CompletedAt - ProviderStartedAt over succeeded jobs",
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            })
            .WithAuth("jwt");
    }

    // Execution tokens never qualify, even if a future issuer copied service claims onto one:
    // counts span every app and agent of a beneficiary, so only the kernel collector may read them.
    private static bool IsUsageCollector(HttpContext context)
        => ComputeResourceAccess.IsTrustedInfrastructure(context)
            && !string.Equals(context.User.FindFirst("token_use")?.Value, "execution",
                StringComparison.OrdinalIgnoreCase);
}
