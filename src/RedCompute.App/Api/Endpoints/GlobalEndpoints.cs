using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        })
            .WithParam("capability", "string", description: "Filter by capability slug", location: ParamLocation.Query)
            .WithParam("status", "string", description: "Filter by job status",
                enumValues: ["Queued", "Running", "Completed", "Failed", "Cancelled"], location: ParamLocation.Query)
            .WithParam("caller", "string", description: "Filter by caller info (X-Caller-Info value)", location: ParamLocation.Query)
            .WithParam("search", "string", description: "Substring match over job name, provider, caller and capability", location: ParamLocation.Query)
            .WithParam("limit", "integer", description: "Max jobs to return", defaultValue: 50, location: ParamLocation.Query)
            .WithParam("offset", "integer", description: "Pagination offset", defaultValue: 0, location: ParamLocation.Query);

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
        })
            .WithParam("olderThanDays", "integer", description: "Delete jobs and logs older than this many days (min 1)", defaultValue: 30, location: ParamLocation.Query);

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
        })
            .WithParam("window", "integer", description: "Window size in seconds", defaultValue: 300, location: ParamLocation.Query);

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
        })
            .WithParam("since", "string", description: "ISO8601 start time (defaults to start of today UTC)", location: ParamLocation.Query)
            .WithParam("until", "string", description: "ISO8601 end time (defaults to now)", location: ParamLocation.Query);

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
        })
            .WithParam("tag", "string", description: "Filter by log tag", location: ParamLocation.Query)
            .WithParam("limit", "integer", description: "Max log entries to return", defaultValue: 100, location: ParamLocation.Query)
            .WithParam("offset", "integer", description: "Pagination offset", defaultValue: 0, location: ParamLocation.Query);

        endpoints.MapGet("/jobs/{id:guid}/events", "Get parsed transcript events for an execute job (text, thinking, tool calls, results)", (Guid id, string? types, int? limit, int? offset) =>
        {
            var job = jobTracker.GetJob(id);
            if (job == null) return Results.NotFound(new { error = "not_found", message = $"Job {id} not found" });

            string? streamOutput = null;
            if (!string.IsNullOrEmpty(job.ResultJson))
            {
                try
                {
                    using var rjDoc = JsonDocument.Parse(job.ResultJson);
                    if (rjDoc.RootElement.TryGetProperty("streamOutput", out var so) && so.ValueKind == JsonValueKind.String)
                        streamOutput = so.GetString();
                }
                catch { }
            }

            if (streamOutput == null)
                return Results.NotFound(new { error = "no_transcript", message = "No transcript available for this job (not an execute job or still running)" });

            var events = ParseStreamOutputToEvents(streamOutput, job.StartedAt ?? job.QueuedAt);

            HashSet<string>? typeFilter = null;
            if (!string.IsNullOrEmpty(types))
                typeFilter = new HashSet<string>(types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

            if (typeFilter != null)
                events = events.Where(e => typeFilter.Contains(e.EventType)).ToList();

            var totalCount = events.Count;
            var pageOffset = offset ?? 0;
            var pageLimit = Math.Clamp(limit ?? 100, 1, 1000);
            var page = events.Skip(pageOffset).Take(pageLimit).ToList();

            return Results.Ok(new
            {
                jobId = id,
                jobName = job.Name,
                jobStatus = job.Status.ToString(),
                totalCount,
                offset = pageOffset,
                limit = pageLimit,
                events = page.Select(e => new
                {
                    e.Id,
                    e.Role,
                    eventType = e.EventType,
                    e.Content,
                    e.ToolName,
                    e.ToolInput,
                    e.ToolResult,
                    e.Timestamp
                })
            });
        })
            .WithParam("types", "string", description: "Comma-separated event type filter: text, thinking, tool_use, tool_result, result, system, prompt", location: ParamLocation.Query)
            .WithParam("limit", "integer", description: "Max events to return (max 1000)", defaultValue: 100, location: ParamLocation.Query)
            .WithParam("offset", "integer", description: "Pagination offset", defaultValue: 0, location: ParamLocation.Query);

    }

    private record TranscriptEvent(int Id, string Role, string EventType, string? Content, string? ToolName, string? ToolInput, string? ToolResult, string Timestamp);

    private static List<TranscriptEvent> ParseStreamOutputToEvents(string streamOutput, DateTimeOffset? baseTime)
    {
        var events = new List<TranscriptEvent>();
        var nextId = 1;
        var baseMs = (baseTime ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var startedStr = (baseTime ?? DateTimeOffset.UtcNow).ToString("O");

        var thinkingContent = new StringBuilder();
        string? thinkingTs = null;
        var textContent = new StringBuilder();
        string? textTs = null;
        var hadDeltas = false;

        void FlushDeltas()
        {
            if (thinkingContent.Length > 0)
            {
                events.Add(new TranscriptEvent(nextId++, "assistant", "thinking", thinkingContent.ToString(), null, null, null, thinkingTs ?? startedStr));
                thinkingContent.Clear();
                thinkingTs = null;
            }
            if (textContent.Length > 0)
            {
                events.Add(new TranscriptEvent(nextId++, "assistant", "text", textContent.ToString(), null, null, null, textTs ?? startedStr));
                textContent.Clear();
                textTs = null;
            }
        }

        foreach (var line in streamOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Lines may be prefixed with ISO8601 timestamp + tab
            string? ts = null;
            var json = trimmed;
            var tabIdx = trimmed.IndexOf('\t');
            if (tabIdx is >= 20 and <= 40)
            {
                var maybeTsStr = trimmed[..tabIdx];
                if (DateTimeOffset.TryParse(maybeTsStr, out var parsedTs))
                {
                    ts = parsedTs.ToString("O");
                    json = trimmed[(tabIdx + 1)..];
                }
            }

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                var fallbackTs = ts ?? DateTimeOffset.FromUnixTimeMilliseconds(baseMs + nextId).ToString("O");

                if (type == "stream_event")
                {
                    if (root.TryGetProperty("event", out var evt)
                        && evt.TryGetProperty("type", out var evtType) && evtType.GetString() == "content_block_delta"
                        && evt.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var deltaType))
                    {
                        var dt = deltaType.GetString();
                        if (dt == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                        {
                            thinkingTs ??= ts;
                            thinkingContent.Append(th.GetString());
                            hadDeltas = true;
                        }
                        else if (dt == "text_delta" && delta.TryGetProperty("text", out var tx))
                        {
                            textTs ??= ts;
                            textContent.Append(tx.GetString());
                            hadDeltas = true;
                        }
                    }
                    continue;
                }

                if (type == "assistant" || type == "result") FlushDeltas();

                if (type == "assistant")
                {
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var btt) ? btt.GetString() : null;
                        if (hadDeltas && (bt == "thinking" || bt == "text")) continue;

                        if (bt == "thinking" && block.TryGetProperty("thinking", out var thTxt))
                            events.Add(new TranscriptEvent(nextId++, "assistant", "thinking", thTxt.GetString(), null, null, null, fallbackTs));
                        else if (bt == "text" && block.TryGetProperty("text", out var txt))
                            events.Add(new TranscriptEvent(nextId++, "assistant", "text", txt.GetString(), null, null, null, fallbackTs));
                        else if (bt == "tool_use")
                        {
                            var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                            string? toolInput = null;
                            if (block.TryGetProperty("input", out var inp))
                                toolInput = inp.ValueKind == JsonValueKind.String ? inp.GetString() : inp.GetRawText();
                            events.Add(new TranscriptEvent(nextId++, "assistant", "tool_use", null, toolName, toolInput, null, fallbackTs));
                        }
                    }
                }
                else if (type == "user")
                {
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var btt) ? btt.GetString() : null;
                        if (bt != "tool_result") continue;
                        var rc = block.TryGetProperty("content", out var bc)
                            ? (bc.ValueKind == JsonValueKind.String ? bc.GetString()! : bc.GetRawText())
                            : block.GetRawText();
                        events.Add(new TranscriptEvent(nextId++, "user", "tool_result", rc, null, null, rc, fallbackTs));
                    }
                }
                else if (type == "result")
                {
                    var resultText = root.TryGetProperty("result", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
                    var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    int inputTok = 0, outputTok = 0;
                    double? costUsd = null;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("input_tokens", out var it)) inputTok = it.GetInt32();
                        if (usage.TryGetProperty("output_tokens", out var ot)) outputTok = ot.GetInt32();
                    }
                    if (root.TryGetProperty("total_cost_usd", out var cost)) costUsd = cost.GetDouble();

                    var summary = $"subtype={subtype} in={inputTok} out={outputTok}" + (costUsd.HasValue ? $" cost=${costUsd:F4}" : "");
                    events.Add(new TranscriptEvent(nextId++, "system", "result", resultText ?? summary, null, null, null, fallbackTs));
                }
                else if (type == "system")
                {
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                    var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    var content = model != null ? $"model={model}" : (subtype ?? "system");
                    events.Add(new TranscriptEvent(nextId++, "system", "system", content, null, null, null, fallbackTs));
                }
            }
        }

        FlushDeltas();

        // Aggregate: merge consecutive same-role/same-type text/thinking; collapse tool_result into preceding tool_use
        var aggregated = new List<TranscriptEvent>();
        foreach (var evt in events)
        {
            // Remap user text to "prompt"
            var e = evt.Role == "user" && evt.EventType == "text" ? evt with { EventType = "prompt" } : evt;
            var last = aggregated.Count > 0 ? aggregated[^1] : null;

            if (last != null && last.EventType == e.EventType && last.Role == e.Role
                && (e.EventType == "text" || e.EventType == "thinking"))
            {
                aggregated[^1] = last with { Content = (last.Content ?? "") + (e.Content ?? "") };
            }
            else if (last != null && last.EventType == "tool_use" && e.EventType == "tool_result")
            {
                aggregated[^1] = last with { ToolResult = e.ToolResult ?? e.Content };
            }
            else
            {
                aggregated.Add(e);
            }
        }

        return aggregated;
    }

    private static DateTimeOffset _startTime;

    public static void Initialize() => _startTime = DateTimeOffset.UtcNow;
}
