using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedCompute.App.Services;
using RedCompute.Core.Discovery;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class UnifiedSessionEndpoints
{
    private static DockerContainerService? _docker;
    private static SessionCallbackRegistry? _callbacks;

    public static void Map(WebApplication app, CapabilityRegistry registry,
        IJobTracker jobTracker, Action<string, Guid?> log,
        DockerContainerService? docker = null, SessionCallbackRegistry? callbacks = null)
    {
        _docker = docker;
        _callbacks = callbacks;

        var telemetry = app.Services.GetService<RedBamboo.AppHost.Telemetry.TelemetryService>();
        telemetry?.DescribeRoute("GET", "/ai-session/providers", "List available AI session providers");
        telemetry?.DescribeRoute("GET", "/ai-session/sessions", "List active sessions");
        telemetry?.DescribeRoute("POST", "/ai-session/sessions", "Start a new interactive session");
        telemetry?.DescribeRoute("GET", "/ai-session/sessions/{id}", "Get session details");
        telemetry?.DescribeRoute("POST", "/ai-session/sessions/{id}/message", "Send a user message to a persistent session. Body: {\"content\": \"...\"}. Returns {\"sent\": true}");
        telemetry?.DescribeRoute("POST", "/ai-session/sessions/{id}/stop", "Stop a running session");
        telemetry?.DescribeRoute("POST", "/ai-session/execute", "Run an agent task with full tool access (fire-and-forget, default 30min, max 2h)");
        telemetry?.DescribeRoute("POST", "/ai-session/generate", "Dual-mode: 'session' creates a persistent interactive session by project name, 'oneshot' runs a stateless LLM completion (read-only tools, 60s)");
        telemetry?.DescribeRoute("GET", "/ai-session/models", "List available models across providers");
        telemetry?.DescribeRoute("GET", "/ai-session/projects", "List known project directories");

        app.MapGet("/ai-session/providers", () =>
        {
            var providers = registry.FindProviders<ISessionProvider>().Select(p => new
            {
                providerId = p.ProviderId,
                displayName = p.ProviderDisplayName,
                capabilities = FormatCapabilities(p.Capabilities),
                models = p.GetAvailableModels(),
            });
            return Results.Json(providers);
        });

        app.MapGet("/ai-session/sessions", (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 20;
            var all = ctx.Request.Query.ContainsKey("all");
            var excludeSource = ctx.Request.Query["excludeSource"].FirstOrDefault();

            var allSessions = new List<UnifiedSessionInfo>();
            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                allSessions.AddRange(provider.GetSessions(limit, all));
            }

            if (excludeSource != null)
                allSessions.RemoveAll(s => s.Source == excludeSource);

            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user")
                allSessions.RemoveAll(s => s.UserId != null && s.UserId != userId);

            allSessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            if (allSessions.Count > limit)
                allSessions = allSessions.Take(limit).ToList();

            return Results.Json(allSessions);
        });

        app.MapPost("/ai-session/sessions", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to create sessions");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var (provider, error) = ResolveProviderFromBody(ctx, registry, body);
            if (error != null) return error;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.PersistentSessions))
                return NotSupported(provider.ProviderId, "persistent sessions");

            var projectPath = body.TryGetProperty("projectPath", out var pp) ? pp.GetString() : null;
            if (string.IsNullOrWhiteSpace(projectPath))
                return Error(422, "validation_failed", "projectPath is required");

            var model = body.TryGetProperty("model", out var m) ? m.GetString() : null;
            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
            var session = await provider.StartSessionAsync(projectPath, callerInfo, model, uId, uName, uAvatar);
            if (session == null)
                return Error(500, "start_failed", provider.LastStartError ?? "Failed to start session");

            return Results.Json(session);
        });

        app.MapGet("/ai-session/sessions/{id}", (HttpContext ctx, string id) =>
        {
            var (provider, info, history) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);

            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            return Results.Json(new { session = info, messages = history });
        });

        app.MapGet("/ai-session/sessions/by-job/{jobId:guid}", (HttpContext ctx, Guid jobId) =>
        {
            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                var (info, history) = provider.GetSessionByJobId(jobId);
                if (info != null)
                    return Results.Json(new { session = info, messages = history });
            }
            return Results.Json(new ErrorResponse { Error = "not_found", Message = $"No session for job '{jobId}'" }, statusCode: 404);
        });

        app.MapPost("/ai-session/sessions/{id}/message", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SendMessage))
                return NotSupported(provider.ProviderId, "interactive messaging");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var content = body.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
                return Error(400, "missing_content", "content is required");

            ImageAttachment[]? images = null;
            if (body.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
            {
                images = imagesEl.EnumerateArray()
                    .Select(i => new ImageAttachment(
                        i.TryGetProperty("mediaType", out var mt) ? mt.GetString()! : "image/png",
                        i.TryGetProperty("base64", out var b) ? b.GetString()! : ""))
                    .ToArray();
            }

            var sent = await provider.SendMessageAsync(id, content, images);
            if (!sent)
                return Error(502, "delivery_failed", $"Message could not be delivered to session '{id}'");
            return Results.Json(new { sent });
        });

        app.MapPost("/ai-session/sessions/{id}/inject", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var role = body.TryGetProperty("role", out var r) ? r.GetString() : null;
            var content = body.TryGetProperty("content", out var c) ? c.GetString() : null;

            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(content))
                return Error(400, "missing_fields", "role and content are required");

            var injected = await provider!.InjectMessageAsync(id, role, content);
            return Results.Json(new { injected });
        });

        app.MapPost("/ai-session/sessions/{id}/answer", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.SendMessage))
                return NotSupported(provider.ProviderId, "interactive messaging");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var answer = body.TryGetProperty("answer", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(answer))
                return Error(422, "validation_failed", "answer is required");

            var sent = provider.SendAnswer(id, answer);
            return Results.Json(new { sent });
        });

        app.MapPost("/ai-session/sessions/{id}/interrupt", (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Interrupt))
                return NotSupported(provider.ProviderId, "session interrupts");

            var result = provider.InterruptSession(id);
            return Results.Json(new { interrupted = result == InterruptResult.Interrupted, reason = result.ToString() });
        });

        app.MapPost("/ai-session/sessions/{id}/resume", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.Resume))
                return NotSupported(provider.ProviderId, "session resume");

            var session = await provider.ResumeSessionAsync(id);
            if (session == null)
                return Error(500, "resume_failed", provider.LastStartError ?? "Failed to resume session");

            return Results.Json(session);
        });

        app.MapPost("/ai-session/sessions/{id}/stop", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            await provider!.StopSessionAsync(id);
            return Results.Json(new { stopped = true });
        });

        app.MapPost("/ai-session/sessions/{id}/dismiss", (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            provider!.DismissSession(id);
            return Results.Json(new { dismissed = true });
        });

        app.MapPost("/ai-session/sessions/{id}/callback", async (HttpContext ctx, string id) =>
        {
            if (_callbacks == null)
                return Error(503, "not_configured", "Callback registry not available");

            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var url = body.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return Error(422, "validation_failed", "url is required");

            var deferred = _callbacks.RegisterIfStillActive(id, url, info.Status, userId ?? info.UserId);
            return Results.Json(new { registered = deferred, currentStatus = info.Status.ToString() });
        });

        app.MapPost("/ai-session/sessions/{id}/config", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.ConfigUpdate))
                return NotSupported(provider.ProviderId, "config updates");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var model = body.TryGetProperty("model", out var m) ? m.GetString() : null;
            var effort = body.TryGetProperty("effort", out var e) ? e.GetString() : null;
            var updated = await provider.UpdateSessionConfigAsync(id, model, effort);
            return Results.Json(updated);
        });

        app.MapPost("/ai-session/sessions/{id}/permission-mode", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");
            if (!provider!.Capabilities.HasFlag(SessionCapabilities.PermissionMode))
                return NotSupported(provider.ProviderId, "permission modes");

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var mode = body.TryGetProperty("mode", out var mm) ? mm.GetString() : null;
            if (string.IsNullOrWhiteSpace(mode))
                return Error(422, "validation_failed", "mode is required");

            var ok = provider.SetPermissionMode(id, mode);
            return Results.Json(new { mode, ok });
        });

        app.MapDelete("/ai-session/sessions/{id}", async (HttpContext ctx, string id) =>
        {
            var (provider, info, _) = FindSessionAcrossProviders(registry, id);
            if (info == null)
                return Results.Json(new ErrorResponse { Error = "not_found", Message = $"Session '{id}' not found" }, statusCode: 404);
            var userId = ResolveUserId(ctx);
            if (userId != null && userId != "local-user" && info.UserId != null && info.UserId != userId)
                return Error(403, "forbidden", "You do not have access to this session");

            await provider!.ForceKillAsync(id);
            return Results.Json(new { killed = true });
        });

        app.MapGet("/ai-session/projects", (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var allProjects = new List<object>();

            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                if (!provider.Capabilities.HasFlag(SessionCapabilities.ProjectDiscovery))
                    continue;
                foreach (var p in provider.ListProjects())
                    allProjects.Add(new { p.Name, p.Path, p.HasClaudeMd, p.HasIcon, provider = provider.ProviderId });
            }

            return Results.Json(allProjects);
        });

        app.MapGet("/ai-session/projects/{name}/icon", (HttpContext ctx, string name) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                if (!provider.Capabilities.HasFlag(SessionCapabilities.ProjectDiscovery))
                    continue;
                var project = provider.ListProjects().FirstOrDefault(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (project == null) continue;
                var iconPath = FindProjectIcon(project.Path);
                if (iconPath != null)
                    return Results.File(iconPath);
            }
            return Results.NotFound();
        });

        app.MapPost("/ai-session/sessions/{id}/open-in-codered", async (string id) =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var res = await http.PostAsync($"http://localhost:18801/api/navigate?session={id}", null);
                return res.IsSuccessStatusCode
                    ? Results.Json(new { sent = true })
                    : Results.Json(new { sent = false, error = "CodeRed returned " + (int)res.StatusCode }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { sent = false, error = ex.Message }, statusCode: 502);
            }
        });

        app.MapGet("/ai-session/models", (HttpContext ctx) =>
        {
            var providerFilter = ctx.Request.Query["provider"].FirstOrDefault();
            var allModels = new List<object>();

            foreach (var provider in registry.FindProviders<ISessionProvider>())
            {
                if (providerFilter != null && provider.ProviderId != providerFilter)
                    continue;
                foreach (var m in provider.GetAvailableModels())
                    allModels.Add(new { m.Id, m.Name, m.Fast, provider = provider.ProviderId });
            }

            return Results.Json(new { models = allModels });
        });

        app.MapPost("/ai-session/execute", async (HttpContext ctx) =>
        {
            var userId = ResolveUserId(ctx);
            if (userId == null)
                return Error(401, "unauthorized", "Authentication required to execute tasks");

            ctx.Items["Telemetry.Kind"] = "job";

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body);
            if (resolveError != null) return resolveError;

            if (!provider!.Capabilities.HasFlag(SessionCapabilities.StatelessExecution))
                return NotSupported(provider.ProviderId, "stateless execution");

            var prompt = body.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(prompt))
                return Error(422, "validation_failed", "prompt is required");

            var workingDir = body.TryGetProperty("workingDir", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : null;
            var model = body.TryGetProperty("model", out var mod) && mod.ValueKind == JsonValueKind.String ? mod.GetString() : null;

            var timeout = 1800;
            if (body.TryGetProperty("timeout", out var to) && to.ValueKind == JsonValueKind.Number)
                timeout = Math.Clamp(to.GetInt32(), 1, 7200);

            Dictionary<string, string>? env = null;
            if (body.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                env = envProp.EnumerateObject().ToDictionary(ep => ep.Name, ep => ep.Value.GetString() ?? "");

            var providerParams = new Dictionary<string, object?>();
            foreach (var key in new[] { "effort", "maxTurns", "allowedTools", "addDirs", "container", "dockerImage", "sandbox" })
            {
                if (body.TryGetProperty(key, out var val))
                {
                    providerParams[key] = val.ValueKind switch
                    {
                        JsonValueKind.String => val.GetString(),
                        JsonValueKind.Number => val.TryGetInt32(out var i) ? i : (object)val.GetDouble(),
                        JsonValueKind.Array => val.EnumerateArray().Select(x => x.GetString()!).Where(x => x != null).ToArray(),
                        _ => null
                    };
                }
            }

            if (!providerParams.ContainsKey("container") || providerParams["container"] is not string)
            {
                if (providerParams.TryGetValue("dockerImage", out var di) && di is string dockerImage && _docker != null)
                {
                    try
                    {
                        var resolved = await _docker.EnsureContainerAsync(dockerImage);
                        providerParams["container"] = resolved;
                        log($"[Docker] Resolved image '{dockerImage}' to container '{resolved}'", null);
                    }
                    catch (Exception ex)
                    {
                        return Error(500, "docker_failed", $"Failed to ensure container for image '{dockerImage}': {ex.Message}");
                    }
                }
            }

            if (userId != null)
                providerParams["userId"] = userId;

            var inputSummary = prompt.Length > 100 ? prompt[..97] + "..." : prompt;
            var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
            var idempotencyKey = ctx.Request.Headers.TryGetValue("X-Idempotency-Key", out var ik) ? ik.ToString() : null;
            var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
            if (string.IsNullOrEmpty(jobName)) jobName = inputSummary;

            var inputJson = JsonSerializer.Serialize(new { prompt, model, workingDir, timeout, provider = provider.ProviderId });
            var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
            var job = jobTracker.CreateJob("ai-session", provider.ProviderDisplayName, inputJson, callerInfo, idempotencyKey, jobName,
                userId: uId, userName: uName, userAvatarUrl: uAvatar);
            jobTracker.MarkRunning(job.Id);
            log($"[{provider.ProviderId}] Execute job {job.Id} started (model={model ?? "default"})", job.Id);

            var streamKey = job.Id.ToString();
            var asyncMode = ctx.Request.Query.ContainsKey("async");

            if (asyncMode)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await provider.ExecuteAsync(prompt, workingDir, model, timeout,
                            CancellationToken.None, streamKey, env, providerParams);
                        var rj = JsonSerializer.Serialize(new
                        {
                            success = result.Success, text = result.Text, streamOutput = result.StreamOutput,
                            model = result.Model, inputTokens = result.InputTokens,
                            outputTokens = result.OutputTokens, costUsd = result.CostUsd, error = result.Error
                        });
                        if (result.Success)
                            jobTracker.MarkCompleted(job.Id, resultJson: rj, costUsd: result.CostUsd);
                        else
                            jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed", resultJson: rj);
                    }
                    catch (Exception ex) { jobTracker.MarkFailed(job.Id, ex.Message); }
                });

                ctx.Response.Headers["X-Job-Id"] = job.Id.ToString();
                return Results.Json(new { ok = true, job_id = job.Id, status = "running" }, statusCode: 202);
            }

            try
            {
                var result = await provider.ExecuteAsync(prompt, workingDir, model, timeout,
                    ctx.RequestAborted, streamKey, env, providerParams);

                var resultJson = JsonSerializer.Serialize(new
                {
                    success = result.Success, text = result.Text, streamOutput = result.StreamOutput,
                    model = result.Model, inputTokens = result.InputTokens,
                    outputTokens = result.OutputTokens, costUsd = result.CostUsd, error = result.Error
                });

                if (result.Success)
                    jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
                else
                    jobTracker.MarkFailed(job.Id, result.Error ?? "Execution failed", resultJson: resultJson);

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
                return Error(500, "execution_failed", ex.Message);
            }
        });

        app.MapPost("/ai-session/generate", async (HttpContext ctx) =>
        {
            ctx.Items["Telemetry.Kind"] = "job";

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Error(400, "invalid_body", "Request body must be valid JSON"); }

            var (provider, resolveError) = ResolveProviderFromBody(ctx, registry, body);
            if (resolveError != null) return resolveError;

            var mode = body.TryGetProperty("mode", out var m) ? m.GetString() : "session";

            if (mode == "oneshot")
                return await HandleGenerateOneshot(ctx, body, provider!, jobTracker);

            return await HandleGenerateSession(ctx, body, provider!, jobTracker);
        });
    }

    private static async Task<IResult> HandleGenerateSession(
        HttpContext ctx, JsonElement body, ISessionProvider provider, IJobTracker jobTracker)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.PersistentSessions))
            return NotSupported(provider.ProviderId, "persistent sessions");

        var project = body.TryGetProperty("project", out var p) ? p.GetString() : null;
        var prompt = body.TryGetProperty("prompt", out var pr) ? pr.GetString() : null;

        if (string.IsNullOrWhiteSpace(project))
            return Error(422, "validation_failed", "project is required for session mode");

        var resolved = provider.ListProjects().FirstOrDefault(proj =>
            proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
        if (resolved == null)
            return Error(422, "validation_failed", $"Project '{project}' not found");

        var model = body.TryGetProperty("model", out var mod) ? mod.GetString() : null;
        var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
        var (uId, uName, uAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
        var session = await provider.StartSessionAsync(resolved.Path, callerInfo, model, uId, uName, uAvatar);
        if (session == null)
            return Error(503, "start_failed", provider.LastStartError ?? "Failed to start session");

        if (!string.IsNullOrWhiteSpace(prompt) && provider.Capabilities.HasFlag(SessionCapabilities.SendMessage))
        {
            await Task.Delay(2000);
            await provider.SendMessageAsync(session.Id, prompt);
        }

        return Results.Json(new { jobId = session.JobId, sessionId = session.Id }, statusCode: 202);
    }

    private static async Task<IResult> HandleGenerateOneshot(
        HttpContext ctx, JsonElement body, ISessionProvider provider, IJobTracker jobTracker)
    {
        if (!provider.Capabilities.HasFlag(SessionCapabilities.Generate))
            return NotSupported(provider.ProviderId, "LLM completion");

        if (!body.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return Error(422, "validation_failed", "messages is required and must be a non-empty array");

        var maxTokens = 1024;
        if (body.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var mtv))
            maxTokens = Math.Clamp(mtv, 1, 8192);

        var model = body.TryGetProperty("model", out var mod) ? mod.GetString() : null;
        var system = body.TryGetProperty("system", out var sys) ? sys.GetString() : null;
        var effort = body.TryGetProperty("effort", out var eff) ? eff.GetString() : null;

        string? prompt = null;
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                prompt = content.GetString();
        }

        var inputJson = JsonSerializer.Serialize(new { model = model ?? "default", messageCount = messages.GetArrayLength(), maxTokens, effort, prompt, system, messages, provider = provider.ProviderId });
        var callerInfo = ctx.Request.Headers.TryGetValue("X-Caller-Info", out var ci) ? ci.ToString() : null;
        var jobName = ctx.Request.Headers.TryGetValue("X-Job-Name", out var jn) ? jn.ToString() : null;
        if (string.IsNullOrEmpty(jobName) && !string.IsNullOrWhiteSpace(prompt))
            jobName = prompt.Length > 60 ? prompt[..57] + "..." : prompt;

        var (gUId, gUName, gUAvatar) = await UserInfoHelper.ResolveFromContext(ctx);
        var job = jobTracker.CreateJob("ai-session", provider.ProviderDisplayName, inputJson, callerInfo, name: jobName,
            userId: gUId, userName: gUName, userAvatarUrl: gUAvatar);
        jobTracker.MarkRunning(job.Id);

        try
        {
            var result = await provider.GenerateAsync(model, system, messages.GetRawText(), maxTokens, ctx.RequestAborted, effort);
            if (!result.Success)
            {
                jobTracker.MarkFailed(job.Id, result.Error ?? "Unknown error");
                return Error(502, "execution_failed", result.Error ?? "Unknown error");
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                success = true, text = result.Text, streamOutput = result.StreamOutput, model = result.Model,
                inputTokens = result.InputTokens, outputTokens = result.OutputTokens, costUsd = result.CostUsd
            });
            jobTracker.MarkCompleted(job.Id, resultJson: resultJson, costUsd: result.CostUsd);
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
            return Error(500, "execution_failed", ex.Message);
        }
    }

    private static (ISessionProvider? provider, IResult? error) ResolveProviderFromBody(
        HttpContext ctx, CapabilityRegistry registry, JsonElement body)
    {
        string? providerName = null;
        if (body.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String)
            providerName = pv.GetString();
        providerName ??= ctx.Request.Headers["X-Provider"].FirstOrDefault();

        var entry = registry.Get("ai-session");
        if (entry == null)
            return (null, Error(503, "not_configured", "ai-session capability is not configured"));

        if (providerName != null)
        {
            var (backend, err) = ProviderResolver.Resolve(entry, providerName, "ai-session");
            if (err != null) return (null, err);
            if (backend is ISessionProvider sp) return (sp, null);
            return (null, Error(500, "not_session_provider", $"Provider '{providerName}' does not implement ISessionProvider"));
        }

        var active = entry.ActiveProvider as ISessionProvider;
        if (active != null) return (active, null);

        return (null, Error(503, "no_providers", "No session providers are registered"));
    }

    private static (ISessionProvider? provider, UnifiedSessionInfo? info, List<UnifiedMessageRecord>? history)
        FindSessionAcrossProviders(CapabilityRegistry registry, string sessionId)
    {
        foreach (var provider in registry.FindProviders<ISessionProvider>())
        {
            var (info, history) = provider.GetSession(sessionId);
            if (info != null)
                return (provider, info, history);
        }
        return (null, null, null);
    }

    private static List<string> FormatCapabilities(SessionCapabilities caps)
    {
        var result = new List<string>();
        foreach (SessionCapabilities flag in Enum.GetValues<SessionCapabilities>())
        {
            if (flag != SessionCapabilities.None && caps.HasFlag(flag))
                result.Add(flag.ToString());
        }
        return result;
    }

    private static IResult NotSupported(string providerId, string feature)
        => Results.Json(new ErrorResponse
        {
            Error = "not_supported",
            Message = $"Provider '{providerId}' does not support {feature}"
        }, statusCode: 422);

    private static string? ResolveUserId(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-User-Id", out var uid) && !string.IsNullOrEmpty(uid))
            return uid.ToString();
        return ctx.User?.FindFirst("sub")?.Value;
    }

    private static IResult Error(int status, string error, string message)
        => Results.Json(new ErrorResponse { Error = error, Message = message }, statusCode: status);

    private static readonly string[] IconCandidates =
    [
        "public/favicon.ico", "public/favicon.png", "public/favicon.svg",
        "public/logo.png", "public/logo.svg",
        "favicon.ico", "favicon.png", "favicon.svg",
        "logo.png", "logo.svg",
        "icon.png", "icon.ico", "icon.svg",
        "wwwroot/favicon.ico",
    ];

    private static string? FindProjectIcon(string projectPath)
    {
        foreach (var candidate in IconCandidates)
        {
            var full = Path.Combine(projectPath, candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
