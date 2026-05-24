using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Discovery;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public static class OpenCodeSessionEndpoints
{
    public static void Map(WebApplication app, OpenCodeSessionService opencode, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        app.MapGet("/opencode/projects", () => Results.Ok(opencode.ListProjects()));

        app.MapGet("/opencode/models", () =>
        {
            return Results.Json(new
            {
                models = new[]
                {
                    new { id = "anthropic/claude-sonnet-4-20250514", name = "Claude Sonnet 4", fast = false },
                    new { id = "anthropic/claude-opus-4-20250514", name = "Claude Opus 4", fast = false },
                    new { id = "openai/gpt-4o", name = "GPT-4o", fast = true },
                    new { id = "google/gemini-2.5-pro", name = "Gemini 2.5 Pro", fast = false },
                },
                @default = "anthropic/claude-sonnet-4-20250514"
            });
        });

        app.MapGet("/opencode/sessions", (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 20;
            var all = ctx.Request.Query.ContainsKey("all");
            return Results.Json(opencode.GetSessions(limit, all));
        });

        app.MapGet("/opencode/sessions/{id}", (string id) =>
        {
            var (info, messages) = opencode.GetSession(id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            return Results.Json(new { session = info, messages });
        });

        app.MapGet("/opencode/sessions/by-job/{jobId:guid}", (Guid jobId) =>
        {
            var (info, messages) = opencode.GetSessionByJobId(jobId);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"No session for job '{jobId}'" }, statusCode: 404);
            return Results.Json(new { session = info, messages });
        });

        // --- Interactive Session Management ---

        app.MapPost("/opencode/sessions", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new { error = "invalid_body", message = "Request body must be valid JSON" }, statusCode: 400); }

            var projectPath = body.TryGetProperty("projectPath", out var pp) && pp.ValueKind == JsonValueKind.String
                ? pp.GetString() : null;
            if (string.IsNullOrWhiteSpace(projectPath))
                return Results.Json(new { error = "validation_failed", message = "projectPath is required" }, statusCode: 422);

            var callerInfo = body.TryGetProperty("callerInfo", out var ci) && ci.ValueKind == JsonValueKind.String
                ? ci.GetString() : null;

            var session = await opencode.StartSession(projectPath, callerInfo);
            if (session == null)
                return Results.Json(new { error = "start_failed", message = opencode.LastStartError ?? "Failed to start session" }, statusCode: 500);

            return Results.Json(new { session }, statusCode: 201);
        });

        app.MapPost("/opencode/sessions/{id}/message", async (string id, HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new { error = "invalid_body", message = "Request body must be valid JSON" }, statusCode: 400); }

            var content = body.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
                return Results.Json(new { error = "validation_failed", message = "content is required" }, statusCode: 422);

            var sent = await opencode.SendMessage(id, content);
            if (!sent)
                return Results.Json(new { error = "send_failed", message = "Failed to send message (session not found or stopped)" }, statusCode: 404);

            return Results.Json(new { sent = true });
        });

        app.MapPost("/opencode/sessions/{id}/resume", async (string id) =>
        {
            var session = await opencode.ResumeSession(id);
            if (session == null)
                return Results.Json(new { error = "resume_failed", message = opencode.LastStartError ?? "Failed to resume session" }, statusCode: 500);

            return Results.Json(new { session });
        });

        app.MapPost("/opencode/sessions/{id}/interrupt", (string id) =>
        {
            var result = opencode.InterruptSession(id);
            return result switch
            {
                RedCompute.Core.Sessions.InterruptResult.Interrupted => Results.Json(new { interrupted = true }),
                RedCompute.Core.Sessions.InterruptResult.NotActive => Results.Json(new { interrupted = false, reason = "not_active" }),
                RedCompute.Core.Sessions.InterruptResult.NotFound => Results.Json(new { error = "not_found", message = "Session not found" }, statusCode: 404),
                _ => Results.Json(new { error = "interrupt_failed", message = "Failed to interrupt session" }, statusCode: 500),
            };
        });

        app.MapPost("/opencode/sessions/{id}/stop", async (string id) =>
        {
            await opencode.StopSession(id);
            return Results.Json(new { stopped = true });
        });

        app.MapPost("/opencode/sessions/{id}/config", async (string id, HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new { error = "invalid_body", message = "Request body must be valid JSON" }, statusCode: 400); }

            var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            var effort = body.TryGetProperty("effort", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;

            var session = await opencode.UpdateSessionConfig(id, model, effort);
            if (session == null)
                return Results.Json(new { error = "not_found", message = "Session not found or config update failed" }, statusCode: 404);

            return Results.Json(new { session });
        });

        app.MapPost("/opencode/sessions/{id}/dismiss", (string id) =>
        {
            opencode.DismissSession(id);
            return Results.Json(new { dismissed = true });
        });

        app.MapDelete("/opencode/sessions/{id}", (string id) =>
        {
            opencode.ForceKill(id);
            return Results.Json(new { killed = true });
        });

        // --- Stateless Execution ---

        app.MapPost("/opencode/execute", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new { error = "invalid_body", message = "Request body must be valid JSON" }, statusCode: 400); }

            return await HandleExecute(ctx, body, opencode, jobTracker, log);
        });
    }

    private static async Task<IResult> HandleExecute(HttpContext ctx, JsonElement body,
        OpenCodeSessionService opencode, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        var prompt = body.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return Results.Json(new { error = "validation_failed", message = "prompt is required" }, statusCode: 422);

        var workingDir = body.TryGetProperty("workingDir", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : null;

        string? model = null;
        if (body.TryGetProperty("model", out var mod) && mod.ValueKind == JsonValueKind.String)
            model = mod.GetString();

        var timeout = 600;
        if (body.TryGetProperty("timeout", out var to))
        {
            if (to.ValueKind == JsonValueKind.Number) timeout = Math.Clamp(to.GetInt32(), 1, 1800);
        }

        var isAsync = ctx.Request.Query.ContainsKey("async");
        var inputSummary = prompt.Length > 100 ? prompt[..97] + "..." : prompt;

        var job = jobTracker.CreateJob("ai-session", "OpenCode",
            JsonSerializer.Serialize(new { prompt, model, workingDir, timeout }),
            name: inputSummary);
        jobTracker.MarkRunning(job.Id);

        var streamKey = job.Id.ToString();

        if (isAsync)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await opencode.ExecuteAsync(prompt, workingDir, model, timeout, CancellationToken.None, streamKey);
                    var resultJson = JsonSerializer.Serialize(new { result.Success, result.Text, result.StreamOutput, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error });
                    if (result.Success)
                        jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
                    else
                        jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed");
                }
                catch (Exception ex)
                {
                    jobTracker.MarkFailed(job.Id, ex.Message);
                }
            });

            return Results.Json(new { jobId = job.Id, status = "running" });
        }

        try
        {
            var result = await opencode.ExecuteAsync(prompt, workingDir, model, timeout, ctx.RequestAborted, streamKey);
            var resultJson = JsonSerializer.Serialize(new { result.Success, result.Text, result.StreamOutput, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error });

            if (result.Success)
                jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
            else
                jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed");

            return Results.Json(new { result.Success, result.Text, result.StreamOutput, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd, result.Error, jobId = job.Id });
        }
        catch (OperationCanceledException)
        {
            jobTracker.MarkCancelled(job.Id);
            return Results.Json(new { error = "cancelled", message = "Request was cancelled" }, statusCode: 499);
        }
    }
}
