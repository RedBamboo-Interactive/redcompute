using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
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
    internal static Task<SessionInputAdmissionResult> AdmitWithCurrentCallerAsync(HttpContext ctx,
        ISessionProvider provider, UnifiedSessionInfo info, SessionInputQueueSubmission submission,
        SessionInputQueueService queue, CancellationToken ct)
    {
        var identity = ExecutionContextScope.Current;
        var owner = ResolveUserId(ctx);
        var authorized = ctx.User.Identity?.IsAuthenticated == true
            && ctx.User.Identity.AuthenticationType != "LocalDefault"
            && owner is not (null or "local-user" or "system") && owner == info.UserId && owner == submission.OwnerUserId
            && identity?.Beneficiary.Kind == "user" && identity.Beneficiary.Id == owner
            && submission.Provenance.OnBehalfOf.Kind == "user" && submission.Provenance.OnBehalfOf.Id == owner
            && submission.Provenance.Actor.Kind == identity.Actor.Kind && submission.Provenance.Actor.Id == identity.Actor.Id
            && submission.Provenance.Actor.EntityId == identity.Actor.EntityId && submission.Provenance.Origin.App.Id == identity.App.Id;
        var token = authorized ? SessionExecutionToken.Issue(ctx, provider.ProviderId, provider.ProviderDisplayName, null, info.Id) : null;
        if (identity is null && owner == info.UserId && owner == submission.OwnerUserId
            && !ctx.Request.Headers.ContainsKey(ProvenanceCapture.HeaderName)
            && submission.Provenance.OnBehalfOf.Kind == "user" && submission.Provenance.OnBehalfOf.Id == owner
            && submission.Provenance.Origin.App.Kind == "direct-client" && submission.Provenance.Origin.App.Id == "direct-redcompute-api"
            && submission.Provenance.Actor.Kind == "app" && submission.Provenance.Actor.Id == "direct-redcompute-api")
            token = SessionExecutionToken.IssueAuthenticatedDirect(ctx, info.Id, submission.MessageUid, provider.ProviderId, provider.ProviderDisplayName);
        return token is null ? queue.AdmitAsync(submission, ct) : queue.AdmitWithCallerAsync(submission, token, ct);
    }
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
    private static SessionInputQueueService? _inputQueue;
    private static MaintenanceDeploymentCoordinator? _maintenance;

    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry,
        IJobTracker jobTracker, Action<string, Guid?> log, RedComputeConfig config,
        DockerContainerService? docker = null, SessionCallbackRegistry? callbacks = null,
        QualityModeService? quality = null, RedLeafSessionReader? redLeafReader = null,
        ProviderConfigService? providerConfig = null, InputAttachmentStore? attachmentStore = null,
        SessionInputQueueService? inputQueue = null,
        RepositoryReferenceValidator? repositoryValidator = null,
        MaintenanceDeploymentCoordinator? maintenance = null,
        Func<UnifiedSessionInfo, JobRecord, CancellationToken, Task>? mirrorConfidential = null)
    {
        _docker = docker;
        _callbacks = callbacks;
        _quality = quality;
        _redLeafReader = redLeafReader;
        _providerConfig = providerConfig;
        _attachmentStore = attachmentStore ?? new InputAttachmentStore(config);
        _inputQueue = inputQueue ?? throw new ArgumentNullException(nameof(inputQueue));
        _maintenance = maintenance;

        bool CanReadSession(HttpContext context, UnifiedSessionInfo session)
            => ComputeResourceAccess.CanReadSession(
                context,
                session,
                session.Confidential && session.JobId is { } jobId
                    ? jobTracker.GetJob(jobId)
                    : null);

        // Steering is a live provider capability, and background tasks are live provider state;
        // neither is durable session history. RedLeaf-backed reads would otherwise report them
        // only after the next session.updated event.
        UnifiedSessionInfo WithLiveCapabilities(UnifiedSessionInfo session)
        {
            var provider = registry.FindProviders<ISessionProvider>().FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, session.Provider, StringComparison.OrdinalIgnoreCase));
            session.SupportsSteering = provider?.Capabilities.HasFlag(SessionCapabilities.SupportsSteering) == true;
            try
            {
                session.BackgroundTasks = provider?.GetBackgroundTasks(session.Id) ?? [];
            }
            catch (Exception ex)
            {
                // Live state is an overlay; a provider fault must not fail the durable read.
                session.BackgroundTasks = [];
                log($"[Sessions] Background tasks unavailable for {session.Provider}/{session.Id}: {ex.Message}", null);
            }
            session.WorkState = SessionWorkStates.Derive(session.Status, PendingQuestions(session).Count, session.BackgroundTasks);
            return session;
        }

        // Parked questions are live control state, like background tasks. They are a top-level
        // overlay on the read, never transcript records, so message ordering and hydration are
        // unaffected.
        IReadOnlyList<PendingSessionQuestion> PendingQuestions(UnifiedSessionInfo session)
        {
            var provider = registry.FindProviders<ISessionProvider>().FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, session.Provider, StringComparison.OrdinalIgnoreCase));
            try
            {
                return provider?.GetPendingQuestions(session.Id) ?? [];
            }
            catch (Exception ex)
            {
                log($"[Sessions] Pending questions unavailable for {session.Provider}/{session.Id}: {ex.Message}", null);
                return [];
            }
        }

        var providerIds = registry.FindProviders<ISessionProvider>().Select(p => p.ProviderId).ToList();
        var providerEnum = providerIds.Count > 0 ? providerIds : null;
        var usageProviderReferences = providerIds
            .Concat(_providerConfig?.GetAll().Select(item => item.Slug) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var usageProviderEnum = usageProviderReferences.Count > 0 ? usageProviderReferences : null;

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

        endpoints.MapGet("/ai-session/providers/{provider}/usage",
            "Read normalized provider-wide subscription and quota windows for the authenticated user. Providers without account usage support return 404.",
            async (HttpContext ctx, string provider) =>
        {
            if (ResolveUserId(ctx) is null)
                return Error(401, "unauthorized", "Authentication required to read provider usage");

            var backendReference = _providerConfig is null
                ? provider
                : _providerConfig.Resolve(_providerConfig.ResolveReference(provider)).Backend;
            var source = FindProviderUsageSource(
                registry.FindProviders<IProviderUsageSource>(), provider, backendReference);
            if (source is null)
                return Error(404, "usage_not_supported", $"Provider '{provider}' does not expose account usage");

            var forceRefresh = bool.TryParse(ctx.Request.Query["refresh"], out var refresh) && refresh;
            try
            {
                var snapshot = await source.GetProviderUsageAsync(forceRefresh, ctx.RequestAborted);
                return snapshot is null
                    ? Error(503, "usage_unavailable", $"Provider '{provider}' did not return account usage")
                    : Results.Json(snapshot);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                log($"[ProviderUsage] Failed to read {provider}: {ex.Message}", null);
                return Error(503, "usage_unavailable", ex.Message);
            }
        })
            .WithParam("provider", "string", description: "AI session provider id or configured provider reference", enumValues: usageProviderEnum)
            .WithParam("refresh", "boolean", description: "Bypass the short provider cache", defaultValue: false, location: ParamLocation.Query);

        endpoints.MapGet("/ai-session/sessions",
            "List sessions across providers: Active and Starting first, then dormant history; newest StartedAt first within each group. Authorization and source filtering precede the requested limit. " +
            WorkStateDescription, async (HttpContext ctx) =>
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
                allSessions = await _redLeafReader!.GetSessionsAsync(providerFilter, limit, all,
                    session => CanReadSession(ctx, session) && (excludeSource is null || session.Source != excludeSource));
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (excludeSource != null)
                allSessions.RemoveAll(s => s.Source == excludeSource);

            allSessions.RemoveAll(session => !CanReadSession(ctx, session));
            foreach (var session in allSessions) WithLiveCapabilities(session);

            return Results.Json(allSessions);
        })
            .WithParam("limit", "integer", description: "Max sessions to return", defaultValue: 20, location: ParamLocation.Query)
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query)
            .WithParam("all", "boolean", description: "Include dismissed sessions (presence flag)", location: ParamLocation.Query)
            .WithParam("excludeSource", "string", description: "Exclude sessions whose source matches this value", location: ParamLocation.Query);

        endpoints.MapGet("/ai-session/sessions/archived",
            "List dismissed (archived) sessions, most recently updated first, as { items, nextOffset }. Paging is over stored rows and authorization filters each page, " +
            "so a page may hold fewer items than the limit; pass nextOffset back until it is null.", async (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            var offset = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault(), out var o) ? o : 0;
            var search = ctx.Request.Query["q"].FirstOrDefault();

            List<UnifiedSessionInfo> items;
            int? nextOffset;
            try
            {
                (items, nextOffset) = await _redLeafReader!.GetDismissedSessionsPageAsync(
                    providerFilter, limit, offset, search, session => CanReadSession(ctx, session));
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            foreach (var session in items) WithLiveCapabilities(session);
            return Results.Json(new { items, nextOffset });
        })
            .WithParam("limit", "integer", description: "Stored rows to scan for this page (clamped to 500)", defaultValue: 50, location: ParamLocation.Query)
            .WithParam("offset", "integer", description: "nextOffset from the previous page", defaultValue: 0, location: ParamLocation.Query)
            .WithParam("q", "string", description: "Case-insensitive match on title or stored session fields", location: ParamLocation.Query)
            .WithParam("provider", "string", description: "Filter by provider", enumValues: providerEnum, location: ParamLocation.Query);

        endpoints.MapPost("/ai-session/sessions",
            "Start a new persistent session. The optional conversation-only profile uses an isolated empty workspace, read-only/no-network execution, denied approvals, and no derived suite credential.", async (HttpContext ctx) =>
        {
            if (_maintenance?.IsDraining == true)
                return Error(503, "maintenance_draining", "RedCompute is draining active turns for a planned deployment");
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to create sessions");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (body.ValueKind != JsonValueKind.Object)
                return Error(400, "invalid_body", "Request body must be a JSON object");

            var (q, qualityError) = ResolveQuality(ctx, body);
            if (qualityError != null) return qualityError;
            var (provider, error) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (error != null) return error;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.PersistentSessions))
                return NotSupported(provider.ProviderId, "persistent sessions");

            var requestedExecutionProfile = body.TryGetProperty("executionProfile", out var profileValue)
                && profileValue.ValueKind != JsonValueKind.Null
                    ? profileValue.GetString()
                    : null;
            if (!SessionExecutionProfile.TryNormalize(requestedExecutionProfile, out var executionProfile))
                return Error(422, "validation_failed",
                    $"executionProfile must be '{SessionExecutionProfile.Default}' or '{SessionExecutionProfile.ConversationOnly}'");
            var conversationOnly = SessionExecutionProfile.IsConversationOnly(executionProfile);
            if (conversationOnly && !provider.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                return Error(422, "execution_profile_not_supported",
                    $"Provider '{provider.ProviderId}' does not support the conversation-only execution profile");

            var projectPath = body.TryGetProperty("projectPath", out var pp) ? pp.GetString() : null;
            if (conversationOnly)
            {
                if (!string.IsNullOrWhiteSpace(projectPath)
                    || body.TryGetProperty("repositoryId", out _)
                    || body.TryGetProperty("scratchDir", out _))
                    return Error(422, "validation_failed",
                        "conversation-only sessions allocate their own isolated workspace; projectPath, repositoryId, and scratchDir must be omitted");
                projectPath = CreateConversationWorkspace();
            }
            else if (string.IsNullOrWhiteSpace(projectPath))
                return Error(422, "validation_failed", "projectPath is required");

            RepositoryReference? repository = null;
            if (body.TryGetProperty("repositoryId", out var repositoryValue)
                && repositoryValue.ValueKind != JsonValueKind.Null)
            {
                if (repositoryValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(repositoryValue.GetString()))
                    return Error(422, "validation_failed", "repositoryId must be a Repository entity UUID");
                if (repositoryValidator is null)
                    return Error(503, "repository_validation_unavailable", "Repository validation is unavailable");

                var validation = await repositoryValidator.ValidateAsync(
                    repositoryValue.GetString()!, projectPath, ctx.RequestAborted);
                if (!validation.Ok)
                    return Error(validation.StatusCode,
                        validation.StatusCode == StatusCodes.Status503ServiceUnavailable
                            ? "repository_validation_unavailable"
                            : "validation_failed",
                        validation.Error ?? "Repository validation failed");
                repository = validation.Repository;
            }

            string? scratchDirectory = conversationOnly ? projectPath : null;
            if (!conversationOnly && body.TryGetProperty("scratchDir", out var scratchValue)
                && scratchValue.ValueKind != JsonValueKind.Null)
            {
                if (scratchValue.ValueKind != JsonValueKind.String
                    || !SessionScratch.TryResolveDirectory(scratchValue.GetString(), out scratchDirectory))
                    return Error(422, "validation_failed", "scratchDir must be an existing absolute directory");
            }

            var model = q.Model;
            var effort = q.Effort;
            string? developerInstructions = null;
            if (body.TryGetProperty("developerInstructions", out var instructionValue)
                && instructionValue.ValueKind != JsonValueKind.Null)
            {
                if (instructionValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(instructionValue.GetString()))
                    return Error(422, "validation_failed",
                        "developerInstructions must be a non-empty string when supplied");
                developerInstructions = instructionValue.GetString();
            }
            var confidential = body.TryGetProperty("confidential", out var confidentialValue)
                && confidentialValue.ValueKind == JsonValueKind.True;
            var keepResident = false;
            if (body.TryGetProperty("keepResident", out var keepResidentValue)
                && keepResidentValue.ValueKind != JsonValueKind.Null)
            {
                if (keepResidentValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Error(422, "validation_failed", "keepResident must be a boolean when supplied");
                keepResident = keepResidentValue.GetBoolean();
            }
            if (conversationOnly && confidential)
                return Error(422, "validation_failed",
                    "conversation-only and confidential are separate ownership contracts and cannot be combined");
            int? thinkingBudget = body.TryGetProperty("thinkingBudget", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null;
            thinkingBudget ??= q.ThinkingBudget;
            var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
            JobProvenance provenance;
            try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions"); }
            catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
            if (confidential
                && (provenance.Assurance != JobProvenanceAssurance.Verified
                    || !provenance.Actor.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(provenance.Actor.EntityId)
                    || !provenance.OnBehalfOf.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(provenance.OnBehalfOf.Id)))
                return Error(422, "invalid_confidential_owner",
                    "Confidential sessions require a verified Agent actor and user beneficiary");
            if (repository is not null && !provenance.Context.Any(reference =>
                    string.Equals(reference.Kind, "repository", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reference.EntityId, repository.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
            {
                provenance = provenance with
                {
                    Context =
                    [
                        .. provenance.Context,
                        new JobContextReference(
                            "repository",
                            EntityId: repository.Id.ToString("D"),
                            NameSnapshot: repository.Name),
                    ],
                };
            }
            UnifiedSessionInfo? session;
            try
            {
                using (SessionExecutionProfile.Push(executionProfile))
                {
                    if (conversationOnly)
                    {
                        session = await provider.StartSessionAsync(projectPath, model, uId, uName, uAvatar,
                            effort, q.EndpointUrl, SessionApiKey(q), thinkingBudget, q.QualityTier, q.ProviderName,
                            repository?.Id, provenance, scratchDirectory, confidential, developerInstructions);
                    }
                    else
                    {
                        using (SessionExecutionToken.Push(ctx, provider.ProviderId, provider.ProviderDisplayName))
                            session = await provider.StartSessionAsync(projectPath, model, uId, uName, uAvatar,
                                effort, q.EndpointUrl, SessionApiKey(q), thinkingBudget, q.QualityTier, q.ProviderName,
                                repository?.Id, provenance, scratchDirectory, confidential, developerInstructions);
                    }
                }
            }
            catch (NotSupportedException ex) when (!string.IsNullOrWhiteSpace(developerInstructions))
            {
                return Error(422, "developer_instructions_not_supported", ex.Message);
            }
            if (session == null)
                return Error(500, "start_failed", provider.LastStartError ?? "Failed to start session");
            if (session.JobId is not { } jobId || jobTracker.GetJob(jobId) == null)
            {
                try { await provider.ForceKillAsync(session.Id); }
                catch (Exception ex) { log($"Failed to clean up untracked session {session.Id}: {ex.Message}", null); }
                log($"Provider {provider.ProviderId} created session {session.Id} without a valid compute job", null);
                return Error(500, "tracking_failed", "Session creation was rolled back because its compute job was not created");
            }
            if (keepResident) jobTracker.SetKeepResident(jobId, true);

            return Results.Json(session);
        })
            .WithParam("projectPath", "string", description: "Path to the project directory. Omit for conversation-only sessions, which receive an isolated workspace.", location: ParamLocation.Body)
            .WithParam("repositoryId", "string", description: "Stable RedLeaf Repository entity UUID. When supplied, its active local_path must exactly match projectPath.", location: ParamLocation.Body)
            .WithParam("provider", "string", description: "Provider to use. Defaults to the active provider.", enumValues: providerEnum, location: ParamLocation.Body)
            .WithParam("model", "string", description: "Model to use. Overrides qualityTier when both are given.", location: ParamLocation.Body)
            .WithParam("effort", "string", description: "Effort level (e.g. low, normal, high)", location: ParamLocation.Body)
            .WithParam("qualityTier", "string", description: "Quality-tier entity slug resolved suite-wide to a provider+model+effort. Ignored when model is set.", location: ParamLocation.Body)
            .WithParam("thinkingBudget", "integer", description: "Thinking/reasoning token budget. Explicit value wins over qualityTier.", location: ParamLocation.Body)
            .WithParam("scratchDir", "string", description: "Existing absolute physical directory used for TEMP, TMP, TMPDIR, and REDLEAF_SCRATCH_DIR.", location: ParamLocation.Body)
            .WithParam("developerInstructions", "string", description: "Provider-native developer instructions applied to the persistent session. Unsupported providers reject the request.", location: ParamLocation.Body)
            .WithParam("executionProfile", "string", description: "Persisted provider execution restrictions. conversation-only is currently supported by Codex.", enumValues: [SessionExecutionProfile.Default, SessionExecutionProfile.ConversationOnly], defaultValue: SessionExecutionProfile.Default, location: ParamLocation.Body)
            .WithParam("confidential", "boolean", description: "Restrict the session and linked job to the beneficiary user and verified owning Agent.", defaultValue: false, location: ParamLocation.Body)
            .WithParam("keepResident", "boolean", description: "Exempt the session from idle parking. For always-on presence sessions only.", defaultValue: false, location: ParamLocation.Body);


        endpoints.MapGet("/ai-session/sessions/{id}",
            "Get session details and message history. pendingQuestions lists the structured questions the live session is parked on " +
            "(Claude AskUserQuestion, Codex requestUserInput) with requestId, options, askedAt and expiresAt; answer one with " +
            "POST /ai-session/sessions/{id}/question. Empty when nothing is pending. Claude questions expire after 30 minutes (the session is told to proceed " +
            "on its own judgement); Codex questions have no deadline (expiresAt null). " + WorkStateDescription, async (HttpContext ctx, string id) =>
        {
            int? tail = null;
            if (int.TryParse(ctx.Request.Query["tail"].FirstOrDefault(), out var requestedTail))
                tail = Math.Clamp(requestedTail, 1, 10_000);

            UnifiedSessionInfo? info;
            List<UnifiedMessageRecord> history;
            try
            {
                (info, history) = await _redLeafReader!.GetSessionAsync(id, tail);
            }
            catch (Exception ex)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);

            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            var queue = await _inputQueue!.GetSummaryAsync(id, userId ?? "local-user", ctx.RequestAborted);
            var transcript = TranscriptOrdering.Cursor(info.Provider, info.Id, history);
            return Results.Json(new { session = WithLiveCapabilities(info), messages = history, inputQueue = queue, transcript,
                pendingQuestions = PendingQuestions(info) });
        })
            .WithResponse(SessionDetailResponseSchema)
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("tail", "integer", description: "Legacy bounded newest-record read. Use transcript-page for complete keyset pagination; the backing stream clamps this compatibility read to 1000 records.", location: ParamLocation.Query);

        endpoints.MapGet("/ai-session/sessions/{id}/transcript-page",
            "Read one canonical durable transcript page without splitting a messageUid boundary",
            async (HttpContext ctx, string id) =>
        {
            var before = ctx.Request.Query["before"].FirstOrDefault();
            var after = ctx.Request.Query["after"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
                return Error(400, "invalid_pagination", "before and after are mutually exclusive");

            var limit = RedLeafSessionReader.MaxTranscriptPageLimit;
            var requestedLimit = ctx.Request.Query["limit"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requestedLimit))
            {
                if (!int.TryParse(requestedLimit, out limit))
                    return Error(400, "invalid_pagination", "limit must be an integer");
                limit = Math.Clamp(limit, 1, RedLeafSessionReader.MaxTranscriptPageLimit);
            }

            UnifiedSessionInfo? info;
            string? entityId;
            try
            {
                (info, entityId) = await _redLeafReader!.GetSessionInfoAsync(id);
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            if (info == null || entityId == null)
                return Error(404, "not_found", $"Session '{id}' not found");

            // Resolve authorization before cursor validation. A cursor must not become
            // an oracle for another user's confidential session or transcript epoch.
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            var epoch = TranscriptOrdering.Epoch(info.Provider, info.Id);
            TranscriptPageCursor? cursor = null;
            var direction = "newest";
            try
            {
                if (!string.IsNullOrWhiteSpace(before))
                {
                    direction = "before";
                    cursor = TranscriptPageCursorCodec.Decode(
                        before, info.Id, epoch, TranscriptPageCursorEdge.Oldest);
                }
                else if (!string.IsNullOrWhiteSpace(after))
                {
                    direction = "after";
                    cursor = TranscriptPageCursorCodec.Decode(
                        after, info.Id, epoch, TranscriptPageCursorEdge.Newest);
                }
            }
            catch (TranscriptPageCursorException ex)
            {
                return Error(
                    ex.Code == "invalid_cursor" ? 400 : 409,
                    ex.Code,
                    ex.Message);
            }

            TranscriptPageReadResult page;
            try
            {
                page = await _redLeafReader.GetTranscriptPageAsync(
                    entityId,
                    info.Id,
                    limit,
                    beforeRecordId: direction == "before" ? cursor?.RecordId : null,
                    afterRecordId: direction == "after" ? cursor?.RecordId : null,
                    ctx.RequestAborted);
            }
            catch (TranscriptPageTooLargeException ex)
            {
                log(
                    $"Transcript page rejected for {info.Provider}/{info.Id}: " +
                    $"boundaryRecords={ex.RecordCount}, boundaryInlineBytes={ex.InlineBytes}",
                    null);
                return Error(413, "transcript_message_too_large", ex.Message);
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}");
            }

            var (fromSequence, throughSequence) = TranscriptPageSequenceRange(page.Messages, epoch);
            var response = new TranscriptPageResponse
            {
                Session = WithLiveCapabilities(info),
                Messages = page.Messages,
                Page = new TranscriptPageMetadata
                {
                    Epoch = epoch,
                    Direction = direction,
                    OldestCursor = page.OldestRecordId is { } oldestId
                        ? TranscriptPageCursorCodec.Encode(new TranscriptPageCursor(
                            info.Id, epoch, oldestId, TranscriptPageCursorEdge.Oldest))
                        : null,
                    NewestCursor = page.NewestRecordId is { } newestId
                        ? TranscriptPageCursorCodec.Encode(new TranscriptPageCursor(
                            info.Id, epoch, newestId, TranscriptPageCursorEdge.Newest))
                        : null,
                    HasEarlier = page.HasEarlier,
                    HasLater = page.HasLater,
                    FromSequence = fromSequence,
                    ThroughSequence = throughSequence,
                    BoundaryComplete = true,
                },
            };
            return Results.Json(response);
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("limit", "integer", description: "Soft mapped-message target, clamped to 1..500; a complete boundary UID may expand the response", defaultValue: 500, location: ParamLocation.Query)
            .WithParam("before", "string", description: "Opaque oldest-edge cursor returned by a previous page", location: ParamLocation.Query)
            .WithParam("after", "string", description: "Opaque newest-edge cursor returned by a previous page", location: ParamLocation.Query)
            .WithResponse(TranscriptPageResponseSchema);

        endpoints.MapPut("/ai-session/sessions/{id}/confidential",
            "Permanently tighten a session and its linked job to the verified owning Agent and beneficiary user.",
            async (HttpContext ctx, string id) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (!body.TryGetProperty("confidential", out var confidentialValue)
                || confidentialValue.ValueKind != JsonValueKind.True)
                return Error(422, "classification_is_sticky",
                    "RedCompute only accepts confidentiality tightening; declassification happens at the owning application boundary");

            var (owned, failure) = await ResolveOwnedSessionJobAsync(ctx, id, jobTracker);
            if (owned is null) return failure!;
            var (info, jobId, _, ownerAgentId) = owned;

            jobTracker.SetConfidential(jobId);
            var job = jobTracker.GetJob(jobId)!;
            info.Confidential = true;
            info.OwnerAgentId = ownerAgentId;
            if (mirrorConfidential is not null)
                await mirrorConfidential(info, job, ctx.RequestAborted);
            return Results.Json(new { sessionId = id, jobId, confidential = true });
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path)
            .WithParam("confidential", "boolean", required: true, location: ParamLocation.Body);

        endpoints.MapPut("/ai-session/sessions/{id}/keep-resident",
            "Exempt a session from idle parking, or return it to the default policy. Owning Agent and beneficiary user only.",
            async (HttpContext ctx, string id) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("keepResident", out var keepValue)
                || keepValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Error(422, "validation_failed", "keepResident must be a boolean");

            var (owned, failure) = await ResolveOwnedSessionJobAsync(ctx, id, jobTracker);
            if (owned is null) return failure!;

            var keepResident = keepValue.GetBoolean();
            jobTracker.SetKeepResident(owned.JobId, keepResident);
            return Results.Json(new { sessionId = id, jobId = owned.JobId, keepResident });
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path)
            .WithParam("keepResident", "boolean", required: true, location: ParamLocation.Body);

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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

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
                return Results.Json(new
                {
                    session = info,
                    messages = history,
                    transcript = TranscriptOrdering.Cursor(info.Provider, info.Id, history),
                });
            return Results.Json(new ErrorResponse { Error = "not_found", Message = $"No session for job '{jobId}'" }, statusCode: 404);
        });

        endpoints.MapPost("/ai-session/sessions/{id}/message",
            "Durably admit a user message, then deliver now, steer into the active turn, or queue after it according to delivery policy.", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
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

                var referenced = new List<StagedInputAttachment>(attachmentIds.Count);
                foreach (var attachmentId in attachmentIds.Distinct(StringComparer.Ordinal))
                {
                    var attachment = await _attachmentStore!.GetAuthorizedAsync(
                        attachmentId, userId ?? "local-user", ctx.RequestAborted)
                        ?? throw new AttachmentStoreException("attachment_not_found", $"Attachment '{attachmentId}' was not found");
                    referenced.Add(attachment);
                }

                if (referenced.Any(a => a.Kind == "file") && !provider.Capabilities.HasFlag(SessionCapabilities.FileAttachments))
                    return Error(422, "file_attachments_not_supported", $"Provider '{provider.ProviderId}' does not support file attachments");
                if (referenced.Any(a => a.Kind == "image") && !provider.Capabilities.HasFlag(SessionCapabilities.ImageAttachments))
                    return Error(422, "image_attachments_not_supported", $"Provider '{provider.ProviderId}' does not support image attachments");
                if (referenced.Any(a => a.Kind == "image") && provider is IImageAttachmentSupportProvider sessionImageSupport)
                {
                    var support = sessionImageSupport.GetImageAttachmentSupport(id);
                    if (!support.Supported)
                        return Error(422, "image_attachments_not_supported", support.Reason ?? $"Session '{id}' does not support image attachments");
                }

                if (info.JobId is not { } invocationJobId || jobTracker.GetJob(invocationJobId) == null)
                    return Error(500, "tracking_failed", "The session has no tracked Compute job");
                JobProvenance invocationProvenance;
                try
                {
                    invocationProvenance = await ProvenanceCapture.ResolveAsync(ctx,
                        "/ai-session/sessions/{id}/message");
                }
                catch (JobProvenanceValidationException ex)
                {
                    return Error(422, "invalid_provenance", ex.Message);
                }

                var deliveryPolicy = SessionInputDeliveryPolicy.AfterCurrent;
                if (body.TryGetProperty("delivery", out var deliveryElement)
                    && deliveryElement.ValueKind != JsonValueKind.Null)
                {
                    if (deliveryElement.ValueKind != JsonValueKind.String)
                        return Error(400, "invalid_delivery_policy", "delivery must be 'after-current', 'steer-current', or 'interrupt-current'");
                    deliveryPolicy = deliveryElement.GetString() switch
                    {
                        "after-current" => SessionInputDeliveryPolicy.AfterCurrent,
                        "steer-current" => SessionInputDeliveryPolicy.SteerCurrent,
                        "interrupt-current" => SessionInputDeliveryPolicy.InterruptCurrent,
                        _ => throw new SessionInputQueueStoreException("invalid_delivery_policy", "delivery must be 'after-current', 'steer-current', or 'interrupt-current'"),
                    };
                }

                var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
                if (idempotencyKey?.Length > 256)
                    return Error(400, "invalid_idempotency_key", "X-Idempotency-Key cannot exceed 256 characters");
                var displayContent = body.TryGetProperty("displayContent", out var display)
                    && display.ValueKind == JsonValueKind.String
                        ? display.GetString() ?? ""
                        : string.Join("\n", inputSpec.Where(part => part.Type == "text").Select(part => part.Value));
                var metadataJson = body.TryGetProperty("metadata", out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object ? metadata.GetRawText() : null;
                if (metadataJson is not null && DeploymentVerificationTarget.FromMetadata(metadataJson) is not null)
                    return Error(400, "invalid_metadata", "Use the typed deploymentVerificationTarget admission field");
                if (body.TryGetProperty("deploymentVerificationTarget", out var targetElement))
                {
                    var target = targetElement.Deserialize<DeploymentVerificationTarget>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (target is null) return Error(400, "invalid_deployment_target", "A target object is required");
                    metadataJson = DeploymentVerificationTarget.WithMetadata(metadataJson, target);
                }
                var submission = new SessionInputQueueSubmission(
                    id,
                    userId ?? "local-user",
                    inputSpec.Select(part => new QueuedSessionInputPart(part.Type, part.Value)).ToArray(),
                    displayContent,
                    metadataJson,
                    invocationProvenance,
                    deliveryPolicy,
                    messageUid,
                    attachmentIds,
                    idempotencyKey);
                var resumeAttempted = IsAutoResumable(provider, info);
                var admitted = await AdmitWithCurrentCallerAsync(ctx, provider, info, submission, _inputQueue!, ctx.RequestAborted);
                var (_, currentInfo, _) = FindSessionAcrossProviders(registry, id);
                var resumed = resumeAttempted && currentInfo?.Status is SessionStatus.Idle or SessionStatus.Active or SessionStatus.Starting;
                var queueSummary = admitted.Queue;
                return Results.Json(new
                {
                    accepted = true,
                    existing = admitted.Existing,
                    disposition = admitted.Disposition,
                    queueItemId = admitted.Item.Id,
                    messageUid = admitted.Item.MessageUid,
                    deliveredMessageUid = admitted.Item.DeliveredMessageUid,
                    resumeAttempted,
                    resumed,
                    attachments = referenced.Select(PublicAttachment).ToArray(),
                    queue = queueSummary,
                    item = PublicQueueItem(admitted.Item, referenced),
                }, statusCode: admitted.Disposition == "delivered" ? 200 : 202);
            }
            catch (AttachmentStoreException ex)
            {
                return AttachmentError(ex);
            }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
            catch (JsonException) { return Error(400, "invalid_deployment_target", "A valid typed deployment verification target is required"); }
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
                    deploymentVerificationTarget = DeploymentVerificationTargetSchema,
                    displayContent = new { type = "string", description = "Human-readable draft shown in queue views when provider input contains an enriched context envelope" },
                    delivery = new { type = "string", @enum = new[] { "after-current", "steer-current", "interrupt-current" }, @default = "after-current", description = "Durable delivery policy. steer-current injects into a supported active turn without interruption and otherwise demotes to after-current." },
                    messageUid = new { type = "string", description = "Stable message identity to persist with the message. Supply it when the same logical message is also stored elsewhere (cross-stream dedup); minted server-side when omitted. Returned in the response." },
                },
            })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("X-Idempotency-Key", "string", required: false, location: ParamLocation.Header,
                description: "Retry-safe key scoped to the session, owner, and creation provenance. Reuse with a different payload returns 409.");

        endpoints.MapGet("/ai-session/sessions/{id}/input-queue",
            "List the durable FIFO for a session. Active items are returned by default; includeTerminal exposes 24-hour delivery and cancellation receipts.",
            async (HttpContext ctx, string id) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", $"Session '{id}' not found");
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            var owner = userId ?? "local-user";
            var includeTerminal = bool.TryParse(ctx.Request.Query["includeTerminal"].FirstOrDefault(), out var include) && include;
            var items = await _inputQueue!.ListAsync(id, owner, includeTerminal, ctx.RequestAborted);
            var summary = await _inputQueue.GetSummaryAsync(id, owner, ctx.RequestAborted);
            var publicItems = await Task.WhenAll(items.Select(item =>
                PublicQueueItemAsync(item, owner, ctx.RequestAborted)));
            return Results.Json(new { items = publicItems, queue = summary });
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("includeTerminal", "boolean", required: false, location: ParamLocation.Query,
                defaultValue: false, description: "Include delivered and cancelled receipts retained for 24 hours")
            .WithResponse(QueueListResponseSchema);

        endpoints.MapGet("/ai-session/sessions/{id}/input-queue/{itemId}",
            "Get one durable queue item or terminal receipt for exact delivery verification",
            async (HttpContext ctx, string id, string itemId) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", $"Session '{id}' not found");
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            var item = await _inputQueue!.GetAsync(id, itemId, userId ?? "local-user", ctx.RequestAborted);
            return item is null
                ? Error(404, "queue_item_not_found", $"Queue item '{itemId}' was not found")
                : Results.Json(await PublicQueueItemAsync(item, userId ?? "local-user", ctx.RequestAborted));
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("itemId", "string", required: true, location: ParamLocation.Path, description: "Queue item id returned by message admission")
            .WithResponse(QueueItemResponseSchema);

        endpoints.MapDelete("/ai-session/sessions/{id}/input-queue/{itemId}",
            "Cancel a pending or failed queue item and release its attachments back to draft state",
            async (HttpContext ctx, string id, string itemId) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", $"Session '{id}' not found");
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            try
            {
                var item = await _inputQueue!.CancelAsync(id, itemId, userId ?? "local-user", ctx.RequestAborted);
                return item is null
                    ? Error(404, "queue_item_not_found", $"Queue item '{itemId}' was not found")
                    : Results.Json(await PublicQueueItemAsync(item, userId ?? "local-user", ctx.RequestAborted));
            }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("itemId", "string", required: true, location: ParamLocation.Path, description: "Queue item id")
            .WithResponse(QueueItemResponseSchema);

        endpoints.MapPost("/ai-session/sessions/{id}/input-queue/{itemId}/retry",
            "Retry a failed queue head after inspecting its structured error",
            async (HttpContext ctx, string id, string itemId) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", $"Session '{id}' not found");
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            try
            {
                var item = await _inputQueue!.RetryAsync(id, itemId, userId ?? "local-user", ctx.RequestAborted);
                return item is null
                    ? Error(404, "queue_item_not_found", $"Queue item '{itemId}' was not found")
                    : Results.Json(await PublicQueueItemAsync(item, userId ?? "local-user", ctx.RequestAborted));
            }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("itemId", "string", required: true, location: ParamLocation.Path, description: "Failed queue item id")
            .WithResponse(QueueItemResponseSchema);

        endpoints.MapPost("/ai-session/sessions/{id}/input-queue/send-now",
            "Request interruption of the active turn and deliver the durable queue once the provider reports a safe idle state",
            async (HttpContext ctx, string id) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", $"Session '{id}' not found");
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            var requested = await _inputQueue!.SendNowAsync(id, userId ?? "local-user", ctx.RequestAborted);
            if (!requested) return Error(409, "queue_empty", "There is no active queued input to send");
            var summary = await _inputQueue.GetSummaryAsync(id, userId ?? "local-user", ctx.RequestAborted);
            return Results.Json(new { requested = true, queue = summary });
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithResponse(new
            {
                type = "object",
                required = new[] { "requested", "queue" },
                properties = new
                {
                    requested = new { type = "boolean" },
                    queue = QueueSummarySchema,
                },
            });

        endpoints.MapPost("/ai-session/sessions/{id}/inject",
            "Inject a message into the session history without triggering a model turn", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

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
            return injected
                ? Results.Json(new { injected = true, messageUid })
                : Error(409, "injection_rejected",
                    $"Provider '{provider.ProviderId}' did not persist message '{messageUid}' in session '{id}'");
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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Interrupt))
                return NotSupported(provider.ProviderId, "session interrupts");

            var result = provider.InterruptSession(id);
            return Results.Json(new { interrupted = result == InterruptResult.Interrupted, reason = result.ToString() });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/background-tasks/{taskId}/stop",
            "Stop one running background task (subagent or background shell) without interrupting the session. "
            + "The response confirms the request; the task's terminal state arrives as a `task` record and session.updated.",
            async (HttpContext ctx, string id, string taskId) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SupportsBackgroundTaskStop))
                return NotSupported(provider.ProviderId, "stopping background tasks");

            var result = await provider.TryStopBackgroundTaskAsync(id, taskId);
            return result.Status switch
            {
                BackgroundTaskStopStatus.Accepted => Results.Json(new { stopRequested = true, taskId }),
                BackgroundTaskStopStatus.SessionNotFound => Error(409, "session_not_running", result.Message ?? $"Session '{id}' is not running"),
                BackgroundTaskStopStatus.TaskNotFound => Error(404, "task_not_found", result.Message ?? $"Background task '{taskId}' was not found"),
                BackgroundTaskStopStatus.Unsupported => NotSupported(provider.ProviderId, "stopping background tasks"),
                _ => Error(502, "stop_failed", result.Message ?? "The provider did not stop the background task"),
            };
        })
            .WithParam("id", "string", required: true, location: ParamLocation.Path, description: "Session id")
            .WithParam("taskId", "string", required: true, location: ParamLocation.Path, description: "Background task id from session.backgroundTasks or a `task` record");

        endpoints.MapPost("/ai-session/sessions/{id}/resume",
            "Resume a stopped session", async (HttpContext ctx, string id) =>
        {
            if (_maintenance?.IsDraining == true)
                return Error(503, "maintenance_draining", "RedCompute is draining active turns for a planned deployment");
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Resume))
                return NotSupported(provider.ProviderId, "session resume");

            JobProvenance provenance;
            try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions/{id}/resume"); }
            catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
            UnifiedSessionInfo? session = null;
            await _inputQueue!.ExplicitResumeAsync(id, async () => {
                using (SessionExecutionToken.Push(ctx, provider.ProviderId, provider.ProviderDisplayName))
                    session = await provider.ResumeSessionAsync(id, provenance);
                return session?.Status is SessionStatus.Idle or SessionStatus.Active or SessionStatus.Starting;
            }, ctx.RequestAborted);
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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            await _inputQueue!.StopSessionAsync(id, () => provider!.StopSessionAsync(id), ctx.RequestAborted);
            return Results.Json(new { stopped = true });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/dismiss",
            "Mark a session as dismissed (hidden from default listings)", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            provider!.DismissSession(id);
            var cancelledQueueItems = await _inputQueue!.CancelSessionAsync(id, ctx.RequestAborted);
            return Results.Json(new { dismissed = true, cancelledQueueItems });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/restore",
            "Clear a dismissal so the session returns to default listings. Does not start a process; resume separately.", (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            if (!provider!.RestoreSession(id))
                return Error(409, "restore_unsupported", $"Provider '{provider.ProviderId}' cannot restore session '{id}'");
            return Results.Json(new { restored = true });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/input-queue/{itemId}/supersede-verification",
            "Explicitly supersede only an exact never-delivered pending deployment verification target with a declared admitted replacement. Preserves terminal audit; ordinary inputs cannot be superseded.",
            async (HttpContext ctx, string id, string itemId) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", "Session not found");
            if (!CanReadSession(ctx, info)) return ComputeResourceAccess.SessionDenied(info);
            var userId = ResolveUserId(ctx);
            if (string.IsNullOrWhiteSpace(userId) || userId == "local-user"
                || ctx.User.Identity?.IsAuthenticated != true || ctx.User.Identity.AuthenticationType == "LocalDefault")
                return Error(403, "authenticated_owner_required", "An authenticated queue owner is required");
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<VerificationSupersession>(ctx.RequestAborted);
                if (body?.Target is null || body.Replacement is null) return Error(400, "invalid_deployment_target", "target and replacement are required");
                var item = await _inputQueue!.SupersedeVerificationAsync(id, itemId, userId, body.Target, body.Replacement, ctx.RequestAborted);
                return item is null ? Error(404, "not_found", "Queue item not found") : Results.Json(await PublicQueueItemAsync(item, userId, ctx.RequestAborted));
            }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
            catch (JsonException) { return Error(400, "invalid_body", "Valid typed target and replacement required"); }
        });

        endpoints.MapPost("/ai-session/sessions/{id}/callback",
            "Register a completion webhook: RedCompute POSTs the session result to the given URL when the session finishes. Preferred over polling for agents awaiting session completion. " +
            "With questionUrl, RedCompute also POSTs once per parked question (sessionId, title, requestId, questions with options, askedAt, expiresAt); the completion callback is unaffected. " +
            "A turn that ends Idle while background tasks still run is reported with backgroundTasksRunning (count) and deliverySequence, and the registration stays armed for the turn that background work wakes; " +
            "every later delivery of that registration carries the next deliverySequence under the same callbackId, while retries of one delivery repeat it unchanged. " +
            "Every completion payload also carries workState (working, awaiting_answer, background or idle) at delivery time.", async (HttpContext ctx, string id) =>
        {
            if (_callbacks == null)
                return Error(503, "not_configured", "Callback registry not available");

            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var url = body.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return Error(422, "validation_failed", "url is required");

            var force = body.TryGetProperty("force", out var f) && f.GetBoolean();
            string? callbackId = null;
            if (body.TryGetProperty("callbackId", out var callback))
            {
                if (callback.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(callback.GetString())
                    || callback.GetString()!.Length > 200)
                    return Error(422, "validation_failed", "callbackId must be a nonempty string of at most 200 characters");
                callbackId = callback.GetString();
            }
            string? promptMessageUid = null;
            if (body.TryGetProperty("promptMessageUid", out var prompt))
            {
                if (prompt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prompt.GetString()) || prompt.GetString()!.Length > 200 || callbackId is null)
                    return Error(422, "validation_failed", "promptMessageUid requires an exact nonempty prompt reference and callbackId");
                promptMessageUid = prompt.GetString();
            }
            string? questionUrl = null;
            if (body.TryGetProperty("questionUrl", out var question) && question.ValueKind != JsonValueKind.Null)
            {
                if (question.ValueKind != JsonValueKind.String || question.GetString()!.Length > 2000
                    || !Uri.TryCreate(question.GetString(), UriKind.Absolute, out var questionUri)
                    || questionUri.Scheme is not ("http" or "https"))
                    return Error(422, "validation_failed", "questionUrl must be an absolute http(s) URL of at most 2000 characters");
                questionUrl = question.GetString();
            }
            bool deferred;
            try { deferred = _callbacks.RegisterIfStillActive(id, url, info.Status, userId ?? info.UserId, info.StopReason, force, callbackId, promptMessageUid, questionUrl,
                info.BackgroundTasks); }
            catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
            return Results.Json(new { registered = deferred, currentStatus = info.Status.ToString() });
        })
            .WithParam("url", "string", required: true, description: "URL to POST the completion payload to", location: ParamLocation.Body)
            .WithParam("force", "boolean", description: "Register even if session is already idle (for pre-registering before sending a message)", location: ParamLocation.Body)
            .WithParam("callbackId", "string", description: "Optional logical completion operation identity, echoed unchanged across retries", location: ParamLocation.Body)
            .WithParam("promptMessageUid", "string", description: "Opt in to durable exact-prompt completion; separate from callbackId. May pre-register before admission.", location: ParamLocation.Body)
            .WithParam("questionUrl", "string", description: "Opt in to question notifications: POSTed once per requestId when the session parks on a question, with " +
                "{event: session.question, sessionId, callbackId, title, provider, requestId, toolName, toolUseId, questions, input, askedAt, expiresAt, answer}. " +
                "Delivered and retried like the completion callback; never consumes it. The opt-in is session-scoped (session, questionUrl, owner), " +
                "independent of prompts, and lasts until the session ends (a resumable stop keeps it; re-registering is idempotent). It also reports, once, " +
                "the end of any turn the session started without a queued input (peer message, self-wake) while no prompt registration covered it: " +
                "a completion payload to url with trigger \"unprompted\", deliverySequence per such turn, no deliveredMessageUid. Omit to receive completions only.", location: ParamLocation.Body);

        endpoints.MapDelete("/ai-session/sessions/{id}/callback/unaccepted",
            "Remove only an exact owner-authorized prompt preregistration after definitive admission rejection; accepted input is never removed.", async (HttpContext ctx, string id) =>
        {
            var (_, info, _) = FindSessionAcrossProviders(registry, id);
            if (info is null) return Error(404, "not_found", "Session not found");
            if (!CanReadSession(ctx, info)) return ComputeResourceAccess.SessionDenied(info);
            var owner = ResolveUserId(ctx);
            if (ctx.User.Identity?.IsAuthenticated != true || ctx.User.Identity.AuthenticationType == "LocalDefault"
                || owner is null or "local-user" || owner != info.UserId)
                return Error(403, "authenticated_owner_required", "Current authenticated owner required");
            var callbackId = ctx.Request.Query["callbackId"].ToString();
            var promptMessageUid = ctx.Request.Query["promptMessageUid"].ToString();
            if (string.IsNullOrWhiteSpace(callbackId) || string.IsNullOrWhiteSpace(promptMessageUid)
                || ctx.Request.Query["definitiveAdmissionFailure"] != "true")
                return Error(422, "validation_failed", "Exact callbackId, promptMessageUid and definitiveAdmissionFailure are required");
            return Results.Json(new { removed = _callbacks is not null && await _callbacks.RemoveUnacceptedAsync(id, callbackId, promptMessageUid, owner, ctx.RequestAborted) });
        });

        endpoints.MapPost("/ai-session/sessions/{id}/permission-mode",
            "Set the session's permission mode", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);
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
            if (!CanReadSession(ctx, info))
                return ComputeResourceAccess.SessionDenied(info);

            await _inputQueue!.StopSessionAsync(id, () => provider!.ForceKillAsync(id), ctx.RequestAborted);
            return Results.Json(new { killed = true });
        });

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
            "List active AI inference Provider entities admitted by capability or quality mode, including their runtime binding and models. Use these slugs in the provider field.", () =>
        {
            if (_providerConfig == null || _quality == null)
                return Error(503, "not_configured", "Provider and quality mode services are not available");

            var defaultSlug = _providerConfig.DefaultProviderSlug;
            var entry = registry.Get("ai-session");
            var providers = FilterInferenceProviders(_providerConfig.GetAll(), _quality.GetAll()).Select(p =>
            {
                var runtime = entry == null
                    ? null
                    : FindRegisteredSessionProvider(entry, p.Slug, p.Backend);
                IReadOnlyList<ModelInfo> models = [];
                string? runtimeError = runtime == null ? "provider_plugin_not_loaded" : null;
                if (runtime != null)
                {
                    try { models = runtime.GetAvailableModels(); }
                    catch (Exception ex)
                    {
                        runtimeError = "model_discovery_failed";
                        log($"[AI Session] Model discovery failed for provider '{p.Slug}': {ex.Message}", null);
                    }
                }

                return new
                {
                    p.Slug,
                    p.Name,
                    p.Backend,
                    providerType = p.ProviderType,
                    runtimeBinding = p.RuntimeBinding,
                    p.Icon,
                    p.Color,
                    iconSvgPath = p.IconSvgPath,
                    endpointUrl = p.EndpointUrl,
                    defaultModel = p.DefaultModel,
                    hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
                    isDefault = string.Equals(p.Slug, defaultSlug, StringComparison.OrdinalIgnoreCase),
                    runtimeAvailable = runtime != null,
                    runtimeProviderId = runtime?.ProviderId,
                    runtimeCapabilities = runtime?.Capabilities.ToString(),
                    toolFreeGenerate = runtime?.Capabilities.HasFlag(SessionCapabilities.ToolFreeGenerate) == true,
                    vision = runtime?.Capabilities.HasFlag(SessionCapabilities.GenerateVision) == true,
                    runtimeError,
                    models = models.Select(model => new { model.Id, model.Name, model.Fast }),
                    p.Status,
                    p.Description,
                };
            });
            return Results.Json(providers);
        });

        endpoints.MapPost("/ai-session/execute",
            "Run an agent task with full tool access (default 30min timeout, max 2h). Use ?async for fire-and-forget with job tracking. Signed execution identity is derived for the admitted one-shot lifetime and passed to the provider process in REDLEAF_EXECUTION_TOKEN.", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to execute tasks");

            ctx.Items["Telemetry.Kind"] = "job";

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var (q, qualityError) = ResolveQuality(ctx, body);
            if (qualityError != null) return qualityError;
            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (resolveError != null) return resolveError;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.StatelessExecution))
                return NotSupported(provider.ProviderId, "stateless execution");

            var prompt = body.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(prompt))
                return Error(422, "validation_failed", "prompt is required");

            var workingDir = body.TryGetProperty("workingDir", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : null;
            var model = q.Model;
            var suiteAccess = !body.TryGetProperty("suiteAccess", out var suiteAccessValue)
                || suiteAccessValue.ValueKind != JsonValueKind.False;

            var timeout = 1800;
            if (body.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number)
                timeout = Math.Clamp(to.GetInt32(), 1, 7200);

            Dictionary<string, string>? env = null;
            if (body.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                env = envProp.EnumerateObject().ToDictionary(ep => ep.Name, ep => ep.Value.GetString() ?? "");

            var providerParams = new Dictionary<string, object?>();
            foreach (var key in new[] { "effort", "maxTurns", "tools", "allowedTools", "addDirs", "container", "dockerImage", "sandbox", "networkAccess" })
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
            env = WithProviderCredentialEnv(env, q.EndpointUrl, q.ApiKey, q.ApiKeyDestination);

            var inputSummary = prompt.Length > 100 ? prompt[..97] + "..." : prompt;
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
                    inputJson, provenance, idempotencyKey, jobName));
            }
            catch (IdempotencyConflictException ex)
            {
                return Results.Conflict(new { error = "idempotency_conflict", message = ex.Message, existingJobId = ex.ExistingJobId });
            }
            if (job.IsIdempotencyReuse)
                return Results.Json(new { jobId = job.Id, status = job.Status.ToString(), idempotentReuse = true },
                    statusCode: job.Status is JobStatus.Running or JobStatus.Queued ? 202 : 200);

            try
            {
                // Materialize before the sync/async split. Detached execution must not depend on
                // request-scoped AsyncLocal state surviving after this handler returns.
                env = suiteAccess
                    ? SessionExecutionToken.CreateStatelessEnvironment(
                        ctx, provider.ProviderId, provider.ProviderDisplayName, timeout, env)
                    : SessionExecutionToken.CreateStatelessEnvironmentWithoutSuiteIdentity(env);
            }
            catch (Exception ex)
            {
                const string message = "Failed to derive provider execution identity";
                jobTracker.MarkFailed(job.Id, message,
                    resultJson: ExecuteFailureResultJson(model, message));
                log($"[{provider.ProviderId}] {message}: {ex.Message}", job.Id);
                return Error(500, "execution_identity_failed", message);
            }
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
                        var rj = ExecuteResultJson(result);
                        if (result.Success)
                            jobTracker.MarkCompleted(job.Id, resultJson: rj, costUsd: result.CostUsd);
                        else
                            jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed", resultJson: rj);
                    }
                    catch (Exception ex)
                    {
                        jobTracker.MarkFailed(job.Id, ex.Message,
                            resultJson: ExecuteFailureResultJson(model, ex.Message));
                    }
                });

                ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                return Results.Json(new { ok = true, job_id = job.Id, status = "running" }, statusCode: 202);
            }

            try
            {
                var result = await provider.ExecuteAsync(prompt, workingDir, model, timeout,
                    ctx.RequestAborted, streamKey, env, providerParams);

                var resultJson = ExecuteResultJson(result);

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
                jobTracker.MarkFailed(job.Id, ex.Message,
                    resultJson: ExecuteFailureResultJson(model, ex.Message));
                return Error(500, "execution_failed", ex.Message);
            }
        })
            .WithParam("async", "boolean", description: "Fire-and-forget: returns 202 with a job id instead of waiting for completion (presence flag)", location: ParamLocation.Query)
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
                    suiteAccess = new { type = "boolean", description = "Pass a derived REDLEAF_EXECUTION_TOKEN to the provider process", @default = true },
                },
            });

        endpoints.MapPost("/ai-session/generate",
            "Run a stateless LLM completion", async (HttpContext ctx) =>
        {
            ctx.Items["Telemetry.Kind"] = "job";

            var (body, bodyError) = await ReadGenerateBodyAsync(ctx);
            if (bodyError != null) return bodyError;

            var (q, qualityError) = ResolveQuality(ctx, body);
            if (qualityError != null) return qualityError;
            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body, q.ProviderName, q.BackendName);
            if (resolveError != null) return resolveError;

            if (!IsStatelessGenerateMode(body))
                return Error(422, "validation_failed",
                    "mode must be 'oneshot'; create persistent sessions through POST /ai-session/sessions with projectPath and repositoryId");

            return await HandleGenerateOneshot(ctx, body, provider!, jobTracker, q);
        })
            .WithParam("mode", "string", description: "Stateless completion mode",
                defaultValue: "oneshot", enumValues: ["oneshot"], location: ParamLocation.Body)
            .WithParam("messages", "array", description: "Required array of {role, content} message objects", location: ParamLocation.Body)
            .WithParam("model", "string", description: "Model to use", location: ParamLocation.Body)
            .WithParam("system", "string", description: "System prompt", location: ParamLocation.Body)
            .WithParam("maxTokens", "integer", description: "Maximum tokens to generate, clamped to 1-8192", defaultValue: 1024, location: ParamLocation.Body)
            .WithParam("requireToolFree", "boolean", description: "Require a runtime that enforces and verifies an empty tool surface. The response reports isolation 'verified-tool-free' or the call fails (422 tool_free_unavailable before running, 502 tool_free_unverified after).", defaultValue: false, location: ParamLocation.Body)
            .WithParam("images", "array", description: "Optional images for the final user message, at most 4: each { attachmentId } (staged via POST /ai-session/input-attachments) or { mediaType, base64 }. PNG, JPEG, still WebP or GIF (first frame), at most 5 MiB and 4096 px per side; every image is decoded, validated and re-encoded as PNG before forwarding. Bodies over 32 MiB answer 413 request_too_large. Providers without vision answer 422 vision_unavailable; refused images answer 422 invalid_images.", location: ParamLocation.Body)
            .WithParam("responseSchema", "object", description: "Optional JSON Schema (bounded subset: type, properties, required, additionalProperties, items, enum, const, minLength, maxLength, pattern, minimum, maximum, exclusiveMinimum, exclusiveMaximum, minItems, maxItems, title, description). Enforced natively where the provider supports strict structured output, otherwise by prompt; RedCompute always validates and returns json, schemaValid, errors and schemaMode.", location: ParamLocation.Body)
            .WithParam("provider", "string", description: "Session provider to use (defaults to active provider)", enumValues: providerEnum, location: ParamLocation.Body)
            .WithParam("effort", "string", description: "Reasoning effort level (provider-specific)", location: ParamLocation.Body)
            .WithParam("qualityTier", "string", description: "Quality-tier entity slug resolved to a model+effort. Ignored when model is set.", location: ParamLocation.Body)
            .WithParam("confidential", "boolean", description: "Restrict the generated job to the verified owning Agent and beneficiary user.", defaultValue: false, location: ParamLocation.Body);
    }

    internal static IReadOnlyList<ProviderEntityConfig> FilterInferenceProviders(
        IReadOnlyList<ProviderEntityConfig> providers,
        IReadOnlyList<QualityMode> qualityModes)
    {
        var inferenceProviderSlugs = qualityModes
            .Select(mode => mode.Provider)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return providers
            .Where(provider => inferenceProviderSlugs.Contains(provider.Slug)
                || provider.Capabilities.Contains("ai-inference", StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    internal static IProviderUsageSource? FindProviderUsageSource(
        IEnumerable<IProviderUsageSource> sources,
        string reference,
        string? backendReference)
    {
        var candidates = sources.ToList();
        return candidates.FirstOrDefault(source =>
                   source.ProviderId.Equals(reference, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(source =>
                   string.Equals(source.ProviderId, backendReference, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryGetNonNullImages(JsonElement body, out JsonElement images)
    {
        if (body.TryGetProperty("images", out images) && images.ValueKind != JsonValueKind.Null)
            return true;

        images = default;
        return false;
    }

    internal static bool IsStatelessGenerateMode(JsonElement body)
    {
        if (!body.TryGetProperty("mode", out var value)) return true;
        return value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), "oneshot", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsConfidentialGenerate(JsonElement body)
        => body.TryGetProperty("confidential", out var value)
            && value.ValueKind == JsonValueKind.True;

    private static async Task<IResult> HandleGenerateOneshot(
        HttpContext ctx, JsonElement body, ISessionProvider provider, IJobTracker jobTracker, QualityResolution q)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.Generate))
            return NotSupported(provider.ProviderId, "LLM completion");

        if (body.TryGetProperty("requireToolFree", out var requireToolFree))
        {
            if (requireToolFree.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Error(422, "validation_failed", "requireToolFree must be a boolean");
            if (requireToolFree.ValueKind == JsonValueKind.True
                && !provider.Capabilities.HasFlag(SessionCapabilities.ToolFreeGenerate))
                return Error(422, "tool_free_unavailable",
                    $"Provider '{provider.ProviderId}' does not support verified tool-free generation");
        }

        var toolFreeRequired = body.TryGetProperty("requireToolFree", out var toolFreeValue)
            && toolFreeValue.ValueKind == JsonValueKind.True;

        ResponseSchema? responseSchema = null;
        if (body.TryGetProperty("responseSchema", out var schemaValue) && schemaValue.ValueKind != JsonValueKind.Null)
        {
            if (!ResponseSchema.TryParse(schemaValue, out responseSchema, out var schemaErrors))
                return Results.Json(new
                {
                    error = "invalid_response_schema",
                    message = "responseSchema uses unsupported or invalid JSON Schema",
                    errors = schemaErrors,
                }, statusCode: 422);
        }

        if (!body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return Error(422, "validation_failed", "messages is required and must be a non-empty array");

        var (images, imageError) = await ReadGenerateImagesAsync(ctx, body, provider);
        if (imageError != null) return imageError;

        var maxTokens = 1024;
        if (body.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var mtv))
            maxTokens = Math.Clamp(mtv, 1, 8192);

        int? timeout = null;
        if (body.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var tov))
            timeout = Math.Clamp(tov, 10, 600);

        var model = q.Model;
        var effort = q.Effort;
        var system = body.TryGetProperty("system", out var sys) ? sys.GetString() : null;
        // Always prompt for the schema: native enforcement is an extra guarantee where a provider
        // has it, and RedCompute's own validation below decides schemaValid either way.
        var providerSystem = responseSchema == null
            ? system
            : string.IsNullOrWhiteSpace(system)
                ? responseSchema.PromptInstruction()
                : system + "\n\n" + responseSchema.PromptInstruction();

        string? prompt = null;
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                prompt = content.GetString();
        }

        var inputJson = JsonSerializer.Serialize(new
        {
            model = model ?? "default", messageCount = messages.GetArrayLength(), maxTokens, effort, prompt, system,
            messages, provider = provider.ProviderId, requireToolFree = toolFreeRequired,
            responseSchema = responseSchema?.Json,
            // Image bytes stay out of the job record; their identity does not.
            images = images.Select(image => new
            {
                mediaType = image.MediaType, width = image.Width, height = image.Height,
                bytes = image.Bytes.Length, sha256 = image.Sha256, sourceSha256 = image.SourceSha256,
            }),
        });
        var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
        if (string.IsNullOrEmpty(jobName) && !string.IsNullOrWhiteSpace(prompt))
            jobName = prompt.Length > 60 ? prompt[..57] + "..." : prompt;

        JobProvenance provenance;
        try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/generate"); }
        catch (JobProvenanceValidationException ex) { return Error(422, "invalid_provenance", ex.Message); }
        var job = jobTracker.CreateJob(new JobSubmission("ai-session", provider.ProviderDisplayName,
            inputJson, provenance, Name: jobName, Confidential: IsConfidentialGenerate(body)));
        jobTracker.StartInvocation(job.Id, provenance);

        try
        {
            var result = await provider.GenerateAsync(new SessionGenerateRequest(
                model, providerSystem, messages.GetRawText(), maxTokens, effort, timeout,
                toolFreeRequired, responseSchema, images.Count > 0 ? images : null), ctx.RequestAborted);
            if (!result.Success)
            {
                jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                return Error(502, "execution_failed", result.Error ?? "Unknown error");
            }

            var isolation = result.Isolation ?? SessionIsolation.Unverified;
            if (toolFreeRequired && isolation != SessionIsolation.VerifiedToolFree)
            {
                // The capability check admitted the call; this is the per-call proof. Without it
                // the model text is withheld, never returned with a weaker label.
                var message = $"Provider '{provider.ProviderId}' did not verify tool-free isolation for this call (reported '{isolation}')";
                jobTracker.MarkFailed(job.Id, message);
                return Error(502, "tool_free_unverified", message);
            }

            var resultJson = GenerateResultJson(result, isolation, responseSchema,
                provider.Capabilities.HasFlag(SessionCapabilities.GenerateVision), images.Count);
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
    /// Generate's request body limit. Kestrel's 30,000,000-byte default cannot hold four images at
    /// the base64 cap (4 x <see cref="GenerateImageValidator.MaxBase64Length"/> is about 28 MB) plus a
    /// prompt, so this route alone allows 32 MiB; other routes keep the default.
    /// </summary>
    internal const long MaxGenerateBodyBytes = 32L * 1024 * 1024;

    internal static async Task<(JsonElement Body, IResult? Error)> ReadGenerateBodyAsync(HttpContext ctx)
    {
        var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
            limit.MaxRequestBodySize = MaxGenerateBodyBytes;
        if (ctx.Request.ContentLength > MaxGenerateBodyBytes)
            return (default, GenerateBodyTooLarge());

        try
        {
            return (await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted), null);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (default, GenerateBodyTooLarge());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (default, Error(400, "invalid_body", "Request body must be valid JSON"));
        }
    }

    private static IResult GenerateBodyTooLarge()
        => Error(413, "request_too_large",
            $"Generate request bodies are limited to {MaxGenerateBodyBytes} bytes; send large images by attachmentId " +
            "(POST /ai-session/input-attachments) instead of base64");

    /// <summary>
    /// Reads <c>images</c> for generate: up to <see cref="GenerateImageValidator.MaxImages"/> entries,
    /// each either <c>{ attachmentId }</c> (an image staged through <c>/ai-session/input-attachments</c>
    /// by the same caller) or <c>{ mediaType, base64 }</c>, the shapes session messages already use.
    /// A provider without <see cref="SessionCapabilities.GenerateVision"/> is refused before any
    /// image is decoded, so callers fail closed instead of having images silently dropped. Every
    /// image is decoded and validated here; providers only ever receive validated bytes.
    /// </summary>
    private static async Task<(IReadOnlyList<GenerateImage> Images, IResult? Error)> ReadGenerateImagesAsync(
        HttpContext ctx, JsonElement body, ISessionProvider provider)
    {
        if (!body.TryGetProperty("images", out var imagesValue) || imagesValue.ValueKind == JsonValueKind.Null)
            return ([], null);
        if (imagesValue.ValueKind != JsonValueKind.Array)
            return ([], InvalidImages(["images must be an array"]));
        if (imagesValue.GetArrayLength() == 0)
            return ([], null);
        if (!provider.Capabilities.HasFlag(SessionCapabilities.GenerateVision))
            return ([], Error(422, "vision_unavailable",
                $"Provider '{provider.ProviderId}' cannot receive image inputs on generate"));
        if (imagesValue.GetArrayLength() > GenerateImageValidator.MaxImages)
            return ([], InvalidImages([$"images: at most {GenerateImageValidator.MaxImages} images per call"]));

        var userId = ResolveUserId(ctx) ?? "local-user";
        var images = new List<GenerateImage>();
        var errors = new List<string>();
        var index = 0;
        foreach (var entry in imagesValue.EnumerateArray())
        {
            var path = $"images[{index++}]";
            try
            {
                var (bytes, declared, readError) = await ReadImageBytesAsync(entry, userId, ctx.RequestAborted);
                if (readError != null)
                {
                    errors.Add($"{path}: {readError}");
                    continue;
                }

                var (image, error) = await GenerateImageValidator.ValidateAsync(bytes!, declared, ctx.RequestAborted);
                if (error != null) errors.Add($"{path}: {error}");
                else images.Add(image!);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Hostile images must never become a 500: anything unexpected is a refusal.
                errors.Add($"{path}: image could not be read ({ex.GetType().Name})");
            }
        }

        return errors.Count > 0 ? ([], InvalidImages(errors)) : (images, null);
    }

    private static async Task<(byte[]? Bytes, string? MediaType, string? Error)> ReadImageBytesAsync(
        JsonElement entry, string userId, CancellationToken ct)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return (null, null, "must be an object");
        var hasAttachment = entry.TryGetProperty("attachmentId", out var idValue);
        var hasBase64 = entry.TryGetProperty("base64", out var base64Value);
        if (hasAttachment == hasBase64)
            return (null, null, "give exactly one of attachmentId or base64");

        if (hasAttachment)
        {
            if (idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idValue.GetString()))
                return (null, null, "attachmentId must be a non-empty string");
            StagedInputAttachment? attachment;
            try
            {
                attachment = await _attachmentStore!.GetAuthorizedAsync(idValue.GetString()!, userId, ct);
            }
            catch (AttachmentStoreException)
            {
                // Another caller's attachment reads the same as a missing one.
                attachment = null;
            }
            if (attachment == null) return (null, null, "attachment is not available to this caller");
            if (attachment.Kind != "image") return (null, null, "attachment is not an image");
            if (attachment.Size > GenerateImageValidator.MaxImageBytes)
                return (null, null, $"image exceeds {GenerateImageValidator.MaxImageBytes} bytes");
            return (await File.ReadAllBytesAsync(attachment.StoredPath, ct), attachment.MediaType, null);
        }

        if (!entry.TryGetProperty("mediaType", out var mediaTypeValue) || mediaTypeValue.ValueKind != JsonValueKind.String)
            return (null, null, "base64 images require mediaType");
        var mediaType = mediaTypeValue.GetString()!;
        if (!GenerateImageValidator.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            return (null, null, $"media type '{mediaType}' is not one of {string.Join(", ", GenerateImageValidator.AllowedMediaTypes)}");
        if (base64Value.ValueKind != JsonValueKind.String)
            return (null, null, "base64 must be a string");
        var base64 = base64Value.GetString()!;
        if (base64.Length > GenerateImageValidator.MaxBase64Length)
            return (null, null, $"image exceeds {GenerateImageValidator.MaxImageBytes} bytes");
        try
        {
            return (Convert.FromBase64String(base64), mediaType, null);
        }
        catch (FormatException)
        {
            return (null, null, "base64 is not valid");
        }
    }

    private static IResult InvalidImages(IReadOnlyList<string> errors)
        => Results.Json(new { error = "invalid_images", message = "One or more images were refused", errors }, statusCode: 422);

    /// <summary>
    /// The generate result document. With a response schema it adds <c>json</c> (the extracted
    /// value or null), <c>schemaValid</c>, <c>errors</c> and <c>schemaMode</c> (<c>native</c> when
    /// the provider enforced the schema itself, else <c>prompt</c>). Validation is RedCompute's
    /// own and is never delegated to the model or provider.
    /// </summary>
    internal static string GenerateResultJson(SessionGenerateResult result, string isolation, ResponseSchema? schema,
        bool vision = false, int imageCount = 0)
    {
        var document = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["text"] = result.Text,
            ["streamOutput"] = result.StreamOutput,
            ["model"] = result.Model,
            ["inputTokens"] = result.InputTokens,
            ["outputTokens"] = result.OutputTokens,
            ["costUsd"] = result.CostUsd,
            ["isolation"] = isolation,
            ["isolationMechanism"] = result.IsolationMechanism,
            ["vision"] = vision,
            ["imageCount"] = imageCount,
        };

        if (schema != null)
        {
            IReadOnlyList<string> errors;
            JsonElement? json = null;
            if (ResponseSchema.TryExtractJson(result.Text, out var extracted, out var extractionError))
            {
                json = extracted;
                errors = schema.Validate(extracted);
            }
            else
            {
                // An undecodable value is never echoed: serializing it would itself throw.
                errors = [$"$: {extractionError}"];
            }

            document["json"] = json;
            document["schemaValid"] = errors.Count == 0;
            document["errors"] = errors;
            document["schemaMode"] = result.NativeResponseSchema ? "native" : "prompt";
        }

        return JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// The model/effort/provider a request resolves to once a quality tier is applied.
    /// An explicit model always wins; otherwise a qualityTier resolves all three suite-wide.
    /// </summary>
    private record QualityResolution(string? Model, string? Effort, string? ProviderName,
        string? BackendName = null, string? EndpointUrl = null, string? ApiKey = null,
        int? ThinkingBudget = null, string? QualityTier = null, string? ApiKeyDestination = null);

    private const string DefaultOpenAiBaseUrl = "https://api.openai.com";

    // Caller env that would route or intercept the child's TLS traffic elsewhere.
    private static readonly string[] CredentialRedirectEnv =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
    ];

    /// <summary>The provider key handed to a session provider, only when bound to the session endpoint.</summary>
    private static string? SessionApiKey(QualityResolution q)
        => ProviderKeyBindings.Release(q.ApiKey, q.ApiKeyDestination, q.EndpointUrl);

    /// <summary>
    /// OPENAI_BASE_URL / OPENAI_API_KEY for an execute child. The provider key is injected
    /// only when it is bound to the base URL the child will actually call (the provider
    /// endpoint, else a caller-supplied base URL, else the SDK default) and the caller env
    /// does not redirect or intercept TLS traffic.
    /// </summary>
    internal static Dictionary<string, string>? WithProviderCredentialEnv(
        Dictionary<string, string>? env, string? endpointUrl, string? apiKey, string? apiKeyDestination)
    {
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            env ??= new Dictionary<string, string>();
            env["OPENAI_BASE_URL"] = endpointUrl;
        }
        if (string.IsNullOrEmpty(apiKey)) return env;

        string? EnvValue(string name) => env?.FirstOrDefault(kv =>
            string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var baseUrl = new[] { EnvValue("OPENAI_BASE_URL"), EnvValue("OPENAI_API_BASE") }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? DefaultOpenAiBaseUrl;
        var redirected = CredentialRedirectEnv.Any(name => !string.IsNullOrWhiteSpace(EnvValue(name)));
        var key = redirected ? null : ProviderKeyBindings.Release(apiKey, apiKeyDestination, baseUrl);
        if (key != null)
        {
            env ??= new Dictionary<string, string>();
            env["OPENAI_API_KEY"] = key;
        }
        return env;
    }

    private static (QualityResolution Resolution, IResult? Error) ResolveQuality(HttpContext ctx, JsonElement body)
    {
        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var effort = body.TryGetProperty("effort", out var ef) && ef.ValueKind == JsonValueKind.String ? ef.GetString() : null;
        var tier = body.TryGetProperty("qualityTier", out var qt) && qt.ValueKind == JsonValueKind.String ? qt.GetString() : null;
        var explicitProvider = body.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
        explicitProvider ??= ctx.Request.Headers["X-Provider"].FirstOrDefault();

        // Explicit model wins, and no tier means nothing to resolve — leave request values untouched.
        // Still resolve provider config so endpoint/apiKey flow through even without a quality tier.
        if (!string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(tier))
        {
            if (explicitProvider != null && _providerConfig != null)
            {
                var pc = _providerConfig.Resolve(explicitProvider);
                return (new QualityResolution(
                    string.IsNullOrWhiteSpace(model) ? pc.DefaultModel : model,
                    effort, explicitProvider, pc.Backend, pc.EndpointUrl, pc.ApiKey,
                    ApiKeyDestination: pc.ApiKeyDestination), null);
            }
            return (new QualityResolution(model, effort, explicitProvider), null);
        }

        if (_quality == null)
            return (new QualityResolution(null, effort, explicitProvider),
                Error(503, "quality_catalog_unavailable", "The quality catalog is unavailable; the request was not started"));

        if (!_quality.TryResolveRequested(tier, explicitProvider, out var resolved, out var failure))
            return (new QualityResolution(null, effort, explicitProvider),
                QualityResolutionError(tier, explicitProvider, failure));

        return (new QualityResolution(
            resolved!.Model,
            string.IsNullOrWhiteSpace(effort) ? resolved.Effort : effort,
            explicitProvider ?? resolved.Provider,
            resolved.Backend,
            resolved.EndpointUrl,
            resolved.ApiKey,
            resolved.ThinkingBudget,
            resolved.QualityTier,
            resolved.ApiKeyDestination), null);
    }

    private static IResult QualityResolutionError(
        string qualityTier,
        string? provider,
        QualityResolutionFailure failure)
    {
        return failure switch
        {
            QualityResolutionFailure.UnknownTier => Error(422, "unknown_quality_tier",
                $"Quality tier '{qualityTier}' is not present in the authoritative catalog; the request was not started"),
            QualityResolutionFailure.ModelUnavailable => Error(503, "quality_model_unavailable",
                $"Quality tier '{qualityTier}' has no resolvable model{(string.IsNullOrWhiteSpace(provider) ? "" : $" for provider '{provider}'")}; the request was not started"),
            _ => Error(503, "quality_catalog_unavailable",
                "The quality catalog is unavailable; the request was not started"),
        };
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
            // A dedicated provider entity is registered under its own slug. Shared profiles
            // continue to fall back to their backend ID (for example Muse Spark -> opencode).
            var backendName = backendOverride
                ?? (_providerConfig?.Resolve(providerName).Backend ?? providerName);
            var provider = FindRegisteredSessionProvider(entry, providerName, backendName);
            if (provider != null) return (provider, null);

            var available = string.Join(", ", entry.Providers.Keys);
            return (null, Error(404, "provider_not_found",
                $"Provider '{providerName}' is not loaded for ai-session. Available: {available}"));
        }

        var active = entry.ActiveProvider as ISessionProvider;
        if (active != null) return (active, null);

        return (null, Error(503, "no_providers", "No session providers are registered"));
    }

    internal static ISessionProvider? FindRegisteredSessionProvider(
        CapabilityEntry entry, string reference, string? backendReference)
    {
        if (entry.Providers.TryGetValue(reference, out var exact))
            return exact as ISessionProvider;

        if (!string.IsNullOrWhiteSpace(backendReference)
            && entry.Providers.TryGetValue(backendReference, out var backend))
            return backend as ISessionProvider;

        return null;
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

    private static bool IsAutoResumable(ISessionProvider provider, UnifiedSessionInfo info)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.Resume)
            || info.Status is not (SessionStatus.Stopped or SessionStatus.Error)
            || string.IsNullOrWhiteSpace(info.ProviderSessionId))
            return false;

        if (SessionStopReasons.IsAutoResumable(info.StopReason))
            return true;

        // OpenCode did not persist stop reasons before planned-restart recovery existed.
        // New explicit stops persist user_stopped, so only legacy null-valued shells enter here.
        return provider.ProviderId.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(info.StopReason);
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

    /// <summary>
    /// The sequence range a transcript page advertises is the exact min/max over the mapped
    /// messages it returns. Clients validate that equality before accepting a page. Storage order
    /// is not sequence order: providers persist a completed text or thinking block at block stop,
    /// after later tool records, so page edges must never stand in for the extremes.
    /// </summary>
    internal static (long? From, long? Through) TranscriptPageSequenceRange(
        IEnumerable<UnifiedMessageRecord> messages,
        string? epoch)
    {
        long? from = null;
        long? through = null;
        foreach (var message in messages)
        {
            if (message.Sequence is not { } sequence
                || !string.Equals(message.Epoch, epoch, StringComparison.Ordinal))
                continue;
            from = from is { } min ? Math.Min(min, sequence) : sequence;
            through = through is { } max ? Math.Max(max, sequence) : sequence;
        }
        return (from, through);
    }

    private sealed record OwnedSessionJob(
        UnifiedSessionInfo Info, Guid JobId, JobRecord Job, string? OwnerAgentId);

    /// <summary>
    /// Resolves a session's linked job for a caller who is its verified owning Agent acting for the
    /// job's beneficiary user. Every other caller sees 404 so ownership never leaks.
    /// </summary>
    private static async Task<(OwnedSessionJob? Owned, IResult? Failure)> ResolveOwnedSessionJobAsync(
        HttpContext ctx, string id, IJobTracker jobTracker)
    {
        UnifiedSessionInfo? info;
        try { (info, _) = await _redLeafReader!.GetSessionInfoAsync(id); }
        catch (Exception ex)
        {
            return (null, Error(503, "redleaf_unavailable", $"RedLeaf is required for session reads: {ex.Message}"));
        }
        if (info?.JobId is not { } jobId)
            return (null, Error(404, "not_found", $"Session '{id}' not found"));
        var job = jobTracker.GetJob(jobId);
        if (job?.CreationProvenance is not { } provenance)
            return (null, Error(404, "not_found", $"Session '{id}' not found"));

        var execution = ConfidentialResourcePolicy.ExecutionIdentity(ctx);
        var actorId = execution?.Actor.EntityId ?? execution?.Actor.Id;
        var ownerAgentId = provenance.Actor.EntityId ?? provenance.Actor.Id;
        if (execution is null
            || !execution.Actor.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actorId, ownerAgentId, StringComparison.OrdinalIgnoreCase)
            || !execution.Beneficiary.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(execution.Beneficiary.Id, job.UserId,
                StringComparison.OrdinalIgnoreCase))
            return (null, Error(404, "not_found", $"Session '{id}' not found"));

        return (new OwnedSessionJob(info, jobId, job, ownerAgentId), null);
    }

    private static IResult Error(int status, string error, string message)
        => Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: status);

    /// <summary>
    /// The execute result document. <c>isolation</c> is what the provider reports it applied to this
    /// call (<see cref="SessionIsolation"/>); a provider that reports nothing is <c>unverified</c>.
    /// </summary>
    internal static string ExecuteResultJson(SessionExecuteResult result)
        => JsonSerializer.Serialize(new
        {
            success = result.Success, text = result.Text, streamOutput = result.StreamOutput,
            model = result.Model, inputTokens = result.InputTokens,
            outputTokens = result.OutputTokens, costUsd = result.CostUsd, error = result.Error,
            isolation = result.Isolation ?? SessionIsolation.Unverified,
            isolationMechanism = result.IsolationMechanism,
        });

    internal static string ExecuteFailureResultJson(string? model, string error)
        => JsonSerializer.Serialize(new
        {
            success = false,
            text = (string?)null,
            streamOutput = (string?)null,
            model,
            inputTokens = 0,
            outputTokens = 0,
            costUsd = 0d,
            error,
        });

    private static async Task<object> PublicQueueItemAsync(
        SessionInputQueueItem item, string ownerUserId, CancellationToken ct)
    {
        var attachments = new List<StagedInputAttachment>();
        foreach (var attachmentId in item.AttachmentIds)
        {
            var attachment = await _attachmentStore!.GetAuthorizedAsync(attachmentId, ownerUserId, ct);
            if (attachment is not null) attachments.Add(attachment);
        }
        return PublicQueueItem(item, attachments);
    }

    private static object PublicQueueItem(
        SessionInputQueueItem item, IEnumerable<StagedInputAttachment>? attachments = null) => new
    {
        id = item.Id,
        clientId = item.ClientId,
        sessionId = item.SessionId,
        sequence = item.Sequence,
        state = item.State.ToString().ToLowerInvariant(),
        delivery = item.DeliveryPolicy switch
        {
            SessionInputDeliveryPolicy.InterruptCurrent => "interrupt-current",
            SessionInputDeliveryPolicy.SteerCurrent => "steer-current",
            _ => "after-current",
        },
        input = item.Input.Select(part => part.Type == "text"
            ? (object)new { type = "text", text = part.Value }
            : new { type = "attachment", attachmentId = part.Value }),
        displayContent = item.DisplayContent,
        metadata = item.MetadataJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(item.MetadataJson),
        deploymentVerification = ObserveDeploymentVerification(item),
        messageUid = item.MessageUid,
        attachmentIds = item.AttachmentIds,
        attachments = (attachments ?? []).Select(PublicAttachment).ToArray(),
        createdAt = item.CreatedAt.ToString("O"),
        updatedAt = item.UpdatedAt.ToString("O"),
        attemptCount = item.AttemptCount,
        nextAttemptAt = item.NextAttemptAt?.ToString("O"),
        error = item.ErrorCode is null ? null : new
        {
            code = item.ErrorCode,
            message = item.ErrorMessage,
            retryable = item.ErrorRetryable,
        },
        deliveredMessageUid = item.DeliveredMessageUid,
        completedAt = item.CompletedAt?.ToString("O"),
    };

    private static object? ObserveDeploymentVerification(SessionInputQueueItem item)
    {
        var target = DeploymentVerificationTarget.FromMetadata(item.MetadataJson);
        if (target is null) return null;
        var observation = item.ErrorCode == "deployment_target_superseded"
            ? new DeploymentVerificationState("superseded", item.ErrorCode)
            : _maintenance?.ReadVerificationTarget(target) ?? new("unknown", "deployment_target_unknown");
        return new { target, observation.State, observation.ErrorCode };
    }

    private static readonly object DeploymentVerificationTargetSchema = new {
        type = "object", required = new[] { "service", "runId" },
        properties = new {
            service = new { type = "string", @enum = new[] { "redleaf", "redcompute" } },
            runId = new { type = "string", maxLength = 100, pattern = "^[A-Za-z0-9-]+$" } },
    };

    private static IResult QueueError(SessionInputQueueStoreException ex) => ex.Code switch
    {
        "forbidden" => Error(403, ex.Code, ex.Message),
        "attachment_not_found" or "attachment_missing" => Error(404, ex.Code, ex.Message),
        "attachment_expired" => Error(410, ex.Code, ex.Message),
        "idempotency_conflict" or "attachment_already_claimed" or "delivery_in_progress"
            or "not_retryable_state" => Error(409, ex.Code, ex.Message),
        "attachments_too_large" or "attachment_too_large" => Error(413, ex.Code, ex.Message),
        _ => Error(422, ex.Code, ex.Message),
    };

    private static readonly object QueueSummarySchema = new
    {
        type = "object",
        required = new[] { "depth", "state" },
        properties = new
        {
            depth = new { type = "integer" },
            state = new { type = "string", @enum = new[] { "empty", "ready", "delivering", "waiting_for_session", "failed" } },
            blockedReason = new { type = new[] { "string", "null" } },
            headItemId = new { type = new[] { "string", "null" } },
            errorCode = new { type = new[] { "string", "null" } },
        },
    };

    private static readonly object TranscriptPageResponseSchema = new
    {
        type = "object",
        required = new[] { "session", "messages", "page" },
        properties = new
        {
            session = new
            {
                type = "object",
                properties = new
                {
                    supportsSteering = new { type = "boolean" },
                    backgroundTasks = new
                    {
                        type = "array",
                        description = "Live background work (subagents, background shells) still running. Never makes the session Active.",
                        items = BackgroundTaskSchema,
                    },
                },
            },
            messages = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        eventType = new { type = "string", description = "text, thinking, tool_use, tool_result, status, error, image, or task (a background task's start/terminal state; toolInput holds it)" },
                        messageUid = new { type = new[] { "string", "null" }, description = "Logical turn. Background children keep the launching turn's uid." },
                        parentToolUseId = new { type = new[] { "string", "null" }, description = "Launching tool call of the background work this record belongs to; null for the main loop" },
                        turnOrigin = new
                        {
                            type = new[] { "object", "null" },
                            description = "Why a turn started without user input; set on its first record and closing status",
                            properties = new { kind = new { type = "string", @enum = new[] { "background-task" } }, taskId = new { type = "string" } },
                        },
                    },
                },
            },
            page = new
            {
                type = "object",
                required = new[]
                {
                    "epoch", "direction", "hasEarlier", "hasLater",
                    "fromSequence", "throughSequence", "boundaryComplete",
                },
                properties = new
                {
                    epoch = new { type = "string" },
                    direction = new { type = "string", @enum = new[] { "newest", "before", "after" } },
                    oldestCursor = new { type = new[] { "string", "null" } },
                    newestCursor = new { type = new[] { "string", "null" } },
                    hasEarlier = new { type = "boolean" },
                    hasLater = new { type = "boolean" },
                    fromSequence = new { type = new[] { "integer", "null" } },
                    throughSequence = new { type = new[] { "integer", "null" } },
                    boundaryComplete = new { type = "boolean" },
                },
            },
        },
    };

    private const string WorkStateDescription =
        "Each session carries workState, derived in one place from the turn status, parked questions and background tasks: " +
        "working (a turn is running), awaiting_answer (the turn is parked on a question), background (the turn ended Idle while background tasks still run), " +
        "idle (nothing running). status stays the turn state. Only Claude Code sessions have background tasks; Codex has no background work concept, " +
        "so a Codex session never reports background.";

    private static readonly object WorkStateSchema = new
    {
        type = new[] { "string", "null" },
        @enum = new[] { SessionWorkStates.Working, SessionWorkStates.AwaitingAnswer, SessionWorkStates.Background, SessionWorkStates.Idle },
        description = WorkStateDescription,
    };

    private static readonly object PendingQuestionSchema = new
    {
        type = "object",
        required = new[] { "requestId", "toolName", "questions", "input", "askedAt" },
        properties = new
        {
            requestId = new { type = "string", description = "Correlation id for POST /ai-session/sessions/{id}/question" },
            toolName = new { type = "string", description = "AskUserQuestion (Claude) or requestUserInput (Codex)" },
            toolUseId = new { type = new[] { "string", "null" }, description = "Transcript tool call or item carrying the question" },
            questions = new { type = "array", items = new { type = "object" }, description = "Questions with their options, verbatim from the provider" },
            input = new { type = "object", description = "The provider's original question input, verbatim" },
            askedAt = new { type = "string", format = "date-time" },
            expiresAt = new { type = new[] { "string", "null" }, format = "date-time", description = "When the provider stops waiting and proceeds without an answer; null when it waits indefinitely. Claude: asked + 30 minutes, then auto-denied. Codex: always null." },
        },
    };

    private static readonly object SessionDetailResponseSchema = new
    {
        type = "object",
        required = new[] { "session", "messages", "inputQueue", "transcript", "pendingQuestions" },
        properties = new
        {
            session = new { type = "object", properties = new { workState = WorkStateSchema } },
            messages = new { type = "array", items = new { type = "object" } },
            inputQueue = new { type = "object" },
            transcript = new { type = "object" },
            pendingQuestions = new
            {
                type = "array",
                description = "Live control state, not transcript: questions the session is parked on now. Empty when none.",
                items = PendingQuestionSchema,
            },
        },
    };

    private static readonly object BackgroundTaskSchema = new
    {
        type = "object",
        required = new[] { "taskId", "kind", "status", "startedAt" },
        properties = new
        {
            taskId = new { type = "string" },
            kind = new { type = "string", @enum = new[] { "agent", "shell", "other" } },
            status = new { type = "string", @enum = new[] { "running", "completed", "failed", "stopped", "lost" } },
            description = new { type = new[] { "string", "null" } },
            agentType = new { type = new[] { "string", "null" } },
            summary = new { type = new[] { "string", "null" } },
            usage = new
            {
                type = new[] { "object", "null" },
                properties = new
                {
                    toolUses = new { type = new[] { "integer", "null" } },
                    tokens = new { type = new[] { "integer", "null" } },
                    durationMs = new { type = new[] { "integer", "null" } },
                },
            },
            startedAt = new { type = "string", format = "date-time" },
            endedAt = new { type = new[] { "string", "null" }, format = "date-time" },
            lastActivity = new { type = new[] { "string", "null" } },
            lastToolName = new { type = new[] { "string", "null" } },
            parentToolUseId = new { type = new[] { "string", "null" }, description = "Tool call that launched the task" },
            messageUid = new { type = new[] { "string", "null" }, description = "Turn that launched the task" },
            parentTaskId = new { type = new[] { "string", "null" } },
        },
    };

    internal static string CreateConversationWorkspace()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute",
            "conversation-sessions");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static readonly object QueueItemResponseSchema = new
    {
        type = "object",
        required = new[] { "id", "sessionId", "sequence", "state", "delivery", "input", "displayContent", "messageUid", "createdAt", "updatedAt", "attemptCount" },
        properties = new
        {
            id = new { type = "string" },
            clientId = new { type = new[] { "string", "null" }, description = "Authorized client idempotency key used to reconcile an uncertain admission" },
            sessionId = new { type = "string" },
            sequence = new { type = "integer" },
            state = new { type = "string", @enum = new[] { "pending", "delivering", "failed", "delivered", "cancelled" } },
            delivery = new { type = "string", @enum = new[] { "after-current", "steer-current", "interrupt-current" } },
            input = new { type = "array", items = new { type = "object" } },
            displayContent = new { type = "string" },
            metadata = new { type = new[] { "object", "null" } },
            messageUid = new { type = "string" },
            attachmentIds = new { type = "array", items = new { type = "string" } },
            attachments = new { type = "array", items = new { type = "object" }, description = "Authorized staged attachment metadata for queue UIs" },
            createdAt = new { type = "string", format = "date-time" },
            updatedAt = new { type = "string", format = "date-time" },
            attemptCount = new { type = "integer" },
            nextAttemptAt = new { type = new[] { "string", "null" }, format = "date-time" },
            error = new { type = new[] { "object", "null" } },
            deliveredMessageUid = new { type = new[] { "string", "null" } },
            completedAt = new { type = new[] { "string", "null" }, format = "date-time" },
        },
    };

    private static readonly object QueueListResponseSchema = new
    {
        type = "object",
        required = new[] { "items", "queue" },
        properties = new
        {
            items = new { type = "array", items = QueueItemResponseSchema },
            queue = QueueSummarySchema,
        },
    };

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

}
