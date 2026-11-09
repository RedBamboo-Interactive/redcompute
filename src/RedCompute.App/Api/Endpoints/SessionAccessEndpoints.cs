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
            + "follows the Agent's or global default when RedLeaf's policy changes. Unknown levels are 422. "
            + LimitsDescription,
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
            var levelProvided = body.TryGetProperty("level", out var value);
            if (levelProvided && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.String || !SessionAccessLevels.TryNormalize(value.GetString(), out var normalized))
                    return Error(422, SessionAccessResolver.InvalidAccessLevel,
                        $"level must be one of {string.Join(", ", SessionAccessLevels.All)}");
                level = normalized;
            }

            string? cap = null;
            var capProvided = body.TryGetProperty("maxAccessLevel", out var capValue);
            if (capProvided && capValue.ValueKind != JsonValueKind.Null)
            {
                if (capValue.ValueKind != JsonValueKind.String || !SessionAccessLevels.TryNormalize(capValue.GetString(), out var normalizedCap))
                    return Error(422, SessionAccessResolver.InvalidAccessLevel,
                        $"maxAccessLevel must be one of {string.Join(", ", SessionAccessLevels.All)}, or null");
                cap = normalizedCap;
            }

            bool? unattended = null;
            if (body.TryGetProperty("unattended", out var unattendedValue))
            {
                if (unattendedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Error(422, "validation_failed", "unattended must be true or false");
                unattended = unattendedValue.GetBoolean();
            }

            if (!capProvided && unattended is null)
                return await ChangeAsync(ctx, access, provider!, info!, level, legacyMode: null);

            var personPresent = ComputeResourceAccess.IsPersonPresent(ctx);
            var limits = await access.ChangeLimitsAsync(provider!, info!, capProvided, cap, unattended, personPresent, ctx.RequestAborted);
            if (!limits.Ok)
                return Results.Json(new
                {
                    error = limits.Error,
                    message = limits.Message,
                    ok = false,
                    accessLevel = limits.State?.AccessLevel,
                    maxAccessLevel = limits.State?.MaxAccessLevel,
                    unattended = limits.State?.Unattended,
                }, statusCode: limits.Status);
            // With limits in the body, level applies only when it is named (null still leaves plan).
            if (!levelProvided)
                return LimitsResponse(limits.State!, limits.Stopped);
            var changed = await access.ChangeAsync(provider!, info!, level, personPresent, ctx.RequestAborted);
            var after = provider!.GetAccessState(info!.Id) ?? limits.State!;
            if (changed.Error is not null)
                return Results.Json(new
                {
                    error = changed.Error,
                    message = changed.Message,
                    ok = false,
                    accessLevel = changed.AccessLevel ?? after.AccessLevel,
                    maxAccessLevel = after.MaxAccessLevel,
                    unattended = after.Unattended,
                }, statusCode: changed.Status);
            return LimitsResponse(after, limits.Stopped);
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("level", "string", description: "The new level. Omit or null to leave plan for the level it was entered from. "
                + "When maxAccessLevel or unattended is sent, an omitted level means no level change (null still leaves plan).",
                enumValues: [.. SessionAccessLevels.All], location: ParamLocation.Body)
            .WithParam("maxAccessLevel", "string", description: "New cap for the session. Setting or lowering it is open to any caller with write access, "
                + "Agents included, and lowers a level above it; raising it or clearing it (null) needs a person (403 cap_change_requires_person).",
                enumValues: [.. SessionAccessLevels.All], location: ParamLocation.Body)
            .WithParam("unattended", "boolean", description: "true marks the session unattended (open to any caller with write access): later "
                + "permission asks are denied at once and never park. false needs a person (403 attended_requires_person).",
                location: ParamLocation.Body)
            .WithResponse(new
            {
                type = "object",
                required = new[] { "accessLevel", "ok" },
                properties = new
                {
                    accessLevel = new { type = new[] { "string", "null" }, @enum = SessionAccessLevels.All.Cast<object?>().Append(null).ToArray() },
                    ok = new { type = "boolean", description = "True when the level is in force. A harness refusal is 409 provider_refused_level instead" },
                    maxAccessLevel = new { type = new[] { "string", "null" }, description = "The cap now in force; returned when maxAccessLevel or unattended was sent" },
                    unattended = new { type = "boolean", description = "Returned when maxAccessLevel or unattended was sent" },
                    stopped = new { type = "boolean", description = "True when a live harness would not switch down to the new cap, so the session was stopped; it relaunches within the cap" },
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

    internal const string LimitsDescription =
        "Also tightens an existing session's limits (one-way without a person): maxAccessLevel sets or lowers its cap, and a cap below the "
        + "current level lowers the level (and the level it returns to after plan) to the highest supported level at or below it, through the "
        + "harness; a live harness that will not switch down is stopped (stopped true) and relaunches within the cap. unattended true marks it "
        + "unattended, so later permission asks are denied at once and never park, and approvals already parked are denied now with the same explanation. Any caller with write access "
        + "may tighten, Agents and automations included. Raising or clearing a cap needs a person (403 cap_change_requires_person), as does "
        + "unattended false (403 attended_requires_person). A cap below every level the provider supports is 422 access_level_cap_unsupported. "
        + "The level's source is kept: a policy-sourced session still follows its default, within the cap. Limits apply before any level in the "
        + "same body, which is then judged against the new cap. The response then also carries maxAccessLevel and unattended.";

    private static IResult LimitsResponse(SessionAccessState state, bool stopped)
        => Results.Json(new
        {
            accessLevel = state.AccessLevel,
            ok = true,
            maxAccessLevel = state.MaxAccessLevel,
            unattended = state.Unattended,
            stopped,
        });

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
