using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RedCompute.App.Data;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Services.Jobs;

/// <summary>
/// Content-free daily job counts for RedLeaf Usage Insights
/// (redleaf docs/usage-insights-architecture.md, "RedCompute: counts-only endpoint").
///
/// The only Jobs data this service reads is:
/// <list type="bullet">
/// <item>filters: <c>UserId</c>, <c>Confidential</c>, <c>ParentJobId</c>, <c>QueuedAt</c>, <c>CapabilitySlug</c>;</item>
/// <item>projection: <c>CapabilitySlug</c>, <c>ProviderName</c>, <c>Status</c>, <c>QueuedAt</c>,
/// <c>ProviderStartedAt</c>, <c>CompletedAt</c>, <c>OutputContentType</c>;</item>
/// <item>provenance: <c>json_extract(CreationProvenanceJson, '$.actor.kind' | '$.actor.id' | '$.actor.entityId')</c>,
/// evaluated inside SQLite so the rest of the provenance snapshot never leaves the database.</item>
/// </list>
/// Every returned value is a closed enum, a configured capability slug, a UTC day or a bounded count.
/// </summary>
public sealed class UsageCountsService
{
    public const int MaxWindowDays = 62;

    // Timestamps are stored as ISO-8601 text carrying their original offset, so the SQL range is a
    // coarse prefilter widened by a day on each side; exact UTC-day bucketing happens in memory.
    private static readonly TimeSpan OffsetMargin = TimeSpan.FromDays(1);

    private readonly Func<RedComputeDbContext> _dbFactory;
    private readonly Func<DateTimeOffset> _clock;

    public UsageCountsService(Func<RedComputeDbContext>? dbFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _dbFactory = dbFactory ?? (() => new RedComputeDbContext());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public UsageCountsResult GetDailyCounts(string? since, string? until, string? beneficiary, string? capability)
    {
        if (string.IsNullOrWhiteSpace(beneficiary))
            return UsageCountsResult.Invalid("beneficiary_required", "beneficiary is required");
        if (!TryParseDay(since, out var sinceDay) || !TryParseDay(until, out var untilDay))
            return UsageCountsResult.Invalid("invalid_day", "since and until are required UTC days in YYYY-MM-DD form");
        if (untilDay < sinceDay)
            return UsageCountsResult.Invalid("invalid_window", "until must not be before since");
        if (untilDay.DayNumber - sinceDay.DayNumber > MaxWindowDays)
            return UsageCountsResult.Invalid("window_too_large", $"since and until may be at most {MaxWindowDays} days apart");

        var now = _clock();
        if (untilDay >= DateOnly.FromDateTime(now.UtcDateTime))
            return UsageCountsResult.Invalid("window_includes_today", "until must be before the current UTC day; today is still open");

        var lower = new DateTimeOffset(sinceDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - OffsetMargin;
        var upper = new DateTimeOffset(untilDay.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) + OffsetMargin;
        var slug = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();

        using var db = _dbFactory();
        var rows = Query(db, beneficiary.Trim(), slug, lower, upper).ToList();
        return new UsageCountsResult(Aggregate(rows, beneficiary.Trim(), sinceDay, untilDay, now), null, null);
    }

    internal static IQueryable<UsageJobRow> Query(RedComputeDbContext db, string beneficiary,
        string? capability, DateTimeOffset lower, DateTimeOffset upper)
    {
        var jobs = db.Jobs.AsNoTracking().Where(j =>
            j.UserId == beneficiary
            && !j.Confidential
            && j.ParentJobId == null
            && j.QueuedAt >= lower
            && j.QueuedAt < upper);
        if (capability != null) jobs = jobs.Where(j => j.CapabilitySlug == capability);

        return jobs.Select(j => new UsageJobRow(
            j.CapabilitySlug,
            j.ProviderName,
            j.Status,
            j.QueuedAt,
            j.ProviderStartedAt,
            j.CompletedAt,
            j.OutputContentType,
            RedComputeDbContext.JsonExtract(j.CreationProvenanceJson, "$.actor.kind"),
            RedComputeDbContext.JsonExtract(j.CreationProvenanceJson, "$.actor.id"),
            RedComputeDbContext.JsonExtract(j.CreationProvenanceJson, "$.actor.entityId")));
    }

    internal static UsageDailyCounts Aggregate(IEnumerable<UsageJobRow> rows, string beneficiary,
        DateOnly since, DateOnly until, DateTimeOffset now)
    {
        var buckets = new Dictionary<(DateOnly Day, UsageGroupKey Key), (int Jobs, long Ticks)>();
        foreach (var row in rows)
        {
            // Host clock jumps can stamp jobs in the future; they are not counted until real time
            // catches up with them.
            if (row.QueuedAt > now) continue;
            var day = DateOnly.FromDateTime(row.QueuedAt.UtcDateTime);
            if (day < since || day > until) continue;

            var key = new UsageGroupKey(row.CapabilitySlug, NormalizeProvider(row.ProviderName),
                NormalizeStatus(row.Status), MediaFamily(row.OutputContentType),
                Initiator(row.ActorKind, row.ActorId, row.ActorEntityId, beneficiary));
            buckets.TryGetValue((day, key), out var bucket);
            buckets[(day, key)] = (bucket.Jobs + 1, bucket.Ticks + ExecutionTicks(row));
        }

        var days = new List<UsageDay>();
        for (var day = since; day <= until; day = day.AddDays(1))
        {
            var groups = buckets.Where(b => b.Key.Day == day)
                .Select(b => new UsageGroup(b.Key.Key.Capability, b.Key.Key.Provider, b.Key.Key.Status,
                    b.Key.Key.Media, b.Key.Key.Initiator, b.Value.Jobs, b.Value.Ticks / TimeSpan.TicksPerSecond))
                .OrderBy(g => g.Capability, StringComparer.Ordinal)
                .ThenBy(g => g.Provider, StringComparer.Ordinal)
                .ThenBy(g => g.Status, StringComparer.Ordinal)
                .ThenBy(g => g.Media, StringComparer.Ordinal)
                .ThenBy(g => g.Initiator, StringComparer.Ordinal)
                .ToList();
            days.Add(new UsageDay(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), groups));
        }
        return new UsageDailyCounts(days);
    }

    internal static string NormalizeProvider(string? providerName)
    {
        var compact = new string((providerName ?? "").Where(char.IsLetterOrDigit).ToArray())
            .ToLowerInvariant();
        return compact switch
        {
            "comfyui" => "comfyui",
            "codex" => "codex",
            "claudecode" => "claude-code",
            "opencode" => "opencode",
            _ => "other",
        };
    }

    internal static string NormalizeStatus(JobStatus status) => status switch
    {
        JobStatus.Completed => "succeeded",
        JobStatus.Failed or JobStatus.TimedOut => "failed",
        JobStatus.Cancelled => "cancelled",
        _ => "other",
    };

    internal static string MediaFamily(string? contentType)
    {
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type == "image/webp") return "animation";
        if (type.StartsWith("image/", StringComparison.Ordinal)) return "image";
        if (type.StartsWith("video/", StringComparison.Ordinal)) return "video";
        if (type.StartsWith("audio/", StringComparison.Ordinal)) return "audio";
        return "none";
    }

    /// <summary>
    /// <c>agent</c> for agent actors; <c>you</c> for a user-facing app acting for the beneficiary or
    /// the beneficiary acting directly; <c>other</c> for legacy unknown, plugin, system and the rest.
    /// </summary>
    internal static string Initiator(string? actorKind, string? actorId, string? actorEntityId, string beneficiary)
        => actorKind?.Trim().ToLowerInvariant() switch
        {
            "agent" => "agent",
            "app" => "you",
            "user" when string.Equals(actorId, beneficiary, StringComparison.Ordinal)
                || string.Equals(actorEntityId, beneficiary, StringComparison.Ordinal) => "you",
            _ => "other",
        };

    private static long ExecutionTicks(UsageJobRow row)
        => row.Status == JobStatus.Completed && row.ProviderStartedAt is { } started && row.CompletedAt is { } completed
            ? Math.Max(0, (completed - started).Ticks)
            : 0;

    private static bool TryParseDay(string? value, out DateOnly day)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private sealed record UsageGroupKey(string Capability, string Provider, string Status, string Media, string Initiator);
}

internal sealed record UsageJobRow(
    string CapabilitySlug,
    string ProviderName,
    JobStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? ProviderStartedAt,
    DateTimeOffset? CompletedAt,
    string? OutputContentType,
    string? ActorKind,
    string? ActorId,
    string? ActorEntityId);

public sealed record UsageCountsResult(UsageDailyCounts? Counts, string? Error, string? Message)
{
    public static UsageCountsResult Invalid(string error, string message) => new(null, error, message);
}

public sealed record UsageDailyCounts(IReadOnlyList<UsageDay> Days);

public sealed record UsageDay(string Day, IReadOnlyList<UsageGroup> Groups);

public sealed record UsageGroup(
    string Capability,
    string Provider,
    string Status,
    string Media,
    string Initiator,
    int Jobs,
    long ExecutionSeconds);
