using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string, Guid?> log,
        HardwareMonitorService? hardwareMonitor = null, RedComputeConfig? config = null)
    {
        _hardwareMonitor = hardwareMonitor;
        _config = config;
        _registry = registry;
        Directory.CreateDirectory(OutputDir);

        foreach (var (capSlug, _) in registry.Capabilities)
        {
            if (capSlug == "ai-session") continue;

            var slug = capSlug;

            // POST /{slug}/generate — universal work endpoint
            app.MapPost($"/{slug}/generate", async (HttpContext ctx) =>
            {
                var entry = registry.Get(slug);
                if (entry == null)
                    return Error(503, "provider_not_configured", $"Capability '{slug}' is not configured");

                if (entry.IsSleeping)
                    return Error(503, "capability_sleeping", $"'{slug}' is sleeping. Wake it via POST /control/wake/{slug}");

                var body = await ReadJsonBody(ctx);

                var requestedProvider = ProviderResolver.GetRequestedProvider(ctx, body);
                var (provider, providerError) = ProviderResolver.Resolve(entry, requestedProvider, slug);
                if (providerError != null) return providerError;
                if (provider == null)
                    return Error(503, "provider_not_configured", $"No provider configured for '{slug}'");

                ProviderResolver.StripProviderFromBody(body);

                var status = await provider.GetStatusAsync();
                if (status != BackendStatus.Running)
                    return Error(503, "provider_not_running", $"Backend for '{slug}' is {status}. Start via POST /control/start/{slug}");

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

                var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
                var jobName = body.GetValueOrDefault("name")?.ToString()
                    ?? ctx.Request.Headers["X-Job-Name"].FirstOrDefault();
                var jobRationale = body.GetValueOrDefault("rationale")?.ToString()
                    ?? ctx.Request.Headers["X-Job-Rationale"].FirstOrDefault();

                body.Remove("name");
                body.Remove("rationale");

                var job = jobTracker.CreateJob(slug, provider.Name,
                    JsonSerializer.Serialize(body),
                    ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(),
                    idempotencyKey, name: jobName, rationale: jobRationale);
                jobTracker.MarkRunning(job.Id);

                var firstParam = body.GetValueOrDefault("prompt")?.ToString()
                    ?? body.GetValueOrDefault("text")?.ToString() ?? "";
                log($"[{slug}] Job {job.Id} started: \"{Truncate(firstParam, 60)}\"", job.Id);

                // Set up progress tracking
                if (provider is IPluginProvider pp && pp.SupportsProgress)
                    pp.SetProgressCallback(frac => jobTracker.UpdateProgress(job.Id, frac));

                var isAsync = ctx.Request.Query.ContainsKey("async")
                    || string.Equals(ctx.Request.Headers["X-Async"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

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
                    }
                    try
                    {
                        var (data, contentType) = await StreamingProxy.FetchFromPathAsync(ctx, proxyUrl, backendPath, body);
                        var outputPath = SaveOutput(job.Id, data, contentType);
                        var size = new FileInfo(outputPath).Length;
                        jobTracker.MarkCompleted(job.Id, outputPath, size, contentType);
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
                        jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
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
                    CapabilitySlug = slug,
                    Parameters = body,
                    CallerInfo = ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(),
                    IdempotencyKey = idempotencyKey
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
                        catch (Exception ex)
                        {
                            jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
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
                        var size = new FileInfo(path).Length;
                        jobTracker.MarkCompleted(job.Id, path, size, result.ContentType, result.ResultJson);
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
                        jobTracker.MarkCompleted(job.Id, resultJson: result.ResultJson);
                        if (_registry != null) { var c = EstimateJobCost(jobTracker.GetJob(job.Id)!, _registry); if (c.HasValue) jobTracker.SetJobCost(job.Id, c.Value); }
                        ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                        try { return Results.Json(JsonSerializer.Deserialize<object>(result.ResultJson)); }
                        catch { return Results.Text(result.ResultJson, "application/json"); }
                    }
                    else
                    {
                        jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Generation failed");
                        return Error(500, "generation_failed", result?.ErrorMessage ?? "Generation failed");
                    }
                }
                catch (HttpRequestException ex)
                {
                    jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
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
                    jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                    log($"[{slug}] Job {job.Id} failed: {ex.Message}", job.Id);
                    return Error(500, "generation_failed", ex.Message);
                }
            });

            // GET /{slug}/jobs/{id}/progress
            app.MapGet($"/{slug}/jobs/{{id:guid}}/progress", (Guid id) =>
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
                    resultMetadata = job.ResultJson
                });
            });

            // GET /{slug}/jobs/{id}/output
            app.MapGet($"/{slug}/jobs/{{id:guid}}/output", async (HttpContext ctx, Guid id) =>
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

                ctx.Response.ContentType = job.OutputContentType ?? "application/octet-stream";
                if (job.ResultJson != null)
                    ctx.Response.Headers["X-Result-Json"] = job.ResultJson;
                await using var stream = File.OpenRead(job.OutputLocation);
                await stream.CopyToAsync(ctx.Response.Body);
                return Results.Empty;
            });
        }

        // Proxy catch-all: /{slug}/{**path} for providers with GetProxyTargetUrl
        foreach (var (capSlug, _) in registry.Capabilities)
        {
            if (capSlug == "ai-session") continue;

            var slug = capSlug;
            app.Map($"/{slug}/{{**path}}", async (HttpContext ctx, string? path) =>
            {
                // Skip if this is a known endpoint pattern (generate, jobs)
                if (path != null && (path.StartsWith("jobs/") || path == "generate"))
                    return;

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

    private static async Task HandleResult(Guid jobId, string slug, JobResult? result, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        if (result is { Success: true, OutputStream: not null })
        {
            await using var outputStream = result.OutputStream;
            var path = SaveOutput(jobId, outputStream, result.ContentType);
            var size = new FileInfo(path).Length;
            jobTracker.MarkCompleted(jobId, path, size, result.ContentType, result.ResultJson);
            log($"[{slug}] Job {jobId} completed ({size / 1024}KB)", jobId);
        }
        else if (result is { Success: true })
        {
            jobTracker.MarkCompleted(jobId, resultJson: result.ResultJson);
            log($"[{slug}] Job {jobId} completed", jobId);
        }
        else
        {
            jobTracker.MarkFailed(jobId, result?.ErrorMessage ?? "Generation failed");
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

    private static string SaveOutput(Guid jobId, Stream data, string? contentType)
    {
        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "audio/wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "application/json" => ".json",
            _ => ".bin"
        };
        var path = Path.Combine(OutputDir, $"{jobId}{ext}");
        using var fs = File.Create(path);
        data.Position = 0;
        data.CopyTo(fs);
        return path;
    }

    private static async Task<Dictionary<string, object?>> ReadJsonBody(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(ctx.Request.Body);
            return body ?? new();
        }
        catch { return new(); }
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
