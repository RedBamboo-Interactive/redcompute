using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// The explicit way to keep a server beyond its session. Everything a session starts dies with the
/// session; a server launched here does not, and is listed and stoppable instead. Launching runs a
/// command line as RedCompute, so it and every change require a signed admin execution identity.
/// </summary>
public static class LongLivedProcessEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(EndpointRegistry endpoints, LongLivedProcessRegistry registry)
    {
        endpoints.MapGet("/processes/long-lived",
            "Servers deliberately kept beyond their session (registered, never reaped): name, port, PID, state (running | restarting | exited), exitedAt (OS exit time; null when it ended while no RedCompute was watching, then lastSeenAliveAt bounds it), restart policy and count, who started them, log path. An exited entry stays listed until DELETE",
            (HttpContext context) => context.User.Identity?.IsAuthenticated == true
                ? Results.Json(new { processes = registry.List() })
                : Results.Unauthorized());

        endpoints.MapPost("/processes/long-lived",
            "Start a server that outlives the calling session, RedCompute and RedLeaf: { name, command (one cmd.exe line), workingDirectory, port?, description?, environment?, restart? }. It is created outside RedCompute's jobs, parented to the desktop shell, with the user's environment plus `environment` (never RedCompute's). restart: \"never\" (default) or \"on-exit\" (relaunched with backoff; 5 exits in a row within 30 s stop it). Output goes to logPath. Admin execution identity required",
            async (HttpContext context) =>
            {
                if (Admin(context) is not { } owner) return Forbidden();
                var request = await ReadAsync<LongLivedLaunchRequest>(context);
                if (request is null) return Results.BadRequest(new { error = "invalid_body", message = "Body must be { name, command, workingDirectory, port?, restart? }" });
                return ToResult(registry.Launch(request, owner));
            });

        endpoints.MapPost("/processes/long-lived/adopt",
            "Register an already running server that is outside every session job: { pid, name, port?, description?, processStartedAt? }. A process inside a live session is refused (409): relaunch it instead. Admin execution identity required",
            async (HttpContext context) =>
            {
                if (Admin(context) is not { } owner) return Forbidden();
                var request = await ReadAsync<LongLivedAdoptRequest>(context);
                if (request is null || request.Pid <= 0) return Results.BadRequest(new { error = "invalid_body", message = "Body must be { pid, name }" });
                return ToResult(registry.Adopt(request, owner));
            });

        endpoints.MapDelete("/processes/long-lived/{id}",
            "Stop a registered long-lived server (its whole tree, only if it is still the same process) and remove it; also clears an exited entry. Admin execution identity required",
            (HttpContext context, string id) => Admin(context) is null ? Forbidden() : ToResult(registry.Stop(id)));
    }

    private static LongLivedOwner? Admin(HttpContext context)
    {
        var identity = ConfidentialResourcePolicy.ExecutionIdentity(context);
        if (identity is null || !ExecutionIdentityClaims.ReadRoles(context.User).Contains("admin", StringComparer.OrdinalIgnoreCase))
            return null;
        var session = identity.Context.FirstOrDefault(item => item.Kind.Equals("session", StringComparison.OrdinalIgnoreCase))?.Id;
        return new LongLivedOwner(UserInfoHelper.ResolveUserId(context), identity.Actor.Name, session);
    }

    private static IResult Forbidden()
        => Results.Json(new { error = "forbidden", message = "Signed admin execution identity required" },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult ToResult(LongLivedProcessResult result)
        => result.Status == StatusCodes.Status200OK
            ? Results.Json(result.Process)
            : Results.Json(new { error = result.Status switch { 400 => "invalid_request", 404 => "not_found", _ => "conflict" }, message = result.Error },
                statusCode: result.Status);

    private static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        try { return await context.Request.ReadFromJsonAsync<T>(JsonOptions, context.RequestAborted); }
        catch { return null; }
    }
}
