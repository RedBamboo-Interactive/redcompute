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
    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string> log)
    {
        app.MapPost("/tts/generate", async (HttpContext ctx) =>
        {
            var entry = registry.Get("tts");
            if (entry?.ActiveProvider == null)
                return ErrorResult(ctx, 503, "provider_not_running", "TTS provider is not running. Start it via POST /control/start/tts");

            var body = await ReadJsonBody(ctx);
            var errors = ValidateTtsRequest(body);
            if (errors.Count > 0)
                return ValidationError(ctx, errors);

            var text = body.GetValueOrDefault("text")?.ToString() ?? "";
            var voice = body.GetValueOrDefault("voice")?.ToString() ?? "default";
            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

            var job = jobTracker.CreateJob("tts", entry.ActiveProvider.Name,
                JsonSerializer.Serialize(body), ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(), idempotencyKey);

            jobTracker.MarkRunning(job.Id);
            log($"[TTS] Job {job.Id} started: \"{Truncate(text, 50)}\" voice={voice}");

            try
            {
                var proxyUrl = entry.ActiveProvider.GetProxyTargetUrl();
                if (proxyUrl != null)
                {
                    await StreamingProxy.ForwardAsync(ctx, proxyUrl, body, log);
                    jobTracker.MarkCompleted(job.Id, contentType: "audio/wav");
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
                        ctx.Response.ContentType = result.ContentType ?? "audio/wav";
                        await result.OutputStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                        jobTracker.MarkCompleted(job.Id, contentType: result.ContentType);
                    }
                    else
                    {
                        jobTracker.MarkFailed(job.Id, result?.ErrorMessage ?? "Provider returned no output");
                        return ErrorResult(ctx, 500, "execution_failed", result?.ErrorMessage ?? "Provider returned no output");
                    }
                }
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message, ex.ToString());
                log($"[TTS] Job {job.Id} failed: {ex.Message}");
                return ErrorResult(ctx, 500, "execution_failed", ex.Message);
            }

            return null;
        });

        app.MapGet("/tts/voices", () =>
        {
            var entry = registry.Get("tts");
            return Results.Ok(new { voices = new[] { new { id = "default", name = "Default" } } });
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

    private static IResult? ErrorResult(HttpContext ctx, int statusCode, string error, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.WriteAsJsonAsync(new ErrorResponse { Error = error, Message = message });
        return null;
    }

    private static IResult? ValidationError(HttpContext ctx, Dictionary<string, string> fields)
    {
        ctx.Response.StatusCode = 422;
        ctx.Response.WriteAsJsonAsync(new ErrorResponse
        {
            Error = "validation_failed",
            Message = "One or more parameters are invalid",
            Fields = fields
        });
        return null;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
