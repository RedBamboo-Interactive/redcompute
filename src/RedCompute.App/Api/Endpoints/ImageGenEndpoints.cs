using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Providers.ComfyUI;

namespace RedCompute.App.Api.Endpoints;

public static class ImageGenEndpoints
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string> log)
    {
        Directory.CreateDirectory(OutputDir);

        app.MapPost("/image-gen/generate", async (HttpContext ctx) =>
        {
            var entry = registry.Get("image-gen");
            if (entry?.ActiveProvider == null)
                return ErrorResult(503, "provider_not_configured", "Image generation provider is not configured. Check config.json");

            var status = await entry.ActiveProvider.GetStatusAsync();
            if (status != BackendStatus.Running)
                return ErrorResult(503, "provider_not_running", $"Image generation backend is {status}. Start it via POST /control/start/image-gen");

            var body = await ReadJsonBody(ctx);
            if (!body.ContainsKey("prompt") || string.IsNullOrWhiteSpace(body.GetValueOrDefault("prompt")?.ToString()))
                return Results.Json(new ErrorResponse { Error = "validation_failed", Message = "One or more parameters are invalid", Fields = new() { ["prompt"] = "required" } }, statusCode: 422);

            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
            var job = jobTracker.CreateJob("image-gen", entry.ActiveProvider.Name,
                JsonSerializer.Serialize(body), ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(), idempotencyKey);
            jobTracker.MarkRunning(job.Id);

            var prompt = body.GetValueOrDefault("prompt")?.ToString() ?? "";
            var workflow = body.GetValueOrDefault("workflow")?.ToString() ?? "default";
            log($"[ImageGen] Job {job.Id} started: \"{Truncate(prompt, 60)}\" workflow={workflow}");

            if (entry.ActiveProvider is ComfyUIProvider comfyProvider)
                comfyProvider.ProgressCallback = frac => jobTracker.UpdateProgress(job.Id, frac);

            var isAsync = ctx.Request.Query.ContainsKey("async")
                || string.Equals(ctx.Request.Headers["X-Async"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            var request = new JobRequest
            {
                CapabilitySlug = "image-gen",
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
                        var result = await entry.ActiveProvider.ExecuteAsync(request);
                        if (result is { Success: true, OutputStream: not null })
                        {
                            var path = SaveOutput(job.Id, result.OutputStream, result.ContentType);
                            var size = new FileInfo(path).Length;
                            jobTracker.MarkCompleted(job.Id, path, size, result.ContentType, result.ResultJson);
                            log($"[ImageGen] Job {job.Id} completed ({size / 1024}KB)");
                        }
                        else
                        {
                            jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Generation failed");
                            log($"[ImageGen] Job {job.Id} failed: {result?.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                        log($"[ImageGen] Job {job.Id} failed: {ex.Message}");
                    }
                });

                return Results.Json(new { jobId = job.Id, status = "running" }, statusCode: 202);
            }

            // Synchronous: hold connection until done
            try
            {
                var result = await entry.ActiveProvider.ExecuteAsync(request, ctx.RequestAborted);
                if (result is { Success: true, OutputStream: not null })
                {
                    var path = SaveOutput(job.Id, result.OutputStream, result.ContentType);
                    var size = new FileInfo(path).Length;
                    jobTracker.MarkCompleted(job.Id, path, size, result.ContentType, result.ResultJson);
                    log($"[ImageGen] Job {job.Id} completed ({size / 1024}KB)");

                    result.OutputStream.Position = 0;
                    ctx.Response.ContentType = result.ContentType ?? "image/png";
                    ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                    ctx.Response.Headers["X-Result-Json"] = result.ResultJson;
                    await result.OutputStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                    return Results.Empty;
                }
                else
                {
                    jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Generation failed");
                    return ErrorResult(500, "generation_failed", result?.ErrorMessage ?? "Generation failed");
                }
            }
            catch (HttpRequestException ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[ImageGen] Job {job.Id} failed (connection): {ex.Message}");
                _ = entry.ActiveProvider.GetStatusAsync();
                return ErrorResult(502, "backend_unavailable", $"Backend connection failed: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                jobTracker.MarkCancelled(job.Id);
                log($"[ImageGen] Job {job.Id} cancelled");
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[ImageGen] Job {job.Id} failed: {ex.Message}");
                return ErrorResult(500, "generation_failed", ex.Message);
            }
        });

        app.MapGet("/image-gen/workflows", () =>
        {
            var provider = GetComfyProvider(registry);
            if (provider == null)
                return Results.Json(new { workflows = Array.Empty<object>(), message = "Provider not configured" });

            var workflows = provider.WorkflowLoader.Workflows.Values.Select(w => new
            {
                w.Name,
                w.Description,
                w.MediaType,
                parameters = w.Parameters.Select(p => new
                {
                    p.Name,
                    type = InferParamType(p),
                    required = p.Default == null,
                    p.Default
                })
            });
            return Results.Ok(new { workflows });
        });

        app.MapGet("/image-gen/workflows/{name}", (string name) =>
        {
            var provider = GetComfyProvider(registry);
            if (provider == null)
                return ErrorResult(503, "provider_not_configured", "Image generation provider is not configured");

            var wf = provider.WorkflowLoader.Get(name);
            if (wf == null)
            {
                var available = string.Join(", ", provider.WorkflowLoader.Workflows.Keys);
                return ErrorResult(404, "not_found", $"Workflow '{name}' not found. Available: {available}");
            }

            return Results.Ok(new
            {
                wf.Name,
                wf.Description,
                wf.MediaType,
                wf.OutputNode,
                parameters = wf.Parameters.Select(p => new
                {
                    p.Name,
                    p.NodeId,
                    p.Field,
                    type = InferParamType(p),
                    required = p.Default == null,
                    p.Default
                })
            });
        });

        app.MapGet("/image-gen/jobs/{id:guid}/progress", (Guid id) =>
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

        app.MapGet("/image-gen/jobs/{id:guid}/output", async (HttpContext ctx, Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null)
                return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            if (job.Status == JobStatus.Running || job.Status == JobStatus.Queued)
                return Results.Json(new { error = "not_ready", message = "Job is still running", progress = job.Progress }, statusCode: 409);

            if (job.Status == JobStatus.Failed)
                return ErrorResult(410, "job_failed", job.ErrorMessage ?? "Job failed");

            if (job.OutputLocation == null || !File.Exists(job.OutputLocation))
                return ErrorResult(404, "output_not_found", "Output file not available");

            ctx.Response.ContentType = job.OutputContentType ?? "image/png";
            ctx.Response.Headers["X-Result-Json"] = job.ResultJson;
            await using var stream = File.OpenRead(job.OutputLocation);
            await stream.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });
    }

    private static ComfyUIProvider? GetComfyProvider(CapabilityRegistry registry)
    {
        var entry = registry.Get("image-gen");
        return entry?.ActiveProvider as ComfyUIProvider;
    }

    private static string InferParamType(Core.ComfyUI.WorkflowParameter p)
    {
        if (p.Name is "seed" or "width" or "height" or "steps") return "integer";
        if (p.Default is long or int or double) return "number";
        return "string";
    }

    private static string SaveOutput(Guid jobId, Stream data, string? contentType)
    {
        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
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

    private static IResult ErrorResult(int statusCode, string error, string message) =>
        Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: statusCode);

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
