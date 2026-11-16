using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services.Placement;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// The placement shadow's evidence for the stage-B flip (design 6, Layer 3): per creation path, how the
/// references resolved compare with the legacy fields that were used. Carries no paths: app ids, route
/// names, placement shapes, error codes and counts only, as <c>/storage/rebase-status</c> does.
/// </summary>
public static class PlacementEndpoints
{
    public const string ShadowStatusPath = "/placement/shadow-status";

    public static void Map(EndpointRegistry endpoints, Func<PlacementShadowState?> status)
    {
        var counts = new
        {
            type = "object",
            properties = new
            {
                exact = new { type = "integer", description = "The same bytes." },
                equivalent = new { type = "integer", description = "The same place written differently (separators, case the node ignores, a trailing separator, an empty list for none)." },
                mismatch = new { type = "integer", description = "A different place, or only one side names one." },
            },
        };
        endpoints.MapGet(ShadowStatusPath,
            "Stage-A placement shadow: every session or execute start that sends placement beside its legacy path fields is resolved by reference in the background and compared with what the legacy fields made the provider use (the legacy fields still decide). "
            + "paths has one entry per caller app (from the job's provenance), route (sessions, execute) and placement shape (workspace, repository[+checkout][+branch][+subPath], scratch(env|add-dir|cwd), joined by +): "
            + "observed starts, compared ones, unavailable (a resolver could not answer, 503) and refused (404/409/422: the legacy fields worked, the placement would not have) with their codes, and per field (cwd, repositoryId, scratch, addDirs, env) exact, equivalent and mismatch counts. "
            + "Counts survive restarts; since is when counting began. Never carries a path.",
            () => Results.Ok(status() ?? new PlacementShadowState()))
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    schemaVersion = new { type = "integer" },
                    since = new { type = "string", format = "date-time", description = "First counted start; null before any." },
                    updatedAt = new { type = "string", format = "date-time" },
                    totals = new
                    {
                        type = "object",
                        properties = new
                        {
                            observed = new { type = "integer" },
                            compared = new { type = "integer" },
                            unavailable = new { type = "integer" },
                            refused = new { type = "integer" },
                            pathsWithDisagreement = new { type = "integer", description = "Paths with a field mismatch or a refused resolution." },
                            exact = new { type = "integer" },
                            equivalent = new { type = "integer" },
                            mismatch = new { type = "integer" },
                        },
                    },
                    paths = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                app = new { type = "string" },
                                route = new { type = "string", @enum = new[] { "sessions", "execute" } },
                                shape = new { type = "string" },
                                observed = new { type = "integer" },
                                compared = new { type = "integer" },
                                unavailable = new { type = "integer" },
                                refused = new { type = "integer" },
                                codes = new { type = "object", description = "Resolution error code to count." },
                                fields = new { type = "object", description = "Field name to its counts.", additionalProperties = counts },
                                firstSeenAt = new { type = "string", format = "date-time" },
                                lastSeenAt = new { type = "string", format = "date-time" },
                                lastMismatchAt = new { type = "string", format = "date-time" },
                            },
                        },
                    },
                },
            })
            .WithAuth("jwt");
    }
}
