using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class ProvenanceCapture
{
    public const string HeaderName = "X-Compute-Provenance";

    public static async Task<JobProvenance> ResolveAsync(HttpContext ctx, string? route = null)
    {
        var routeTemplate = route
            ?? (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? ctx.Request.Path.Value
            ?? "unknown";

        if (ExecutionJobProvenance.TryResolve(ctx, routeTemplate, out var executionProvenance))
            return executionProvenance!;

        if (ctx.Request.Headers.TryGetValue(HeaderName, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var supplied = JsonSerializer.Deserialize<JobProvenance>(raw.ToString(), JobProvenance.JsonOptions)
                    ?? throw new JsonException("empty provenance");
                if (IsTrustedRedLeafService(ctx))
                {
                    var verified = supplied with
                    {
                        // RedLeaf may deliberately mark legacy/referrer-derived identity as
                        // asserted. Trust the service to verify what it stamped, never upgrade
                        // a lower assurance it intentionally preserved.
                        Assurance = supplied.Assurance,
                        Trace = supplied.Trace with
                        {
                            RequestId = supplied.Trace.RequestId ?? ctx.TraceIdentifier,
                            CorrelationId = supplied.Trace.CorrelationId ?? CorrelationId(ctx),
                        },
                    };
                    verified.ValidateForNewJob();
                    return verified;
                }

                // Untrusted callers may make an asserted structured claim, but never a verified
                // one. A real authenticated principal overrides the claimed beneficiary; without
                // one, the structured user/system beneficiary remains explicitly asserted.
                var principalBeneficiary = await ResolveBeneficiaryAsync(ctx);
                var assertedBeneficiary = principalBeneficiary.Kind == "user"
                    ? principalBeneficiary
                    : supplied.OnBehalfOf;
                var asserted = supplied with
                {
                    OnBehalfOf = assertedBeneficiary,
                    Assurance = JobProvenanceAssurance.Asserted,
                    Trace = supplied.Trace with
                    {
                        RequestId = ctx.TraceIdentifier,
                        CorrelationId = CorrelationId(ctx),
                    },
                    CapturedAt = DateTimeOffset.UtcNow,
                };
                asserted.ValidateForNewJob();
                return asserted;
            }
            catch (JsonException ex)
            {
                throw new JobProvenanceValidationException($"Invalid {HeaderName}: {ex.Message}");
            }
        }

        var (userId, userName, avatar) = await UserInfoHelper.ResolveFromContext(ctx);
        JobBeneficiary beneficiary;
        if (!string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "local-user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase))
        {
            beneficiary = new JobBeneficiary("user", userId, userName, avatar);
        }
        else
        {
            var reason = ctx.Request.Headers["X-System-Reason"].FirstOrDefault();
            beneficiary = !string.IsNullOrWhiteSpace(reason)
                ? new JobBeneficiary("system", Reason: reason)
                : new JobBeneficiary("unknown");
        }

        var provenance = JobProvenance.DirectRedCompute(
            ctx.Request.Method, routeTemplate, beneficiary,
            ctx.Request.Headers["X-Caller-Info"].FirstOrDefault(),
            ctx.TraceIdentifier, CorrelationId(ctx));
        provenance.ValidateForNewJob();
        return provenance;
    }

    public static bool IsTrustedRedLeafService(HttpContext ctx)
        => string.Equals(ctx.User?.FindFirst("client_id")?.Value, "redleaf", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(ctx.User?.FindFirst("compute_provenance")?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static async Task<JobBeneficiary> ResolveBeneficiaryAsync(HttpContext ctx)
    {
        var (userId, userName, avatar) = await UserInfoHelper.ResolveFromContext(ctx);
        if (!string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "local-user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase))
            return new JobBeneficiary("user", userId, userName, avatar);

        var reason = ctx.Request.Headers["X-System-Reason"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(reason)
            ? new JobBeneficiary("system", Reason: reason)
            : new JobBeneficiary("unknown");
    }

    private static string? CorrelationId(HttpContext ctx)
        => ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault();
}
