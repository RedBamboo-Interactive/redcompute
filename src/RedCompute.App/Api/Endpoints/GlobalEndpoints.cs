using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
                    enabled = entry.Definition.Enabled
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
                j.CallerInfo
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
                input = job.InputJson,
                job.OutputLocation,
                job.OutputSizeBytes,
                job.OutputContentType,
                job.ErrorMessage,
                job.ErrorDetails,
                job.CallerInfo
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
    }

    private static DateTimeOffset _startTime;

    public static void Initialize() => _startTime = DateTimeOffset.UtcNow;
}
