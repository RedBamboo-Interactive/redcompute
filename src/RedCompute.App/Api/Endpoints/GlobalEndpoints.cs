using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Helpers;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.Api.Endpoints;

public static class GlobalEndpoints
{
    public static void Map(WebApplication app, CapabilityRegistry registry, JobTrackingService jobTracker)
    {
        app.MapGet("/status", async () =>
        {
            var capabilities = new List<object>();
            foreach (var (slug, entry) in registry.Capabilities)
            {
                var status = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : BackendStatus.Stopped;

                capabilities.Add(new
                {
                    slug,
                    entry.Definition.DisplayName,
                    entry.Definition.Type,
                    status = status.ToString(),
                    provider = entry.ActiveProvider?.Name,
                    enabled = entry.Definition.Enabled,
                    sleeping = entry.IsSleeping
                });
            }

            return Results.Ok(new
            {
                service = "RedCompute",
                uptime = (DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                capabilities
            });
        });

        app.MapGet("/jobs", (string? capability, string? status, int? limit, int? offset) =>
        {
            JobStatus? statusFilter = null;
            if (status != null && Enum.TryParse<JobStatus>(status, true, out var parsed))
                statusFilter = parsed;

            var jobs = jobTracker.GetJobs(capability, statusFilter, limit ?? 50, offset ?? 0);
            return Results.Ok(jobs.Select(j => new
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
                j.Rationale
            }));
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
            return Results.Ok(new { id, status = "Cancelled" });
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
                    type = entry.Definition.Type.ToString(),
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

        app.MapGet("/logs", (string? tag, string? search, string? since, string? until, Guid? jobId, string? level, int? limit, int? offset) =>
        {
            DateTime? sinceDate = since != null ? DateTime.Parse(since).ToUniversalTime() : null;
            DateTime? untilDate = until != null ? DateTime.Parse(until).ToUniversalTime() : null;
            bool? errorsOnly = level == "error" ? true : null;

            var (entries, totalCount) = App.Logger.QueryLogs(tag, search, sinceDate, untilDate, jobId, errorsOnly, limit ?? 100, offset ?? 0);

            return Results.Ok(new
            {
                totalCount,
                returnedCount = entries.Count,
                offset = offset ?? 0,
                entries = entries.Select(l => new
                {
                    l.Id,
                    timestamp = l.Timestamp.ToString("O"),
                    l.Tag,
                    l.TagCategory,
                    l.Message,
                    fullMessage = l.FullMessage,
                    l.IsMultiline,
                    l.IsError,
                    l.JobId
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

        app.MapGet("/logs/summary", () =>
        {
            var since = DateTime.Now.Date;
            var (entries, totalCount) = App.Logger.QueryLogs(since: since, limit: 10000);
            var errorCount = entries.Count(e => e.IsError);
            var byTag = entries.Where(e => e.Tag != "").GroupBy(e => e.Tag)
                .ToDictionary(g => g.Key, g => g.Count());
            var recentErrors = entries.Where(e => e.IsError).Take(10).Select(e => new
            {
                timestamp = e.Timestamp.ToString("O"),
                e.Tag,
                e.Message,
                e.JobId
            });

            return Results.Ok(new
            {
                since = since.ToString("O"),
                totalEntries = totalCount,
                errorCount,
                byTag,
                recentErrors
            });
        });
    }

    private static DateTimeOffset _startTime;

    public static void Initialize() => _startTime = DateTimeOffset.UtcNow;
}
