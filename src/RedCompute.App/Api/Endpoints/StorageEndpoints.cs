using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Rebase;
using RedCompute.Core.Storage;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// Where a data move stands for RedCompute's own stored paths. The kernel polls it after handing over
/// <c>REDCOMPUTE_REBASE_MAP</c> and clears the pending rebase only when this reports <c>completed</c> for the
/// generation it sent. Carries no paths: owner names, counts and error text only.
/// <c>GET /storage/manifest</c> declares what a data move may copy: the class of every location RedCompute owns,
/// as paths relative to its data root (never an absolute path, never file contents).
/// </summary>
public static class StorageEndpoints
{
    public const string RebaseStatusPath = "/storage/rebase-status";

    public const string ManifestPath = "/storage/manifest";

    /// <summary>The manifest document's schema version; the kernel refuses one it does not know.</summary>
    public const int ManifestSchemaVersion = 1;

    public static void Map(EndpointRegistry endpoints, Func<RebaseStatus?>? status = null, Func<ComputeStorage>? storage = null)
    {
        status ??= () => StoredPathRebase.Current?.Status;
        storage ??= () => ComputeStorage.Current;
        endpoints.MapGet(ManifestPath,
            "What a data move may do with RedCompute's own files (read by the kernel before a move). schemaVersion 1; locations lists every location RedCompute owns as a path relative to its data root ('/'-separated, never absolute) with its class (durable: copied and verified; rebuildable: copied only when asked; disposable: never copied) and mutability (immutable: files are written once and safe to copy while running; mutable: copied only while stopped). Anything under the data root that is not listed is durable and mutable. SQLite sidecars (-wal, -shm, -journal) follow their database. Carries no absolute paths and no file contents.",
            () => Results.Ok(new
            {
                schemaVersion = ManifestSchemaVersion,
                locations = storage().Locations().Select(l => new { name = l.Name, path = l.Path, @class = l.Class, mutability = l.Mutability }),
            }))
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    schemaVersion = new { type = "integer" },
                    locations = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new { type = "string" },
                                path = new { type = "string", description = "Relative to RedCompute's data root, '/'-separated." },
                                @class = new { type = "string", @enum = new[] { "durable", "rebuildable", "disposable" } },
                                mutability = new { type = "string", @enum = new[] { "immutable", "mutable" } },
                            },
                        },
                    },
                },
            })
            .WithAuth("jwt");
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
