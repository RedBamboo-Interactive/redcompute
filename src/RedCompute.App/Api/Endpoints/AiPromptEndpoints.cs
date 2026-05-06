using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class AiPromptEndpoints
{
    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        app.MapPost("/ai-prompt/generate", async (HttpContext ctx) =>
        {
            var entry = registry.Get("ai-prompt");
            if (entry == null)
                return ErrorResult(ctx, 503, "provider_not_configured", "AI Prompt capability is not configured. Check config.json");

            if (entry.IsSleeping)
                return ErrorResult(ctx, 503, "capability_sleeping", "AI Prompt is sleeping. Wake it via POST /control/wake/ai-prompt");

            var requestedProvider = ProviderResolver.GetRequestedProvider(ctx);
            var (provider, providerError) = ProviderResolver.Resolve(entry, requestedProvider, "AiPrompt");
            if (providerError != null) return providerError;
            if (provider == null)
                return ErrorResult(ctx, 503, "provider_not_configured", "AI Prompt provider is not configured. Check config.json");

            var status = await provider.GetStatusAsync();
            if (status != BackendStatus.Running)
                return ErrorResult(ctx, 503, "provider_not_running",
                    $"AI Prompt backend is {status}. Start it via POST /control/start/ai-prompt");

            JsonElement body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
            }
            catch
            {
                return ErrorResult(ctx, 400, "invalid_body", "Request body must be valid JSON");
            }

            var errors = ValidateRequest(body);
            if (errors.Count > 0)
                return ValidationError(ctx, errors);

            var parameters = new Dictionary<string, object?>();
            if (body.TryGetProperty("model", out var model)) parameters["model"] = model.GetString();
            if (body.TryGetProperty("system", out var system)) parameters["system"] = system.GetString();
            if (body.TryGetProperty("messages", out var messages)) parameters["messages"] = messages;
            if (body.TryGetProperty("maxTokens", out var maxTokens)) parameters["maxTokens"] = maxTokens.GetInt32();

            var inputSummary = JsonSerializer.Serialize(new
            {
                model = parameters.TryGetValue("model", out var m) ? m : "default",
                messageCount = messages.GetArrayLength(),
                maxTokens = parameters.TryGetValue("maxTokens", out var mt) ? mt : 1024
            });

            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var idempotencyKey = ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var ik) ? ik.ToString() : null;
            var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
            var rationale = ctx.Request.Headers.TryGetValue("X-Job-Rationale", out var jr) ? jr.ToString() : null;

            var job = jobTracker.CreateJob("ai-prompt", provider.Name, inputSummary, callerInfo, idempotencyKey, jobName, rationale);
            jobTracker.MarkRunning(job.Id);
            log($"[AiPrompt] Job {job.Id} started", job.Id);

            try
            {
                var jobRequest = new JobRequest
                {
                    CapabilitySlug = "ai-prompt",
                    Parameters = parameters,
                    Provider = requestedProvider,
                    CallerInfo = callerInfo,
                    IdempotencyKey = idempotencyKey,
                    Name = jobName,
                    Rationale = rationale
                };

                var result = await provider.ExecuteAsync(jobRequest, ctx.RequestAborted);

                if (result == null || !result.Success)
                {
                    var errorMsg = result?.ErrorMessage ?? "Provider returned no result";
                    jobTracker.MarkFailed(job.Id, errorMsg);
                    log($"[AiPrompt] Job {job.Id} failed: {errorMsg}", job.Id);
                    return ErrorResult(ctx, 502, "execution_failed", errorMsg);
                }

                jobTracker.MarkCompleted(job.Id, resultJson: result.ResultJson, contentType: "application/json");
                log($"[AiPrompt] Job {job.Id} completed", job.Id);

                ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                return Results.Content(result.ResultJson ?? "{}", "application/json");
            }
            catch (TaskCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                jobTracker.MarkCancelled(job.Id);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                jobTracker.MarkFailed(job.Id, ex.Message);
                log($"[AiPrompt] Job {job.Id} exception: {ex.Message}", job.Id);
                return ErrorResult(ctx, 500, "execution_failed", ex.Message);
            }
        });

        app.MapGet("/ai-prompt/models", (HttpContext ctx) =>
        {
            var entry = registry.Get("ai-prompt");
            var defaultModel = "haiku";

            if (entry != null)
            {
                var config = entry.Config;
                if (config.Providers.TryGetValue(entry.DefaultProviderName ?? "", out var provConfig))
                {
                    if (provConfig.Extra?.TryGetValue("DefaultModel", out var dm) == true && dm != null)
                        defaultModel = dm.ToString()!;
                }
            }

            return Results.Json(new
            {
                models = new[]
                {
                    new { id = "haiku", name = "Haiku", fast = true },
                    new { id = "sonnet", name = "Sonnet", fast = false },
                    new { id = "opus", name = "Opus", fast = false }
                },
                @default = defaultModel
            });
        });
    }

    private static Dictionary<string, string> ValidateRequest(JsonElement body)
    {
        var errors = new Dictionary<string, string>();

        if (!body.TryGetProperty("messages", out var messages))
        {
            errors["messages"] = "required";
        }
        else if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
        {
            errors["messages"] = "must be a non-empty array";
        }

        if (body.TryGetProperty("maxTokens", out var mt))
        {
            if (mt.ValueKind != JsonValueKind.Number || !mt.TryGetInt32(out var val) || val < 1 || val > 8192)
                errors["maxTokens"] = "must be an integer between 1 and 8192";
        }

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
