using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Api.Endpoints;

internal static class ComputeResourceAccess
{
    public static bool CanReadSession(
        HttpContext context,
        UnifiedSessionInfo session,
        JobRecord? job = null)
    {
        if (IsTrustedInfrastructure(context)) return true;
        if (session.Confidential)
            return ConfidentialResourcePolicy.CanRead(context, new ConfidentialResource(
                session.UserId, session.OwnerAgentId, true, TrustedOwnerAppId(job)));

        var userId = UserInfoHelper.ResolveUserId(context);
        return userId is null or "local-user"
            || session.UserId is null
            || string.Equals(session.UserId, userId, StringComparison.Ordinal);
    }

    public static bool CanReadJob(HttpContext context, JobRecord job)
    {
        if (IsTrustedInfrastructure(context)) return true;
        if (!job.Confidential) return true;
        var provenance = job.CreationProvenance;
        var ownerAgentId = provenance?.Actor.EntityId ?? provenance?.Actor.Id;
        return ConfidentialResourcePolicy.CanRead(context, new ConfidentialResource(
            job.UserId ?? provenance?.OnBehalfOf.Id, ownerAgentId, true,
            TrustedOwnerAppId(job)));
    }

    public static IResult SessionDenied(UnifiedSessionInfo session)
        => session.Confidential
            ? Results.NotFound(new { error = "not_found", message = "Session not found" })
            : Results.Json(new { error = "forbidden", message = "You do not have access to this session" },
                statusCode: StatusCodes.Status403Forbidden);

    public static IResult JobDenied()
        => Results.NotFound(new { error = "not_found", message = "Job not found" });

    private static bool IsTrustedInfrastructure(HttpContext context)
        => string.Equals(context.User.FindFirst("client_id")?.Value, "redleaf",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.User.FindFirst("compute_provenance")?.Value, "true",
                StringComparison.OrdinalIgnoreCase);

    private static string? TrustedOwnerAppId(JobRecord? job)
    {
        var provenance = job?.CreationProvenance;
        if (provenance is null
            || provenance.Assurance is not (JobProvenanceAssurance.Verified
                or JobProvenanceAssurance.BackfilledExact)
            || !(string.Equals(provenance.Origin.Service, "redcompute",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(provenance.Origin.Service, "redleaf",
                    StringComparison.OrdinalIgnoreCase)))
            return null;

        return provenance.Origin.App.Id;
    }
}
