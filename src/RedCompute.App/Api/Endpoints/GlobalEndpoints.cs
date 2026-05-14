using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Helpers;
using RedCompute.App.Services;
using RedCompute.App.Services.Claude;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Claude;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class GlobalEndpoints
{
    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker, LoggingService logger, ClaudeSessionService? claudeService = null)
    {
        app.MapGet("/status", async () =>
        {
            var capabilities = new List<object>();
            foreach (var (slug, entry) in registry.Capabilities)
            {
                var defaultStatus = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : BackendStatus.Stopped;

                var providerStatuses = new List<object>();
                foreach (var (name, prov) in entry.Providers)
                {
                    var pStatus = await prov.GetStatusAsync();
                    providerStatuses.Add(new { name, status = pStatus.ToString() });
                }

                capabilities.Add(new
                {
                    slug,
                    entry.Definition.DisplayName,
                    type = slug,
                    status = defaultStatus.ToString(),
                    provider = entry.ActiveProvider?.Name,
                    defaultProvider = entry.DefaultProviderName,
                    providers = providerStatuses,
                    sleeping = entry.IsSleeping,
                    disabled = entry.IsManuallyDisabled,
                    icon = entry.Definition.Icon,
                    color = entry.Definition.Color,
                    description = entry.Definition.Description,
                    category = entry.Definition.Category,
                    rerunnable = entry.ActiveProvider is IPluginProvider pp && pp.SupportsRerun
                });
            }

            return Results.Ok(new
            {
                service = "RedCompute",
                uptime = (DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                capabilities
            });
        });

        app.MapGet("/jobs", (string? capability, string? status, string? caller, string? search, int? limit, int? offset) =>
        {
            JobStatus? statusFilter = null;
            if (status != null && Enum.TryParse<JobStatus>(status, true, out var parsed))
                statusFilter = parsed;

            var (jobs, totalCount) = jobTracker.GetJobs(capability, statusFilter, caller, search, limit ?? 50, offset ?? 0);

            var sessionStatuses = new Dictionary<Guid, SessionStatus>();
            if (claudeService != null)
            {
                var aiSessionJobIds = jobs
                    .Where(j => j.CapabilitySlug == "ai-session" && j.Status == JobStatus.Running)
                    .Select(j => j.Id)
                    .ToList();
                if (aiSessionJobIds.Count > 0)
                    sessionStatuses = claudeService.GetSessionStatusesByJobIds(aiSessionJobIds);
            }

            return Results.Ok(new
            {
                items = jobs.Select(j => new
                {
                    j.Id,
                    capability = j.CapabilitySlug,
                    j.ProviderName,
                    status = j.Status.ToString(),
                    j.QueuedAt,
                    j.StartedAt,
                    j.CompletedAt,
                    durationMs = j.DurationMs,
                    j.ErrorMessage,
                    j.CallerInfo,
                    j.Name,
                    j.Rationale,
                    sessionStatus = sessionStatuses.TryGetValue(j.Id, out var ss) ? ss.ToString() : (string?)null
                }),
                total = totalCount
            });
        });

        app.MapGet("/jobs/{id:guid}", (Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            return Results.Ok(new
            {
                job.Id,
                capability = job.CapabilitySlug,
                job.ProviderName,
                status = job.Status.ToString(),
                job.QueuedAt,
                job.StartedAt,
                job.CompletedAt,
                durationMs = job.DurationMs,
                job.Progress,
                input = job.InputJson,
                job.OutputLocation,
                job.OutputSizeBytes,
                job.OutputContentType,
                resultMetadata = job.ResultJson,
                job.ErrorMessage,
                job.ErrorDetails,
                job.CallerInfo,
                job.Name,
                job.Rationale
            });
        });

        app.MapDelete("/jobs/{id:guid}", (Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                return Results.BadRequest(new { error = "invalid_state", message = "Job already finished" });

            jobTracker.MarkCancelled(id);
            claudeService?.CancelExecution(id.ToString());
            return Results.Ok(new { id, status = "Cancelled" });
        });

        app.MapPost("/jobs/{id:guid}/rerun", async (Guid id, HttpContext ctx) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            var entry = registry.Get(job.CapabilitySlug);
            var canRerun = entry?.ActiveProvider is IPluginProvider pp && pp.SupportsRerun;
            if (!canRerun)
                return Results.BadRequest(new { error = "not_rerunnable", message = $"Cannot rerun '{job.CapabilitySlug}' jobs" });
            var generatePath = $"/{job.CapabilitySlug}/generate";

            var inputDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(job.InputJson) ?? new();
            inputDict["name"] = $"Rerun: {job.Name ?? job.CapabilitySlug}";
            inputDict["rationale"] = $"Rerun of job {id}";
            var rerunBody = System.Text.Json.JsonSerializer.Serialize(inputDict);

            var port = ctx.Connection.LocalPort;
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}{generatePath}?async")
            {
                Content = new StringContent(rerunBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Caller-Info", $"rerun:{id}");

            try
            {
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = "rerun_failed", message = ex.Message }, statusCode: 502);
            }
        });

        app.MapDelete("/jobs/cleanup", (int? olderThanDays) =>
        {
            var days = olderThanDays ?? 30;
            if (days < 1) return Results.BadRequest(new { error = "invalid_param", message = "olderThanDays must be >= 1" });
            var deletedJobs = jobTracker.CleanupOldJobs(days);
            var deletedLogs = logger.CleanupOldLogs(days);
            return Results.Ok(new { message = $"Cleaned up jobs and logs older than {days} days", deletedJobs, deletedLogs });
        });

        app.MapGet("/activity", (int? window) =>
        {
            var windowSeconds = window ?? 300;
            var now = DateTimeOffset.UtcNow;
            var since = now.AddSeconds(-windowSeconds);
            var jobs = jobTracker.GetJobsSince(since);

            var grouped = jobs.GroupBy(j => j.CapabilitySlug);
            var capabilities = new List<object>();

            foreach (var (slug, entry) in registry.Capabilities)
            {
                var capJobs = grouped.FirstOrDefault(g => g.Key == slug)?.ToList() ?? new();
                var active = capJobs.Where(j => j.Status == JobStatus.Running).ToList();
                var queued = capJobs.Where(j => j.Status == JobStatus.Queued).ToList();
                var completed = capJobs.Where(j => j.Status == JobStatus.Completed).ToList();
                var failed = capJobs.Where(j => j.Status == JobStatus.Failed).ToList();

                var totalDurationMs = capJobs
                    .Where(j => j.DurationMs.HasValue)
                    .Sum(j => j.DurationMs!.Value);

                var lastActivity = capJobs
                    .Select(j => j.CompletedAt ?? j.StartedAt ?? j.QueuedAt)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max();

                capabilities.Add(new
                {
                    slug,
                    entry.Definition.DisplayName,
                    type = slug,
                    activeJobs = active.Count,
                    queuedJobs = queued.Count,
                    completedInWindow = completed.Count,
                    failedInWindow = failed.Count,
                    totalDurationMs,
                    utilizationPct = windowSeconds > 0
                        ? Math.Round(totalDurationMs / (windowSeconds * 1000.0) * 100, 2)
                        : 0,
                    lastActivityAt = lastActivity == DateTimeOffset.MinValue ? (DateTimeOffset?)null : lastActivity,
                    currentJobs = active.Concat(queued).Select(j => new
                    {
                        j.Id,
                        status = j.Status.ToString(),
                        j.StartedAt,
                        elapsedMs = j.StartedAt.HasValue
                            ? (long)(now - j.StartedAt.Value).TotalMilliseconds
                            : (long?)null
                    })
                });
            }

            return Results.Ok(new
            {
                windowSeconds,
                from = since,
                to = now,
                capabilities
            });
        });

        app.MapPost("/control/start/{slug}", async (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null) return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            if (entry.ActiveProvider == null) return Results.BadRequest(new { error = "no_provider", message = "No active provider configured" });

            entry.IsManuallyDisabled = false;
            var success = await entry.ActiveProvider.StartAsync();
            if (success)
                return Results.Ok(new { slug, status = "Running" });
            return Results.Json(new { error = "start_failed", message = $"Failed to start provider for '{slug}'" }, statusCode: 500);
        });

        app.MapPost("/control/stop/{slug}", async (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null) return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            if (entry.ActiveProvider == null) return Results.Ok(new { slug, status = "Stopped" });

            entry.IsManuallyDisabled = true;
            await entry.ActiveProvider.StopAsync();
            return Results.Ok(new { slug, status = "Stopped" });
        });

        app.MapPost("/control/sleep/{slug}", (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            entry.IsSleeping = true;
            return Results.Ok(new { slug, sleeping = true });
        });

        app.MapPost("/control/wake/{slug}", (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            entry.IsSleeping = false;
            return Results.Ok(new { slug, sleeping = false });
        });

        app.MapPost("/control/start/{slug}/{providerName}", async (string slug, string providerName) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            if (!entry.Providers.TryGetValue(providerName, out var provider))
                return Results.NotFound(new { error = "provider_not_found", message = $"Provider '{providerName}' not found for '{slug}'. Available: {string.Join(", ", entry.Providers.Keys)}" });

            var success = await provider.StartAsync();
            if (success)
                return Results.Ok(new { slug, provider = providerName, status = "Running" });
            return Results.Json(new { error = "start_failed", message = $"Failed to start provider '{providerName}' for '{slug}'" }, statusCode: 500);
        });

        app.MapPost("/control/stop/{slug}/{providerName}", async (string slug, string providerName) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            if (!entry.Providers.TryGetValue(providerName, out var provider))
                return Results.NotFound(new { error = "provider_not_found", message = $"Provider '{providerName}' not found for '{slug}'. Available: {string.Join(", ", entry.Providers.Keys)}" });

            await provider.StopAsync();
            return Results.Ok(new { slug, provider = providerName, status = "Stopped" });
        });

        // ============================================================
        // LOG ENDPOINTS (AI-Native)
        // ============================================================

        app.MapGet("/jobs/{id:guid}/logs", (Guid id, string? tag, int? limit, int? offset) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            var logs = App.Logger.GetLogsForJob(id, tag, limit ?? 100, offset ?? 0);
            var totalCount = App.Logger.GetLogCountForJob(id);

            return Results.Ok(new
            {
                jobId = id,
                jobName = job.Name,
                jobStatus = job.Status.ToString(),
                totalLogCount = totalCount,
                entries = logs.Select(l => new
                {
                    l.Id,
                    timestamp = l.Timestamp.ToString("O"),
                    l.Tag,
                    l.TagCategory,
                    l.Message,
                    fullMessage = l.FullMessage,
                    l.IsMultiline,
                    l.IsError
                })
            });
        });

        app.MapGet("/logs/tags", () =>
        {
            var tagDefs = LogEntryParser.GetTagDefinitions();
            var counts = App.Logger.GetTagCounts(DateTime.Now.AddHours(-24));

            var tags = tagDefs.Select(kvp => new
            {
                tag = kvp.Key,
                category = kvp.Value.Category,
                color = kvp.Value.Color,
                recentCount = counts.GetValueOrDefault(kvp.Key, 0)
            }).OrderByDescending(t => t.recentCount).ToList();

            return Results.Ok(new { tags });
        });

    }

    private static DateTimeOffset _startTime;

    public static void Initialize() => _startTime = DateTimeOffset.UtcNow;
}
