using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Helpers;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class GlobalEndpoints
{
    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry, JobTrackingService jobTracker, LoggingService logger)
    {
        endpoints.MapGet("/status", "Service status with uptime and capability states", async () =>
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

        endpoints.MapGet("/jobs", "List jobs with optional filters", (string? capability, string? status, string? caller, string? search, int? limit, int? offset) =>
        {
            JobStatus? statusFilter = null;
            if (status != null && Enum.TryParse<JobStatus>(status, true, out var parsed))
                statusFilter = parsed;

            var (jobs, totalCount) = jobTracker.GetJobs(capability, statusFilter, caller, search, limit ?? 50, offset ?? 0);

            var sessionStatuses = new Dictionary<Guid, string>();
            var aiSessionJobIds = jobs
                .Where(j => j.CapabilitySlug == "ai-session" && j.Status == JobStatus.Running)
                .Select(j => j.Id)
                .ToList();
            if (aiSessionJobIds.Count > 0)
            {
                foreach (var ext in registry.FindProviders<IJobExtendedProvider>())
                {
                    foreach (var (k, v) in ext.GetJobSubStatuses(aiSessionJobIds))
                        sessionStatuses[k] = v;
                }
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
                    j.CostUsd,
                    j.UserId,
                    j.UserName,
                    j.UserAvatarUrl,
                    sessionStatus = sessionStatuses.TryGetValue(j.Id, out var ss) ? ss : (string?)null
                }),
                total = totalCount
            });
        });

        endpoints.MapGet("/jobs/{id:guid}", "Get job details", (Guid id) =>
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
                resultJson = job.ResultJson,
                job.ErrorMessage,
                job.ErrorDetails,
                job.CallerInfo,
                job.Name,
                job.Rationale,
                job.CostUsd,
                job.UserId,
                job.UserName,
                job.UserAvatarUrl
            });
        });

        endpoints.MapDelete("/jobs/{id:guid}", "Cancel a running job", (Guid id) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                return Results.BadRequest(new { error = "invalid_state", message = "Job already finished" });

            jobTracker.MarkCancelled(id);
            foreach (var ext in registry.FindProviders<IJobExtendedProvider>())
                ext.CancelJob(id.ToString());
            return Results.Ok(new { id, status = "Cancelled" });
        });

        endpoints.MapPost("/jobs/{id:guid}/rerun", "Rerun a completed job", async (Guid id, HttpContext ctx) =>
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

        endpoints.MapDelete("/jobs/cleanup", "Clean up old jobs and logs", (int? olderThanDays) =>
        {
            var days = olderThanDays ?? 30;
            if (days < 1) return Results.BadRequest(new { error = "invalid_param", message = "olderThanDays must be >= 1" });
            var deletedJobs = jobTracker.CleanupOldJobs(days);
            var deletedLogs = logger.CleanupOldLogs(days);
            return Results.Ok(new { message = $"Cleaned up jobs and logs older than {days} days", deletedJobs, deletedLogs });
        });

        endpoints.MapGet("/activity", "Capability activity summary within a time window", (int? window) =>
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

        endpoints.MapGet("/activity/summary", "Activity summary over a time range with per-capability and per-caller breakdowns", (string? since, string? until) =>
        {
            var now = DateTimeOffset.UtcNow;
            var sinceDate = since != null && DateTimeOffset.TryParse(since, out var s) ? s : now.Date;
            var untilDate = until != null && DateTimeOffset.TryParse(until, out var u) ? u : now;

            var jobs = jobTracker.GetJobsSince(sinceDate)
                .Where(j => j.QueuedAt >= sinceDate && j.QueuedAt <= untilDate)
                .ToList();

            var totalCost = jobs.Where(j => j.CostUsd.HasValue).Sum(j => j.CostUsd!.Value);
            var totalDuration = jobs.Where(j => j.DurationMs.HasValue).Sum(j => j.DurationMs!.Value);

            var byCapability = new List<object>();
            foreach (var group in jobs.GroupBy(j => j.CapabilitySlug).OrderByDescending(g => g.Count()))
            {
                var capJobs = group.ToList();
                var entry = registry.Get(group.Key);
                var displayName = entry?.Definition.DisplayName ?? group.Key;

                byCapability.Add(new
                {
                    slug = group.Key,
                    displayName,
                    jobs = capJobs.Count,
                    completed = capJobs.Count(j => j.Status == JobStatus.Completed),
                    failed = capJobs.Count(j => j.Status == JobStatus.Failed),
                    running = capJobs.Count(j => j.Status == JobStatus.Running),
                    totalCostUsd = Math.Round(capJobs.Where(j => j.CostUsd.HasValue).Sum(j => j.CostUsd!.Value), 4),
                    totalDurationMs = capJobs.Where(j => j.DurationMs.HasValue).Sum(j => j.DurationMs!.Value),
                    items = capJobs.Select(j => new
                    {
                        j.Id,
                        j.Name,
                        status = j.Status.ToString(),
                        j.CallerInfo,
                        j.CostUsd,
                        durationMs = j.DurationMs,
                        j.StartedAt,
                        j.CompletedAt
                    })
                });
            }

            var byCaller = jobs
                .GroupBy(j => j.CallerInfo ?? "(direct)")
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    caller = g.Key,
                    jobs = g.Count(),
                    completed = g.Count(j => j.Status == JobStatus.Completed),
                    totalCostUsd = Math.Round(g.Where(j => j.CostUsd.HasValue).Sum(j => j.CostUsd!.Value), 4)
                });

            return Results.Ok(new
            {
                since = sinceDate,
                until = untilDate,
                totals = new
                {
                    jobs = jobs.Count,
                    completed = jobs.Count(j => j.Status == JobStatus.Completed),
                    failed = jobs.Count(j => j.Status == JobStatus.Failed),
                    running = jobs.Count(j => j.Status == JobStatus.Running),
                    totalCostUsd = Math.Round(totalCost, 4),
                    totalDurationMs = totalDuration
                },
                byCapability,
                byCaller
            });
        });

        endpoints.MapPost("/control/start/{slug}", "Start a capability's active provider", async (string slug) =>
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

        endpoints.MapPost("/control/stop/{slug}", "Stop a capability's active provider", async (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null) return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            if (entry.ActiveProvider == null) return Results.Ok(new { slug, status = "Stopped" });

            entry.IsManuallyDisabled = true;
            await entry.ActiveProvider.StopAsync();
            return Results.Ok(new { slug, status = "Stopped" });
        });

        endpoints.MapPost("/control/sleep/{slug}", "Put a capability to sleep", (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            entry.IsSleeping = true;
            return Results.Ok(new { slug, sleeping = true });
        });

        endpoints.MapPost("/control/wake/{slug}", "Wake a sleeping capability", (string slug) =>
        {
            var entry = registry.Get(slug);
            if (entry == null)
                return Results.NotFound(new { error = "not_found", message = $"Capability '{slug}' not found" });
            entry.IsSleeping = false;
            return Results.Ok(new { slug, sleeping = false });
        });

        endpoints.MapPost("/control/start/{slug}/{providerName}", "Start a specific provider for a capability", async (string slug, string providerName) =>
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

        endpoints.MapPost("/control/stop/{slug}/{providerName}", "Stop a specific provider for a capability", async (string slug, string providerName) =>
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

        endpoints.MapGet("/jobs/{id:guid}/logs", "Get logs for a specific job", (Guid id, string? tag, int? limit, int? offset) =>
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

    }

    private static DateTimeOffset _startTime;

    public static void Initialize() => _startTime = DateTimeOffset.UtcNow;
}
