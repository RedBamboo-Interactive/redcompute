using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// Changing a session's access level, the legacy permission-mode alias, and RedLeaf's policy-change
/// signal. Every change goes through <see cref="SessionAccessService"/> and the same rules.
/// </summary>
public static class SessionAccessEndpoints
{
    internal const string LevelDescription =
        "Access levels, lowest first: plan (read and plan only), ask (workspace edits run; anything else parks as a permission card a person answers), "
        + "auto (the provider's own classifier decides; Claude only), full (everything runs unprompted). A level the provider cannot enforce becomes ask; "
        + "session.supportedAccessLevels lists what it can.";

    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry, IJobTracker jobTracker, SessionAccessService access)
    {
        endpoints.MapPost("/ai-session/sessions/{id}/access-level",
            "Change the session's access level. " + LevelDescription + " Lowering is open to anyone with write access to the session. "
            + "Raising needs a person (an Agent's or automation's token gets 403 raise_requires_person), Full enabled in RedLeaf "
            + "(403 full_access_not_enabled) and stays at or below the session's maxAccessLevel (403 above_session_cap). "
            + "Entering plan remembers the level it left; omit level (or send null) to leave plan for that level. "
            + "A live session changes at once and is recorded at the new level only once its harness confirmed the switch; when the harness "
            + "refuses (or does not answer in time) the session keeps its level and the call is 409 provider_refused_level, whose message "
            + "says so in plain words with the harness's own reason, and whose accessLevel is the level kept. "
            + "A stopped session launches at the new level. The level set here is explicit (session accessLevelSource explicit): it no longer "
            + "follows the Agent's or global default when RedLeaf's policy changes. Unknown levels are 422.",
            async (HttpContext ctx, string id) =>
        {
            var (provider, info, error) = Find(ctx, registry, jobTracker, id);
            if (error is not null) return error;

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (body.ValueKind != JsonValueKind.Object)
                return Error(400, "invalid_body", "Request body must be a JSON object");

            string? level = null;
            if (body.TryGetProperty("level", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String || !SessionAccessLevels.TryNormalize(value.GetString(), out var normalized))
                    return Error(422, SessionAccessResolver.InvalidAccessLevel,
                        $"level must be one of {string.Join(", ", SessionAccessLevels.All)}");
                level = normalized;
            }

            return await ChangeAsync(ctx, access, provider!, info!, level, legacyMode: null);
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("level", "string", description: "The new level. Omit or null to leave plan for the level it was entered from.",
                enumValues: [.. SessionAccessLevels.All], location: ParamLocation.Body)
            .WithResponse(new
            {
                type = "object",
                required = new[] { "accessLevel", "ok" },
                properties = new
                {
                    accessLevel = new { type = new[] { "string", "null" }, @enum = SessionAccessLevels.All.Cast<object?>().Append(null).ToArray() },
                    ok = new { type = "boolean", description = "True when the level is in force. A harness refusal is 409 provider_refused_level instead" },
                },
            });

        endpoints.MapPost("/ai-session/sessions/{id}/permission-mode",
            "Compatibility alias of POST /ai-session/sessions/{id}/access-level, under exactly the same rules. Legacy mode names map to levels: "
            + "bypassPermissions -> full; acceptEdits, default, manual, dontAsk -> ask; plan; auto. Responds with the provider-native mode now in force.",
            async (HttpContext ctx, string id) =>
        {
            var (provider, info, error) = Find(ctx, registry, jobTracker, id);
            if (error is not null) return error;

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var mode = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("mode", out var mm)
                && mm.ValueKind == JsonValueKind.String ? mm.GetString() : null;
            if (string.IsNullOrWhiteSpace(mode))
                return Error(422, "validation_failed", "mode is required");
            if (!SessionAccessLevels.TryFromPermissionMode(mode, out var level))
                return Error(422, SessionAccessResolver.InvalidAccessLevel,
                    $"mode must be one of {string.Join(", ", LegacyModes)}");

            return await ChangeAsync(ctx, access, provider!, info!, level, legacyMode: mode);
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("mode", "string", required: true, description: "Legacy permission mode, mapped to an access level",
                enumValues: LegacyModes, location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/access-policy/changed",
            "RedLeaf calls this after it writes the Agent permission policy. RedCompute reads the policy fresh. Every session whose "
            + "accessLevelSource is policy (its level came from the Agent's or global default) moves to the re-resolved default, raised or "
            + "lowered, within its maxAccessLevel; a live one changes through its harness and is stopped if it keeps running above its new level. "
            + "Sessions with an explicit level keep it, except that when Full access is no longer enabled every session holding full is demoted "
            + "to its resolved default. Approvals already parked stay parked. Authenticated service callers (RedLeaf's kernel) and people only; "
            + "an Agent or automation token gets 403. Body (optional, as RedLeaf sends it): {version (policy shape version), revision}; "
            + "it only labels the log line, the policy itself is always re-read. Idempotent.",
            async (HttpContext ctx) =>
        {
            if (!MayAnnouncePolicyChange(ctx))
                return Error(403, "service_caller_required", "Only RedLeaf announces a policy change");
            var revision = await ReadRevisionAsync(ctx);
            // Not tied to the request: RedLeaf gives up waiting after 3 s, and a live session's switch
            // can take longer to confirm. Every session must still be brought to the new policy.
            var changed = await access.PolicyChangedAsync(CancellationToken.None, revision);
            var policy = await access.Policy.GetAsync(ct: ctx.RequestAborted);
            return Results.Json(new
            {
                refreshed = true,
                revision,
                policyAvailable = policy.Available,
                policyStored = policy.Stored,
                fullAccessEnabled = policy.FullAccessEnabled,
                demotedSessions = changed.Demoted,
                followedSessions = changed.Followed,
            });
        })
            .WithParam("version", "integer", description: "Policy shape version (1)", location: ParamLocation.Body)
            .WithParam("revision", "integer", description: "RedLeaf's policy revision after the write, for the log", location: ParamLocation.Body)
            .WithResponse(new
            {
                type = "object",
                properties = new
                {
                    refreshed = new { type = "boolean" },
                    revision = new { type = new[] { "integer", "null" }, description = "The revision RedLeaf announced, echoed" },
                    policyAvailable = new { type = "boolean", description = "False when RedLeaf could not be read; RedCompute then runs fail-closed (ask, Full disabled)" },
                    policyStored = new { type = "boolean", description = "False when RedLeaf holds no saved policy (the owner never saved Agent permissions) or could not be read. With no saved policy the default is auto (ask where the provider has no auto) and Full is disabled" },
                    fullAccessEnabled = new { type = "boolean" },
                    demotedSessions = new { type = "array", items = new { type = "string" }, description = "Sessions moved off full because Full is no longer enabled" },
                    followedSessions = new { type = "array", items = new { type = "string" }, description = "Policy-sourced sessions whose level changed to follow the default" },
                },
            });
    }

    /// <summary>RedLeaf's announced revision. The body is a label only: missing or malformed is fine.</summary>
    private static async Task<long?> ReadRevisionAsync(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return null;
            using var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("revision", out var revision)
                && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var value) ? value : null;
        }
        catch (Exception ex) when (ex is JsonException or System.IO.IOException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static readonly string[] LegacyModes =
        ["plan", "default", "acceptEdits", "manual", "dontAsk", "auto", "bypassPermissions", .. SessionAccessLevels.All.Where(l => l is not ("plan" or "auto"))];

    private static async Task<IResult> ChangeAsync(HttpContext ctx, SessionAccessService access,
        ISessionProvider provider, UnifiedSessionInfo info, string? level, string? legacyMode)
    {
        var result = await access.ChangeAsync(provider, info, level,
            ComputeResourceAccess.IsPersonPresent(ctx), ctx.RequestAborted);
        if (result.Error == SessionAccessService.ProviderRefusedLevel)
            return Results.Json(new { error = result.Error, message = result.Message, ok = false, accessLevel = result.AccessLevel },
                statusCode: result.Status);
        if (result.Error is not null)
            return Error(result.Status, result.Error, result.Message ?? result.Error);
        if (legacyMode is null)
            return Results.Json(new { accessLevel = result.AccessLevel, ok = result.Ok });
        var native = provider.GetSession(info.Id).Info?.PermissionMode;
        return Results.Json(new { mode = native ?? legacyMode, ok = result.Ok, accessLevel = result.AccessLevel });
    }

    /// <summary>
    /// The kernel's infrastructure token, a person, or a signed service or system subject. Agents,
    /// automations, session processes and tokenless loopback requests are refused. The route only
    /// re-reads RedLeaf's own policy, so it can never raise anything.
    /// </summary>
    internal static bool MayAnnouncePolicyChange(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated != true
            || ctx.User.Identity.AuthenticationType == ConfidentialResourcePolicy.LocalDefaultAuthenticationType)
            return false;
        if (ComputeResourceAccess.IsTrustedInfrastructure(ctx) || ComputeResourceAccess.IsPersonPresent(ctx)) return true;
        if (ConfidentialResourcePolicy.ExecutionIdentity(ctx) is not null) return false;
        var subject = ctx.User.FindFirst("sub")?.Value;
        return subject == "system" || subject?.StartsWith("service:", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static (ISessionProvider? Provider, UnifiedSessionInfo? Info, IResult? Error) Find(
        HttpContext ctx, CapabilityRegistry registry, IJobTracker jobTracker, string id)
    {
        foreach (var candidate in registry.FindProviders<ISessionProvider>())
        {
            var info = candidate.GetSession(id).Info;
            if (info is null) continue;
            var job = info.Confidential && info.JobId is { } jobId ? jobTracker.GetJob(jobId) : null;
            if (!ComputeResourceAccess.CanReadSession(ctx, info, job))
                return (null, null, ComputeResourceAccess.SessionDenied(info));
            if (candidate.SupportedAccessLevels.Count == 0)
                return (null, null, Error(422, "access_levels_not_supported",
                    $"Provider '{candidate.ProviderId}' does not support access levels"));
            return (candidate, info, null);
        }
        return (null, null, Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404));
    }

    private static IResult Error(int status, string error, string message)
        => Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: status);
}
