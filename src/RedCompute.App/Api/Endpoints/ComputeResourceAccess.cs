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

    /// <summary>
    /// A caller with no identity of its own: no token, the tokenless loopback principal
    /// (LocalDefault, <c>local-user</c>), or an identity RedLeaf signed on such a caller's behalf
    /// (beneficiary <c>system</c>: a browser token, or the compute proxy). Any process on this
    /// machine can be one, so what it starts never runs above Ask, whatever the policy default.
    /// </summary>
    internal static bool IsAnonymous(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || string.Equals(user.Identity.AuthenticationType, ConfidentialResourcePolicy.LocalDefaultAuthenticationType, StringComparison.Ordinal))
            return true;
        if (string.Equals(user.FindFirst("sub")?.Value, LocalUserSubject, StringComparison.OrdinalIgnoreCase)) return true;
        // RedLeaf turns a tokenless loopback caller into a signed system-beneficiary identity in two
        // places: the browser-token mint and the compute proxy. Both say so in the signed reason.
        if (ConfidentialResourcePolicy.ExecutionIdentity(context) is { } execution)
            return execution.Beneficiary.Kind.Equals("system", StringComparison.OrdinalIgnoreCase)
                && (execution.Context.Any(item => item.Kind.Equals("browser", StringComparison.OrdinalIgnoreCase))
                    || execution.Beneficiary.Reason?.Contains("LocalDefault", StringComparison.OrdinalIgnoreCase) == true);
        return false;
    }

    /// <summary>The cap an anonymous caller's session or one-shot gets on top of any it asked for.</summary>
    internal static string? AnonymousCap(HttpContext context, string? requestedCap)
        => IsAnonymous(context)
            ? requestedCap is null ? SessionAccessLevels.Ask : SessionAccessLevels.Lower(requestedCap, SessionAccessLevels.Ask)
            : requestedCap;

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

    /// <summary>Whether a person asked for a rollback: exactly <see cref="IsPersonPresent"/>.</summary>
    internal static bool IsPersonAction(HttpContext context) => IsPersonPresent(context);

    /// <summary>
    /// The access-level contract's person-present predicate, and the one implementation of it:
    /// raising a session's access level and approving a permission require it. Decided from the
    /// verified token alone, structured as RedLeaf's <c>AgentPermissionAccess.IsPersonPresent</c>
    /// but accepting every browser app, since Nova's and Code's web apps show the approval cards:
    /// <list type="bullet">
    /// <item>An ordinary user session of a real account is a person. The anonymous loopback
    /// principal (<c>LocalDefault</c>, <c>local-user</c>), <c>system</c> and <c>service:*</c>
    /// subjects are not: an Agent that drops its bearer on loopback lands there.</item>
    /// <item>Nothing derived from LocalDefault is a person either. RedLeaf's
    /// <c>POST /api/auth/execution-token</c> mints a browser token for a tokenless loopback caller
    /// (beneficiary <c>system</c>, subject <c>local-user</c>, its route taken from request headers
    /// anyone can set), so an Agent with a shell can obtain one. The subject and beneficiary checks
    /// below refuse it, and there is deliberately no loopback-owner exception.</item>
    /// <item>An execution token is a person only when it is a browser app's own token, minted by
    /// RedLeaf's <c>POST /api/auth/execution-token</c> from a user session: actor kind <c>app</c>
    /// naming that same app, beneficiary kind <c>user</c> equal to the token subject, and a
    /// <c>browser</c> context. Never an <c>agent</c> or <c>automation</c> actor.</item>
    /// </list>
    /// Stricter than "has a browser reference" in one way: a session's own process holds a token
    /// derived from the browser token that started it, and that token still carries the browser
    /// step. So the context must be the browser step (with its page route) alone, or after exactly
    /// one signed <c>redcompute-call</c> hop through a kernel service (Nova's routes calling here);
    /// causes are notes, not steps. Anything else, a session process included, is not a person.
    /// </summary>
    internal static bool IsPersonPresent(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || string.Equals(user.Identity.AuthenticationType, ConfidentialResourcePolicy.LocalDefaultAuthenticationType, StringComparison.Ordinal))
            return false;
        var subject = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject is LocalUserSubject or "system"
            || subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
            return false;

        var execution = ConfidentialResourcePolicy.ExecutionIdentity(context);
        var claimsExecution = user.HasClaim(claim => claim.Type == ExecutionIdentityClaims.TokenUseClaim);
        if (execution is null)
            // An execution token whose identity did not verify is nobody; a plain login is a person,
            // unless it is a client's (service) token.
            return !claimsExecution && ConfidentialResourcePolicy.IsExplicitHuman(user) && user.FindFirst("client_id") is null;

        return execution.Beneficiary.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
            && string.Equals(execution.Beneficiary.Id, subject, StringComparison.OrdinalIgnoreCase)
            && IsBrowserAppToken(execution);
    }

    /// <summary>A browser app's own token, directly or after one kernel hop; never an Agent's or automation's.</summary>
    private static bool IsBrowserAppToken(ExecutionIdentity execution)
    {
        if (execution.Actor.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase)
            || execution.Actor.Kind.Equals("automation", StringComparison.OrdinalIgnoreCase)
            || !execution.Actor.Kind.Equals("app", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(execution.Actor.Id, execution.App.Id, StringComparison.OrdinalIgnoreCase))
            return false;

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
