using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.App.Api;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class SttEndpoints
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        Directory.CreateDirectory(OutputDir);

        app.MapPost("/stt/transcribe", async (HttpContext ctx) =>
        {
            var entry = registry.Get("stt");
            if (entry == null)
                return ErrorResult(ctx, 503, "provider_not_configured", "STT capability is not configured. Check config.json");

            if (entry.IsSleeping)
                return ErrorResult(ctx, 503, "capability_sleeping", "STT is sleeping. Wake it via POST /control/wake/stt");

            var requestedProvider = ProviderResolver.GetRequestedProvider(ctx);
            var (provider, providerError) = ProviderResolver.Resolve(entry, requestedProvider, "STT");
            if (providerError != null) return providerError;
            if (provider == null)
                return ErrorResult(ctx, 503, "provider_not_configured", "STT provider is not configured. Check config.json");

            var status = await provider.GetStatusAsync();
            if (status != BackendStatus.Running)
                return ErrorResult(ctx, 503, "provider_not_running",
                    $"STT backend is {status}. Start it via POST /control/start/stt");

            var proxyUrl = provider.GetProxyTargetUrl();
            if (proxyUrl == null)
                return ErrorResult(ctx, 501, "not_proxyable", "STT provider does not support proxy");

            byte[] audioBytes;
            string audioFilename;
            string audioContentType;
            string language;
            string task;
            bool wordTimestamps;
            bool stream;
            string? initialPrompt;
            bool vadFilter;

            var contentType = ctx.Request.ContentType ?? "";
            if (contentType.Contains("multipart"))
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var audioFile = form.Files.GetFile("audio");
                if (audioFile == null || audioFile.Length == 0)
                    return ValidationError(ctx, new() { ["audio"] = "required" });

                using var ms = new MemoryStream();
                await audioFile.CopyToAsync(ms, ctx.RequestAborted);
                audioBytes = ms.ToArray();
                audioFilename = audioFile.FileName ?? "audio.wav";
                audioContentType = audioFile.ContentType ?? "audio/wav";

                language = form["language"].FirstOrDefault() ?? "auto";
                task = form["task"].FirstOrDefault() ?? "transcribe";
                wordTimestamps = string.Equals(form["word_timestamps"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
                stream = string.Equals(form["stream"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
                initialPrompt = form["initial_prompt"].FirstOrDefault();
                vadFilter = !string.Equals(form["vad_filter"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase);
            }
            else if (contentType.Contains("json"))
            {
                Dictionary<string, object?>? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                }
                catch
                {
                    return ErrorResult(ctx, 400, "invalid_body", "Invalid JSON body");
                }
                if (body == null)
                    return ErrorResult(ctx, 400, "invalid_body", "Empty JSON body");

                var audioBase64 = body.GetValueOrDefault("audio_base64")?.ToString();
                if (string.IsNullOrEmpty(audioBase64))
                    return ValidationError(ctx, new() { ["audio_base64"] = "required" });

                try { audioBytes = Convert.FromBase64String(audioBase64); }
                catch { return ValidationError(ctx, new() { ["audio_base64"] = "invalid base64" }); }

                audioFilename = "audio.wav";
                audioContentType = body.GetValueOrDefault("audio_content_type")?.ToString() ?? "audio/wav";
                language = body.GetValueOrDefault("language")?.ToString() ?? "auto";
                task = body.GetValueOrDefault("task")?.ToString() ?? "transcribe";
                wordTimestamps = body.TryGetValue("word_timestamps", out var wt) && wt is JsonElement we && we.ValueKind == JsonValueKind.True;
                stream = body.TryGetValue("stream", out var sv) && sv is JsonElement se && se.ValueKind == JsonValueKind.True;
                initialPrompt = body.GetValueOrDefault("initial_prompt")?.ToString();
                vadFilter = !(body.TryGetValue("vad_filter", out var vf) && vf is JsonElement ve && ve.ValueKind == JsonValueKind.False);
            }
            else
            {
                return ErrorResult(ctx, 415, "unsupported_media_type",
                    "Send multipart/form-data with 'audio' file, or application/json with 'audio_base64'");
            }

            var errors = ValidateSttRequest(language, task);
            if (errors.Count > 0)
                return ValidationError(ctx, errors);

            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();
            var jobName = ctx.Request.Headers["X-Job-Name"].FirstOrDefault();
            var jobRationale = ctx.Request.Headers["X-Job-Rationale"].FirstOrDefault();

            var inputSummary = JsonSerializer.Serialize(new { audioSizeKb = audioBytes.Length / 1024, language, task, wordTimestamps, stream });
            var job = jobTracker.CreateJob("stt", provider.Name, inputSummary,
                ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(), idempotencyKey,
                name: jobName, rationale: jobRationale);

            jobTracker.MarkRunning(job.Id);
            log($"[STT] Job {job.Id} started: {audioBytes.Length / 1024}KB audio, language={language}, task={task}", job.Id);

            var isAsync = ctx.Request.Query.ContainsKey("async")
                || string.Equals(ctx.Request.Headers["X-Async"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

            if (isAsync)
            {
                var capturedProvider = provider;
                var capturedAudio = audioBytes;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                        using var content = BuildMultipartContent(capturedAudio, audioFilename, audioContentType,
                            language, task, wordTimestamps, initialPrompt, vadFilter);
                        using var response = await client.PostAsync($"{proxyUrl.TrimEnd('/')}/transcribe", content);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errBody = await response.Content.ReadAsStringAsync();
                            jobTracker.MarkFailed(job.Id, $"Backend returned {response.StatusCode}: {errBody}");
                            log($"[STT] Job {job.Id} failed: backend {response.StatusCode}", job.Id);
                            return;
                        }

                        var json = await response.Content.ReadAsStringAsync();
                        var path = SaveJsonOutput(job.Id, json);
                        var size = new FileInfo(path).Length;
                        jobTracker.MarkCompleted(job.Id, path, size, "application/json", json);
                        log($"[STT] Job {job.Id} completed", job.Id);
                    }
                    catch (Exception ex)
                    {
                        jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                        log($"[STT] Job {job.Id} failed: {ex.Message}", job.Id);
                    }
                });

                return Results.Json(new { jobId = job.Id, status = "running" }, statusCode: 202);
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                if (stream)
                {
                    using var content = BuildMultipartContent(audioBytes, audioFilename, audioContentType,
                        language, task, wordTimestamps, initialPrompt, vadFilter);
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl.TrimEnd('/')}/transcribe/stream")
                    {
                        Content = content
                    };
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errBody = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
                        jobTracker.MarkFailed(job.Id, $"Backend returned {response.StatusCode}");
                        ctx.Response.StatusCode = (int)response.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ctx.RequestAborted);
                        return Results.Empty;
                    }

                    ctx.Response.ContentType = "application/x-ndjson";
                    ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();

                    await using var backendStream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
                    using var reader = new StreamReader(backendStream);
                    var allLines = new StringBuilder();
                    string? lastLine = null;

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync(ctx.RequestAborted);
                        if (string.IsNullOrEmpty(line)) continue;
                        lastLine = line;
                        allLines.AppendLine(line);
                        await ctx.Response.WriteAsync(line + "\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }

                    var path = SaveJsonOutput(job.Id, allLines.ToString());
                    var size = new FileInfo(path).Length;
                    jobTracker.MarkCompleted(job.Id, path, size, "application/x-ndjson", lastLine);
                    log($"[STT] Job {job.Id} completed (streamed)", job.Id);
                }
                else
                {
                    using var content = BuildMultipartContent(audioBytes, audioFilename, audioContentType,
                        language, task, wordTimestamps, initialPrompt, vadFilter);
                    using var response = await client.PostAsync($"{proxyUrl.TrimEnd('/')}/transcribe", content, ctx.RequestAborted);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errBody = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
                        jobTracker.MarkFailed(job.Id, $"Backend returned {response.StatusCode}");
                        ctx.Response.StatusCode = (int)response.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ctx.RequestAborted);
                        return Results.Empty;
                    }

                    var json = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
                    var path = SaveJsonOutput(job.Id, json);
                    var size = new FileInfo(path).Length;

                    ctx.Response.ContentType = "application/json";
                    ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                    await ctx.Response.WriteAsync(json, ctx.RequestAborted);

                    jobTracker.MarkCompleted(job.Id, path, size, "application/json", json);
                    log($"[STT] Job {job.Id} completed", job.Id);
                }
            }
            catch (HttpRequestException ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[STT] Job {job.Id} failed (connection): {ex.Message}", job.Id);
                _ = provider.GetStatusAsync();
                return ErrorResult(ctx, 502, "backend_unavailable",
                    $"Backend connection failed: {ex.Message}. The backend may have stopped.");
            }
            catch (TaskCanceledException)
            {
                jobTracker.MarkCancelled(job.Id);
                log($"[STT] Job {job.Id} cancelled (client disconnected or timeout)", job.Id);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[STT] Job {job.Id} failed: {ex.Message}", job.Id);
                return ErrorResult(ctx, 500, "execution_failed", ex.Message);
            }

            return Results.Empty;
        });

        app.MapGet("/stt/jobs/{id:guid}/output", async (HttpContext ctx, Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null)
                return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            if (job.Status == JobStatus.Running || job.Status == JobStatus.Queued)
                return Results.Json(new { error = "not_ready", message = "Job is still running", progress = job.Progress }, statusCode: 409);

            if (job.Status == JobStatus.Failed)
                return Results.Json(new ErrorResponse { Error = "job_failed", Message = job.ErrorMessage ?? "Job failed" }, statusCode: 410);

            if (job.ResultJson != null)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(job.ResultJson);
                return Results.Empty;
            }

            if (job.OutputLocation == null || !File.Exists(job.OutputLocation))
                return Results.Json(new ErrorResponse { Error = "output_not_found", Message = "Output file not available" }, statusCode: 404);

            ctx.Response.ContentType = job.OutputContentType ?? "application/json";
            await using var fileStream = File.OpenRead(job.OutputLocation);
            await fileStream.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        app.MapGet("/stt/models", async (HttpContext ctx) =>
        {
            var entry = registry.Get("stt");
            var requestedProvider = ProviderResolver.GetRequestedProvider(ctx);
            var sttProvider = entry?.ResolveProvider(requestedProvider);
            if (sttProvider == null)
                return Results.Json(new { models = Array.Empty<object>(), message = "Provider not configured" });

            var proxyUrl = sttProvider.GetProxyTargetUrl();
            if (proxyUrl != null)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var json = await client.GetStringAsync($"{proxyUrl.TrimEnd('/')}/models", ctx.RequestAborted);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(json, ctx.RequestAborted);
                    return Results.Empty;
                }
                catch { }
            }

            return Results.Json(new { models = Array.Empty<object>(), current = (string?)null });
        });

        app.MapGet("/stt/languages", async (HttpContext ctx) =>
        {
            var entry = registry.Get("stt");
            var requestedProvider = ProviderResolver.GetRequestedProvider(ctx);
            var sttProvider = entry?.ResolveProvider(requestedProvider);
            if (sttProvider == null)
                return Results.Json(new { languages = Array.Empty<object>(), message = "Provider not configured" });

            var proxyUrl = sttProvider.GetProxyTargetUrl();
            if (proxyUrl != null)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var json = await client.GetStringAsync($"{proxyUrl.TrimEnd('/')}/languages", ctx.RequestAborted);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(json, ctx.RequestAborted);
                    return Results.Empty;
                }
                catch { }
            }

            return Results.Json(new { languages = Array.Empty<object>() });
        });
    }

    private static MultipartFormDataContent BuildMultipartContent(
        byte[] audioBytes, string filename, string contentType,
        string language, string task, bool wordTimestamps,
        string? initialPrompt, bool vadFilter)
    {
        var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(audioContent, "audio", filename);
        content.Add(new StringContent(language), "language");
        content.Add(new StringContent(task), "task");
        content.Add(new StringContent(wordTimestamps.ToString().ToLowerInvariant()), "word_timestamps");
        if (initialPrompt != null)
            content.Add(new StringContent(initialPrompt), "initial_prompt");
        content.Add(new StringContent(vadFilter.ToString().ToLowerInvariant()), "vad_filter");
        return content;
    }

    private static string SaveJsonOutput(Guid jobId, string json)
    {
        var path = Path.Combine(OutputDir, $"{jobId}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static Dictionary<string, string> ValidateSttRequest(string language, string task)
    {
        var errors = new Dictionary<string, string>();
        if (task != "transcribe" && task != "translate")
            errors["task"] = "must be 'transcribe' or 'translate'";
        if (language != "auto" && language.Length > 10)
            errors["language"] = "must be an ISO 639-1 code or 'auto'";
        return errors;
    }

    private static IResult ErrorResult(HttpContext ctx, int statusCode, string error, string message)
    {
        return Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: statusCode);
    }

    private static IResult ValidationError(HttpContext ctx, Dictionary<string, string> fields)
    {
        return Results.Json(new ErrorResponse
        {
            Error = "validation_failed",
            Message = "One or more parameters are invalid",
            Fields = fields
        }, statusCode: 422);
    }
}
