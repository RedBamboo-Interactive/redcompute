using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class VoiceSessionEndpoints
{
    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry,
        VoiceSessionCoordinator coordinator)
    {
        endpoints.MapPost("/voice-session/sessions", "Create a provider-neutral WebRTC voice session", async (HttpContext ctx) =>
        {
            NoStore(ctx);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            var offerSdp = String(body, "offerSdp");
            var discussionId = String(body, "discussionId");
            var aiSessionId = String(body, "aiSessionId");
            var instructions = String(body, "instructions");
            if (string.IsNullOrWhiteSpace(offerSdp) || string.IsNullOrWhiteSpace(discussionId)
                || string.IsNullOrWhiteSpace(aiSessionId) || string.IsNullOrWhiteSpace(instructions))
                return Error(400, "missing_fields", "offerSdp, discussionId, aiSessionId and instructions are required");

            var capability = registry.Get("voice-session");
            var requestedProvider = String(body, "provider");
            var backend = capability?.ResolveProvider(requestedProvider);
            if (backend is not IVoiceSessionProvider provider)
                return Error(503, "provider_not_configured", "No voice-session provider is configured");
            var provenance = JobProvenanceHttp.Resolve(ctx, "/voice-session/sessions");
            try
            {
                var admission = await coordinator.CreateAsync(provider, new(
                    offerSdp!, String(body, "model") ?? provider.DefaultModel,
                    String(body, "voice") ?? provider.DefaultVoice, instructions!, discussionId!, aiSessionId!, provenance,
                    History(body)),
                    ctx.RequestAborted);
                return Results.Json(new
                {
                    id = admission.Session.Id,
                    provider = admission.Session.Provider,
                    model = admission.Session.Model,
                    voice = admission.Session.Voice,
                    state = admission.Session.State,
                    answerSdp = admission.AnswerSdp,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
            {
                return Error(503, "provider_not_configured", "OpenAI Live needs a vaulted project API key");
            }
            catch (ArgumentException ex) { return Error(400, "invalid_request", ex.Message); }
            catch (Exception)
            {
                return Error(502, "voice_provider_failed", "The voice provider could not create the session");
            }
        });

        endpoints.MapGet("/voice-session/sessions/{id}", "Get safe voice-session lifecycle state", (HttpContext ctx, string id) =>
        {
            NoStore(ctx);
            var snapshot = coordinator.Get(id, UserInfoHelper.ResolveUserId(ctx), IsTrustedInfrastructure(ctx));
            return snapshot is null ? Results.NotFound(new { error = "not_found" }) : Results.Json(snapshot);
        });

        endpoints.MapPost("/voice-session/sessions/{id}/delegations/{delegationId}/update",
            "Return a verified Nova progress or result update to a client-owned voice delegation",
            async (HttpContext ctx, string id, string delegationId) =>
        {
            NoStore(ctx);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            var eventId = String(body, "eventId");
            var kind = String(body, "kind");
            var content = String(body, "content");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(kind)
                || string.IsNullOrWhiteSpace(content) || content.Length > 4_000)
                return Error(400, "invalid_update", "eventId, kind and content (up to 4000 characters) are required");
            try
            {
                var updated = await coordinator.UpdateDelegationAsync(id, delegationId,
                    new(eventId!, kind!, content!), UserInfoHelper.ResolveUserId(ctx), IsTrustedInfrastructure(ctx),
                    ctx.RequestAborted);
                return updated ? Results.Ok(new { updated = true }) : Results.NotFound(new { error = "not_found" });
            }
            catch (ArgumentException ex) { return Error(400, "invalid_update", ex.Message); }
            catch (Exception) { return Error(502, "voice_provider_failed", "The voice provider could not accept the update"); }
        });

        endpoints.MapPost("/voice-session/sessions/{id}/close", "Gracefully close a voice session", async (HttpContext ctx, string id) =>
        {
            NoStore(ctx);
            var closed = await coordinator.CloseAsync(id, UserInfoHelper.ResolveUserId(ctx), IsTrustedInfrastructure(ctx), ctx.RequestAborted);
            return closed ? Results.Ok(new { closed = true }) : Results.NotFound(new { error = "not_found" });
        });
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<VoiceHistoryMessage> History(JsonElement body)
    {
        if (!body.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<VoiceHistoryMessage>();
        var characters = 0;
        foreach (var item in history.EnumerateArray())
        {
            var role = String(item, "role");
            var content = String(item, "content")?.Trim();
            if (role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(content)) continue;
            if (content.Length > 8_000) content = content[..8_000];
            if (characters + content.Length > 24_000) break;
            result.Add(new(role, content));
            characters += content.Length;
            if (result.Count == 24) break;
        }
        return result;
    }

    private static bool IsTrustedInfrastructure(HttpContext ctx)
        => string.Equals(ctx.User.FindFirst("client_id")?.Value, "redleaf", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ctx.User.FindFirst("compute_provenance")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
