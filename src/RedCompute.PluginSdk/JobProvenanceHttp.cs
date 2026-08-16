using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

/// <summary>HTTP provenance capture available to custom plugin endpoints.</summary>
public static class JobProvenanceHttp
{
    public static JobProvenance Resolve(HttpContext ctx, string route)
    {
        if (ExecutionJobProvenance.TryResolve(ctx, route, out var executionProvenance))
            return executionProvenance!;

        var userId = ctx.User?.FindFirst("sub")?.Value;
        var realUser = !string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "local-user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase) &&
            !userId.StartsWith("service:", StringComparison.OrdinalIgnoreCase);
        var beneficiary = realUser
            ? new JobBeneficiary("user", userId, ctx.User?.FindFirst("name")?.Value, ctx.User?.FindFirst("picture")?.Value)
            : !string.IsNullOrWhiteSpace(ctx.Request.Headers["X-System-Reason"].FirstOrDefault())
                ? new JobBeneficiary("system", Reason: ctx.Request.Headers["X-System-Reason"].FirstOrDefault())
                : new JobBeneficiary("unknown");

        if (ctx.Request.Headers.TryGetValue("X-Compute-Provenance", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var supplied = JsonSerializer.Deserialize<JobProvenance>(raw.ToString(), JobProvenance.JsonOptions)
                ?? throw new JobProvenanceValidationException("Invalid X-Compute-Provenance");
            var trusted = string.Equals(ctx.User?.FindFirst("client_id")?.Value, "redleaf", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ctx.User?.FindFirst("compute_provenance")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var result = supplied with
            {
                OnBehalfOf = trusted ? supplied.OnBehalfOf : beneficiary,
                Assurance = trusted ? supplied.Assurance : JobProvenanceAssurance.Asserted,
                Trace = supplied.Trace with { RequestId = supplied.Trace.RequestId ?? ctx.TraceIdentifier },
            };
            result.ValidateForNewJob();
            return result;
        }

        var direct = JobProvenance.DirectRedCompute(ctx.Request.Method, route, beneficiary,
            ctx.TraceIdentifier,
            ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault());
        direct.ValidateForNewJob();
        return direct;
    }
}
