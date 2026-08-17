using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RedCompute.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter<JobEventKind>))]
public enum JobEventKind
{
    [JsonStringEnumMemberName("created")]
    Created,
    [JsonStringEnumMemberName("scheduled")]
    Scheduled,
    [JsonStringEnumMemberName("claimed")]
    Claimed,
    [JsonStringEnumMemberName("started")]
    Started,
    [JsonStringEnumMemberName("resumed")]
    Resumed,
    [JsonStringEnumMemberName("retried")]
    Retried,
    [JsonStringEnumMemberName("rerun")]
    Rerun,
    [JsonStringEnumMemberName("reused")]
    Reused,
    [JsonStringEnumMemberName("backfilled")]
    Backfilled,
    [JsonStringEnumMemberName("lease-renewed")]
    LeaseRenewed,
    [JsonStringEnumMemberName("progress")]
    Progress,
    [JsonStringEnumMemberName("child-linked")]
    ChildLinked,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("skipped")]
    Skipped,
    [JsonStringEnumMemberName("timed-out")]
    TimedOut,
}

/// <summary>Append-only audit event. This entity is inserted and never updated.</summary>
public sealed class JobLifecycleEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public JobEventKind Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string? ProvenanceJson { get; set; }

    [NotMapped]
    public JobProvenance? Provenance
    {
        get => JobProvenance.FromJson(ProvenanceJson);
        set => ProvenanceJson = value?.ToJson();
    }

    public string DataJson { get; set; } = "{}";
}

public enum JobOutboxKind
{
    JobProjection,
    JobEvent,
}

/// <summary>Durable delivery intent committed in the same transaction as its job mutation.</summary>
public sealed class JobOutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public JobOutboxKind Kind { get; set; }
    public Guid JobId { get; set; }
    public Guid? JobEventId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed record JobSubmission(
    string CapabilitySlug,
    string ProviderName,
    string InputJson,
    JobProvenance Provenance,
    string? IdempotencyKey = null,
    string? Name = null,
    string? Rationale = null,
    bool ExternalExecution = false,
    string? IdempotencyScope = null,
    bool Confidential = false);

public sealed record ExternalJobLease(
    Guid JobId,
    string LeaseToken,
    string WorkerId,
    DateTimeOffset ExpiresAt,
    int Attempt);

public sealed class JobLeaseConflictException(Guid jobId, string message) : Exception(message)
{
    public Guid JobId { get; } = jobId;
}

public sealed class JobLeaseValidationException(Guid jobId, string message) : Exception(message)
{
    public Guid JobId { get; } = jobId;
}

public sealed class IdempotencyConflictException(string key, Guid existingJobId)
    : Exception($"Idempotency key '{key}' conflicts with existing job {existingJobId}")
{
    public string Key { get; } = key;
    public Guid ExistingJobId { get; } = existingJobId;
}
