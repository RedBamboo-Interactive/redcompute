using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.Core.Discovery;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

/// <summary>The explicit rollback route. Nothing else in RedCompute rolls a conversation back.</summary>
public static class SessionRollbackEndpoints
{
    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry, IJobTracker jobTracker, SessionRollbackService rollbacks)
    {
        endpoints.MapPost("/ai-session/sessions/{id}/rollback",
            "Rewind the conversation to just before one of the person's messages, identified by its messageUid. That message and "
            + "everything after it leave the provider's context and every client's view; the next message continues from what is kept. "
            + "Conversation only: files are never changed. A person's action: 403 rollback_requires_person refuses Agents, automations, "
            + "services, session processes and a request with no signed person. Only when session.supportsRollback is true, and only on an idle session: "
            + "409 rollback_not_idle lists every reason in `reasons` (turn_active, question_parked, background_tasks_running, input_queued, "
            + "session_stopped). Other refusals: rollback_unsupported, target_not_user_message, target_before_compaction, target_not_mapped, "
            + "target_mid_turn, provider_thread_legacy, provider_history_missing, provider_rejected (409); target_not_found (404); "
            + "provider_unavailable, rollback_incomplete (503, safe to retry). A repeated request returns the same result. "
            + "Durable records are marked, not deleted, and the transcript moves to a new epoch: clients reset on it. Never automatic.",
            async (HttpContext ctx, string id) =>
        {
            string? messageUid;
            try
            {
                using var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                messageUid = body.RootElement.ValueKind == JsonValueKind.Object
                    && body.RootElement.TryGetProperty("messageUid", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                messageUid = null;
            }
            if (string.IsNullOrWhiteSpace(messageUid))
                return Results.Json(new ErrorResponse { Error = "invalid_request", Message = "messageUid is required" }, statusCode: 400);

            ISessionProvider? provider = null;
            UnifiedSessionInfo? info = null;
            foreach (var candidate in registry.FindProviders<ISessionProvider>())
            {
                info = candidate.GetSession(id).Info;
                if (info is null) continue;
                provider = candidate;
                break;
            }
            if (provider is null || info is null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);

            var job = info.Confidential && info.JobId is { } jobId ? jobTracker.GetJob(jobId) : null;
            if (!ComputeResourceAccess.CanReadSession(ctx, info, job))
                return ComputeResourceAccess.SessionDenied(info);
            if (!ComputeResourceAccess.IsPersonAction(ctx))
                return Results.Json(new
                {
                    error = "rollback_requires_person",
                    message = "Rewinding a conversation is a person's action, from the message menu. This request does not come from a person.",
                    reasons = Array.Empty<string>(),
                }, statusCode: StatusCodes.Status403Forbidden);

            var outcome = await rollbacks.RollbackAsync(provider, id, messageUid, ctx.RequestAborted);
            if (!outcome.RolledBack)
                return Results.Json(new
                {
                    error = outcome.Error,
                    message = outcome.Message,
                    reasons = outcome.Reasons ?? [],
                }, statusCode: outcome.StatusCode);

            return Results.Json(new
            {
                rolledBack = true,
                rollbackId = outcome.RollbackId,
                messageUid = outcome.MessageUid,
                epoch = outcome.Epoch,
                throughSequence = outcome.ThroughSequence,
                removedMessageUids = outcome.RemovedMessageUids,
                restoredText = outcome.RestoredText,
                fromCreatedAt = outcome.FromCreatedAt,
                rolledBackAt = outcome.RolledBackAt,
                alreadyRolledBack = outcome.AlreadyRolledBack,
                filesChanged = false,
            });
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithRequestBody(new
            {
                type = "object",
                required = new[] { "messageUid" },
                properties = new
                {
                    messageUid = new { type = "string", description = "The messageUid of the person's message to rewind to just before" },
                },
            })
            .WithResponse(new
            {
                type = "object",
                required = new[] { "rolledBack", "rollbackId", "messageUid", "epoch", "removedMessageUids", "filesChanged" },
                properties = new
                {
                    rolledBack = new { type = "boolean" },
                    rollbackId = new { type = "string" },
                    messageUid = new { type = "string" },
                    epoch = new { type = "string", description = "The transcript's new epoch" },
                    throughSequence = new { type = "integer", description = "The sequence this rollback consumed; later events are above it" },
                    removedMessageUids = new { type = "array", items = new { type = "string" }, description = "Every logical message that left the transcript" },
                    restoredText = new { type = new[] { "string", "null" }, description = "The removed message's text, for the composer" },
                    fromCreatedAt = new { type = "string", format = "date-time", description = "Storage time of the removed message; a product's own records from here on go with it" },
                    rolledBackAt = new { type = "string", format = "date-time", description = "When this rollback began. A product's own records at or after it came later and are kept, however late a repeat arrives" },
                    alreadyRolledBack = new { type = "boolean", description = "This repeated an earlier rollback and changed nothing" },
                    filesChanged = new { type = "boolean", description = "Always false" },
                },
            });
    }
}
