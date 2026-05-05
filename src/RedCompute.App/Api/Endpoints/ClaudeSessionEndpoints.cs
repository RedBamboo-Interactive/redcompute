using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services.Claude;

namespace RedCompute.App.Api.Endpoints;

public static class ClaudeSessionEndpoints
{
    public static void Map(WebApplication app, ClaudeSessionService claude)
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

        // Standard capability generate endpoint
        app.MapPost("/ai-session/generate", async (AiSessionGenerateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Project))
                return Results.UnprocessableEntity(new { error = "validation", message = "project is required" });

            // Resolve project name to full path
            var project = claude.ListProjects().FirstOrDefault(p =>
                p.Name.Equals(req.Project, StringComparison.OrdinalIgnoreCase));
            if (project == null)
                return Results.UnprocessableEntity(new { error = "validation", message = $"Project '{req.Project}' not found" });

            var session = claude.StartSession(project.Path);
            if (session == null)
                return Results.Json(
                    new { error = "start_failed", message = claude.LastStartError ?? "Unknown error" },
                    statusCode: 503);

            // Send initial prompt if provided
            if (!string.IsNullOrWhiteSpace(req.Prompt))
            {
                await Task.Delay(2000); // Give the process a moment to initialize
                claude.SendMessage(session.Id, req.Prompt);
            }

            return Results.Accepted($"/claude/sessions/{session.Id}", new { jobId = session.JobId, sessionId = session.Id });
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
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.UnprocessableEntity(new { error = "validation", message = "content is required" });

            var sent = claude.SendMessage(id, req.Content);
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

        app.MapDelete("/claude/sessions/{id}", async (string id) =>
        {
            await claude.ForceKill(id);
            return Results.Ok(new { killed = true });
        });
    }

    private record AiSessionGenerateRequest(string? Project, string? Prompt);
    private record StartSessionRequest(string? ProjectPath);
    private record SendMessageRequest(string? Content);
}
