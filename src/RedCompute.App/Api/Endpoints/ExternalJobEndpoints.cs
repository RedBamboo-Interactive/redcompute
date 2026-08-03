using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Api.Endpoints;

/// <summary>
/// Durable lifecycle envelope for work executed by a trusted suite service. Compute
/// records and audits the attempt; the caller leases it and performs the side effect.
/// This is the correct path for automations, backups, maintenance, and connectors that
/// are jobs but are not generation-provider work.
/// </summary>
public static partial class ExternalJobEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9:._-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyScopePattern();

    public static void Map(EndpointRegistry endpoints, JobTrackingService jobs)
    {
        endpoints.MapPost("/jobs/external/backfill",
            "Idempotently import one terminal historical suite execution with its original timestamps.",
            async (HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                var body = await ReadAsync<ExternalJobBackfillRequest>(ctx);
                if (body is null || body.Id == Guid.Empty)
                    return Results.BadRequest(new { error = "invalid_request", message = "A deterministic id is required" });
                if (!Enum.TryParse<JobStatus>(body.Status, true, out var status)
                    || !JobTrackingService.IsTerminal(status))
                    return Results.BadRequest(new { error = "invalid_status", message = "A terminal status is required" });
                if (body.QueuedAt == default || body.CompletedAt == default
                    || body.CompletedAt < body.QueuedAt
                    || body.StartedAt is { } started && (started < body.QueuedAt || started > body.CompletedAt))
                    return Results.BadRequest(new { error = "invalid_timeline", message = "Historical timestamps are missing or out of order" });

                var capability = string.IsNullOrWhiteSpace(body.Capability)
                    ? "automation"
                    : body.Capability.Trim();
                if (!CapabilityPattern().IsMatch(capability))
                    return Results.BadRequest(new
                    {
                        error = "invalid_capability",
                        message = "capability must be a lowercase slug",
                    });

                JobProvenance provenance;
                try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/jobs/external/backfill"); }
                catch (JobProvenanceValidationException ex)
                { return Results.UnprocessableEntity(new { error = "invalid_provenance", message = ex.Message }); }
                if (provenance.Assurance is not
                    (JobProvenanceAssurance.BackfilledExact or JobProvenanceAssurance.BackfilledInferred))
                    return Results.UnprocessableEntity(new
                    {
                        error = "invalid_assurance",
                        message = "Historical imports require backfilled-exact or backfilled-inferred assurance",
                    });

                try
                {
                    var input = body.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? "{}" : body.Input.GetRawText();
                    var job = new JobRecord
                    {
                        Id = body.Id,
                        CapabilitySlug = capability,
                        ProviderName = string.IsNullOrWhiteSpace(body.Provider)
                            ? "Legacy suite automation" : body.Provider.Trim(),
                        Status = status,
                        QueuedAt = body.QueuedAt,
                        StartedAt = body.StartedAt ?? body.QueuedAt,
                        CompletedAt = body.CompletedAt,
                        InputJson = input,
                        ResultJson = body.ResultJson,
                        ErrorMessage = body.Error,
                        ErrorDetails = body.ErrorDetails,
                        OutputLocation = body.OutputLocation,
                        OutputSizeBytes = body.OutputSizeBytes,
                        OutputContentType = body.ContentType,
                        CostUsd = body.CostUsd,
                        CallerInfo = body.CallerInfo,
                        IdempotencyKey = body.IdempotencyKey ?? $"external-history:{body.Id:N}",
                        Name = body.Name,
                        Rationale = body.Rationale,
                        CreationProvenance = provenance,
                        ExternalExecution = true,
                        AttemptCount = 1,
                    };
                    var restored = jobs.BackfillExternalJob(job, body.ScheduledFor, body.Source ?? "redleaf-history");
                    return Results.Json(ToAttempt(restored, restored.IsIdempotencyReuse),
                        statusCode: restored.IsIdempotencyReuse
                            ? StatusCodes.Status200OK : StatusCodes.Status201Created);
                }
                catch (IdempotencyConflictException ex)
                {
                    return Results.Conflict(new
                    {
                        error = "idempotency_conflict",
                        message = ex.Message,
                        existingJobId = ex.ExistingJobId,
                    });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = "invalid_request", message = ex.Message });
                }
            });

        endpoints.MapPost("/jobs/external",
            "Create or idempotently reuse a Compute job whose work is performed by the authenticated RedLeaf service.",
            async (HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                ExternalJobCreateRequest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<ExternalJobCreateRequest>(ctx.Request.Body,
                        JobProvenance.JsonOptions, ctx.RequestAborted);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = "invalid_json", message = ex.Message });
                }
                if (body is null)
                    return Results.BadRequest(new { error = "invalid_request", message = "A JSON body is required" });

                var capability = string.IsNullOrWhiteSpace(body.Capability) ? "automation" : body.Capability.Trim();
                if (!CapabilityPattern().IsMatch(capability))
                    return Results.BadRequest(new { error = "invalid_capability", message = "capability must be a lowercase slug" });
                var idempotencyScope = string.IsNullOrWhiteSpace(body.IdempotencyScope)
                    ? null : body.IdempotencyScope.Trim();
                if (idempotencyScope is not null && !IdempotencyScopePattern().IsMatch(idempotencyScope))
                    return Results.BadRequest(new
                    {
                        error = "invalid_idempotency_scope",
                        message = "idempotencyScope must be a lowercase logical namespace using letters, digits, colon, dot, underscore, or hyphen",
                    });

                JobProvenance provenance;
                try { provenance = await ProvenanceCapture.ResolveAsync(ctx, "/jobs/external"); }
                catch (JobProvenanceValidationException ex)
                {
                    return Results.UnprocessableEntity(new { error = "invalid_provenance", message = ex.Message });
                }

                try
                {
                    var input = body.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? "{}" : body.Input.GetRawText();
                    var job = jobs.CreateJob(new JobSubmission(
                        capability,
                        string.IsNullOrWhiteSpace(body.Provider) ? "External suite worker" : body.Provider.Trim(),
                        input,
                        provenance,
                        body.CallerInfo,
                        body.IdempotencyKey,
                        body.Name,
                        body.Rationale,
                        ExternalExecution: true,
                        IdempotencyScope: idempotencyScope));

                    if (!job.IsIdempotencyReuse)
                        jobs.AppendJobEvent(job.Id, JobEventKind.Scheduled, new
                        {
                            scheduledFor = body.ScheduledFor,
                            trigger = body.Trigger,
                            definitionVersion = body.DefinitionVersion,
                        }, provenance);

                    return Results.Json(ToAttempt(job, job.IsIdempotencyReuse),
                        statusCode: job.IsIdempotencyReuse ? StatusCodes.Status200OK : StatusCodes.Status201Created);
                }
                catch (IdempotencyConflictException ex)
                {
                    return Results.Conflict(new
                    {
                        error = "idempotency_conflict",
                        message = ex.Message,
                        existingJobId = ex.ExistingJobId,
                    });
                }
            });

        endpoints.MapPost("/jobs/{id:guid}/claim",
            "Atomically lease an externally executed job. An expired lease can be reclaimed as a retry of the same attempt.",
            async (Guid id, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                var body = await ReadAsync<ClaimRequest>(ctx);
                if (body is null || string.IsNullOrWhiteSpace(body.WorkerId))
                    return Results.BadRequest(new { error = "invalid_request", message = "workerId is required" });
                try
                {
                    var provenance = await ProvenanceCapture.ResolveAsync(ctx, "/jobs/{id}/claim");
                    var lease = jobs.ClaimExternalJob(id, body.WorkerId,
                        TimeSpan.FromSeconds(body.LeaseSeconds ?? 300), provenance);
                    return Results.Ok(lease);
                }
                catch (KeyNotFoundException ex) { return Results.NotFound(new { error = "not_found", message = ex.Message }); }
                catch (JobLeaseConflictException ex) { return Results.Conflict(new { error = "lease_conflict", message = ex.Message }); }
                catch (JobProvenanceValidationException ex) { return Results.UnprocessableEntity(new { error = "invalid_provenance", message = ex.Message }); }
            });

        endpoints.MapPost("/jobs/{id:guid}/lease/renew",
            "Renew the current external worker lease.",
            async (Guid id, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                if (!TryLease(ctx, out var token)) return MissingLease();
                var body = await ReadAsync<RenewRequest>(ctx);
                try
                {
                    var provenance = await ProvenanceCapture.ResolveAsync(ctx, "/jobs/{id}/lease/renew");
                    return Results.Ok(jobs.RenewExternalLease(id, token,
                        TimeSpan.FromSeconds(body?.LeaseSeconds ?? 300), provenance));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or JobLeaseValidationException)
                { return LeaseError(ex); }
            });

        endpoints.MapPost("/jobs/{id:guid}/progress",
            "Append meaningful external-work progress and update the job projection.",
            async (Guid id, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                if (!TryLease(ctx, out var token)) return MissingLease();
                var body = await ReadAsync<ProgressRequest>(ctx);
                if (body is null) return Results.BadRequest(new { error = "invalid_request" });
                try
                {
                    jobs.ReportExternalProgress(id, token, body.Progress, body.Message,
                        body.Detail.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : body.Detail);
                    return Results.Ok(new { id, progress = Math.Clamp(body.Progress, 0, 1) });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or JobLeaseValidationException)
                { return LeaseError(ex); }
            });

        endpoints.MapPost("/jobs/{id:guid}/retry",
            "Append a durable retry decision for an externally executed job.",
            async (Guid id, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                if (!TryLease(ctx, out var token)) return MissingLease();
                var body = await ReadAsync<RetryRequest>(ctx);
                if (body is null || body.RetryNumber < 1)
                    return Results.BadRequest(new { error = "invalid_request", message = "retryNumber must be positive" });
                try
                {
                    jobs.ReportExternalRetry(id, token, body.RetryNumber,
                        TimeSpan.FromSeconds(Math.Max(0, body.DelaySeconds)), body.Error);
                    return Results.Ok(new { id, body.RetryNumber, body.DelaySeconds });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or JobLeaseValidationException)
                { return LeaseError(ex); }
            });

        endpoints.MapPost("/jobs/{id:guid}/children/{childId:guid}",
            "Append an immutable owned-child link, or use relation 'reference' for a pre-existing job shared by multiple attempts.",
            async (Guid id, Guid childId, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                if (!TryLease(ctx, out var token)) return MissingLease();
                var body = await ReadAsync<ChildRequest>(ctx);
                try
                {
                    jobs.LinkExternalChild(id, token, childId, body?.Relation ?? "child");
                    return Results.Ok(new { id, childJobId = childId, relation = body?.Relation ?? "child" });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or JobLeaseValidationException)
                { return LeaseError(ex); }
            });

        MapTerminal(endpoints, jobs, "complete", JobStatus.Completed);
        MapTerminal(endpoints, jobs, "fail", JobStatus.Failed);
        MapTerminal(endpoints, jobs, "skip", JobStatus.Skipped);
        MapTerminal(endpoints, jobs, "timeout", JobStatus.TimedOut);
        MapTerminal(endpoints, jobs, "cancel", JobStatus.Cancelled);
    }

    private static void MapTerminal(EndpointRegistry endpoints, JobTrackingService jobs,
        string route, JobStatus status)
    {
        endpoints.MapPost($"/jobs/{{id:guid}}/{route}",
            $"Close an externally executed job as {status}.",
            async (Guid id, HttpContext ctx) =>
            {
                if (!ProvenanceCapture.IsTrustedRedLeafService(ctx)) return Forbidden();
                if (!TryLease(ctx, out var token)) return MissingLease();
                var body = await ReadAsync<TerminalRequest>(ctx);
                try
                {
                    switch (status)
                    {
                        case JobStatus.Completed:
                            jobs.CompleteExternalJob(id, token, body?.ResultJson, body?.OutputLocation,
                                body?.OutputSizeBytes, body?.ContentType, body?.CostUsd);
                            break;
                        case JobStatus.Failed:
                            jobs.FailExternalJob(id, token, body?.Error ?? "External work failed",
                                body?.ErrorDetails, body?.ResultJson);
                            break;
                        case JobStatus.Skipped:
                            jobs.SkipExternalJob(id, token, body?.Reason ?? "Skipped", body?.ResultJson);
                            break;
                        case JobStatus.TimedOut:
                            jobs.TimeoutExternalJob(id, token, body?.Reason ?? "Timed out", body?.ResultJson);
                            break;
                        case JobStatus.Cancelled:
                            jobs.CancelExternalJob(id, token, body?.Reason);
                            break;
                    }
                    return Results.Ok(new { id, status = status.ToString() });
                }
                catch (Exception ex) when (ex is KeyNotFoundException or JobLeaseValidationException)
                { return LeaseError(ex); }
            });
    }

    private static object ToAttempt(JobRecord job, bool reused) => new
    {
        jobId = job.Id,
        capability = job.CapabilitySlug,
        status = job.Status.ToString(),
        reused,
        job.ExternalExecution,
        job.ParentJobId,
        job.AttemptCount,
        job.LeaseOwner,
        job.LeaseExpiresAt,
    };

    private static async Task<T?> ReadAsync<T>(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return default;
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body,
                JobProvenance.JsonOptions, ctx.RequestAborted);
        }
        catch (JsonException) { return default; }
    }

    private static bool TryLease(HttpContext ctx, out string token)
    {
        token = ctx.Request.Headers["X-Job-Lease"].FirstOrDefault() ?? "";
        return !string.IsNullOrWhiteSpace(token);
    }

    private static IResult Forbidden() => Results.Json(new
        { error = "forbidden", message = "Only the authenticated RedLeaf service may manage external jobs" },
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult MissingLease() => Results.BadRequest(new
        { error = "lease_required", message = "X-Job-Lease is required" });

    private static IResult LeaseError(Exception ex) => ex is KeyNotFoundException
        ? Results.NotFound(new { error = "not_found", message = ex.Message })
        : Results.Conflict(new { error = "invalid_lease", message = ex.Message });

    private sealed record ExternalJobCreateRequest(
        string? Capability,
        string? Provider,
        JsonElement Input,
        string? CallerInfo,
        string? IdempotencyKey,
        string? IdempotencyScope,
        string? Name,
        string? Rationale,
        DateTimeOffset? ScheduledFor,
        string? Trigger,
        string? DefinitionVersion);

    private sealed record ExternalJobBackfillRequest(
        Guid Id,
        string? Capability,
        string? Provider,
        JsonElement Input,
        string Status,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset CompletedAt,
        DateTimeOffset? ScheduledFor,
        string? ResultJson,
        string? Error,
        string? ErrorDetails,
        string? OutputLocation,
        long? OutputSizeBytes,
        string? ContentType,
        double? CostUsd,
        string? CallerInfo,
        string? IdempotencyKey,
        string? Name,
        string? Rationale,
        string? Source);

    private sealed record ClaimRequest(string WorkerId, int? LeaseSeconds);
    private sealed record RenewRequest(int? LeaseSeconds);
    private sealed record ProgressRequest(double Progress, string? Message, JsonElement Detail);
    private sealed record RetryRequest(int RetryNumber, double DelaySeconds, string? Error);
    private sealed record ChildRequest(string? Relation);
    private sealed record TerminalRequest(
        string? Reason,
        string? Error,
        string? ErrorDetails,
        string? ResultJson,
        string? OutputLocation,
        long? OutputSizeBytes,
        string? ContentType,
        double? CostUsd);
}
