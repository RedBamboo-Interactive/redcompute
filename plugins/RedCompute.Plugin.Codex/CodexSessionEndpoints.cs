using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public static class CodexSessionEndpoints
{
    public static void Map(WebApplication app, CodexSessionService codex, CodexModelCatalog catalog,
        IJobTracker jobTracker, Action<string, Guid?> log)
    {
        app.MapGet("/codex/models", async (bool? refresh, CancellationToken ct) =>
        {
            var models = await catalog.GetAsync(forceRefresh: refresh == true, ct);
            if (models.Count == 0)
                return Results.Json(new
                {
                    error = "catalog_unavailable",
                    message = catalog.LastError ?? "Could not read the model catalog from codex app-server. Is the CLI installed and logged in?"
                }, statusCode: 503);

            return Results.Json(new
            {
                models = models.Where(m => !m.Hidden).Select(m => new
                {
                    id = m.Id,
                    name = m.DisplayName,
                    description = m.Description,
                    fast = m.Fast,
                    isDefault = m.IsDefault,
                    defaultEffort = m.DefaultReasoningEffort,
                    efforts = m.SupportedReasoningEfforts.Select(e => new { id = e.Id, description = e.Description }),
                    serviceTiers = m.ServiceTiers.Select(t => new { id = t.Id, name = t.Name, description = t.Description }),
                    supportsImages = m.SupportsImages,
                }),
                @default = models.FirstOrDefault(m => m.IsDefault)?.Id ?? models[0].Id
            });
        });

        app.MapGet("/codex/sessions", (int? limit, bool? all) =>
        {
            return Results.Ok(codex.GetSessions(limit ?? 20, includeDismissed: all == true));
        });

        app.MapGet("/codex/sessions/{id}", (string id) =>
        {
            var (info, history) = codex.GetSession(id);
            if (info == null)
                return Results.NotFound(new { error = "not_found", message = "Session not found" });
            return Results.Ok(new { session = info, messages = history });
        });

        app.MapGet("/codex/sessions/by-job/{jobId:guid}", (Guid jobId) =>
        {
            var (info, history) = codex.GetSessionByJobId(jobId);
            if (info == null)
                return Results.NotFound(new { error = "not_found", message = "No session found for this job" });
            return Results.Ok(new { session = info, messages = history });
        });

        app.MapPost("/codex/sessions/{id}/stop", async (string id) =>
        {
            codex.CancelExecution(id);
            await Task.CompletedTask;
            return Results.Ok(new { stopped = true });
        });

        app.MapPost("/codex/sessions/{id}/dismiss", (string id) =>
        {
            codex.DismissSession(id);
            return Results.Ok(new { dismissed = true });
        });

        app.MapDelete("/codex/sessions/{id}", async (string id) =>
        {
            codex.CancelExecution(id);
            await Task.CompletedTask;
            return Results.Ok(new { killed = true });
        });

        app.MapPost("/codex/execute", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new { error = "invalid_body", message = "Request body must be valid JSON" }, statusCode: 400); }

            return await HandleExecute(ctx, body, codex, catalog, jobTracker, log);
        });
    }

    private static async Task<IResult> HandleExecute(HttpContext ctx, JsonElement body,
        CodexSessionService codex, CodexModelCatalog catalog, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        var prompt = body.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return Results.Json(new { error = "validation_failed", message = "prompt is required" }, statusCode: 422);

        var workingDir = body.TryGetProperty("workingDir", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : null;

        string? model = null;
        if (body.TryGetProperty("model", out var mod) && mod.ValueKind == JsonValueKind.String)
        {
            model = mod.GetString();
            if (model != null && !await catalog.IsValidModelAsync(model, ctx.RequestAborted))
            {
                var known = await catalog.GetAsync(ct: ctx.RequestAborted);
                return Results.Json(new
                {
                    error = "validation_failed",
                    message = $"model must be one of: {string.Join(", ", known.Where(m => !m.Hidden).Select(m => m.Id))}"
                }, statusCode: 422);
            }
        }

        string? sandbox = null;
        if (body.TryGetProperty("sandbox", out var sb) && sb.ValueKind == JsonValueKind.String)
            sandbox = sb.GetString();

        var timeout = 600;
        if (body.TryGetProperty("timeout", out var to))
        {
            if (to.ValueKind != JsonValueKind.Number || !to.TryGetInt32(out var val) || val < 1 || val > 1800)
                return Results.Json(new { error = "validation_failed", message = "timeout must be between 1 and 1800" }, statusCode: 422);
            timeout = val;
        }

        Dictionary<string, string>? env = null;
        if (body.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
            env = envProp.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.GetString() ?? "");

        var inputSummary = JsonSerializer.Serialize(new { prompt, model, sandbox, timeout });
        var idempotencyKey = ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var ik) ? ik.ToString() : null;
        var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
        var rationale = ctx.Request.Headers.TryGetValue("X-Job-Rationale", out var jr) ? jr.ToString() : null;

        if (string.IsNullOrEmpty(jobName) && !string.IsNullOrWhiteSpace(prompt))
            jobName = prompt.Length > 60 ? prompt[..57] + "..." : prompt;

        JobProvenance provenance;
        try { provenance = JobProvenanceHttp.Resolve(ctx, "/codex/execute"); }
        catch (JobProvenanceValidationException ex)
        {
            return Results.Json(new { error = "invalid_provenance", message = ex.Message }, statusCode: 422);
        }
        var providerLabel = model ?? "Codex";
        JobRecord job;
        try
        {
            job = jobTracker.CreateJob(new JobSubmission("ai-session", providerLabel, inputSummary,
                provenance, idempotencyKey, jobName, rationale));
        }
        catch (IdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = "idempotency_conflict", message = ex.Message, existingJobId = ex.ExistingJobId });
        }
        if (job.IsIdempotencyReuse)
            return Results.Json(new { jobId = job.Id, status = job.Status.ToString(), idempotentReuse = true },
                statusCode: job.Status is JobStatus.Running or JobStatus.Queued ? 202 : 200);
        jobTracker.StartInvocation(job.Id, provenance);
        log($"[Codex] Execute job {job.Id} started (model={model ?? "default"})", job.Id);

        var asyncMode = ctx.Request.Query.ContainsKey("async");

        if (asyncMode)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await codex.ExecuteExecAsync(
                        prompt!, null, workingDir, model, sandbox, timeout,
                        CancellationToken.None,
                        streamKey: job.Id.ToString(),
                        env: env);

                    var streamKey = job.Id.ToString();
                    if (result.Success)
                    {
                        var rj = JsonSerializer.Serialize(new
                        {
                            success = true, text = result.Text, streamOutput = result.StreamOutput,
                            model = result.Model, inputTokens = result.InputTokens,
                            outputTokens = result.OutputTokens, costUsd = result.CostUsd,
                            error = (string?)null
                        });
                        jobTracker.MarkCompleted(job.Id, resultJson: rj, contentType: "application/json", costUsd: result.CostUsd);
                        codex.EmitStreamEvent(streamKey, new CodexStreamEvent { Type = "status", Content = "completed" });
                        log($"[Codex] Execute job {job.Id} completed ({result.InputTokens}in/{result.OutputTokens}out)", job.Id);
                    }
                    else
                    {
                        jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                        codex.EmitStreamEvent(streamKey, new CodexStreamEvent { Type = "status", Content = "failed" });
                        log($"[Codex] Execute job {job.Id} failed: {result.Error}", job.Id);
                    }
                }
                catch (Exception ex)
                {
                    jobTracker.MarkFailed(job.Id, ex.Message);
                    codex.EmitStreamEvent(job.Id.ToString(), new CodexStreamEvent { Type = "status", Content = "failed" });
                    log($"[Codex] Execute job {job.Id} exception: {ex.Message}", job.Id);
                }
            });

            ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
            return Results.Json(new { ok = true, job_id = job.Id, status = "running" }, statusCode: 202);
        }

        try
        {
            var result = await codex.ExecuteExecAsync(
                prompt!, null, workingDir, model, sandbox, timeout,
                ctx.RequestAborted,
                streamKey: job.Id.ToString(),
                env: env);

            if (!result.Success)
            {
                jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                log($"[Codex] Execute job {job.Id} failed: {result.Error}", job.Id);
                return Results.Json(new { error = "execution_failed", message = result.Error ?? "Unknown error" }, statusCode: 502);
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                success = true,
                text = result.Text,
                streamOutput = result.StreamOutput,
                model = result.Model,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
                costUsd = result.CostUsd,
                error = (string?)null
            });
            jobTracker.MarkCompleted(job.Id, resultJson: resultJson, contentType: "application/json", costUsd: result.CostUsd);
            log($"[Codex] Execute job {job.Id} completed ({result.InputTokens}in/{result.OutputTokens}out)", job.Id);

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
            log($"[Codex] Execute job {job.Id} exception: {ex.Message}", job.Id);
            return Results.Json(new { error = "execution_failed", message = ex.Message }, statusCode: 500);
        }
    }
}
