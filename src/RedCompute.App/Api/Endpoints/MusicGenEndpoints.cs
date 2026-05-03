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
using RedCompute.Providers.Suno;

namespace RedCompute.App.Api.Endpoints;

public static class MusicGenEndpoints
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        Directory.CreateDirectory(OutputDir);

        app.MapPost("/music-gen/generate", async (HttpContext ctx) =>
        {
            var entry = registry.Get("music-gen");
            if (entry?.ActiveProvider == null)
                return ErrorResult(503, "provider_not_configured", "Music generation provider is not configured. Check config.json");

            var status = await entry.ActiveProvider.GetStatusAsync();
            if (status != BackendStatus.Running)
                return ErrorResult(503, "provider_not_running", $"Music generation backend is {status}. Start it via POST /control/start/music-gen");

            var body = await ReadJsonBody(ctx);
            if (!body.ContainsKey("prompt") || string.IsNullOrWhiteSpace(body.GetValueOrDefault("prompt")?.ToString()))
                return Results.Json(new ErrorResponse { Error = "validation_failed", Message = "One or more parameters are invalid", Fields = new() { ["prompt"] = "required" } }, statusCode: 422);

            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
            var jobName = body.GetValueOrDefault("name")?.ToString()
                ?? ctx.Request.Headers["X-Job-Name"].FirstOrDefault();
            var jobRationale = body.GetValueOrDefault("rationale")?.ToString()
                ?? ctx.Request.Headers["X-Job-Rationale"].FirstOrDefault();

            var job = jobTracker.CreateJob("music-gen", entry.ActiveProvider.Name,
                JsonSerializer.Serialize(body), ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(), idempotencyKey,
                name: jobName, rationale: jobRationale);
            jobTracker.MarkRunning(job.Id);

            var prompt = body.GetValueOrDefault("prompt")?.ToString() ?? "";
            log($"[MusicGen] Job {job.Id} started: \"{Truncate(prompt, 60)}\"", job.Id);

            if (entry.ActiveProvider is SunoProvider sunoProvider)
                sunoProvider.ProgressCallback = frac => jobTracker.UpdateProgress(job.Id, frac);

            var isAsync = ctx.Request.Query.ContainsKey("async")
                || string.Equals(ctx.Request.Headers["X-Async"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            var request = new JobRequest
            {
                CapabilitySlug = "music-gen",
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
                            var path = SaveOutput(job.Id, result.OutputStream, "audio/mpeg");
                            var size = new FileInfo(path).Length;
                            SaveExtraClips(job.Id, entry.ActiveProvider as SunoProvider);
                            jobTracker.MarkCompleted(job.Id, path, size, "audio/mpeg", result.ResultJson);
                            log($"[MusicGen] Job {job.Id} completed ({size / 1024}KB)", job.Id);
                        }
                        else
                        {
                            jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Generation failed");
                            log($"[MusicGen] Job {job.Id} failed: {result?.ErrorMessage}", job.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                        log($"[MusicGen] Job {job.Id} failed: {ex.Message}", job.Id);
                    }
                });

                return Results.Json(new { jobId = job.Id, status = "running" }, statusCode: 202);
            }

            // Synchronous
            try
            {
                var result = await entry.ActiveProvider.ExecuteAsync(request, ctx.RequestAborted);
                if (result is { Success: true, OutputStream: not null })
                {
                    var path = SaveOutput(job.Id, result.OutputStream, "audio/mpeg");
                    var size = new FileInfo(path).Length;
                    SaveExtraClips(job.Id, entry.ActiveProvider as SunoProvider);
                    jobTracker.MarkCompleted(job.Id, path, size, "audio/mpeg", result.ResultJson);
                    log($"[MusicGen] Job {job.Id} completed ({size / 1024}KB)", job.Id);

                    result.OutputStream.Position = 0;
                    ctx.Response.ContentType = "audio/mpeg";
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
                log($"[MusicGen] Job {job.Id} failed (connection): {ex.Message}", job.Id);
                return ErrorResult(502, "backend_unavailable", $"Suno API connection failed: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                jobTracker.MarkCancelled(job.Id);
                log($"[MusicGen] Job {job.Id} cancelled", job.Id);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[MusicGen] Job {job.Id} failed: {ex.Message}", job.Id);
                return ErrorResult(500, "generation_failed", ex.Message);
            }
        });

        app.MapGet("/music-gen/jobs/{id:guid}/progress", (Guid id) =>
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

        app.MapGet("/music-gen/jobs/{id:guid}/output", async (HttpContext ctx, Guid id, int? clip) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null)
                return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            if (job.Status == JobStatus.Running || job.Status == JobStatus.Queued)
                return Results.Json(new { error = "not_ready", message = "Job is still running", progress = job.Progress }, statusCode: 409);

            if (job.Status == JobStatus.Failed)
                return ErrorResult(410, "job_failed", job.ErrorMessage ?? "Job failed");

            var clipIndex = clip ?? 0;
            string? filePath;

            if (clipIndex == 0)
            {
                filePath = job.OutputLocation;
            }
            else
            {
                filePath = Path.Combine(OutputDir, $"{job.Id}_clip{clipIndex}.mp3");
            }

            if (filePath == null || !File.Exists(filePath))
                return ErrorResult(404, "output_not_found", $"Output file for clip {clipIndex} not available");

            ctx.Response.ContentType = "audio/mpeg";
            ctx.Response.Headers["X-Result-Json"] = job.ResultJson;
            await using var stream = File.OpenRead(filePath);
            await stream.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });
    }

    private static void SaveExtraClips(Guid jobId, SunoProvider? provider)
    {
        if (provider?.LastClipResults == null) return;

        for (var i = 1; i < provider.LastClipResults.Count; i++)
        {
            var clip = provider.LastClipResults[i];
            if (clip.AudioData == null) continue;

            var path = Path.Combine(OutputDir, $"{jobId}_clip{i}.mp3");
            using var fs = File.Create(path);
            clip.AudioData.Position = 0;
            clip.AudioData.CopyTo(fs);
        }
    }

    private static string SaveOutput(Guid jobId, Stream data, string contentType)
    {
        var ext = contentType switch
        {
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
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
