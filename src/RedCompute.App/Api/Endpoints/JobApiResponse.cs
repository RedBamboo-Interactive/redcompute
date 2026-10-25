using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Api.Endpoints;

internal static class JobApiResponse
{
    public static readonly string OutputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");

    private const string StageDescription =
        "admitted=durably accepted but not invoked; waiting=Running while waiting for the provider/GPU and before its first progress signal; executing=the provider has emitted progress; completed, failed, cancelled, skipped, and timed_out mirror terminal outcomes.";

    public static Dictionary<string, object?> Progress(JobRecord job, string? outputDirectory = null)
    {
        var response = Common(job, outputDirectory);
        response["id"] = job.Id;
        response["status"] = job.Status.ToString();
        response["progress"] = job.Progress;
        response["errorMessage"] = job.ErrorMessage;
        response["durationMs"] = job.DurationMs;
        response["resultJson"] = job.ResultJson;
        return response;
    }

    public static Dictionary<string, object?> Details(JobRecord job, string? outputDirectory = null)
    {
        var response = Common(job, outputDirectory);
        response["id"] = job.Id;
        response["capability"] = job.CapabilitySlug;
        response["providerName"] = job.ProviderName;
        response["status"] = job.Status.ToString();
        response["queuedAt"] = job.QueuedAt;
        response["startedAt"] = job.StartedAt;
        response["completedAt"] = job.CompletedAt;
        response["durationMs"] = job.DurationMs;
        response["progress"] = job.Progress;
        response["input"] = job.InputJson;
        // Existing compatibility fields remain unchanged. New projections below never
        // include OutputLocation or any other filesystem path.
        response["outputLocation"] = job.OutputLocation;
        response["outputSizeBytes"] = job.OutputSizeBytes;
        response["outputContentType"] = job.OutputContentType;
        response["resultJson"] = job.ResultJson;
        response["errorMessage"] = job.ErrorMessage;
        response["errorDetails"] = job.ErrorDetails;
        response["name"] = job.Name;
        response["rationale"] = job.Rationale;
        response["costUsd"] = job.CostUsd;
        response["userId"] = job.UserId;
        response["confidential"] = job.Confidential;
        response["keepResident"] = job.KeepResident;
        response["supervisorSessionId"] = job.SupervisorSessionId;
        response["userName"] = job.UserName;
        response["userAvatarUrl"] = job.UserAvatarUrl;
        response["externalExecution"] = job.ExternalExecution;
        response["parentJobId"] = job.ParentJobId;
        response["attemptCount"] = job.AttemptCount;
        response["leaseOwner"] = job.LeaseOwner;
        response["leaseExpiresAt"] = job.LeaseExpiresAt;
        response["creationProvenance"] = job.CreationProvenance;
        response["auditComplete"] = JobTrackingService.IsAuditComplete(job.CreationProvenance);
        return response;
    }

    private static Dictionary<string, object?> Common(JobRecord job, string? outputDirectory) => new()
    {
        ["terminal"] = JobTrackingService.IsTerminal(job.Status),
        ["stage"] = Stage(job),
        ["result"] = ParseResultObject(job.ResultJson),
        ["outputs"] = Outputs(job, outputDirectory ?? OutputDirectory),
        ["links"] = new
        {
            self = $"/jobs/{job.Id}",
            progress = $"/{job.CapabilitySlug}/jobs/{job.Id}/progress",
            output = $"/{job.CapabilitySlug}/jobs/{job.Id}/output",
            lifecycleEvents = $"/jobs/{job.Id}/lifecycle-events",
        },
    };

    internal static string Stage(JobRecord job) => job.Status switch
    {
        JobStatus.Queued => "admitted",
        JobStatus.Running when job.ProviderStartedAt is null => "waiting",
        JobStatus.Running => "executing",
        JobStatus.Completed => "completed",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Skipped => "skipped",
        JobStatus.TimedOut => "timed_out",
        _ => job.Status.ToString().ToLowerInvariant(),
    };

    private static JsonObject? ParseResultObject(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try { return JsonNode.Parse(resultJson) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static object[] Outputs(JobRecord job, string outputDirectory)
    {
        var artifacts = JobArtifactStore.ListAvailable(job.Id, outputDirectory,
            job.OutputLocation, job.OutputContentType);
        if (artifacts.Count == 0) return [];

        var baseUrl = $"/{job.CapabilitySlug}/jobs/{job.Id}/output";
        return artifacts.Select((artifact, index) => new
        {
            url = index == 0 ? baseUrl : ArtifactUrl(baseUrl, artifact.Name),
            contentType = artifact.ContentType,
            sizeBytes = new FileInfo(artifact.Path).Length,
        }).Cast<object>().ToArray();
    }

    private static string ArtifactUrl(string baseUrl, string name)
    {
        if (name.StartsWith("clip-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var clip))
            return $"{baseUrl}?clip={clip}";
        return $"{baseUrl}?artifact={Uri.EscapeDataString(name)}";
    }

    public static object ProgressSchema => Schema(includeDetails: false);
    public static object DetailSchema => Schema(includeDetails: true);

    private static object Schema(bool includeDetails)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = Field("string", "Job UUID."),
            ["status"] = Field("string", "Compatibility lifecycle status."),
            ["progress"] = Field("number", "Provider progress from 0 to 1; nullable."),
            ["errorMessage"] = Field("string", "Real terminal error when available; nullable."),
            ["durationMs"] = Field("integer", "Elapsed execution duration for terminal jobs; nullable."),
            ["resultJson"] = Field("string", "Compatibility raw result JSON string; nullable."),
            ["terminal"] = Field("boolean", "True for Completed, Failed, Cancelled, Skipped, or TimedOut."),
            ["stage"] = new { type = "string", description = StageDescription,
                @enum = new[] { "admitted", "waiting", "executing", "completed", "failed", "cancelled", "skipped", "timed_out" } },
            ["result"] = Field("object", "resultJson parsed only when it is a JSON object; otherwise null."),
            ["outputs"] = new
            {
                type = "array",
                description = "Downloadable artifacts with service-relative URLs; empty when no file output exists.",
                items = new { type = "object", properties = new { url = Field("string", "Service-relative output URL."), contentType = Field("string", "Artifact media type."), sizeBytes = Field("integer", "Artifact byte length.") } },
            },
            ["links"] = new
            {
                type = "object",
                properties = new { self = Field("string", "Job details URL."), progress = Field("string", "Capability progress URL."), output = Field("string", "Primary output URL."), lifecycleEvents = Field("string", "Lifecycle audit URL.") },
            },
        };
        if (includeDetails)
        {
            properties["capability"] = Field("string", "Capability slug.");
            properties["providerName"] = Field("string", "Provider name.");
            properties["queuedAt"] = Field("string", "Admission timestamp.");
            properties["startedAt"] = Field("string", "Invocation timestamp; nullable.");
            properties["completedAt"] = Field("string", "Terminal timestamp; nullable.");
        }
        return new { type = "object", properties };
    }

    private static object Field(string type, string description) => new { type, description };
}

internal static class JobLongPoll
{
    public const int MaximumSeconds = 60;

    public static bool TryParse(HttpContext context, out int seconds, out IResult? error)
    {
        seconds = 0;
        error = null;
        if (!context.Request.Query.TryGetValue("waitSeconds", out var raw)) return true;
        if (raw.Count != 1 || !int.TryParse(raw[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed))
        {
            error = Results.BadRequest(new
            {
                error = "invalid_wait_seconds",
                message = $"waitSeconds must be an integer; values are clamped to 0..{MaximumSeconds}",
            });
            return false;
        }
        seconds = Math.Clamp(parsed, 0, MaximumSeconds);
        return true;
    }

    public static async Task WaitAsync(
        JobTrackingService tracker, Guid id, int seconds, CancellationToken cancellationToken)
    {
        if (seconds > 0)
            await tracker.WaitForTerminalAsync(id, TimeSpan.FromSeconds(seconds), cancellationToken);
    }
}
