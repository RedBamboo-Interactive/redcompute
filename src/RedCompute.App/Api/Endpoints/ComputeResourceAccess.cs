using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Api.Endpoints;

internal static class ComputeResourceAccess
{
    private const string LocalUserSubject = "local-user";

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

    /// <summary>
    /// Whether a person asked for this, rather than an Agent, an automation, a service or a process a
    /// session started. Reading access says nothing about that: an Agent can read its own conversation.
    /// Three things arrive here as a person, and nothing else does:
    /// a signed login used directly; a browser app's own token, forwarded by the kernel's proxy (Code);
    /// and that same token after one signed hop through a kernel service (Nova's route calling here).
    /// The browser token counts for the loopback owner too, whose token benefits <c>system</c> and keeps
    /// <c>local-user</c> as its subject. A session's own token also descends from a browser token, so the
    /// context has to be exactly one of those shapes: it says which steps the call came through.
    /// </summary>
    internal static bool IsPersonAction(HttpContext context) => IsPersonPresent(context);

    /// <summary>
    /// The access-level contract's person-present predicate, and the one implementation of it:
    /// raising a session's access level, approving a permission and rolling back all require it.
    /// True for an ordinary human access token, and for an execution token RedLeaf minted at
    /// <c>POST /api/auth/execution-token</c> for a browser app the person is using: actor kind
    /// <c>app</c> naming that same app, beneficiary the token's own subject (or <c>system</c> for the
    /// loopback owner), and a first context step <c>browser</c> with the page route, optionally after
    /// exactly one signed <c>redcompute-call</c> hop through a kernel service. False for every Agent
    /// (<c>actor.kind: agent</c>) and automation token, every token a session process holds, service and
    /// infrastructure tokens, and a tokenless loopback request.
    /// </summary>
    internal static bool IsPersonPresent(HttpContext context)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject)) return false;

        var execution = ConfidentialResourcePolicy.ExecutionIdentity(context);
        if (execution is null)
            return ConfidentialResourcePolicy.IsExplicitHuman(context.User)
                && context.User.FindFirst("client_id") is null
                && subject is not (LocalUserSubject or "system")
                && !subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase);

        if (!execution.Actor.Kind.Equals("app", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(execution.Actor.Id, execution.App.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        var beneficiary = execution.Beneficiary;
        var isThePerson = beneficiary.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? subject != LocalUserSubject && string.Equals(beneficiary.Id, subject, StringComparison.OrdinalIgnoreCase)
            : beneficiary.Kind.Equals("system", StringComparison.OrdinalIgnoreCase) && subject == LocalUserSubject;
        if (!isThePerson) return false;

        // A cause is a note about the call, wherever it sits, and not a step the call came through.
        var steps = execution.Context.Where(item => item.Kind != JobCauses.ContextKind).ToList();
        if (steps.Count == 0
            || !steps[0].Kind.Equals("browser", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(steps[0].Route))
            return false;
        if (execution.ParentExecutionId is null) return steps.Count == 1;
        return steps.Count == 2
            && steps[1].Kind.Equals("redcompute-call", StringComparison.OrdinalIgnoreCase)
            && steps[1].Id is null && steps[1].EntityId is null && steps[1].Name is null && steps[1].Route is null;
    }

    internal static bool IsTrustedInfrastructure(HttpContext context)
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
