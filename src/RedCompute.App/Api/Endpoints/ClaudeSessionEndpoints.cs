using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Discovery;

namespace RedCompute.App.Api.Endpoints;

public static class ClaudeSessionEndpoints
{
    public static void Map(WebApplication app, ClaudeSessionService claude, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        app.MapGet("/claude/projects", () =>
        {
            var projects = claude.ListProjects();
            return Results.Ok(projects);
        });

        app.MapGet("/claude/projects/{name}/icon", (string name) =>
        {
            var iconPath = claude.GetProjectIconPath(name);
            if (iconPath == null)
                return Results.NotFound();
            return Results.File(iconPath);
        });

        app.MapPost("/ai-session/generate", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.Json(new ErrorResponse { Error = "invalid_body", Message = "Request body must be valid JSON" }, statusCode: 400); }

            var mode = body.TryGetProperty("mode", out var m) ? m.GetString() : "session";

            if (mode == "oneshot")
                return await HandleOneshot(ctx, body, claude, jobTracker, log);

            return await HandleSessionGenerate(body, claude);
        });

        app.MapGet("/ai-session/models", () =>
        {
            return Results.Json(new
            {
                models = new[]
                {
                    new { id = "haiku", name = "Haiku", fast = true },
                    new { id = "sonnet", name = "Sonnet", fast = false },
                    new { id = "opus", name = "Opus", fast = false }
                },
                @default = "haiku"
            });
        });

        app.MapPost("/claude/sessions", (StartSessionRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProjectPath))
                return Results.UnprocessableEntity(new { error = "validation", message = "projectPath is required" });

            var session = claude.StartSession(req.ProjectPath);
            if (session == null)
                return Results.Json(
                    new { error = "start_failed", message = claude.LastStartError ?? "Unknown error" },
                    statusCode: 503);

            return Results.Ok(session);
        });

        app.MapGet("/claude/sessions", () =>
        {
            return Results.Ok(claude.GetSessions());
        });

        app.MapGet("/claude/sessions/{id}", (string id) =>
        {
            var (info, history) = claude.GetSession(id);
            if (info == null)
                return Results.NotFound(new { error = "not_found", message = "Session not found" });

            return Results.Ok(new { session = info, messages = history });
        });

        app.MapPost("/claude/sessions/{id}/message", (string id, SendMessageRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content) && (req.Images == null || req.Images.Length == 0))
                return Results.UnprocessableEntity(new { error = "validation", message = "content or images required" });

            var sent = claude.SendMessage(id, req.Content ?? "", req.Images);
            if (!sent)
                return Results.NotFound(new { error = "send_failed", message = "Session not found or not active" });

            return Results.Ok(new { sent = true });
        });

        app.MapPost("/claude/sessions/{id}/answer", (string id, AnswerQuestionRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Answer))
                return Results.UnprocessableEntity(new { error = "validation", message = "answer is required" });

            var sent = claude.SendAnswer(id, req.Answer);
            if (!sent)
                return Results.NotFound(new { error = "send_failed", message = "Session not found or not active" });

            return Results.Ok(new { sent = true });
        });

        app.MapPost("/claude/sessions/{id}/interrupt", (string id) =>
        {
            var result = claude.InterruptSession(id);
            return result switch
            {
                ClaudeSessionService.InterruptResult.Interrupted =>
                    Results.Ok(new { interrupted = true }),
                ClaudeSessionService.InterruptResult.NotActive =>
                    Results.Ok(new { interrupted = false, reason = "not_active" }),
                ClaudeSessionService.InterruptResult.NotFound =>
                    Results.NotFound(new { error = "not_found", message = "Session not found" }),
                _ =>
                    Results.Json(new { error = "interrupt_failed", message = "Failed to send interrupt" },
                        statusCode: 500),
            };
        });

        app.MapPost("/claude/sessions/{id}/permission-mode", (string id, SetPermissionModeRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode))
                return Results.UnprocessableEntity(new { error = "validation", message = "mode is required" });

            var ok = claude.SetPermissionMode(id, req.Mode);
            if (!ok)
                return Results.NotFound(new { error = "not_found", message = "Session not found or not active" });

            return Results.Ok(new { mode = req.Mode });
        });

        app.MapPost("/claude/sessions/{id}/resume", (string id) =>
        {
            var session = claude.ResumeSession(id);
            if (session == null)
                return Results.Json(
                    new { error = "resume_failed", message = claude.LastStartError ?? "Unknown error" },
                    statusCode: 503);

            return Results.Ok(session);
        });

        app.MapPost("/claude/sessions/{id}/stop", async (string id) =>
        {
            await claude.StopSession(id);
            return Results.Ok(new { stopped = true });
        });

        app.MapPost("/claude/sessions/{id}/dismiss", (string id) =>
        {
            claude.DismissSession(id);
            return Results.Ok(new { dismissed = true });
        });

        app.MapPost("/claude/sessions/{id}/config", async (string id, UpdateConfigRequest req) =>
        {
            var result = await claude.UpdateSessionConfig(id, req.Model, req.Effort);
            return result != null ? Results.Json(result) : Results.NotFound();
        });

        app.MapDelete("/claude/sessions/{id}", async (string id) =>
        {
            await claude.ForceKill(id);
            return Results.Ok(new { killed = true });
        });
    }

    private static async Task<IResult> HandleOneshot(HttpContext ctx, JsonElement body, ClaudeSessionService claude, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        if (!body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return Results.Json(new ErrorResponse { Error = "validation_failed", Message = "messages is required and must be a non-empty array" }, statusCode: 422);

        var maxTokens = 1024;
        if (body.TryGetProperty("maxTokens", out var mt))
        {
            if (mt.ValueKind != JsonValueKind.Number || !mt.TryGetInt32(out var val) || val < 1 || val > 8192)
                return Results.Json(new ErrorResponse { Error = "validation_failed", Message = "maxTokens must be an integer between 1 and 8192" }, statusCode: 422);
            maxTokens = val;
        }

        var model = body.TryGetProperty("model", out var mod) ? mod.GetString() : null;
        var system = body.TryGetProperty("system", out var sys) ? sys.GetString() : null;

        var inputSummary = JsonSerializer.Serialize(new { model = model ?? "default", messageCount = messages.GetArrayLength(), maxTokens });
        var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
        var idempotencyKey = ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var ik) ? ik.ToString() : null;
        var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
        var rationale = ctx.Request.Headers.TryGetValue("X-Job-Rationale", out var jr) ? jr.ToString() : null;

        var job = jobTracker.CreateJob("ai-session", "Claude Code", inputSummary, callerInfo, idempotencyKey, jobName, rationale);
        jobTracker.MarkRunning(job.Id);
        log($"[Claude] Oneshot job {job.Id} started", job.Id);

        try
        {
            var result = await claude.ExecuteOneshotAsync(model, system, messages, maxTokens, ctx.RequestAborted);

            if (!result.Success)
            {
                jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                log($"[Claude] Oneshot job {job.Id} failed: {result.Error}", job.Id);
                return Results.Json(new ErrorResponse { Error = "execution_failed", Message = result.Error ?? "Unknown error" }, statusCode: 502);
            }

            var resultJson = JsonSerializer.Serialize(new { text = result.Text, model = result.Model, inputTokens = result.InputTokens, outputTokens = result.OutputTokens });
            jobTracker.MarkCompleted(job.Id, resultJson: resultJson, contentType: "application/json");
            log($"[Claude] Oneshot job {job.Id} completed", job.Id);

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
            log($"[Claude] Oneshot job {job.Id} exception: {ex.Message}", job.Id);
            return Results.Json(new ErrorResponse { Error = "execution_failed", Message = ex.Message }, statusCode: 500);
        }
    }

    private static async Task<IResult> HandleSessionGenerate(JsonElement body, ClaudeSessionService claude)
    {
        var project = body.TryGetProperty("project", out var p) ? p.GetString() : null;
        var prompt = body.TryGetProperty("prompt", out var pr) ? pr.GetString() : null;

        if (string.IsNullOrWhiteSpace(project))
            return Results.UnprocessableEntity(new { error = "validation", message = "project is required" });

        var resolved = claude.ListProjects().FirstOrDefault(proj =>
            proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (resolved == null)
            return Results.UnprocessableEntity(new { error = "validation", message = $"Project '{project}' not found" });

        var session = claude.StartSession(resolved.Path);
        if (session == null)
            return Results.Json(
                new { error = "start_failed", message = claude.LastStartError ?? "Unknown error" },
                statusCode: 503);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await Task.Delay(2000);
            claude.SendMessage(session.Id, prompt);
        }

        return Results.Accepted($"/claude/sessions/{session.Id}", new { jobId = session.JobId, sessionId = session.Id });
    }

    private record StartSessionRequest(string? ProjectPath);
    private record SendMessageRequest(string? Content, ImageAttachment[]? Images);
    private record AnswerQuestionRequest(string? Answer);
    private record SetPermissionModeRequest(string? Mode);
    private record UpdateConfigRequest(string? Model, string? Effort);
}
