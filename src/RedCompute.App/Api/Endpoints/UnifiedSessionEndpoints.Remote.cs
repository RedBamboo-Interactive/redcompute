using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static partial class UnifiedSessionEndpoints
{
    internal sealed partial class RemoteSessions(ControllerRemoteSessions controller, RedLeafSessionReader reader,
        RedLeafStreamClient streams, IJobTracker jobs, SessionInputQueueService queue)
    {
        internal async Task<IResult> StartAsync(HttpContext ctx, JsonElement body,
            Func<RemoteSessionPlacementPreview, JobProvenance, CancellationToken, Task> authorize)
        {
            var ct = ctx.RequestAborted;
            try
            {
                var request = RemoteSessionStartRequest.Parse(body, RequestKey(ctx));
                var provenance = await AuthorizedProvenanceAsync(ctx, "/ai-session/sessions");
                if (request.ValidateOnly)
                {
                    var preview = await controller.ValidateAsync(ctx.User, request.IdempotencyKey, request.Placement, ct);
                    await authorize(preview, provenance, ct);
                    return Results.Json(new { validateOnly = true, eligible = true, placement = preview });
                }

                var started = await controller.StartAsync(ctx.User, request.IdempotencyKey, request.Placement, ct,
                    preview => authorize(preview, provenance, ct));
                var logicalId = started.Scope.LogicalSessionId;
                var placement = new
                {
                    nodeId = started.Scope.NodeId,
                    sessionRuntime = new
                    {
                        id = started.Scope.RuntimeId,
                        workspace = started.Placement.Workspace,
                        revision = started.Placement.Revision,
                        providerInstanceId = started.Placement.ProviderInstanceId,
                    },
                };
                var job = jobs.CreateJob(new JobSubmission(SessionCapability, started.Placement.ProviderId,
                    JsonSerializer.Serialize(new { placement, sessionId = logicalId }), provenance,
                    IdempotencyKey: logicalId, Name: started.Placement.Workspace, ExternalExecution: true,
                    Placement: new(started.Scope.NodeId, IsLocal: false, PlacementResolution.Explicit)));
                if (job.IsIdempotencyReuse)
                {
                    var (current, _) = await reader.GetSessionInfoAsync(logicalId);
                    if (current is not null)
                    {
                        if (current.JobId != job.Id || current.UserId != started.Scope.OwnerId)
                            throw new ControllerRemoteSessionException(409, "remote_start_conflict");
                        return Results.Json(current, statusCode: StatusCodes.Status201Created);
                    }
                }
                else jobs.MarkRunning(job.Id);
                var info = new UnifiedSessionInfo
                {
                    Id = logicalId,
                    UserId = started.Scope.OwnerId,
                    Provider = started.Placement.ProviderId,
                    ProviderEntity = started.Placement.ProviderInstanceId,
                    ProviderSessionId = started.Thread.NativeThreadId,
                    ProjectName = started.Placement.Workspace,
                    ProjectPath = "",
                    NodeId = started.Scope.NodeId,
                    Placement = JsonSerializer.SerializeToNode(placement)!.AsObject(),
                    JobId = job.Id,
                    Model = started.Thread.Model,
                    Effort = started.Thread.Effort,
                    StartedAt = started.Thread.StartedAt ?? job.QueuedAt,
                    Status = SessionStatus.Idle,
                    OwnerAgentId = provenance.Actor.Kind == "agent" ? provenance.Actor.EntityId : null,
                    AccessLevel = started.Preview.AccessLevel,
                    MaxAccessLevel = started.Preview.MaxAccessLevel,
                    AccessLevelSource = SessionAccessLevelSources.Explicit,
                    Source = provenance.Origin.App.Id,
                };
                await streams.UpsertEntityAsync($"ai-session-{info.Provider}-{info.Id}", "ai-session",
                    info.ProjectName, new
                    {
                        provider = info.Provider, active_provider = info.Provider, provider_entity = info.ProviderEntity,
                        session_id = info.Id, project_name = info.ProjectName, project_path = info.ProjectPath,
                        node_id = info.NodeId, placement, job_id = info.JobId, status = info.Status.ToString(),
                        started_at = info.StartedAt.ToString("O"), external_session_id = info.ProviderSessionId,
                        model = info.Model, effort = info.Effort,
                        owner_id = info.UserId, user_id = info.UserId, owner_agent_id = info.OwnerAgentId,
                        source = info.Source, app = info.Source, access_level = info.AccessLevel,
                        max_access_level = info.MaxAccessLevel, access_level_source = info.AccessLevelSource,
                    }, ct);
                return Results.Json(info, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { return Failure(ex); }
        }

        internal async Task<IResult> ResumeAsync(HttpContext ctx, string id)
        {
            var ct = ctx.RequestAborted;
            try
            {
                await AuthorizedProvenanceAsync(ctx, "/ai-session/sessions/{id}/resume");
                var info = await ReadOwnedAsync(ctx, id) ?? throw new ControllerRemoteSessionException(404, "session_not_found");
                await queue.ExplicitResumeAsync(id, async () =>
                {
                    await controller.ResumeAsync(ctx.User, id, RequestKey(ctx) ?? Guid.NewGuid().ToString("N"), ct);
                    return true;
                }, ct);
                if (info.Status == SessionStatus.Stopped)
                {
                    info.Status = SessionStatus.Idle;
                    info.StopReason = null;
                    await streams.PatchEntityDataAsync($"ai-session-{info.TranscriptProvider ?? info.Provider}-{info.Id}",
                        new { status = info.Status.ToString(), stop_reason = (string?)null }, ct);
                }
                queue.Signal(id);
                return Results.Json(info);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { return Failure(ex); }
        }

        internal async Task<IResult> MessageAsync(HttpContext ctx, string id)
        {
            var ct = ctx.RequestAborted;
            try
            {
                var info = await ReadOwnedAsync(ctx, id) ?? throw new ControllerRemoteSessionException(404, "session_not_found");
                var provenance = await AuthorizedProvenanceAsync(ctx, "/ai-session/sessions/{id}/message");
                var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ct);
                var text = ReadTextInput(body);
                var key = RequestKey(ctx);
                var uid = body.TryGetProperty("messageUid", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : key is null ? Guid.NewGuid().ToString("N")
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
                if (!ControllerRemoteSessions.Identifier(uid) || key is not null && !ControllerRemoteSessions.Identifier(key))
                    throw new ControllerRemoteSessionException(422, "invalid_remote_request_key");
                var submission = new SessionInputQueueSubmission(id, info.UserId!, [new("text", text)], text, null,
                    provenance, SessionInputDeliveryPolicy.AfterCurrent, uid, [], key ?? uid);
                var admitted = await queue.AdmitAsync(submission, ct, waitForDelivery: false);
                return Results.Json(new
                {
                    accepted = true, existing = admitted.Existing, disposition = admitted.Disposition,
                    queueItemId = admitted.Item.Id, messageUid = admitted.Item.MessageUid, queue = admitted.Queue,
                    item = PublicQueueItem(admitted.Item),
                }, statusCode: StatusCodes.Status202Accepted);
            }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { return Failure(ex); }
        }

        internal async Task<UnifiedSessionInfo?> ReadOwnedAsync(HttpContext ctx, string id)
        {
            var owner = NodeAccess.Subject(ctx.User);
            if (owner is null) return null;
            var (info, _) = await reader.GetSessionInfoAsync(id);
            if (info?.UserId != owner) return null;
            var binding = await queue.ReadNodeBindingAsync(id, ctx.RequestAborted);
            return binding?.Active.Native.Remote is not null ? ProjectRemoteBinding(info, binding) : info;
        }

        private static async Task<JobProvenance> AuthorizedProvenanceAsync(HttpContext ctx, string route)
        {
            var owner = NodeAccess.Subject(ctx.User) ?? throw new ControllerRemoteSessionException(401, "signed_owner_required");
            var identity = ConfidentialResourcePolicy.ExecutionIdentity(ctx);
            if (identity?.Beneficiary.Kind != "user" || identity.Beneficiary.Id != owner || identity.Actor.Kind is not ("app" or "agent"))
                throw new ControllerRemoteSessionException(403, "signed_execution_required");
            var provenance = await ProvenanceCapture.ResolveAsync(ctx, route);
            if (provenance.Assurance != JobProvenanceAssurance.Verified || provenance.OnBehalfOf.Id != owner)
                throw new ControllerRemoteSessionException(403, "signed_execution_required");
            return provenance;
        }

        private static string? RequestKey(HttpContext ctx) => ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        private static string ReadTextInput(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) throw new JsonException();
            var allowed = new[] { "content", "input", "messageUid", "delivery", "waitForDelivery", "displayContent" };
            if (body.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))
                || body.TryGetProperty("delivery", out var delivery) && delivery.ValueKind != JsonValueKind.Null
                   && (delivery.ValueKind != JsonValueKind.String || delivery.GetString() != "after-current"))
                throw new ControllerRemoteSessionException(422, "remote_input_not_supported");
            string? text;
            if (body.TryGetProperty("input", out var parts))
            {
                if (body.TryGetProperty("content", out _) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 1
                    || parts[0].ValueKind != JsonValueKind.Object
                    || !parts[0].TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text"
                    || !parts[0].TryGetProperty("text", out var part) || part.ValueKind != JsonValueKind.String)
                    throw new ControllerRemoteSessionException(422, "remote_input_not_supported");
                text = part.GetString();
            }
            else
            {
                text = body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    ? content.GetString() : null;
            }

            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 100_000
                || body.TryGetProperty("displayContent", out var display)
                   && display.ValueKind != JsonValueKind.Null && (display.ValueKind != JsonValueKind.String || display.GetString() != text))
                throw new ControllerRemoteSessionException(422, "invalid_remote_input");
            return text;
        }

        private static bool IsExpected(Exception ex) => ex is ControllerRemoteSessionException
            or HttpRequestException or NodeHubException or JsonException or JobProvenanceValidationException or IdempotencyConflictException;

        private static IResult Failure(Exception ex) => ex switch
        {
            ControllerRemoteSessionException e => Error(e.Status, e.Code, "The remote session operation could not be completed."),
            NodeHubException e => Error(e.Code == "node_not_found" ? 404 : 409, e.Code, "The registered session runtime is unavailable."),
            IdempotencyConflictException => Error(409, "idempotency_conflict", "The request key already belongs to a different start."),
            JobProvenanceValidationException or JsonException => Error(422, "invalid_remote_request", "The remote session request is invalid."),
            _ => Error(503, "remote_session_unavailable", "The remote session will accept an exact retry."),
        };
    }
}
