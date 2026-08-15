using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

/// <summary>Maps the suite-wide signed execution identity into RedCompute's immutable job audit model.</summary>
public static class ExecutionJobProvenance
{
    private const string TokenUseClaim = "token_use";
    private const string IdentityClaim = "execution_identity";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryResolve(HttpContext context, string route, out JobProvenance? provenance)
    {
        provenance = null;
        if (!string.Equals(context.User.FindFirst(TokenUseClaim)?.Value, "execution",
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (context.Request.Headers.ContainsKey("X-Compute-Provenance"))
            throw new JobProvenanceValidationException(
                "Authorization execution identity and X-Compute-Provenance cannot be combined");

        var raw = context.User.FindFirst(IdentityClaim)?.Value;
        ExecutionIdentityDto identity;
        try
        {
            identity = JsonSerializer.Deserialize<ExecutionIdentityDto>(raw ?? "", JsonOptions)
                ?? throw new JsonException("execution identity is empty");
        }
        catch (JsonException ex)
        {
            throw new JobProvenanceValidationException($"Invalid signed execution identity: {ex.Message}");
        }

        ValidateIdentity(identity, context.User.FindFirst("sub")?.Value);

        var references = (identity.Context ?? []).Select(item => new JobContextReference(
            item.Kind, item.Id, item.EntityId, item.Name, item.Route)).ToList();
        references.Add(new JobContextReference("execution", identity.ExecutionId));
        if (!string.IsNullOrWhiteSpace(identity.ParentExecutionId))
            references.Add(new JobContextReference("parent-execution", identity.ParentExecutionId));

        provenance = new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin(
                "redcompute",
                new JobAppReference(
                    "app",
                    identity.App.Id,
                    identity.App.EntityId,
                    identity.App.Name,
                    identity.App.Icon,
                    identity.App.Color),
                new JobEntrypoint("http", route, context.Request.Method)),
            new JobActor(
                identity.Actor.Kind,
                identity.Actor.Name,
                identity.Actor.EntityId,
                identity.Actor.Id,
                identity.Actor.Avatar),
            new JobBeneficiary(
                identity.Beneficiary.Kind,
                identity.Beneficiary.Id,
                identity.Beneficiary.Name,
                identity.Beneficiary.Avatar,
                identity.Beneficiary.Reason),
            references,
            new JobTrace(
                identity.Trace?.RequestId ?? context.TraceIdentifier,
                identity.Trace?.CorrelationId
                    ?? context.Request.Headers["X-Correlation-Id"].FirstOrDefault(),
                identity.Trace?.ParentJobId),
            JobProvenanceAssurance.Verified,
            DateTimeOffset.UtcNow);
        provenance.ValidateForNewJob();
        return true;
    }

    private static void ValidateIdentity(ExecutionIdentityDto identity, string? subjectId)
    {
        if (identity.SchemaVersion != 1)
            throw new JobProvenanceValidationException(
                $"Unsupported signed execution identity schema version {identity.SchemaVersion}");
        if (!Guid.TryParse(identity.ExecutionId, out _))
            throw new JobProvenanceValidationException(
                "Signed execution identity executionId must be a GUID");
        if (identity.ParentExecutionId is not null
            && !Guid.TryParse(identity.ParentExecutionId, out _))
            throw new JobProvenanceValidationException(
                "Signed execution identity parentExecutionId must be a GUID when supplied");
        if (identity.Trace?.ParentJobId is not null
            && !Guid.TryParse(identity.Trace.ParentJobId, out _))
            throw new JobProvenanceValidationException(
                "Signed execution identity trace.parentJobId must be a GUID when supplied");
        if (identity.App is null
            || string.IsNullOrWhiteSpace(identity.App.Id)
            || string.IsNullOrWhiteSpace(identity.App.Name))
            throw new JobProvenanceValidationException(
                "Signed execution identity app id and name are required");
        if (identity.Actor is null
            || string.IsNullOrWhiteSpace(identity.Actor.Kind)
            || string.IsNullOrWhiteSpace(identity.Actor.Id)
            || string.IsNullOrWhiteSpace(identity.Actor.Name))
            throw new JobProvenanceValidationException(
                "Signed execution identity actor kind, id, and name are required");
        if (identity.Beneficiary is null
            || string.IsNullOrWhiteSpace(identity.Beneficiary.Kind))
            throw new JobProvenanceValidationException(
                "Signed execution identity beneficiary kind is required");
        if (identity.Beneficiary.Kind.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(identity.Beneficiary.Id))
                throw new JobProvenanceValidationException(
                    "Signed execution identity user beneficiary requires an id");
            if (!string.Equals(identity.Beneficiary.Id, subjectId,
                    StringComparison.OrdinalIgnoreCase))
                throw new JobProvenanceValidationException(
                    "Signed execution identity beneficiary must match the authenticated subject");
        }
        else if (identity.Beneficiary.Kind.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(identity.Beneficiary.Reason))
                throw new JobProvenanceValidationException(
                    "Signed execution identity system beneficiary requires a reason");
        }
        else
        {
            throw new JobProvenanceValidationException(
                "Signed execution identity beneficiary kind must be user or system");
        }
        if (identity.Context?.Count > 16)
            throw new JobProvenanceValidationException(
                "Signed execution identity accepts at most 16 context references");
        if (identity.Context?.Any(item => item is null
                || string.IsNullOrWhiteSpace(item.Kind)) == true)
            throw new JobProvenanceValidationException(
                "Every signed execution context reference requires a kind");
    }

    private sealed record ExecutionIdentityDto(
        int SchemaVersion,
        string ExecutionId,
        ExecutionAppDto App,
        ExecutionActorDto Actor,
        ExecutionBeneficiaryDto Beneficiary,
        IReadOnlyList<ExecutionContextDto>? Context,
        string? ParentExecutionId,
        ExecutionTraceDto? Trace);

    private sealed record ExecutionAppDto(
        string Id, string Name, string? EntityId, string? Icon, string? Color);

    private sealed record ExecutionActorDto(
        string Kind, string Id, string Name, string? EntityId, string? Avatar);

    private sealed record ExecutionBeneficiaryDto(
        string Kind, string? Id, string? Name, string? Avatar, string? Reason);

    private sealed record ExecutionContextDto(
        string Kind, string? Id, string? EntityId, string? Name, string? Route);

    private sealed record ExecutionTraceDto(
        string? RequestId, string? CorrelationId, string? ParentJobId);
}
