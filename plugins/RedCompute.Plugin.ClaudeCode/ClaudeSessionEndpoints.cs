using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Discovery;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public static class ClaudeSessionEndpoints
{
    public static void Map(WebApplication app, ClaudeSessionService claude, IJobTracker jobTracker, Action<string, Guid?> log)
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

        app.MapPost("/claude/sessions", (HttpContext ctx, StartSessionRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProjectPath))
                return Results.UnprocessableEntity(new { error = "validation", message = "projectPath is required" });

            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var session = claude.StartSession(req.ProjectPath, callerInfo);
            if (session == null)
                return Results.Json(
                    new { error = "start_failed", message = claude.LastStartError ?? "Unknown error" },
                    statusCode: 503);

            return Results.Ok(session);
        });

        app.MapGet("/claude/sessions", (int? limit, bool? all, string? excludeSource) =>
        {
            var sessions = claude.GetSessions(limit ?? 20, includeDismissed: all == true);
            if (!string.IsNullOrEmpty(excludeSource))
                sessions = sessions.Where(s => s.Source != excludeSource).ToList();
            return Results.Ok(sessions);
        });

        app.MapGet("/claude/sessions/{id}", (string id) =>
        {
            var (info, history) = claude.GetSession(id);
            if (info == null)
                return Results.NotFound(new { error = "not_found", message = "Session not found" });

            return Results.Ok(new { session = info, messages = history });
        });

        app.MapGet("/claude/sessions/by-job/{jobId:guid}", (Guid jobId) =>
        {
            var (info, history) = claude.GetSessionByJobId(jobId);
            if (info == null)
                return Results.NotFound(new { error = "not_found", message = "No session found for this job" });

            return Results.Ok(new { session = info, messages = history });
        });

        app.MapPost("/claude/sessions/{id}/open-in-codered", async (string id) =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var res = await http.PostAsync($"http://localhost:18801/api/navigate?session={id}", null);
                return res.IsSuccessStatusCode
                    ? Results.Ok(new { sent = true })
                    : Results.Json(new { sent = false, error = "CodeRed returned " + (int)res.StatusCode }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { sent = false, error = ex.Message }, statusCode: 502);
            }
        });

        app.MapPost("/claude/sessions/{id}/message", async (string id, SendMessageRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content) && (req.Images == null || req.Images.Length == 0))
                return Results.UnprocessableEntity(new { error = "validation", message = "content or images required" });

            var sent = await claude.SendMessage(id, req.Content ?? "", req.Images);
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

    private record StartSessionRequest(string? ProjectPath);
    private record SendMessageRequest(string? Content, ImageAttachment[]? Images);
    private record AnswerQuestionRequest(string? Answer);
    private record SetPermissionModeRequest(string? Mode);
    private record UpdateConfigRequest(string? Model, string? Effort);
}
