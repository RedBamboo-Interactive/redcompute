using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedCompute.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter<JobProvenanceAssurance>))]
public enum JobProvenanceAssurance
{
    [JsonStringEnumMemberName("verified")]
    Verified,
    [JsonStringEnumMemberName("asserted")]
    Asserted,
    [JsonStringEnumMemberName("backfilled-exact")]
    BackfilledExact,
    [JsonStringEnumMemberName("backfilled-inferred")]
    BackfilledInferred,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

public sealed record JobAppReference(
    string Kind,
    string Id,
    string? EntityId,
    string NameSnapshot,
    string? IconSnapshot = null,
    string? ColorSnapshot = null);

public sealed record JobEntrypoint(
    string Kind,
    string Route,
    string? Method = null);

public sealed record JobOrigin(
    string Service,
    JobAppReference App,
    JobEntrypoint Entrypoint);

public sealed record JobActor(
    string Kind,
    string NameSnapshot,
    string? EntityId = null,
    string? Id = null,
    string? AvatarSnapshot = null);

public sealed record JobBeneficiary(
    string Kind,
    string? Id = null,
    string? NameSnapshot = null,
    string? AvatarSnapshot = null,
    string? Reason = null);

public sealed record JobContextReference(
    string Kind,
    string? Id = null,
    string? EntityId = null,
    string? NameSnapshot = null,
    string? Route = null);

public sealed record JobTrace(
    string? RequestId = null,
    string? CorrelationId = null,
    string? ParentJobId = null);

/// <summary>
/// Versioned, immutable audit identity captured at one job invocation boundary.
/// Stable references and display snapshots deliberately coexist: references link
/// to today's entities while snapshots preserve what the operator saw at the time.
/// </summary>
public sealed record JobProvenance(
    int SchemaVersion,
    JobOrigin Origin,
    JobActor Actor,
    JobBeneficiary OnBehalfOf,
    IReadOnlyList<JobContextReference> Context,
    JobTrace Trace,
    JobProvenanceAssurance Assurance,
    DateTimeOffset CapturedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static JobProvenance? FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<JobProvenance>(json, JsonOptions);

    public void ValidateForNewJob()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new JobProvenanceValidationException($"Unsupported provenance schema version {SchemaVersion}");
        if (string.IsNullOrWhiteSpace(Origin.Service))
            throw new JobProvenanceValidationException("origin.service is required");
        if (string.IsNullOrWhiteSpace(Origin.App.Kind) || string.IsNullOrWhiteSpace(Origin.App.Id) ||
            string.IsNullOrWhiteSpace(Origin.App.NameSnapshot))
            throw new JobProvenanceValidationException("origin.app kind, id, and nameSnapshot are required");
        if (string.IsNullOrWhiteSpace(Origin.Entrypoint.Kind) || string.IsNullOrWhiteSpace(Origin.Entrypoint.Route))
            throw new JobProvenanceValidationException("origin.entrypoint kind and route are required");
        if (string.IsNullOrWhiteSpace(Actor.Kind) || string.IsNullOrWhiteSpace(Actor.NameSnapshot) ||
            (string.IsNullOrWhiteSpace(Actor.EntityId) && string.IsNullOrWhiteSpace(Actor.Id)))
            throw new JobProvenanceValidationException("actor kind, identity, and nameSnapshot are required");

        var beneficiaryKind = OnBehalfOf.Kind.Trim().ToLowerInvariant();
        if (beneficiaryKind == "user")
        {
            if (string.IsNullOrWhiteSpace(OnBehalfOf.Id) ||
                string.Equals(OnBehalfOf.Id, "local-user", StringComparison.OrdinalIgnoreCase))
                throw new JobProvenanceValidationException("A user beneficiary requires a real user id; local-user is not audit attribution");
        }
        else if (beneficiaryKind == "system")
        {
            if (string.IsNullOrWhiteSpace(OnBehalfOf.Reason))
                throw new JobProvenanceValidationException("A system beneficiary requires an explicit reason");
        }
        else
        {
            throw new JobProvenanceValidationException("New jobs require a user beneficiary or an explicit system beneficiary");
        }

        if (CapturedAt == default)
            throw new JobProvenanceValidationException("capturedAt is required");
    }

    public static JobProvenance DirectRedCompute(
        string method,
        string route,
        JobBeneficiary beneficiary,
        string? callerInfo,
        string? requestId,
        string? correlationId)
    {
        var caller = string.IsNullOrWhiteSpace(callerInfo) ? "Direct RedCompute client" : callerInfo.Trim();
        return new JobProvenance(
            CurrentSchemaVersion,
            new JobOrigin(
                "redcompute",
                new JobAppReference("direct-client", "direct-redcompute-api", null, caller),
                new JobEntrypoint("http", route, method)),
            new JobActor("app", caller, Id: "direct-redcompute-api"),
            beneficiary,
            [],
            new JobTrace(requestId, correlationId),
            JobProvenanceAssurance.Asserted,
            DateTimeOffset.UtcNow);
    }
}

public sealed class JobProvenanceValidationException(string message) : Exception(message);
