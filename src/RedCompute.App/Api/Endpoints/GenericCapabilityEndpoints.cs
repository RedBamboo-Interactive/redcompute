using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class GenericCapabilityEndpoints
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    private static HardwareMonitorService? _hardwareMonitor;
    private static RedComputeConfig? _config;
    private static CapabilityRegistry? _registry;

    public static void Map(WebApplication app, EndpointRegistry endpoints, CapabilityRegistry registry,
        JobTrackingService jobTracker, Action<string, Guid?> log,
        HardwareMonitorService? hardwareMonitor = null, RedComputeConfig? config = null)
    {
        _hardwareMonitor = hardwareMonitor;
        _config = config;
        _registry = registry;
        Directory.CreateDirectory(OutputDir);

        foreach (var (capSlug, capability) in registry.Capabilities)
        {
            if (capSlug == "ai-session" || capability.IsExternal) continue;

            var slug = capSlug;

            // POST /{slug}/generate — universal work endpoint
            var generateEndpoint = endpoints.MapPost($"/{slug}/generate",
                $"Generate {slug} output via the active provider. Body parameters come from the provider's input schema (see the capability entry in /discover).",
                async (HttpContext ctx) =>
            {
                ctx.Items["Telemetry.Kind"] = "job";

                var entry = registry.Get(slug);
                if (entry == null)
                    return Error(503, "provider_not_configured", $"Capability '{slug}' is not configured");

                if (entry.IsSleeping)
                    return Error(503, "capability_sleeping", $"'{slug}' is sleeping. Wake it via POST /control/wake/{slug}");

                var (body, bodyError) = await ReadJsonBody(ctx);
                if (bodyError is not null)
                    return Error(400, "invalid_json", bodyError);

                var requestedProvider = ProviderResolver.GetRequestedProvider(ctx, body);
                var (provider, providerError) = ProviderResolver.Resolve(entry, requestedProvider, slug);
                if (providerError != null) return providerError;
                if (provider == null)
                    return Error(503, "provider_not_configured", $"No provider configured for '{slug}'");

                ProviderResolver.StripProviderFromBody(body);
                var jobName = body.GetValueOrDefault("name")?.ToString()
                    ?? ctx.Request.Headers["X-Job-Name"].FirstOrDefault();
                var jobRationale = body.GetValueOrDefault("rationale")?.ToString()
                    ?? ctx.Request.Headers["X-Job-Rationale"].FirstOrDefault();
                body.Remove("name");
                body.Remove("rationale");



                // Validate against provider's declared schema
                if (provider is IPluginProvider plugin)
                {
                    var validationErrors = ValidateAgainstSchema(body, plugin.InputParameters);
                    var customErrors = plugin.ValidateParameters(body);
                    foreach (var kv in customErrors) validationErrors[kv.Key] = kv.Value;

                    if (validationErrors.Count > 0)
                        return Results.Json(new ErrorResponse
                        {
                            Error = "validation_failed",
                            Message = "One or more parameters are invalid",
                            Fields = validationErrors
                        }, statusCode: 422);
                }

                var status = await provider.GetStatusAsync();
                if (status != BackendStatus.Running)
                    return Error(503, "provider_not_running", $"Backend for '{slug}' is {status}. Start via POST /control/start/{slug}");

                var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

                JobProvenance provenance;
                try
                {
                    provenance = await ProvenanceCapture.ResolveAsync(ctx, $"/{slug}/generate");
                }
                catch (JobProvenanceValidationException ex)
                {
                    return Error(422, "invalid_provenance", ex.Message);
                }

                JobRecord job;
                try
                {
                    job = jobTracker.CreateJob(new JobSubmission(
                        slug, provider.Name, JsonSerializer.Serialize(body), provenance,
                        idempotencyKey, jobName, jobRationale));
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

                if (job.IsIdempotencyReuse)
                    return Results.Json(new { jobId = job.Id, status = job.Status.ToString(), idempotentReuse = true },
                        statusCode: job.Status is JobStatus.Running or JobStatus.Queued ? 202 : 200);

                jobTracker.StartInvocation(job.Id, provenance,
                    provenance.Trace.ParentJobId != null ? JobEventKind.Rerun : JobEventKind.Started);

                var firstParam = body.GetValueOrDefault("prompt")?.ToString()
                    ?? body.GetValueOrDefault("text")?.ToString() ?? "";
                log($"[{slug}] Job {job.Id} started: \"{Truncate(firstParam, 60)}\"", job.Id);

                // Set up progress tracking
                if (provider is IPluginProvider pp && pp.SupportsProgress)
                    pp.SetProgressCallback(frac => jobTracker.UpdateProgress(job.Id, frac));

                var isAsync = IsAsyncRequested(ctx);

                // Check if this is a proxy provider
                var isProxy = provider is IPluginProvider px && px.IsProxy;
                var proxyUrl = provider.GetProxyTargetUrl();

                if (isProxy && proxyUrl != null)
                {
                    var backendPath = "/generate";
                    if (provider is IPluginProvider proxyPlugin)
                    {
                        backendPath = proxyPlugin.ProxyGeneratePath;
                        body = proxyPlugin.TransformParameters(body);

                        var prepareError = await proxyPlugin.PrepareAsync(body, proxyUrl, ctx.RequestAborted);
                        if (prepareError != null)
                        {
                            jobTracker.TryMarkFailedUnlessCancelled(job.Id, prepareError);
                            log($"[{slug}] Job {job.Id} prepare failed: {prepareError}", job.Id);
                            return Error(422, "prepare_failed", prepareError);
                        }
                    }
                    try
                    {
                        var (data, contentType) = await StreamingProxy.FetchFromPathAsync(ctx, proxyUrl, backendPath, body);
                        var outputPath = SaveOutput(job.Id, data, contentType);
                        var size = new FileInfo(outputPath).Length;
                        jobTracker.TryMarkCompletedUnlessCancelled(job.Id, outputPath, size, contentType);
                        if (_registry != null)
                        {
                            var proxyCost = EstimateJobCost(jobTracker.GetJob(job.Id)!, _registry);
                            if (proxyCost.HasValue) jobTracker.SetJobCost(job.Id, proxyCost.Value);
                        }
                        log($"[{slug}] Job {job.Id} completed ({size / 1024}KB)", job.Id);

                        data.Position = 0;
                        ctx.Response.ContentType = contentType;
                        ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                        ctx.Response.ContentLength = data.Length;
                        await data.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                        await data.DisposeAsync();
                        return Results.Empty;
                    }
                    catch (HttpRequestException ex)
                    {
                        jobTracker.TryMarkFailedUnlessCancelled(job.Id, ex.Message, ex.ToString());
                        return Error(502, "backend_unavailable", $"Backend connection failed: {ex.Message}");
                    }
                    catch (TaskCanceledException)
                    {
                        jobTracker.MarkCancelled(job.Id);
                        return Results.Empty;
                    }
                }

                var request = new JobRequest
                {
                    JobId = job.Id,
                    QueuedAt = job.QueuedAt,
                    InvocationStartedAt = jobTracker.GetJob(job.Id)?.StartedAt,
                    CapabilitySlug = slug,
                    Parameters = body,
                    IdempotencyKey = idempotencyKey,
                    Provenance = provenance,
                };

                if (isAsync)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await provider.ExecuteAsync(request);
                            await HandleResult(job.Id, slug, result, jobTracker, log);
                        }
                        catch (OperationCanceledException)
                        {
                            if (jobTracker.GetJob(job.Id)?.Status != JobStatus.Cancelled)
                                jobTracker.MarkCancelled(job.Id);
                        }
                        catch (Exception ex)
                        {
                            jobTracker.TryMarkFailedUnlessCancelled(job.Id, ex.Message, ex.ToString());
                            log($"[{slug}] Job {job.Id} failed: {ex.Message}", job.Id);
                        }
                    });

                    return Results.Json(new { jobId = job.Id, status = "running" }, statusCode: 202);
                }

                // Synchronous execution
                try
                {
                    var result = await provider.ExecuteAsync(request, ctx.RequestAborted);
                    if (result is { Success: true, OutputStream: not null })
                    {
                        await using var outputStream = result.OutputStream;
                        var path = SaveOutput(job.Id, outputStream, result.ContentType);
                        JobArtifactStore.Save(job.Id, OutputDir, path, result.ContentType,
                            result.PrimaryOutputName, result.PrimaryFileName, result.ExtraOutputs);
                        var size = new FileInfo(path).Length;
                        jobTracker.TryMarkCompletedUnlessCancelled(job.Id, path, size, result.ContentType, result.ResultJson);
                        if (_registry != null) { var c = EstimateJobCost(jobTracker.GetJob(job.Id)!, _registry); if (c.HasValue) jobTracker.SetJobCost(job.Id, c.Value); }
                        log($"[{slug}] Job {job.Id} completed ({size / 1024}KB)", job.Id);

                        outputStream.Position = 0;
                        ctx.Response.ContentType = result.ContentType ?? "application/octet-stream";
                        ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                        if (result.ResultJson != null)
                            ctx.Response.Headers["X-Result-Json"] = result.ResultJson;
                        await outputStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                        return Results.Empty;
                    }
                    else if (result is { Success: true, ResultJson: not null })
                    {
                        jobTracker.TryMarkCompletedUnlessCancelled(job.Id, resultJson: result.ResultJson);
                        if (_registry != null) { var c = EstimateJobCost(jobTracker.GetJob(job.Id)!, _registry); if (c.HasValue) jobTracker.SetJobCost(job.Id, c.Value); }
                        ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                        try { return Results.Json(JsonSerializer.Deserialize<object>(result.ResultJson)); }
                        catch { return Results.Text(result.ResultJson, "application/json"); }
                    }
                    else
                    {
                        var errorCode = result?.ErrorCode ?? "generation_failed";
                        var errorStatus = result?.ErrorStatusCode ?? 500;
                        jobTracker.TryMarkFailedUnlessCancelled(job.Id, result?.ErrorMessage ?? "Generation failed", resultJson: result?.ResultJson);
                        return Error(errorStatus, errorCode,
                            result?.ErrorMessage ?? "Generation failed");
                    }
                }
                catch (HttpRequestException ex)
                {
                    jobTracker.TryMarkFailedUnlessCancelled(job.Id, ex.Message, ex.ToString());
                    _ = provider.GetStatusAsync();
                    return Error(502, "backend_unavailable", $"Backend connection failed: {ex.Message}");
                }
                catch (TaskCanceledException)
                {
                    jobTracker.MarkCancelled(job.Id);
                    return Results.Empty;
                }
                catch (Exception ex)
                {
                    jobTracker.TryMarkFailedUnlessCancelled(job.Id, ex.Message, ex.ToString());
                    log($"[{slug}] Job {job.Id} failed: {ex.Message}", job.Id);
                    return Error(500, "generation_failed", ex.Message);
                }
            })
                .WithParam("async", "boolean", description: "Fire-and-forget: returns 202 with a job id instead of streaming the result", location: ParamLocation.Query)
                .WithParam("Authorization", "string", description: "Preferred provenance and authentication: Bearer <signed suite execution token>. AI session processes receive the token in REDLEAF_EXECUTION_TOKEN; send it unchanged, inspect it through GET /auth/execution-context, and verify resulting jobs through GET /jobs?executionId=<id>.", location: ParamLocation.Header)
                .WithParam("X-Async", "string", description: "Set to 'true' as an alternative to ?async", location: ParamLocation.Header)
                .WithParam("X-Compute-Provenance", "string", description: "Legacy fallback for an authenticated RedLeaf service when no signed execution token can be resolved. Never combine it with an execution bearer token.", location: ParamLocation.Header)
                .WithParam("X-System-Reason", "string", description: "Required explicit reason when a direct request has no real user beneficiary", location: ParamLocation.Header)
                .WithParam("X-Idempotency-Key", "string", description: "Dedupe key — repeated requests with the same key reuse the original job", location: ParamLocation.Header)
                .WithParam("X-Job-Name", "string", description: "Human-readable job name (body 'name' takes precedence)", location: ParamLocation.Header)
                .WithParam("X-Job-Rationale", "string", description: "Why the job was queued (body 'rationale' takes precedence)", location: ParamLocation.Header)
                .WithParam("X-Provider", "string", description: "Provider to use for this request (body 'provider' takes precedence)", location: ParamLocation.Header);


            if (capability.ActiveProvider is IPluginProvider structured &&
                structured.RequestSchema is not null && structured.ResponseSchema is not null)
            {
                generateEndpoint.WithRequestBody(structured.RequestSchema)
                    .WithResponse(structured.ResponseSchema);
            }
            // GET /{slug}/jobs/{id}/progress
            endpoints.MapGet($"/{slug}/jobs/{{id:guid}}/progress",
                $"Real-time progress and status of a {slug} job", (Guid id) =>
            {
                var job = jobTracker.GetJob(id);
                if (job == null)
                    return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

                return Results.Ok(new
                {
                    job.Id,
                    status = job.Status.ToString(),
                    job.Progress,
                    job.ErrorMessage,
                    durationMs = job.DurationMs,
                    resultJson = job.ResultJson
                });
            });

            // GET /{slug}/jobs/{id}/output
            endpoints.MapGet($"/{slug}/jobs/{{id:guid}}/output",
                $"Download the output of a completed {slug} job", async (HttpContext ctx, Guid id, string? artifact, int? clip) =>
            {
                var job = jobTracker.GetJob(id);
                if (job == null)
                    return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

                if (job.Status is JobStatus.Running or JobStatus.Queued)
                    return Results.Json(new { error = "not_ready", message = "Job is still running", progress = job.Progress }, statusCode: 409);

                if (job.Status == JobStatus.Failed)
                    return Error(410, "job_failed", job.ErrorMessage ?? "Job failed");

                if (job.OutputLocation == null || !File.Exists(job.OutputLocation))
                {
                    if (job.ResultJson != null)
                        return Results.Text(job.ResultJson, "application/json");
                    return Error(404, "output_not_found", "Output file not available");
                }

                JobArtifactLocation? selected;
                try
                {
                    selected = JobArtifactStore.Resolve(
                        id, OutputDir, job.OutputLocation, job.OutputContentType, artifact, clip);
                }
                catch (InvalidDataException ex)
                {
                    return Error(400, "invalid_artifact", ex.Message);
                }
                if (selected is null)
                    return Error(404, "artifact_not_found", $"Artifact '{artifact ?? (clip.HasValue ? $"clip-{clip}" : "primary")}' is not available for job {id}");

                ctx.Response.ContentType = selected.ContentType;
                if (job.ResultJson != null)
                    ctx.Response.Headers["X-Result-Json"] = job.ResultJson;
                if (!string.IsNullOrWhiteSpace(selected.FileName))
                    ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{selected.FileName.Replace("\"", "", StringComparison.Ordinal)}\"";
                await using var stream = File.OpenRead(selected.Path);
                await stream.CopyToAsync(ctx.Response.Body);
                return Results.Empty;
            })
                .WithParam("artifact", "string",
                    description: "Stable named artifact selector from resultJson. Omit for primary. Named artifacts survive process restarts through a sidecar manifest.",
                    location: ParamLocation.Query)
                .WithParam("clip", "integer",
                    description: "Legacy variation index alias: 0 or omitted = primary clip, 1..N = additional clips. Prefer artifact names from resultJson.",
                    location: ParamLocation.Query);
        }

        // Structured capability metadata and bounded provider diagnostics.
        foreach (var (capSlug, capability) in registry.Capabilities)
        {
            var schemaProvider = capability.ActiveProvider as IPluginProvider;
            if (schemaProvider?.ContractVersion is null ||
                schemaProvider.RequestSchema is null ||
                schemaProvider.ResponseSchema is null)
                continue;

            var slug = capSlug;
            endpoints.MapGet($"/{slug}/contract",
                $"Complete machine-readable {slug} request and response contract", () =>
                Results.Ok(new
                {
                    slug,
                    version = schemaProvider.ContractVersion,
                    request = schemaProvider.RequestSchema,
                    response = schemaProvider.ResponseSchema,
                    notes = new
                    {
                        confidence = "Distribution concentration, not probability of correctness.",
                        actionAuthority = "A decision result is advisory and grants no authority to act.",
                    },
                }));

            endpoints.MapGet($"/{slug}/status",
                $"Detailed readiness and model status for {slug}", async (HttpContext ctx) =>
            {
                var requested = ProviderResolver.GetRequestedProvider(ctx);
                var (provider, error) = ProviderResolver.Resolve(capability, requested, slug);
                if (error is not null) return error;
                if (provider is IProviderStatusDetails details)
                    return Results.Ok(await details.GetStatusDetailsAsync(ctx.RequestAborted));
                return Results.Ok(new
                {
                    provider = provider?.Name,
                    status = provider is null
                        ? BackendStatus.Stopped.ToString()
                        : (await provider.GetStatusAsync(ctx.RequestAborted)).ToString(),
                });
            }).WithParam("X-Provider", "string",
                description: "Provider to inspect", location: ParamLocation.Header);

            endpoints.MapPost($"/{slug}/validate",
                $"Run one synchronous, bounded, job-backed provider validation for {slug}",
                (HttpContext ctx) => RunProviderValidationAsync(
                    ctx, slug, capability, jobTracker, log))
                .WithParam("X-Provider", "string",
                    description: "Provider to validate", location: ParamLocation.Header)
                .WithResponse(ProviderValidationContract.ResponseSchema);
        }

        // Proxy catch-all: /{slug}/{**path} for providers with GetProxyTargetUrl
        foreach (var (capSlug, _) in registry.Capabilities)
        {
            if (capSlug == "ai-session") continue;

            var slug = capSlug;
            endpoints.Describe("ANY", $"/{slug}/{{**path}}",
                $"Proxy passthrough to the active {slug} backend (when the provider exposes one). Known routes (generate, jobs/*) are served directly.");
            app.Map($"/{slug}/{{**path}}", async (HttpContext ctx, string? path) =>
            {
                // Known endpoint patterns (generate, jobs) are handled by the routes above;
                // reaching here means the path didn't match anything proxyable.
                if (path != null && (path.StartsWith("jobs/") || path == "generate"))
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new ErrorResponse
                    {
                        Error = "not_found",
                        Message = $"No endpoint at '/{slug}/{path}'"
                    });
                    return;
                }

                var entry = registry.Get(slug);
                var proxyProvider = entry?.ActiveProvider;
                if (proxyProvider == null) { ctx.Response.StatusCode = 503; return; }

                var proxyUrl = proxyProvider.GetProxyTargetUrl();
                if (proxyUrl == null) { ctx.Response.StatusCode = 503; return; }

                await StreamingProxy.ForwardRawAsync(ctx, proxyUrl, path, log);
            });
        }

        // Let each IPluginProvider with ICustomEndpointProvider register its custom endpoints
        foreach (var (_, entry) in registry.Capabilities)
        {
            foreach (var provider in entry.Providers.Values)
            {
                if (provider is ICustomEndpointProvider customProvider)
                    customProvider.MapCustomEndpoints(app);
            }
        }
    }
    internal static async Task<IResult> RunProviderValidationAsync(
        HttpContext ctx,
        string slug,
        CapabilityEntry capability,
        JobTrackingService jobTracker,
        Action<string, Guid?> log)
    {
        ctx.Items["Telemetry.Kind"] = "job";
        var admissionError = await ValidateValidationBodyAsync(ctx);
        if (admissionError is not null) return admissionError;

        var requested = ProviderResolver.GetRequestedProvider(ctx);
        var (provider, error) = ProviderResolver.Resolve(capability, requested, slug);
        if (error is not null) return error;
        if (provider is not IProviderSelfValidator validator)
            return Error(501, "validation_not_supported",
                $"Provider '{provider?.Name}' does not expose bounded validation");
        if (await provider.GetStatusAsync(ctx.RequestAborted) != BackendStatus.Running)
            return Error(503, "provider_not_running",
                $"Backend for '{slug}' is not running");

        JobProvenance provenance;
        try
        {
            provenance = await ProvenanceCapture.ResolveAsync(ctx, $"/{slug}/validate");
        }
        catch (JobProvenanceValidationException ex)
        {
            return Error(422, "invalid_provenance", ex.Message);
        }

        var inputJson = JsonSerializer.Serialize(new
        {
            operation = "provider-validation",
            route = $"/{slug}/validate",
            contractVersion = (provider as IPluginProvider)?.ContractVersion,
        });
        var job = jobTracker.CreateJob(new JobSubmission(
            slug, provider.Name, inputJson, provenance,
            Name: $"{slug} provider validation",
            Rationale: "Bounded real-provider contract validation"));
        jobTracker.StartInvocation(job.Id, provenance);
        var startedAt = jobTracker.GetJob(job.Id)?.StartedAt ?? DateTimeOffset.UtcNow;
        var context = new ProviderValidationContext(
            job.Id, job.QueuedAt, startedAt, provenance);
        log($"[{slug}] Validation job {job.Id} started", job.Id);

        try
        {
            var result = await validator.ValidateProviderAsync(context, ctx.RequestAborted);
            var resultJson = ValidationResultJson(result);
            var validation = JsonSerializer.Deserialize<JsonElement>(resultJson);
            if (validation.ValueKind != JsonValueKind.Object)
                throw new JsonException("Provider validation result must be a JSON object");

            if (result.Success)
            {
                if (!jobTracker.TryMarkCompletedUnlessCancelled(
                        job.Id, contentType: "application/json", resultJson: resultJson))
                    return ValidationCancelled(job.Id);
                log($"[{slug}] Validation job {job.Id} completed", job.Id);
                return Results.Json(new { jobId = job.Id, validation },
                    statusCode: result.StatusCode);
            }

            var errorCode = result.ErrorCode ?? "provider_validation_failed";
            var message = result.Message ?? "Provider validation failed";
            if (!jobTracker.TryMarkFailedUnlessCancelled(
                    job.Id, message, errorCode, resultJson))
                return ValidationCancelled(job.Id);
            log($"[{slug}] Validation job {job.Id} failed: {errorCode}", job.Id);
            return Results.Json(new
            {
                jobId = job.Id,
                error = errorCode,
                message,
                validation,
            }, statusCode: result.StatusCode);
        }
        catch (OperationCanceledException)
        {
            if (jobTracker.GetJob(job.Id)?.Status != JobStatus.Cancelled)
                jobTracker.MarkCancelled(job.Id);
            return ValidationCancelled(job.Id);
        }
        catch (Exception ex)
        {
            const string errorCode = "provider_validation_exception";
            var resultJson = JsonSerializer.Serialize(new
            {
                ok = false,
                error = errorCode,
                message = ex.Message,
            });
            if (!jobTracker.TryMarkFailedUnlessCancelled(
                    job.Id, ex.Message, errorCode, resultJson))
                return ValidationCancelled(job.Id);
            log($"[{slug}] Validation job {job.Id} failed: {errorCode}", job.Id);
            return Results.Json(new
            {
                jobId = job.Id,
                error = errorCode,
                message = ex.Message,
                validation = JsonSerializer.Deserialize<JsonElement>(resultJson),
            }, statusCode: 500);
        }
    }

    private static string ValidationResultJson(ProviderValidationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResultJson))
        {
            using var parsed = JsonDocument.Parse(result.ResultJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Provider validation result must be a JSON object");
            return parsed.RootElement.GetRawText();
        }

        return JsonSerializer.Serialize(new
        {
            ok = result.Success,
            error = result.Success ? null : result.ErrorCode ?? "provider_validation_failed",
            message = result.Message,
        });
    }

    private static IResult ValidationCancelled(Guid jobId)
        => Results.Json(new
        {
            jobId,
            error = "job_cancelled",
            message = "Validation job was cancelled",
        }, statusCode: 409);

    private static async Task<IResult?> ValidateValidationBodyAsync(HttpContext ctx)
    {
        const int maxBytes = 4096;
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                return Error(413, "validation_request_too_large",
                    $"Validation request body must be empty or at most {maxBytes} bytes");
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Error(422, "validation_failed",
                    "Validation request body must be an empty object");
            if (document.RootElement.EnumerateObject().Any())
                return Error(422, "validation_failed",
                    "Validation request body does not accept fields; select providers with X-Provider");
            return null;
        }
        catch (JsonException ex)
        {
            return Error(400, "invalid_json", ex.Message);
        }
    }


    private static async Task HandleResult(Guid jobId, string slug, JobResult? result, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        if (jobTracker.GetJob(jobId)?.Status == JobStatus.Cancelled)
        {
            if (result?.OutputStream is not null)
                await result.OutputStream.DisposeAsync();
            log($"[{slug}] Ignored terminal result for cancelled job {jobId}", jobId);
            return;
        }

        if (result is { Success: true, OutputStream: not null })
        {
            await using var outputStream = result.OutputStream;
            var path = SaveOutput(jobId, outputStream, result.ContentType);
            JobArtifactStore.Save(jobId, OutputDir, path, result.ContentType,
                result.PrimaryOutputName, result.PrimaryFileName, result.ExtraOutputs);
            var size = new FileInfo(path).Length;
            jobTracker.TryMarkCompletedUnlessCancelled(jobId, path, size, result.ContentType, result.ResultJson);
            log($"[{slug}] Job {jobId} completed ({size / 1024}KB)", jobId);
        }
        else if (result is { Success: true })
        {
            jobTracker.TryMarkCompletedUnlessCancelled(jobId, resultJson: result.ResultJson);
            log($"[{slug}] Job {jobId} completed", jobId);
        }
        else
        {
            jobTracker.TryMarkFailedUnlessCancelled(jobId, result?.ErrorMessage ?? "Generation failed", resultJson: result?.ResultJson);
            log($"[{slug}] Job {jobId} failed: {result?.ErrorMessage}", jobId);
            return;
        }

        // Estimate cost after completion (job now has CompletedAt and DurationMs)
        var job = jobTracker.GetJob(jobId);
        if (job != null && _registry != null)
        {
            var cost = EstimateJobCost(job, _registry);
            if (cost.HasValue) jobTracker.SetJobCost(jobId, cost.Value);
        }
    }

    private static Dictionary<string, string> ValidateAgainstSchema(
        Dictionary<string, object?> body, Dictionary<string, ParameterSchema> schema)
    {
        var errors = new Dictionary<string, string>();
        foreach (var (name, param) in schema)
        {
            if (!param.Required) continue;
            var present = !IsEmpty(body.GetValueOrDefault(name));
            if (!present && param.Type == "file")
                present = !IsEmpty(body.GetValueOrDefault(name + "_base64"));
            if (!present)
                errors[name] = "required";
        }
        return errors;
    }

    private static bool IsEmpty(object? val)
    {
        if (val == null) return true;
        if (val is string s) return string.IsNullOrWhiteSpace(s);
        if (val is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Null || je.ValueKind == JsonValueKind.Undefined) return true;
            if (je.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(je.GetString());
        }
        return false;
    }

    internal static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "audio/flac" => ".flac",
        "audio/wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "application/json" => ".json",
        _ => ".bin"
    };

    private static string SaveOutput(Guid jobId, Stream data, string? contentType)
    {
        var path = Path.Combine(OutputDir, $"{jobId}{ExtensionFor(contentType)}");
        using var fs = File.Create(path);
        data.Position = 0;
        data.CopyTo(fs);
        return path;
    }

    private static async Task<(Dictionary<string, object?> Body, string? Error)> ReadJsonBody(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(
                ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return (body ?? new(), null);
        }
        catch (JsonException ex) { return (new(), ex.Message); }
    }

    internal static bool IsAsyncRequested(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue("async", out var values))
        {
            var value = values.FirstOrDefault();
            return string.IsNullOrEmpty(value) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(ctx.Request.Headers["X-Async"].FirstOrDefault(),
            "true", StringComparison.OrdinalIgnoreCase);
    }
    private static IResult Error(int statusCode, string error, string message) =>
        Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: statusCode);

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static double? EstimateJobCost(JobRecord job, CapabilityRegistry registry)
    {
        var entry = registry.Get(job.CapabilitySlug);
        ProviderConfig? providerConfig = null;
        entry?.Config.Providers.TryGetValue(job.ProviderName, out providerConfig);

        // Check for fixed costPerJob in provider config (for cloud APIs like Suno)
        if (providerConfig?.Extra != null &&
            providerConfig.Extra.TryGetValue("costPerJob", out var costVal) && costVal != null)
        {
            if (costVal is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetDouble();
            if (double.TryParse(costVal.ToString(), out var parsed))
                return parsed;
        }

        // For local GPU providers: estimate from power draw × duration
        if (job.DurationMs is not > 0 || _hardwareMonitor == null || _config == null)
            return null;

        var snapshot = _hardwareMonitor.GetSnapshot();
        if (snapshot?.Gpus == null || snapshot.Gpus.Count == 0)
            return null;

        var gpu = snapshot.Gpus[0];
        if (gpu.PowerWatts <= 0) return null;

        var hours = job.DurationMs.Value / 3_600_000.0;
        var kwh = (gpu.PowerWatts / 1000.0) * hours;
        return Math.Round(kwh * _config.ElectricityRatePerKwh, 6);
    }
}
