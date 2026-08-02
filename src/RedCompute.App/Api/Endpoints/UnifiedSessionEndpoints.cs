using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class UnifiedSessionEndpoints
{
    private static readonly HashSet<string> SupportedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    private static DockerContainerService? _docker;
    private static SessionCallbackRegistry? _callbacks;
    private static QualityModeService? _quality;
    private static RedLeafSessionReader? _redLeafReader;
    private static ProviderConfigService? _providerConfig;
    private static InputAttachmentStore? _attachmentStore;

    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry,
        IJobTracker jobTracker, Action<string, Guid?> log, RedComputeConfig config,
        DockerContainerService? docker = null, SessionCallbackRegistry? callbacks = null,
        QualityModeService? quality = null, RedLeafSessionReader? redLeafReader = null,
        ProviderConfigService? providerConfig = null, InputAttachmentStore? attachmentStore = null)
    {
        _docker = docker;
        _callbacks = callbacks;
        _quality = quality;
        _redLeafReader = redLeafReader;
        _providerConfig = providerConfig;
        _attachmentStore = attachmentStore ?? new InputAttachmentStore(config);

        var providerIds = registry.FindProviders<ISessionProvider>().Select(p => p.ProviderId).ToList();
        var providerEnum = providerIds.Count > 0 ? providerIds : null;

        endpoints.MapPost("/ai-session/input-attachments",
            "Stage one provider input attachment before a session or message exists", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to upload attachments");
            if (!ctx.Request.HasFormContentType)
                return Error(415, "unsupported_media_type", "Upload must use multipart/form-data");
            try
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                if (form.Files.Count != 1)
                    return Error(400, "invalid_attachment_count", "Upload exactly one file per request");
                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                var staged = await _attachmentStore.UploadAsync(stream, file.FileName, file.ContentType, userId, ctx.RequestAborted);
                return Results.Json(PublicAttachment(staged));
            }
            catch (AttachmentStoreException ex)
            {
                return AttachmentError(ex);
            }
            catch (InvalidDataException ex)
            {
                return Error(400, "invalid_upload", ex.Message);
            }
        });

        endpoints.MapGet("/ai-session/input-attachments/{id}",
            "Download or preview an authorized staged or claimed input attachment", async (HttpContext ctx, string id) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null) return Error(401, "unauthorized", "Authentication required");
            try
            {
                var attachment = await _attachmentStore.GetAuthorizedAsync(id, userId, ctx.RequestAborted);
                if (attachment is null) return Error(404, "attachment_not_found", $"Attachment '{id}' was not found");
                var download = ctx.Request.Query["download"] == "true";
                return Results.File(attachment.StoredPath, attachment.MediaType,
                    fileDownloadName: download ? attachment.Name : null, enableRangeProcessing: true);
            }
            catch (AttachmentStoreException ex) { return AttachmentError(ex); }
        });

        endpoints.MapDelete("/ai-session/input-attachments/{id}",
            "Delete an unclaimed draft attachment", async (HttpContext ctx, string id) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null) return Error(401, "unauthorized", "Authentication required");
            try
            {
                await _attachmentStore.DeleteDraftAsync(id, userId, ctx.RequestAborted);
                return Results.NoContent();
            }
            catch (AttachmentStoreException ex) { return AttachmentError(ex); }
        });

        endpoints.MapGet("/ai-session/providers",
            "List all registered AI session providers with their capabilities and models", () =>
        {
            var providers = registry.FindProviders<ISessionProvider>().Select(p => new
            {
                providerId = p.ProviderId,
                displayName = p.ProviderDisplayName,
                capabilities = FormatCapabilities(p.Capabilities),
                models = p.GetAvailableModels(),
            });
            return Results.Json(providers);
        });

        endpoints.MapGet("/ai-session/sessions",
            "List sessions across all providers, newest first", async (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 20;
            var all = ctx.Request.Query.ContainsKey("all");
            var excludeSource = ctx.Request.Query["excludeSource"].FirstOrDefault();

            // Read-path cutover: RedLeaf is the source of truth for session
            // history. By decision it is a hard dependency — no local fallback.
            List<UnifiedSessionInfo> allSessions;
            try
            {
                allSessions = await _redLeafReader!.GetSessionsAsync(providerFilter, limit, all);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (excludeSource != null)
                allSessions.RemoveAll(s => s.Source == excludeSource);

            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user")
                allSessions.RemoveAll(s => s.UserId != null && s.UserId != userId);

            return Results.Json(allSessions);
        })
            .WithParam("limit", "integer", description: "Max sessions to return", defaultValue: 20, location: ParamLocation.Query)
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query)
            .WithParam("all", "boolean", description: "Include dismissed sessions (presence flag)", location: ParamLocation.Query)
            .WithParam("excludeSource", "string", description: "Exclude sessions whose source matches this value", location: ParamLocation.Query);

        endpoints.MapPost("/ai-session/sessions",
            "Start a new interactive session in a project directory", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to create sessions");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (body.ValueKind != JsonValueKind.Object)
                return Error(400, "invalid_body", "Request body must be a JSON object");

            var q = ResolveQuality(ctx, body);
            var (provider, error) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (error != null) return error;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.PersistentSessions))
                return NotSupported(provider.ProviderId, "persistent sessions");

            var projectPath = body.TryGetProperty("projectPath", out var pp) ? pp.GetString() : null;
            if (string.IsNullOrWhiteSpace(projectPath))
                return Error(422, "validation_failed", "projectPath is required");

            var model = q.Model;
            var effort = q.Effort;
            int? thinkingBudget = body.TryGetProperty("thinkingBudget", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null;
            thinkingBudget ??= q.ThinkingBudget;
            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
            JobProvenance provenance;
            try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions"); }
            catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
            var session = await provider.StartSessionAsync(projectPath, callerInfo, model, uId, uName, uAvatar,
                effort, q.EndpointUrl, q.ApiKey, thinkingBudget, q.QualityTier, q.ProviderName, provenance);
            if (session == null)
                return Error(500, "start_failed", provider.LastStartError ?? "Failed to start session");
            if (session.JobId is not { } jobId || jobTracker.GetJob(jobId) == null)
            {
                try { await provider.ForceKillAsync(session.Id); }
                catch (Exception ex) { log($"Failed to clean up untracked session {session.Id}: {ex.Message}", null); }
                log($"Provider {provider.ProviderId} created session {session.Id} without a valid compute job", null);
                return Error(500, "tracking_failed", "Session creation was rolled back because its compute job was not created");
            }

            return Results.Json(session);
        })
            .WithParam("projectPath", "string", required: true, description: "Path to the project directory", location: ParamLocation.Body)
            .WithParam("provider", "string", description: "Provider to use. Defaults to the active provider.", enumValues: providerEnum, location: ParamLocation.Body)
            .WithParam("model", "string", description: "Model to use. Overrides qualityTier when both are given.", location: ParamLocation.Body)
            .WithParam("effort", "string", description: "Effort level (e.g. low, normal, high)", location: ParamLocation.Body)
            .WithParam("qualityTier", "string", description: "Quality-tier entity slug resolved suite-wide to a provider+model+effort. Ignored when model is set.", location: ParamLocation.Body)
            .WithParam("thinkingBudget", "integer", description: "Thinking/reasoning token budget. Explicit value wins over qualityTier.", location: ParamLocation.Body);

        endpoints.MapGet("/ai-session/sessions/{id}",
            "Get session details and message history", async (HttpContext ctx, string id) =>
        {
            UnifiedSessionInfo? info;
            List<UnifiedMessageRecord> history;
            try
            {
                (info, history) = await _redLeafReader!.GetSessionAsync(id);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);

            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            return Results.Json(new { session = info, messages = history });
        });

        endpoints.MapGet("/ai-session/sessions/{id}/messages/{recordId:long}/output",
            "Stream a payload-backed transcript output. Supports HTTP byte ranges; bytes are loaded only when requested.", async (HttpContext ctx, string id, long recordId) =>
        {
            UnifiedSessionInfo? info;
            string? entityId;
            try
            {
                (info, entityId) = await _redLeafReader!.GetSessionInfoAsync(id);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for payload reads: {ex.Message}");
            }

            if (info == null || entityId == null)
                return Error(404, "not_found", $"Session '{id}' not found");

            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            HttpResponseMessage upstream;
            try
            {
                upstream = await _redLeafReader.OpenPayloadAsync(
                    entityId, recordId, ctx.Request.Headers.Range.ToString(), ctx.RequestAborted);
            }
            catch (KeyNotFoundException ex)
            {
                return Error(404, "not_found", ex.Message);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"Could not read transcript payload: {ex.Message}");
            }

            using (upstream)
            {
                ctx.Response.StatusCode = (int)upstream.StatusCode;
                if (upstream.Content.Headers.ContentType is { } contentType)
                    ctx.Response.ContentType = contentType.ToString();
                if (upstream.Content.Headers.ContentLength is { } contentLength)
                    ctx.Response.ContentLength = contentLength;
                if (upstream.Content.Headers.ContentRange is { } contentRange)
                    ctx.Response.Headers.ContentRange = contentRange.ToString();
                if (upstream.Headers.AcceptRanges.Count > 0)
                    ctx.Response.Headers.AcceptRanges = string.Join(", ", upstream.Headers.AcceptRanges);
                if (upstream.Headers.ETag is { } etag)
                    ctx.Response.Headers.ETag = etag.ToString();
                if (upstream.Content.Headers.LastModified is { } lastModified)
                    ctx.Response.Headers.LastModified = lastModified.ToString("R");
                if (ctx.Request.Query["download"] == "true")
                    ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"tool-output-{recordId}.txt\"";

                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return Results.Empty;
            }
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("recordId", "integer", required: true, location: ParamLocation.Path, description: "Payload-backed transcript record id")
            .WithParam("download", "boolean", description: "Force a full-output download", defaultValue: false);

        endpoints.MapGet("/ai-session/sessions/by-job/{jobId:guid}",
            "Get the session associated with a job ID", async (HttpContext ctx, Guid jobId) =>
        {
            UnifiedSessionInfo? info;
            List<UnifiedMessageRecord> history;
            try
            {
                (info, history) = await _redLeafReader!.GetSessionByJobIdAsync(jobId);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (info != null)
                return Results.Json(new { session = info, messages = history });
            return Results.Json(new ErrorResponse { Error = "not_found", Message = $"No session for job '{jobId}'" }, statusCode: 404);
        });

        endpoints.MapPost("/ai-session/sessions/{id}/message",
            "Send a user message to a persistent session. Returns {\"sent\": true}", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SendMessage))
                return NotSupported(provider.ProviderId, "interactive messaging");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (body.ValueKind != JsonValueKind.Object)
                return Error(400, "invalid_body", "Request body must be a JSON object");

            // Provider-neutral message identity: honor a caller-supplied uid
            // (cross-posted messages share it across stores), mint otherwise.
            var messageUid = body.TryGetProperty("messageUid", out var mu)
                && mu.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mu.GetString())
                    ? mu.GetString()! : Guid.NewGuid().ToString("N");

            var inputSpec = new List<(string Type, string Value)>();
            var attachmentIds = new List<string>();
            var hasTypedInput = body.TryGetProperty("input", out var inputEl) && inputEl.ValueKind != JsonValueKind.Null;
            try
            {
                if (hasTypedInput)
                {
                    if (inputEl.ValueKind != JsonValueKind.Array)
                        return Error(400, "invalid_input", "input must be an array");
                    if (body.TryGetProperty("content", out _) || body.TryGetProperty("images", out _))
                        return Error(400, "mixed_input_contracts", "Use either typed input or legacy content/images, not both");
                    foreach (var part in inputEl.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object
                            || !part.TryGetProperty("type", out var typeEl)
                            || typeEl.ValueKind != JsonValueKind.String)
                            return Error(400, "invalid_input_part", "Every input part requires a string type");
                        var type = typeEl.GetString();
                        if (type == "text")
                        {
                            if (!part.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                                return Error(400, "invalid_text_part", "Text input parts require text");
                            inputSpec.Add(("text", textEl.GetString() ?? ""));
                        }
                        else if (type == "attachment")
                        {
                            if (!part.TryGetProperty("attachmentId", out var idEl) || idEl.ValueKind != JsonValueKind.String
                                || string.IsNullOrWhiteSpace(idEl.GetString()))
                                return Error(400, "invalid_attachment_part", "Attachment input parts require attachmentId");
                            var attachmentId = idEl.GetString()!;
                            inputSpec.Add(("attachment", attachmentId));
                            attachmentIds.Add(attachmentId);
                        }
                        else return Error(400, "unsupported_input_part", $"Unsupported input part type '{type}'");
                    }
                }
                else
                {
                    var content = body.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(content)) inputSpec.Add(("text", content));

                    if (TryGetNonNullImages(body, out var imagesEl))
                    {
                        if (imagesEl.ValueKind != JsonValueKind.Array)
                            return Error(400, "invalid_images", "images must be an array");
                        var imageIndex = 0;
                        foreach (var image in imagesEl.EnumerateArray())
                        {
                            if (image.ValueKind != JsonValueKind.Object)
                                return Error(400, "invalid_image", "Each image must be an object");
                            var mediaType = image.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String
                                ? mt.GetString() ?? "image/png" : "image/png";
                            if (!SupportedImageMediaTypes.Contains(mediaType))
                                return Error(400, "unsupported_image_type", $"Unsupported image media type '{mediaType}'");
                            var base64 = image.TryGetProperty("base64", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                            if (string.IsNullOrWhiteSpace(base64))
                                return Error(400, "invalid_image", "Each image requires non-empty base64 data");
                            byte[] bytes;
                            try { bytes = Convert.FromBase64String(base64); }
                            catch (FormatException) { return Error(400, "invalid_image", "Image data must be valid base64"); }
                            var staged = await _attachmentStore!.UploadBytesAsync(bytes,
                                $"legacy-image-{++imageIndex}{ImageExtension(mediaType)}", mediaType, userId ?? "local-user", ctx.RequestAborted);
                            inputSpec.Add(("attachment", staged.Id));
                            attachmentIds.Add(staged.Id);
                        }
                    }
                }

                if (!inputSpec.Any(p => p.Type == "text" && !string.IsNullOrWhiteSpace(p.Value)) && attachmentIds.Count == 0)
                    return Error(400, "missing_content", "At least one non-empty text or attachment input part is required");

                var claimed = attachmentIds.Count > 0
                    ? await _attachmentStore!.ClaimAsync(attachmentIds, userId ?? "local-user", id, messageUid, ctx.RequestAborted)
                    : Array.Empty<StagedInputAttachment>();
                var claimedById = claimed.ToDictionary(a => a.Id, StringComparer.Ordinal);

                if (claimed.Any(a => a.Kind == "file") && !provider.Capabilities.HasFlag(SessionCapabilities.FileAttachments))
                {
                    await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                    return Error(422, "file_attachments_not_supported", $"Provider '{provider.ProviderId}' does not support file attachments");
                }
                if (claimed.Any(a => a.Kind == "image") && !provider.Capabilities.HasFlag(SessionCapabilities.ImageAttachments))
                {
                    await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                    return Error(422, "image_attachments_not_supported", $"Provider '{provider.ProviderId}' does not support image attachments");
                }
                if (claimed.Any(a => a.Kind == "image") && provider is IImageAttachmentSupportProvider sessionImageSupport)
                {
                    var support = sessionImageSupport.GetImageAttachmentSupport(id);
                    if (!support.Supported)
                    {
                        await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                        return Error(422, "image_attachments_not_supported", support.Reason ?? $"Session '{id}' does not support image attachments");
                    }
                }

                var input = inputSpec.Select(part => part.Type == "text"
                    ? SessionInputPart.TextPart(part.Value)
                    : SessionInputPart.AttachmentPart(claimedById[part.Value].ToProviderAttachment())).ToArray();
                var publicAttachments = claimed.Select(PublicAttachment).ToArray();
                var hasMetadata = body.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object;
                string? attachmentsJson = null;
                if (hasMetadata || publicAttachments.Length > 0)
                {
                    var envelope = new Dictionary<string, object?> { ["attachments"] = publicAttachments };
                    if (hasMetadata) envelope["metadata"] = meta;
                    attachmentsJson = JsonSerializer.Serialize(envelope);
                }

                if (info.JobId is not { } invocationJobId || jobTracker.GetJob(invocationJobId) == null)
                {
                    if (claimed.Count > 0) await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                    return Error(500, "tracking_failed", "The session has no tracked Compute job");
                }
                JobProvenance invocationProvenance;
                try
                {
                    invocationProvenance = await ProvenanceCapture.ResolveAsync(ctx,
                        "/ai-session/sessions/{id}/message");
                }
                catch (JobProvenanceValidationException ex)
                {
                    if (claimed.Count > 0) await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                    return Error(422, "invalid_provenance", ex.Message);
                }
                jobTracker.StartInvocation(invocationJobId, invocationProvenance, JobEventKind.Resumed);

                bool sent;
                try
                {
                    sent = await provider.SendInputAsync(id, input, attachmentsJson, messageUid);
                }
                catch (Exception ex)
                {
                    jobTracker.MarkFailed(invocationJobId, ex.Message);
                    if (claimed.Count > 0) await _attachmentStore!.ReleaseClaimAsync(id, messageUid, CancellationToken.None);
                    if (ctx.RequestAborted.IsCancellationRequested) throw;
                    return Error(502, "delivery_failed", $"Message could not be delivered to session '{id}': {ex.Message}");
                }
                if (!sent)
                {
                    jobTracker.MarkFailed(invocationJobId, $"Provider '{provider.ProviderId}' rejected the input");
                    if (claimed.Count > 0) await _attachmentStore!.ReleaseClaimAsync(id, messageUid, ctx.RequestAborted);
                    return Error(502, "delivery_failed", $"Message could not be delivered to session '{id}'");
                }
                return Results.Json(new { sent, messageUid, attachments = publicAttachments });
            }
            catch (AttachmentStoreException ex)
            {
                await _attachmentStore!.ReleaseClaimAsync(id, messageUid, CancellationToken.None);
                return AttachmentError(ex);
            }
        })
            .WithRequestBody(new
            {
                type = "object",
                properties = new
                {
                    input = new
                    {
                        type = "array",
                        description = "Provider-neutral ordered input parts. Use instead of legacy content/images.",
                        items = new
                        {
                            oneOf = new object[]
                            {
                                new { type = "object", required = new[] { "type", "text" }, properties = new { type = new { type = "string", @enum = new[] { "text" } }, text = new { type = "string" } } },
                                new { type = "object", required = new[] { "type", "attachmentId" }, properties = new { type = new { type = "string", @enum = new[] { "attachment" } }, attachmentId = new { type = "string" } } },
                            },
                        },
                    },
                    content = new { type = "string", description = "Message text to send. Optional when at least one image is supplied." },
                    images = new
                    {
                        type = "array",
                        description = "Optional image attachments",
                        items = new
                        {
                            type = "object",
                            required = new[] { "base64" },
                            properties = new
                            {
                                mediaType = new { type = "string", description = "MIME type of the image", @default = "image/png" },
                                base64 = new { type = "string", description = "Base64-encoded image data" },
                            },
                        },
                    },
                    metadata = new { type = "object", description = "Arbitrary key-value metadata (display-only, not sent to model)" },
                    messageUid = new { type = "string", description = "Stable message identity to persist with the message. Supply it when the same logical message is also stored elsewhere (cross-stream dedup); minted server-side when omitted. Returned in the response." },
                },
            });

        endpoints.MapPost("/ai-session/sessions/{id}/inject",
            "Inject a message into the session history without triggering a model turn", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var role = body.TryGetProperty("role", out var r) ? r.GetString() : null;
            var content = body.TryGetProperty("content", out var c) ? c.GetString() : null;

            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(content))
                return Error(400, "missing_fields", "role and content are required");

            string? attachmentsJson = null;
            var audioUrl = body.TryGetProperty("audioUrl", out var au) ? au.GetString() : null;
            var hasImages = body.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array;
            var hasMetadata = body.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object;
            if (audioUrl != null || hasImages || hasMetadata)
            {
                var attachments = new Dictionary<string, object?>();
                if (audioUrl != null) attachments["audioUrl"] = audioUrl;
                if (hasImages) attachments["images"] = imgs;
                if (hasMetadata) attachments["metadata"] = meta;
                attachmentsJson = JsonSerializer.Serialize(attachments);
            }

            var messageUid = body.TryGetProperty("messageUid", out var mu)
                && mu.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mu.GetString())
                    ? mu.GetString()! : Guid.NewGuid().ToString("N");

            var injected = await provider!.InjectMessageAsync(id, role, content, attachmentsJson, messageUid);
            return Results.Json(new { injected, messageUid });
        })
            .WithParam("role", "string", required: true, description: "Role of the injected message (e.g. user, assistant)", location: ParamLocation.Body)
            .WithParam("content", "string", required: true, description: "Message content to inject", location: ParamLocation.Body)
            .WithParam("audioUrl", "string", required: false, description: "URL to an audio file (display-only, not sent to model)", location: ParamLocation.Body)
            .WithParam("images", "array", required: false, description: "Attached images as [{mediaType, base64}] or [{url}] (display-only)", location: ParamLocation.Body)
            .WithParam("metadata", "object", required: false, description: "Arbitrary key-value metadata (display-only)", location: ParamLocation.Body)
            .WithParam("messageUid", "string", required: false, description: "Stable message identity to persist with the injected message; minted server-side when omitted. Returned in the response.", location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/sessions/{id}/answer",
            "Answer a pending question from the session", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SendMessage))
                return NotSupported(provider.ProviderId, "interactive messaging");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var answer = body.TryGetProperty("answer", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(answer))
                return Error(422, "validation_failed", "answer is required");

            var sent = provider.SendAnswer(id, answer);
            return Results.Json(new { sent });
        })
            .WithParam("answer", "string", required: true, description: "Answer text", location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/sessions/{id}/question",
            "Answer a structured question the session is parked on (Claude Code's AskUserQuestion). " +
            "The requestId comes from the 'question' stream event; unlike /answer this is not a " +
            "conversation turn — it unblocks the tool call the session is waiting on.",
            async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SendMessage))
                return NotSupported(provider.ProviderId, "interactive messaging");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var requestId = body.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(requestId))
                return Error(422, "validation_failed", "requestId is required");

            var decline = body.TryGetProperty("decline", out var d) && d.ValueKind == JsonValueKind.True;

            // answers accepts either shape: an object keyed by question text, or an array in
            // question order. The array form is safer for clients — the server holds the
            // authoritative question text and zips positionally.
            Dictionary<string, string>? answers = null;
            List<string>? positional = null;
            if (body.TryGetProperty("answers", out var a))
            {
                if (a.ValueKind == JsonValueKind.Object)
                {
                    answers = new Dictionary<string, string>();
                    foreach (var prop in a.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            answers[prop.Name] = prop.Value.GetString()!;
                    }
                }
                else if (a.ValueKind == JsonValueKind.Array)
                {
                    positional = a.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }
            }

            var response = body.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String
                ? resp.GetString() : null;

            if (!decline && answers is not { Count: > 0 } && positional is not { Count: > 0 }
                && string.IsNullOrWhiteSpace(response))
                return Error(422, "validation_failed", "answers, response or decline is required");

            var result = provider.SubmitQuestionAnswer(id, new SessionQuestionAnswer
            {
                RequestId = requestId,
                Answers = answers,
                PositionalAnswers = positional,
                Response = response,
                Decline = decline,
                DeclineReason = body.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
                    ? rr.GetString() : null,
            });

            return result switch
            {
                QuestionAnswerResult.Answered or QuestionAnswerResult.Declined
                    => Results.Json(new { answered = true, outcome = result.ToString().ToLowerInvariant() }),
                QuestionAnswerResult.SessionNotFound
                    => Error(404, "not_found", $"Session '{id}' is not running"),
                // Already answered, expired, or cancelled by the CLI — the question is gone
                // either way, and the client should have seen a question_resolved event.
                QuestionAnswerResult.RequestNotFound
                    => Error(409, "request_not_pending", $"No question is pending for request '{requestId}'"),
                _ => Error(500, "answer_failed", "Failed to deliver the answer to the session"),
            };
        })
            .WithParam("requestId", "string", required: true, description: "Correlation id from the 'question' stream event", location: ParamLocation.Body)
            .WithParam("answers", "object", description: "Chosen option labels, either keyed by question text or as an array in question order. Multi-select answers are a single comma-separated string.", location: ParamLocation.Body)
            .WithParam("response", "string", description: "Freeform text the user typed instead of picking an option", location: ParamLocation.Body)
            .WithParam("decline", "boolean", description: "Dismiss the question instead of answering it", location: ParamLocation.Body)
            .WithParam("reason", "string", description: "Message shown to the model when decline is true", location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/sessions/{id}/interrupt",
            "Interrupt the session's current operation", (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Interrupt))
                return NotSupported(provider.ProviderId, "session interrupts");

            var result = provider.InterruptSession(id);
            return Results.Json(new { interrupted = result == InterruptResult.Interrupted, reason = result.ToString() });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/resume",
            "Resume a stopped session", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Resume))
                return NotSupported(provider.ProviderId, "session resume");

            JobProvenance provenance;
            try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions/{id}/resume"); }
            catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
            var session = await provider.ResumeSessionAsync(id, provenance);
            if (session == null)
                return Error(500, "resume_failed", provider.LastStartError ?? "Failed to resume session");

            return Results.Json(session);
        });

        endpoints.MapPost("/ai-session/sessions/{id}/stop",
            "Stop a running session gracefully", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            await provider!.StopSessionAsync(id);
            return Results.Json(new { stopped = true });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/dismiss",
            "Mark a session as dismissed (hidden from default listings)", (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            provider!.DismissSession(id);
            return Results.Json(new { dismissed = true });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/callback",
            "Register a completion webhook: RedCompute POSTs the session result to the given URL when the session finishes. Preferred over polling for agents awaiting session completion.", async (HttpContext ctx, string id) =>
        {
            if (_callbacks == null)
                return Error(503, "not_configured", "Callback registry not available");

            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var url = body.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return Error(422, "validation_failed", "url is required");

            var force = body.TryGetProperty("force", out var f) && f.GetBoolean();
            var deferred = _callbacks.RegisterIfStillActive(id, url, info.Status, userId ?? info.UserId, info.StopReason, force);
            return Results.Json(new { registered = deferred, currentStatus = info.Status.ToString() });
        })
            .WithParam("url", "string", required: true, description: "URL to POST the completion payload to", location: ParamLocation.Body)
            .WithParam("force", "boolean", description: "Register even if session is already idle (for pre-registering before sending a message)", location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/sessions/{id}/config",
            "Update session model and reasoning effort", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.ConfigUpdate))
                return NotSupported(provider.ProviderId, "config updates");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var effort = body.TryGetProperty("effort", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var qualityTier = body.TryGetProperty("qualityTier", out var qt) && qt.ValueKind == JsonValueKind.String ? qt.GetString() : null;
            int? thinkingBudget = body.TryGetProperty("thinkingBudget", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null;

            // Explicit model wins; otherwise a qualityTier resolves to a model (+effort+thinkingBudget) for this provider.
            if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(qualityTier) && _quality != null)
            {
                var resolved = _quality.Resolve(qualityTier, provider.ProviderId);
                model = resolved.Model;
                if (string.IsNullOrWhiteSpace(effort)) effort = resolved.Effort;
                thinkingBudget ??= resolved.ThinkingBudget;
                qualityTier = resolved.QualityTier ?? qualityTier;
            }
            else if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(qualityTier))
                qualityTier = info.QualityTier;
            else if (!string.IsNullOrWhiteSpace(model))
                qualityTier = null;

            var updated = await provider.UpdateSessionConfigAsync(id, model, effort, thinkingBudget, qualityTier);
            return Results.Json(updated);
        })
            .WithParam("model", "string", description: "Model to switch to. Overrides qualityTier when both are given.", location: ParamLocation.Body)
            .WithParam("effort", "string", description: "Reasoning effort level", location: ParamLocation.Body)
            .WithParam("qualityTier", "string", description: "Quality-tier entity slug resolved to a model+effort for this session's provider. Ignored when model is set.", location: ParamLocation.Body)
            .WithParam("thinkingBudget", "integer", description: "Thinking/reasoning token budget", location: ParamLocation.Body);

        endpoints.MapPost("/ai-session/sessions/{id}/permission-mode",
            "Set the session's permission mode", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.PermissionMode))
                return NotSupported(provider.ProviderId, "permission modes");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var mode = body.TryGetProperty("mode", out var mm) ? mm.GetString() : null;
            if (string.IsNullOrWhiteSpace(mode))
                return Error(422, "validation_failed", "mode is required");

            var ok = provider.SetPermissionMode(id, mode);
            return Results.Json(new { mode, ok });
        })
            .WithParam("mode", "string", required: true, description: "Permission mode",
                enumValues: ["default", "acceptEdits", "plan", "bypassPermissions", "dontAsk", "auto"],
                location: ParamLocation.Body);

        endpoints.MapDelete("/ai-session/sessions/{id}",
            "Force-kill a session", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            await provider!.ForceKillAsync(id);
            return Results.Json(new { killed = true });
        });

        endpoints.MapGet("/ai-session/projects",
            "List known project directories across providers", (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var allProjects = new List<object>();

            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                if (!provider.Capabilities.HasFlag(SessionCapabilities.ProjectDiscovery))
                    continue;
                foreach (var p in provider.ListProjects())
                    allProjects.Add(new { p.Name, p.Path, p.HasClaudeMd, p.HasIcon, provider = provider.ProviderId });
            }

            return Results.Json(allProjects);
        })
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query);

        endpoints.MapGet("/ai-session/projects/{name}/icon",
            "Serve the project's icon image (favicon/logo) if one exists", (HttpContext ctx, string name) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                if (!provider.Capabilities.HasFlag(SessionCapabilities.ProjectDiscovery))
                    continue;
                var project = provider.ListProjects().FirstOrDefault(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (project == null) continue;
                var iconPath = FindProjectIcon(project.Path);
                if (iconPath != null)
                    return Results.File(iconPath);
            }
            return Results.NotFound();
        })
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query);

        endpoints.MapPost("/ai-session/sessions/{id}/open-in-codered",
            "Ask the local CodeRed instance to navigate to this session", async (string id) =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var res = await http.PostAsync($"{config.CodeRedUrl.TrimEnd('/')}/api/navigate?session={id}", null);
                return res.IsSuccessStatusCode
                    ? Results.Json(new { sent = true })
                    : Results.Json(new { sent = false, error = "CodeRed returned " + (int)res.StatusCode }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { sent = false, error = ex.Message }, statusCode: 502);
            }
        });

        endpoints.MapGet("/ai-session/models",
            "List available models across providers", (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var allModels = new List<object>();

            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                foreach (var m in provider.GetAvailableModels())
                    allModels.Add(new { m.Id, m.Name, m.Fast, provider = provider.ProviderId });
            }

            return Results.Json(new { models = allModels });
        })
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query);

        endpoints.MapGet("/ai-session/quality-modes",
            "List entity-defined quality tiers and the provider+model+effort each resolves to. Lets any suite app build a tier picker without duplicating entity data.", () =>
        {
            if (_quality == null)
                return Error(503, "not_configured", "Quality mode service is not available");

            var defaultTier = _quality.DefaultTierSlug;
            return Results.Json(new
            {
                tiers = _quality.GetTiers().Select(t => new
                {
                    t.Slug, t.Label, t.Color, t.Icon, t.SortOrder,
                    isDefault = string.Equals(t.Slug, defaultTier, StringComparison.OrdinalIgnoreCase),
                }),
                modes = _quality.GetAll().Select(m => new
                {
                    m.Id, m.Slug, qualityTier = m.QualityTier, m.Provider, m.Model,
                    m.Effort, m.ThinkingBudget, m.Timeout, m.MaxTurns, m.IsDefault, m.Description,
                }),
            });
        });

        endpoints.MapPost("/ai-session/quality-modes/refresh",
            "Re-fetch quality mode definitions from RedLeaf and return the refreshed entity snapshot.", async () =>
        {
            if (_quality == null)
                return Error(503, "not_configured", "Quality mode service is not available");

            await Task.WhenAll(
                _quality.RefreshAsync(),
                _providerConfig?.RefreshAsync() ?? Task.CompletedTask);

            var defaultTier = _quality.DefaultTierSlug;
            return Results.Json(new
            {
                refreshed = true,
                tiers = _quality.GetTiers().Select(t => new
                {
                    t.Slug, t.Label, t.Color, t.Icon, t.SortOrder,
                    isDefault = string.Equals(t.Slug, defaultTier, StringComparison.OrdinalIgnoreCase),
                }),
                modes = _quality.GetAll().Select(m => new
                {
                    m.Id, m.Slug, qualityTier = m.QualityTier, m.Provider, m.Model,
                    m.Effort, m.ThinkingBudget, m.Timeout, m.MaxTurns, m.IsDefault, m.Description,
                }),
            });
        });

        endpoints.MapGet("/ai-session/providers/configured",
            "List all active AI provider configurations, resolved from RedLeaf. Use these slugs in the provider field.", () =>
        {
            if (_providerConfig == null)
                return Error(503, "not_configured", "Provider config service is not available");

            var defaultSlug = _providerConfig.DefaultProviderSlug;
            var providers = _providerConfig.GetAll().Select(p => new
            {
                p.Slug,
                p.Name,
                p.Backend,
                p.Icon,
                p.Color,
                iconSvgPath = p.IconSvgPath,
                endpointUrl = p.EndpointUrl,
                hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
                defaultModel = p.DefaultModel,
                isDefault = string.Equals(p.Slug, defaultSlug, StringComparison.OrdinalIgnoreCase),
                p.Status,
                p.Description,
            });
            return Results.Json(providers);
        });

        endpoints.MapPost("/ai-session/execute",
            "Run an agent task with full tool access (default 30min timeout, max 2h). Use ?async for fire-and-forget with job tracking.", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to execute tasks");

            ctx.Items["Telemetry.Kind"] = "job";

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var q = ResolveQuality(ctx, body);
            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (resolveError != null) return resolveError;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.StatelessExecution))
                return NotSupported(provider.ProviderId, "stateless execution");

            var prompt = body.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(prompt))
                return Error(422, "validation_failed", "prompt is required");

            var workingDir = body.TryGetProperty("workingDir", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : null;
            var model = q.Model;

            var timeout = 1800;
            if (body.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number)
                timeout = Math.Clamp(to.GetInt32(), 1, 7200);

            Dictionary<string, string>? env = null;
            if (body.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                env = envProp.EnumerateObject().ToDictionary(ep => ep.Name, ep => ep.Value.GetString() ?? "");

            var providerParams = new Dictionary<string, object?>();
            foreach (var key in new[] { "effort", "maxTurns", "allowedTools", "addDirs", "container", "dockerImage", "sandbox", "networkAccess" })
            {
                if (body.TryGetProperty(key, out var val))
                {
                    providerParams[key] = val.ValueKind switch
                    {
                        JsonValueKind.String => val.GetString(),
                        JsonValueKind.Number => val.TryGetInt32(out var i) ? i : (object)val.GetDouble(),
                        JsonValueKind.Array => val.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray(),
                        JsonValueKind.True or JsonValueKind.False => val.GetBoolean(),
                        _ => null
                    };
                }
            }

            // qualityTier supplies effort when not given explicitly (q.Effort already prefers an explicit effort).
            if (!string.IsNullOrWhiteSpace(q.Effort))
                providerParams["effort"] = q.Effort;

            // Docker Desktop is not started at launch. A session naming a container or an image is
            // the first thing that genuinely needs the daemon, so bring it up here and nowhere else.
            var namesContainer = providerParams.TryGetValue("container", out var reqContainer) && reqContainer is string { Length: > 0 };
            var namesImage = providerParams.TryGetValue("dockerImage", out var reqImage) && reqImage is string { Length: > 0 };
            if (namesContainer || namesImage)
                await DockerDesktopService.EnsureRunningAsync(s => log(s, null));

            if (!providerParams.ContainsKey("container") || providerParams["container"] is not string)
            {
                if (providerParams.TryGetValue("dockerImage", out var di) && di is string dockerImage && _docker != null)
                {
                    try
                    {
                        var resolved = await _docker.EnsureContainerAsync(dockerImage);
                        providerParams["container"] = resolved;
                        log($"[Docker] Resolved image '{dockerImage}' to container '{resolved}'", null);
                    }
                    catch (Exception ex)
                    {
                        return Error(500, "docker_failed", $"Failed to ensure container for image '{dockerImage}': {ex.Message}");
                    }
                }
            }

            if (userId != null)
                providerParams["userId"] = userId;

            // Thread provider endpoint/key into env so providers that support custom endpoints can use them.
            if (!string.IsNullOrEmpty(q.EndpointUrl))
            {
                env ??= new Dictionary<string, string>();
                env["OPENAI_BASE_URL"] = q.EndpointUrl;
            }
            if (!string.IsNullOrEmpty(q.ApiKey))
            {
                env ??= new Dictionary<string, string>();
                env["OPENAI_API_KEY"] = q.ApiKey;
            }

            var inputSummary = prompt.Length > 100 ? prompt[..97] + "..." : prompt;
            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var idempotencyKey = ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var ik) ? ik.ToString() : null;
            var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
            if (string.IsNullOrEmpty(jobName)) jobName = inputSummary;

            var inputJson = JsonSerializer.Serialize(new { prompt, model, workingDir, timeout, provider = provider.ProviderId });
            JobProvenance provenance;
            try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/execute"); }
            catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
            JobRecord job;
            try
            {
                job = jobTracker.CreateJob(new JobSubmission("ai-session", provider.ProviderDisplayName,
                    inputJson, provenance, callerInfo, idempotencyKey, jobName));
            }
            catch (IdempotencyConflictException ex)
            {
                return Results.Conflict(new { error = "idempotency_conflict", message = ex.Message, existingJobId = ex.ExistingJobId });
            }
            if (job.IsIdempotencyReuse)
                return Results.Json(new { jobId = job.Id, status = job.Status.ToString(), idempotentReuse = true },
                    statusCode: job.Status is JobStatus.Running or JobStatus.Queued ? 202 : 200);
            jobTracker.StartInvocation(job.Id, provenance);
            log($"[{provider.ProviderId}] Execute job {job.Id} started (model={model ?? "default"})", job.Id);

            var streamKey = job.Id.ToString();
            var asyncMode = ctx.Request.Query.ContainsKey("async");

            if (asyncMode)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await provider.ExecuteAsync(prompt, workingDir, model, timeout,
                            CancellationToken.None, streamKey, env, providerParams);
                        var rj = JsonSerializer.Serialize(new
                        {
                            success = result.Success, text = result.Text, streamOutput = result.StreamOutput,
                            model = result.Model, inputTokens = result.InputTokens,
                            outputTokens = result.OutputTokens, costUsd = result.CostUsd, error = result.Error
                        });
                        if (result.Success)
                            jobTracker.MarkCompleted(job.Id, resultJson: rj, costUsd: result.CostUsd);
                        else
                            jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed", resultJson: rj);
                    }
                    catch (Exception ex) { jobTracker.MarkFailed(job.Id, ex.Message); }
                });

                ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                return Results.Json(new { ok = true, job_id = job.Id, status = "running" }, statusCode: 202);
            }

            try
            {
                var result = await provider.ExecuteAsync(prompt, workingDir, model, timeout,
                    ctx.RequestAborted, streamKey, env, providerParams);

                var resultJson = JsonSerializer.Serialize(new
                {
                    success = result.Success, text = result.Text, streamOutput = result.StreamOutput,
                    model = result.Model, inputTokens = result.InputTokens,
                    outputTokens = result.OutputTokens, costUsd = result.CostUsd, error = result.Error
                });

                if (result.Success)
                    jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
                else
                    jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed", resultJson: resultJson);

                ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                return Results.Content(resultJson, "application/json");
            }
            catch (TaskCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                jobTracker.MarkCancelled(job.Id);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message);
                return Error(500, "execution_failed", ex.Message);
            }
        })
            .WithParam("async", "boolean", description: "Fire-and-forget: returns 202 with a job id instead of waiting for completion (presence flag)", location: ParamLocation.Query)
            .WithParam("X-Caller-Info", "string", description: "Legacy asserted caller label retained for compatibility; never treated as verified provenance", location: ParamLocation.Header)
            .WithParam("X-Idempotency-Key", "string", description: "Dedupe key scoped by capability, origin, actor, and beneficiary; conflicting payload reuse is rejected", location: ParamLocation.Header)
            .WithParam("X-Job-Name", "string", description: "Human-readable job name (defaults to a prompt excerpt)", location: ParamLocation.Header)
            .WithRequestBody(new
            {
                type = "object",
                required = new[] { "prompt" },
                properties = new
                {
                    prompt = new { type = "string", description = "Prompt text for the agent task" },
                    model = new { type = "string", description = "Model to use (provider default if omitted). Overrides qualityTier when both are given." },
                    qualityTier = new { type = "string", description = "Quality-tier entity slug resolved suite-wide to a provider+model+effort. Ignored when model is set." },
                    workingDir = new { type = "string", description = "Working directory for the agent" },
                    timeout = new { type = "integer", description = "Timeout in seconds, clamped to 1-7200", @default = 1800 },
                    provider = new { type = "string", description = "Session provider to use (defaults to active provider)", @enum = providerEnum },
                    env = new { type = "object", description = "Environment variables passed to the agent process (string values)" },
                    effort = new { type = "string", description = "Reasoning effort level (provider-specific)" },
                    maxTurns = new { type = "integer", description = "Maximum agent turns (provider-specific)" },
                    allowedTools = new { type = "array", items = new { type = "string" }, description = "Tool names the agent may use (provider-specific)" },
                    addDirs = new { type = "array", items = new { type = "string" }, description = "Additional directories the agent may access (provider-specific)" },
                    container = new { type = "string", description = "Existing Docker container to run in" },
                    dockerImage = new { type = "string", description = "Docker image — a container is created/reused automatically when no container is given" },
                    sandbox = new { type = "string", description = "Sandbox mode (provider-specific, e.g. read-only, workspace-write, danger-full-access)" },
                    networkAccess = new { type = "boolean", description = "Enable command network access for workspace-write executions" },
                },
            });

        endpoints.MapPost("/ai-session/generate",
            "Dual-mode: 'session' creates a persistent interactive session by project name, 'oneshot' runs a stateless LLM completion", async (HttpContext ctx) =>
        {
            ctx.Items["Telemetry.Kind"] = "job";

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var q = ResolveQuality(ctx, body);
            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (resolveError != null) return resolveError;

            var mode = body.TryGetProperty("mode", out var m) ? m.GetString() : "session";

            if (mode == "oneshot")
                return await HandleGenerateOneshot(ctx, body, provider!, jobTracker, q);

            return await HandleGenerateSession(ctx, body, provider!, jobTracker, q);
        })
            .WithParam("mode", "string", description: "'session' starts a persistent session; 'oneshot' runs a stateless LLM completion",
                defaultValue: "session", enumValues: ["session", "oneshot"], location: ParamLocation.Body)
            .WithParam("project", "string", description: "(session mode, required) Project name to start the session in", location: ParamLocation.Body)
            .WithParam("prompt", "string", description: "(session mode) Initial message to send once the session is up", location: ParamLocation.Body)
            .WithParam("messages", "array", description: "(oneshot mode, required) Array of {role, content} message objects", location: ParamLocation.Body)
            .WithParam("model", "string", description: "Model to use", location: ParamLocation.Body)
            .WithParam("system", "string", description: "(oneshot mode) System prompt", location: ParamLocation.Body)
            .WithParam("maxTokens", "integer", description: "(oneshot mode) Maximum tokens to generate, clamped to 1-8192", defaultValue: 1024, location: ParamLocation.Body)
            .WithParam("provider", "string", description: "Session provider to use (defaults to active provider)", enumValues: providerEnum, location: ParamLocation.Body)
            .WithParam("effort", "string", description: "Reasoning effort level (provider-specific)", location: ParamLocation.Body)
            .WithParam("qualityTier", "string", description: "Quality-tier entity slug resolved to a model+effort. Ignored when model is set.", location: ParamLocation.Body);
    }

    internal static bool TryGetNonNullImages(JsonElement body, out JsonElement images)
    {
        if (body.TryGetProperty("images", out images) && images.ValueKind != JsonValueKind.Null)
            return true;

        images = default;
        return false;
    }

    private static async Task<IResult> HandleGenerateSession(
        HttpContext ctx, JsonElement body, ISessionProvider provider, IJobTracker jobTracker, QualityResolution q)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.PersistentSessions))
            return NotSupported(provider.ProviderId, "persistent sessions");

        var project = body.TryGetProperty("project", out var p) ? p.GetString() : null;
        var prompt = body.TryGetProperty("prompt", out var pr) ? pr.GetString() : null;

        if (string.IsNullOrWhiteSpace(project))
            return Error(422, "validation_failed", "project is required for session mode");

        var resolved = provider.ListProjects().FirstOrDefault(proj =>
            proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (resolved == null)
            return Error(422, "validation_failed", $"Project '{project}' not found");

        int? thinkingBudget = body.TryGetProperty("thinkingBudget", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null;
        thinkingBudget ??= q.ThinkingBudget;
        var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
        var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
        JobProvenance provenance;
        try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/generate"); }
        catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
        var session = await provider.StartSessionAsync(resolved.Path, callerInfo, q.Model, uId, uName, uAvatar,
            q.Effort, q.EndpointUrl, q.ApiKey, thinkingBudget, null, null, provenance);
        if (session == null)
            return Error(503, "start_failed", provider.LastStartError ?? "Failed to start session");
        if (session.JobId is not { } jobId || jobTracker.GetJob(jobId) == null)
        {
            try { await provider.ForceKillAsync(session.Id); } catch { }
            return Error(500, "tracking_failed", "Session creation was rolled back because its compute job was not created");
        }

        if (!string.IsNullOrWhiteSpace(prompt) && provider.Capabilities.HasFlag(SessionCapabilities.SendMessage))
        {
            await Task.Delay(2000);
            await provider.SendMessageAsync(session.Id, prompt);
        }

        return Results.Json(new { jobId, sessionId = session.Id }, statusCode: 202);
    }

    private static async Task<IResult> HandleGenerateOneshot(
        HttpContext ctx, JsonElement body, ISessionProvider provider, IJobTracker jobTracker, QualityResolution q)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.Generate))
            return NotSupported(provider.ProviderId, "LLM completion");

        if (!body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return Error(422, "validation_failed", "messages is required and must be a non-empty array");

        var maxTokens = 1024;
        if (body.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var mtv))
            maxTokens = Math.Clamp(mtv, 1, 8192);

        int? timeout = null;
        if (body.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tov))
            timeout = Math.Clamp(tov, 10, 600);

        var model = q.Model;
        var effort = q.Effort;
        var system = body.TryGetProperty("system", out var sys) ? sys.GetString() : null;

        string? prompt = null;
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                prompt = content.GetString();
        }

        var inputJson = JsonSerializer.Serialize(new { model = model ?? "default", messageCount = messages.GetArrayLength(), maxTokens, effort, prompt, system, messages, provider = provider.ProviderId });
        var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
        var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
        if (string.IsNullOrEmpty(jobName) && !string.IsNullOrWhiteSpace(prompt))
            jobName = prompt.Length > 60 ? prompt[..57] + "..." : prompt;

        JobProvenance provenance;
        try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/generate"); }
        catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
        var job = jobTracker.CreateJob(new JobSubmission("ai-session", provider.ProviderDisplayName,
            inputJson, provenance, callerInfo, Name: jobName));
        jobTracker.StartInvocation(job.Id, provenance);

        try
        {
            var result = await provider.GenerateAsync(model, system, messages.GetRawText(), maxTokens, ctx.RequestAborted, effort, timeout);
            if (!result.Success)
            {
                jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                return Error(502, "execution_failed", result.Error ?? "Unknown error");
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                success = true, text = result.Text, streamOutput = result.StreamOutput, model = result.Model,
                inputTokens = result.InputTokens, outputTokens = result.OutputTokens, costUsd = result.CostUsd
            });
            jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
            ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
            return Results.Content(resultJson, "application/json");
        }
        catch (TaskCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            jobTracker.MarkCancelled(job.Id);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            jobTracker.MarkFailed(job.Id, ex.Message);
            return Error(500, "execution_failed", ex.Message);
        }
    }

    /// <summary>
    /// The model/effort/provider a request resolves to once a quality tier is applied.
    /// An explicit model always wins; otherwise a qualityTier resolves all three suite-wide.
    /// </summary>
    private record QualityResolution(string? Model, string? Effort, string? ProviderName,
        string? BackendName = null, string? EndpointUrl = null, string? ApiKey = null,
        int? ThinkingBudget = null, string? QualityTier = null);

    private static QualityResolution ResolveQuality(HttpContext ctx, JsonElement body)
    {
        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var effort = body.TryGetProperty("effort", out var ef) && ef.ValueKind == JsonValueKind.String ? ef.GetString() : null;
        var tier = body.TryGetProperty("qualityTier", out var qt) && qt.ValueKind == JsonValueKind.String ? qt.GetString() : null;
        var explicitProvider = body.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
        explicitProvider ??= ctx.Request.Headers["X-Provider"].FirstOrDefault();

        // Explicit model wins, and no tier means nothing to resolve — leave request values untouched.
        // Still resolve provider config so endpoint/apiKey flow through even without a quality tier.
        if (!string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(tier) || _quality == null)
        {
            if (explicitProvider != null && _providerConfig != null)
            {
                var pc = _providerConfig.Resolve(explicitProvider);
                return new QualityResolution(model ?? pc.DefaultModel, effort, explicitProvider, pc.Backend, pc.EndpointUrl, pc.ApiKey);
            }
            return new QualityResolution(model, effort, explicitProvider);
        }

        var resolved = _quality.Resolve(tier, explicitProvider);
        return new QualityResolution(
            resolved.Model,
            string.IsNullOrWhiteSpace(effort) ? resolved.Effort : effort,
            explicitProvider ?? resolved.Provider,
            resolved.Backend,
            resolved.EndpointUrl,
            resolved.ApiKey,
            resolved.ThinkingBudget,
            resolved.QualityTier);
    }

    private static (ISessionProvider? provider, IResult? error) ResolveProviderFromBody(
        HttpContext ctx, CapabilityRegistry registry, JsonElement body,
        string? providerOverride = null, string? backendOverride = null)
    {
        string? providerName = null;
        if (body.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String)
            providerName = pv.GetString();
        providerName ??= ctx.Request.Headers["X-Provider"].FirstOrDefault();
        // A quality-tier-resolved provider feeds selection only when the caller named none explicitly.
        providerName ??= providerOverride;

        var entry = registry.Get("ai-session");
        if (entry == null)
            return (null, Error(503, "not_configured", "ai-session capability is not configured"));

        if (providerName != null)
        {
            // Translate provider slug (e.g. "anthropic-direct") to backend ID (e.g. "claude-code")
            // so ProviderResolver can find the registered ISessionProvider instance.
            var backendName = backendOverride
                ?? (_providerConfig?.Resolve(providerName).Backend ?? providerName);
            var (backend, err) = ProviderResolver.Resolve(entry, backendName, "ai-session");
            if (err != null) return (null, err);
            if (backend is ISessionProvider sp) return (sp, null);
            return (null, Error(500, "not_session_provider", $"Provider '{providerName}' does not implement ISessionProvider"));
        }

        var active = entry.ActiveProvider as ISessionProvider;
        if (active != null) return (active, null);

        return (null, Error(503, "no_providers", "No session providers are registered"));
    }

    private static (ISessionProvider? provider, UnifiedSessionInfo? info, List<UnifiedMessageRecord>? history)
        FindSessionAcrossProviders(CapabilityRegistry registry, string sessionId)
    {
        foreach (var provider in registry.FindProviders<ISessionProvider>())
        {
            var (info, history) = provider.GetSession(sessionId);
            if (info != null)
                return (provider, info, history);
        }
        return (null, null, null);
    }

    private static List<string> FormatCapabilities(SessionCapabilities caps)
    {
        var result = new List<string>();
        foreach (SessionCapabilities flag in Enum.GetValues<SessionCapabilities>())
        {
            if (flag != SessionCapabilities.None && caps.HasFlag(flag))
                result.Add(flag.ToString());
        }
        return result;
    }

    private static IResult NotSupported(string providerId, string feature)
        => Results.Json(new ErrorResponse
        {
            Error = "not_supported",
            Message = $"Provider '{providerId}' does not support {feature}"
        }, statusCode: 422);

    private static string? ResolveUserId(HttpContext ctx)
        => UserInfoHelper.ResolveUserId(ctx);

    private static IResult Error(int status, string error, string message)
        => Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: status);

    private static object PublicAttachment(StagedInputAttachment attachment) => new
    {
        id = attachment.Id,
        kind = attachment.Kind,
        name = attachment.Name,
        mediaType = attachment.MediaType,
        size = attachment.Size,
        sha256 = attachment.Sha256,
        downloadUrl = $"/ai-session/input-attachments/{Uri.EscapeDataString(attachment.Id)}",
        expiresAt = attachment.ExpiresAt?.ToString("O"),
    };

    private static IResult AttachmentError(AttachmentStoreException ex) => ex.Code switch
    {
        "unauthorized" => Error(401, ex.Code, ex.Message),
        "forbidden" => Error(403, ex.Code, ex.Message),
        "attachment_not_found" or "attachment_missing" => Error(404, ex.Code, ex.Message),
        "attachment_already_claimed" or "attachment_claimed" => Error(409, ex.Code, ex.Message),
        "attachment_expired" => Error(410, ex.Code, ex.Message),
        "attachment_too_large" or "attachments_too_large" => Error(413, ex.Code, ex.Message),
        _ => Error(422, ex.Code, ex.Message),
    };

    private static string ImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private static readonly string[] IconCandidates =
    [
        "public/favicon.ico", "public/favicon.png", "public/favicon.svg",
        "public/logo.png", "public/logo.svg",
        "favicon.ico", "favicon.png", "favicon.svg",
        "logo.png", "logo.svg",
        "icon.png", "icon.ico", "icon.svg",
        "wwwroot/favicon.ico",
    ];

    private static string? FindProjectIcon(string projectPath)
    {
        foreach (var candidate in IconCandidates)
        {
            var full = Path.Combine(projectPath, candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
