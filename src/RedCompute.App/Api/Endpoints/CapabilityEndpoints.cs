using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
namespace RedCompute.App.Api.Endpoints;

public static class CapabilityEndpoints
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string> log)
    {
        Directory.CreateDirectory(OutputDir);

        app.MapPost("/tts/generate", async (HttpContext ctx) =>
        {
            var entry = registry.Get("tts");
            if (entry?.ActiveProvider == null)
                return ErrorResult(ctx, 503, "provider_not_configured", "TTS provider is not configured. Check config.json");

            var body = await ReadJsonBody(ctx);
            var errors = ValidateTtsRequest(body);
            if (errors.Count > 0)
                return ValidationError(ctx, errors);

            var status = await entry.ActiveProvider.GetStatusAsync();
            if (status != BackendStatus.Running)
            {
                return ErrorResult(ctx, 503, "provider_not_running",
                    $"TTS backend is {status}. Start it via POST /control/start/tts");
            }

            var text = body.GetValueOrDefault("text")?.ToString() ?? "";
            var voice = body.GetValueOrDefault("voice")?.ToString() ?? "Serena";
            var language = body.GetValueOrDefault("language")?.ToString() ?? "English";
            var emotion = body.GetValueOrDefault("emotion")?.ToString() ?? "neutral";
            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

            // Ensure the right model/checkpoint is loaded for this voice
            var proxyUrlForModel = entry.ActiveProvider.GetProxyTargetUrl();
            if (proxyUrlForModel != null)
            {
                var voicesBasePath = TtsVoiceDiscovery.GetVoicesBasePath(registry);
                var baseModel = TtsVoiceDiscovery.GetBaseModel(registry);
                var (ready, modelError) = await EnsureModelReady(proxyUrlForModel, voice, voicesBasePath, baseModel, log);
                if (!ready)
                    return ErrorResult(ctx, 422, "voice_not_available", modelError ?? $"Voice '{voice}' is not available");
            }

            // Map RedCompute simplified params → Qwen3-TTS backend format
            var backendBody = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["speaker"] = voice,
                ["language"] = language,
                ["emotion"] = emotion
            };
            if (body.TryGetValue("instruct", out var instruct) && instruct != null)
                backendBody["instruct"] = instruct.ToString();
            if (body.TryGetValue("speed", out var speed) && speed != null)
                backendBody["speed"] = speed;

            var jobName = body.GetValueOrDefault("name")?.ToString()
                ?? ctx.Request.Headers["X-Job-Name"].FirstOrDefault();
            var jobRationale = body.GetValueOrDefault("rationale")?.ToString()
                ?? ctx.Request.Headers["X-Job-Rationale"].FirstOrDefault();

            var job = jobTracker.CreateJob("tts", entry.ActiveProvider.Name,
                JsonSerializer.Serialize(body), ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(), idempotencyKey,
                name: jobName, rationale: jobRationale);

            jobTracker.MarkRunning(job.Id);
            log($"[TTS] Job {job.Id} started: \"{Truncate(text, 50)}\" voice={voice}");

            try
            {
                var proxyUrl = entry.ActiveProvider.GetProxyTargetUrl();
                if (proxyUrl != null)
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    var json = JsonSerializer.Serialize(backendBody);
                    var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync($"{proxyUrl.TrimEnd('/')}/synthesize/wav", httpContent, ctx.RequestAborted);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errBody = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
                        jobTracker.MarkFailed(job.Id, $"Backend returned {response.StatusCode}");
                        ctx.Response.StatusCode = (int)response.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ctx.RequestAborted);
                        return Results.Empty;
                    }

                    using var ms = new MemoryStream();
                    await response.Content.CopyToAsync(ms, ctx.RequestAborted);

                    var path = SaveOutput(job.Id, ms, "audio/wav");
                    var size = new FileInfo(path).Length;

                    ms.Position = 0;
                    ctx.Response.ContentType = "audio/wav";
                    ctx.Response.ContentLength = ms.Length;
                    ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                    await ms.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);

                    jobTracker.MarkCompleted(job.Id, path, size, "audio/wav");
                }
                else
                {
                    var request = new JobRequest
                    {
                        CapabilitySlug = "tts",
                        Parameters = body,
                        CallerInfo = ctx.Request.Headers["X-Caller-Info"].FirstOrDefault()
                    };
                    var result = await entry.ActiveProvider.ExecuteAsync(request, ctx.RequestAborted);
                    if (result is { Success: true, OutputStream: not null })
                    {
                        var contentType = result.ContentType ?? "audio/wav";
                        var path = SaveOutput(job.Id, result.OutputStream, contentType);
                        var size = new FileInfo(path).Length;

                        result.OutputStream.Position = 0;
                        ctx.Response.ContentType = contentType;
                        ctx.Response.ContentLength = result.OutputStream.Length;
                        ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                        await result.OutputStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);

                        jobTracker.MarkCompleted(job.Id, path, size, contentType);
                    }
                    else
                    {
                        jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Provider returned no output");
                        return ErrorResult(ctx, 500, "execution_failed", result?.ErrorMessage ?? "Provider returned no output");
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[TTS] Job {job.Id} failed (connection): {ex.Message}");
                _ = entry.ActiveProvider.GetStatusAsync();
                return ErrorResult(ctx, 502, "backend_unavailable",
                    $"Backend connection failed: {ex.Message}. The backend may have stopped.");
            }
            catch (TaskCanceledException)
            {
                jobTracker.MarkCancelled(job.Id);
                log($"[TTS] Job {job.Id} cancelled (client disconnected or timeout)");
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[TTS] Job {job.Id} failed: {ex.Message}");
                return ErrorResult(ctx, 500, "execution_failed", ex.Message);
            }

            return Results.Empty;
        });

        app.MapGet("/tts/jobs/{id:guid}/output", async (HttpContext ctx, Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null)
                return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            if (job.Status == JobStatus.Running || job.Status == JobStatus.Queued)
                return Results.Json(new { error = "not_ready", message = "Job is still running", progress = job.Progress }, statusCode: 409);

            if (job.Status == JobStatus.Failed)
                return Results.Json(new ErrorResponse { Error = "job_failed", Message = job.ErrorMessage ?? "Job failed" }, statusCode: 410);

            if (job.OutputLocation == null || !File.Exists(job.OutputLocation))
                return Results.Json(new ErrorResponse { Error = "output_not_found", Message = "Output file not available" }, statusCode: 404);

            ctx.Response.ContentType = job.OutputContentType ?? "audio/wav";
            ctx.Response.Headers["X-Result-Json"] = job.ResultJson;
            await using var stream = File.OpenRead(job.OutputLocation);
            await stream.CopyToAsync(ctx.Response.Body);
            return Results.Empty;
        });

        app.MapGet("/tts/voices", async (HttpContext ctx) =>
        {
            var entry = registry.Get("tts");
            if (entry?.ActiveProvider == null)
                return Results.Json(new { voices = Array.Empty<object>(), message = "Provider not configured" });

            // Discover custom voices from disk
            var voicesBasePath = TtsVoiceDiscovery.GetVoicesBasePath(registry);
            var customNames = TtsVoiceDiscovery.DiscoverCustomVoices(voicesBasePath);

            // Built-in speakers are always available (via model reload if needed)
            var builtinNames = new HashSet<string>(TtsVoiceDiscovery.BuiltInSpeakers, StringComparer.OrdinalIgnoreCase);
            var proxyUrl = entry.ActiveProvider.GetProxyTargetUrl();
            if (proxyUrl != null)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var json = await client.GetStringAsync($"{proxyUrl}/speakers");
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("speakers", out var speakers))
                    {
                        foreach (var s in speakers.EnumerateArray())
                        {
                            var name = s.TryGetProperty("name", out var n) ? n.GetString() : s.GetString();
                            if (name != null && !customNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                                builtinNames.Add(name);
                        }
                    }
                }
                catch { /* keep default built-in list */ }
            }

            var sortedBuiltin = builtinNames.Order(StringComparer.OrdinalIgnoreCase).ToList();

            var voices = customNames.Select(n => new { name = n, type = "custom" })
                .Concat(sortedBuiltin.Select(n => new { name = n, type = "builtin" }));

            return Results.Json(new { voices });
        });

        // Catch-all proxy for capabilities that pass through directly
        app.MapFallback("/{slug}/{**path}", async (HttpContext ctx, string slug, string? path) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Error = "not_found",
                    Message = $"No capability registered at '/{slug}'"
                });
                return;
            }

            if (entry.ActiveProvider == null)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Error = "provider_not_running",
                    Message = $"Provider for '{slug}' is not running"
                });
                return;
            }

            var proxyUrl = entry.ActiveProvider.GetProxyTargetUrl();
            if (proxyUrl == null)
            {
                ctx.Response.StatusCode = 501;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Error = "not_proxyable",
                    Message = $"Provider for '{slug}' does not support direct proxy"
                });
                return;
            }

            await StreamingProxy.ForwardRawAsync(ctx, proxyUrl, path, log);
        });
    }

    private static string SaveOutput(Guid jobId, Stream data, string contentType)
    {
        var ext = contentType switch
        {
            "audio/wav" => ".wav",
            "audio/mpeg" => ".mp3",
            _ => ".bin"
        };
        var path = Path.Combine(OutputDir, $"{jobId}{ext}");
        using var fs = File.Create(path);
        data.Position = 0;
        data.CopyTo(fs);
        return path;
    }

    private static Dictionary<string, string> ValidateTtsRequest(Dictionary<string, object?> body)
    {
        var errors = new Dictionary<string, string>();
        if (!body.ContainsKey("text") || string.IsNullOrWhiteSpace(body["text"]?.ToString()))
            errors["text"] = "required";

        if (body.TryGetValue("speed", out var speedVal) && speedVal != null)
        {
            if (double.TryParse(speedVal.ToString(), out var speed))
            {
                if (speed < 0.5 || speed > 2.0)
                    errors["speed"] = "must be between 0.5 and 2.0";
            }
            else
            {
                errors["speed"] = "must be a number";
            }
        }

        return errors;
    }

    private static async Task<Dictionary<string, object?>> ReadJsonBody(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(ctx.Request.Body);
            return body ?? new();
        }
        catch
        {
            return new();
        }
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

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static async Task<(bool Ready, string? Error)> EnsureModelReady(
        string proxyUrl, string voice, string? voicesBasePath, string? baseModel, Action<string> log)
    {
        // Check if the backend already has this speaker loaded
        try
        {
            using var infoClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var infoJson = await infoClient.GetStringAsync($"{proxyUrl}/model/info");
            var info = JsonDocument.Parse(infoJson).RootElement;
            if (info.TryGetProperty("speakers", out var speakers))
            {
                foreach (var s in speakers.EnumerateArray())
                {
                    if (string.Equals(s.GetString(), voice, StringComparison.OrdinalIgnoreCase))
                        return (true, null);
                }
            }
        }
        catch (Exception ex)
        {
            log($"[TTS] Could not check model info: {ex.Message}");
        }

        // Speaker not loaded — determine which checkpoint to reload
        string reloadPath;
        if (TtsVoiceDiscovery.IsBuiltIn(voice))
        {
            // Built-in speaker: reload the base model
            if (string.IsNullOrEmpty(baseModel))
                return (false, $"Voice '{voice}' not in current model and no base model configured");
            reloadPath = baseModel;
            log($"[TTS] Reloading base model for built-in voice '{voice}': {reloadPath}");
        }
        else
        {
            // Custom voice: reload its checkpoint from disk
            if (string.IsNullOrEmpty(voicesBasePath))
                return (false, $"Voice '{voice}' not in current model and no VoicesBasePath configured");
            var checkpointPath = Path.Combine(voicesBasePath, voice, "model", "checkpoint");
            if (!Directory.Exists(checkpointPath))
                return (false, $"Voice '{voice}' not found. No checkpoint at: {checkpointPath}");
            reloadPath = TtsVoiceDiscovery.ConvertToWslPath(checkpointPath);
            log($"[TTS] Reloading checkpoint for custom voice '{voice}': {reloadPath}");
        }

        try
        {
            using var reloadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var reloadBody = JsonSerializer.Serialize(new { checkpoint_path = reloadPath });
            var content = new StringContent(reloadBody, Encoding.UTF8, "application/json");
            var response = await reloadClient.PostAsync($"{proxyUrl}/model/reload", content);

            if (response.IsSuccessStatusCode)
            {
                log($"[TTS] Model reloaded for voice '{voice}'");
                return (true, null);
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            log($"[TTS] Model reload failed: {response.StatusCode} - {errorBody}");
            return (false, $"Model reload failed ({response.StatusCode}): {errorBody}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Model reload timed out (120s)");
        }
        catch (Exception ex)
        {
            return (false, $"Model reload error: {ex.Message}");
        }
    }
}
