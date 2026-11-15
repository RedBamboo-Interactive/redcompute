using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// The hand-over of a consistent copy of the job database to RedLeaf's off-site backup. Kernel service
/// token only: the copy holds every job, prompt and output path.
/// </summary>
public static class BackupEndpoints
{
    public const string JobDatabasePath = "/backups/job-database";

    public static void Map(EndpointRegistry endpoints, JobDatabaseSnapshotService snapshots)
    {
        endpoints.MapPost(JobDatabasePath,
            "Writes a consistent online snapshot of the job database (SQLite online backup, while RedCompute keeps running) to backups/redcompute-snapshot-<UTC timestamp>.db under the data root; a successful snapshot removes any older one, so at most one stays. Synchronous: takes minutes on a large database. Returns path, size, sha256, page count, duration and the quick_check result. 409 while one runs. RedLeaf service token only.",
            async (HttpContext context) =>
            {
                if (!IsKernelService(context)) return Forbidden();
                try
                {
                    var result = await snapshots.TryCreateAsync();
                    return result is null
                        ? Results.Json(new { error = "snapshot_running", message = "A snapshot is already running" },
                            statusCode: StatusCodes.Status409Conflict)
                        : Results.Ok(result);
                }
                catch (JobDatabaseSnapshotException ex)
                {
                    return Results.Json(new { error = "snapshot_failed", message = ex.Message, quickCheck = ex.QuickCheck },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            })
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Absolute path of the snapshot file on the RedCompute host" },
                    sizeBytes = new { type = "integer" },
                    sha256 = new { type = "string", description = "Lowercase hex SHA-256 of the snapshot file" },
                    pageCount = new { type = "integer" },
                    pageSize = new { type = "integer" },
                    durationSeconds = new { type = "number" },
                    quickCheck = new { type = "string", description = "PRAGMA quick_check on the snapshot; always ok in a success response" },
                    createdAt = new { type = "string", format = "date-time" },
                },
            })
            .WithAuth("jwt");

        endpoints.MapDelete(JobDatabasePath,
            "Removes the job database snapshot file. 409 while a snapshot runs. RedLeaf service token only.",
            (HttpContext context) =>
            {
                if (!IsKernelService(context)) return Forbidden();
                return snapshots.TryDelete() switch
                {
                    null => Results.Json(new { error = "snapshot_running", message = "A snapshot is running" },
                        statusCode: StatusCodes.Status409Conflict),
                    var deleted => Results.Ok(new { deleted }),
                };
            })
            .WithAuth("jwt");
    }

    private static IResult Forbidden()
        => Results.Json(new { error = "forbidden", message = "Database snapshots require the RedLeaf service token" },
            statusCode: StatusCodes.Status403Forbidden);

    // The same rule as the usage collector: the kernel's token, never an execution token.
    private static bool IsKernelService(HttpContext context)
        => ComputeResourceAccess.IsTrustedInfrastructure(context)
            && !string.Equals(context.User.FindFirst("token_use")?.Value, "execution",
                StringComparison.OrdinalIgnoreCase);
}
