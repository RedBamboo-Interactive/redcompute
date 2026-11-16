using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Rebase;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// Where a data move stands for RedCompute's own stored paths. The kernel polls it after handing over
/// <c>REDCOMPUTE_REBASE_MAP</c> and clears the pending rebase only when this reports <c>completed</c> for the
/// generation it sent. Carries no paths: owner names, counts and error text only.
/// </summary>
public static class StorageEndpoints
{
    public const string RebaseStatusPath = "/storage/rebase-status";

    public static void Map(EndpointRegistry endpoints, Func<RebaseStatus?>? status = null)
    {
        status ??= () => StoredPathRebase.Current?.Status;
        endpoints.MapGet(RebaseStatusPath,
            "Progress of the stored-path rebase after a data move (REDCOMPUTE_REBASE_MAP). generation is the generation of the map this process was given, or the last one it completed when it was given none; state is pending, running, completed or failed; owners lists each store (job-outputs, input-attachments, claude-sessions, codex-sessions, opencode-sessions, long-lived-processes, config) with its own state and counts. A failed rebase is retried at the next start.",
            () => Results.Ok(status() ?? new RebaseStatus(0, RebaseStates.Completed, [])))
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    generation = new { type = "integer", description = "The rebase map generation this process works on, or last completed." },
                    state = new { type = "string", @enum = new[] { "pending", "running", "completed", "failed" } },
                    owners = new
                    {
                        type = "array",
                        description = "One entry per store that holds paths.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                owner = new { type = "string" },
                                state = new { type = "string", @enum = new[] { "pending", "running", "completed", "failed" } },
                                examined = new { type = "integer", description = "Values looked at." },
                                changed = new { type = "integer", description = "Values rewritten." },
                                skipped = new { type = "integer", description = "Values left as they were (a UNIQUE clash)." },
                                error = new { type = "string", description = "Why the owner failed; absent otherwise." },
                            },
                        },
                    },
                },
            })
            .WithAuth("jwt");
    }
}
