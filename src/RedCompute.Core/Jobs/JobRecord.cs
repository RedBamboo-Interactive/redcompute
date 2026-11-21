using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RedCompute.Core.Jobs;

public class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string CapabilitySlug { get; set; }
    public required string ProviderName { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string InputJson { get; set; } = "{}";
    public string? OutputLocation { get; set; }
    public long? OutputSizeBytes { get; set; }
    public string? OutputContentType { get; set; }

    public double? Progress { get; set; }
    public DateTimeOffset? ProviderStartedAt { get; set; }
    public string? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
    public string? CallerInfo { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? IdempotencyScope { get; set; }
    public string? IdempotencyFingerprint { get; set; }

    /// <summary>
    /// True when Compute owns the durable lifecycle envelope while another trusted
    /// service performs the work. Automation, maintenance, and connector jobs use
    /// this path instead of pretending their work belongs to a generation provider.
    /// </summary>
    public bool ExternalExecution { get; set; }
    public Guid? ParentJobId { get; set; }
    public string? LeaseOwner { get; set; }
    [JsonIgnore]
    public string? LeaseTokenHash { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }

    [JsonIgnore]
    public string? CreationProvenanceJson { get; set; }

    [NotMapped]
    public JobProvenance? CreationProvenance
    {
        get => JobProvenance.FromJson(CreationProvenanceJson);
        set => CreationProvenanceJson = value?.ToJson();
    }

    [NotMapped, JsonIgnore]
    public bool IsIdempotencyReuse { get; set; }

    public string? Name { get; set; }
    public string? Rationale { get; set; }
    public double? CostUsd { get; set; }

    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public bool Confidential { get; set; }
    // Exempts the job's AI session from idle parking. Presence sessions (Nova LIVE, heartbeat)
    // stay resident; every other session keeps the default policy.
    public bool KeepResident { get; set; }
    // The AI session that delegated this job's session and waits on it. While this session
    // still has work, idle parking keeps the supervisor resident. Last binding wins.
    public string? SupervisorSessionId { get; set; }

    /// <summary>
    /// The node that executes the job, stamped when the job is created. Null on a row older than
    /// node ids (and on a run whose node identity is not persisted): it reads as the registry's
    /// legacy node, this machine.
    /// </summary>
    public string? NodeId { get; set; }

    /// <summary>
    /// Why the job runs on <see cref="NodeId"/> (<see cref="PlacementResolution.Why"/>), recorded only
    /// when the request carried <c>placement</c>; display only. Null on every other job, and never
    /// written as JSON then, so a job created without <c>placement</c> serialises as it always did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementWhy { get; set; }

    public long? DurationMs => CompletedAt.HasValue && StartedAt.HasValue
        ? (long)(CompletedAt.Value - StartedAt.Value).TotalMilliseconds
        : null;
}
