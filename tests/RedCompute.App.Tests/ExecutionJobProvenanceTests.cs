using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ExecutionJobProvenanceTests
{
    [Fact]
    public void Signed_execution_identity_maps_to_verified_job_provenance()
    {
        var identity = Identity();
        var context = Request(identity);

        Assert.True(ExecutionJobProvenance.TryResolve(
            context, "/image/generate", out var provenance));

        Assert.NotNull(provenance);
        Assert.Equal(JobProvenanceAssurance.Verified, provenance!.Assurance);
        Assert.Equal("redcompute", provenance.Origin.Service);
        Assert.Equal("nova", provenance.Origin.App.Id);
        Assert.Equal("agent", provenance.Actor.Kind);
        Assert.Equal("nova", provenance.Actor.Id);
        Assert.Equal("user-1", provenance.OnBehalfOf.Id);
        Assert.Contains(provenance.Context,
            item => item.Kind == "discussion" && item.Id == "discussion-1");
        Assert.Contains(provenance.Context,
            item => item.Kind == "execution" && item.Id == identity.ExecutionId);
        Assert.Equal("/image/generate", provenance.Origin.Entrypoint.Route);
        Assert.Equal("POST", provenance.Origin.Entrypoint.Method);
        Assert.Equal("signed-request", provenance.Trace.RequestId);
        Assert.Equal("signed-correlation", provenance.Trace.CorrelationId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", provenance.Trace.ParentJobId);
    }

    [Theory]
    [InlineData("beneficiary", "beneficiary")]
    [InlineData("agent", "agent")]
    [InlineData("other-person", "other-person")]
    [InlineData("app", "app")]
    [InlineData("you", null)]
    public void The_signed_cause_becomes_the_jobs_own_property(string signed, string? expected)
    {
        var identity = Identity() with
        {
            Context = [new ExecutionContextReference("discussion", "discussion-1"),
                new ExecutionContextReference(JobCauses.ContextKind, signed)],
        };

        Assert.True(ExecutionJobProvenance.TryResolve(Request(identity), "/image/generate", out var provenance));

        Assert.Equal(expected, provenance!.Cause);
        Assert.DoesNotContain(provenance.Context, item => item.Kind == JobCauses.ContextKind);
        Assert.Equal(expected, JobProvenance.FromJson(provenance.ToJson())!.Cause);
    }

    [Fact]
    public void A_token_without_a_cause_records_none_even_for_an_agent_actor()
    {
        Assert.True(ExecutionJobProvenance.TryResolve(Request(Identity()), "/image/generate", out var provenance));

        Assert.Equal("agent", provenance!.Actor.Kind);
        Assert.Null(provenance.Cause);
        Assert.DoesNotContain("\"cause\"", provenance.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_from_an_untrusted_caller_cannot_claim_a_person_started_it()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));
        var claimed = JobProvenance.DirectRedCompute("POST", "/image/generate",
            new JobBeneficiary("user", "user-1"), null, null) with { Cause = JobCauses.Beneficiary };
        context.Request.Headers["X-Compute-Provenance"] = claimed.ToJson();

        Assert.Null(JobProvenanceHttp.Resolve(context, "/image/generate").Cause);
    }

    [Fact]
    public void A_direct_caller_records_no_cause()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));

        Assert.Null(JobProvenanceHttp.Resolve(context, "/image/generate").Cause);
    }

    [Fact]
    public void Signed_execution_identity_rejects_an_ambiguous_legacy_header()
    {
        var context = Request(Identity());
        context.Request.Headers["X-Compute-Provenance"] = "{}";

        var error = Assert.Throws<JobProvenanceValidationException>(() =>
            ExecutionJobProvenance.TryResolve(context, "/image/generate", out _));

        Assert.Contains("cannot be combined", error.Message);
    }

    [Fact]
    public void Ordinary_request_falls_through_to_legacy_capture()
    {
        Assert.False(ExecutionJobProvenance.TryResolve(
            Request(), "/image/generate", out var provenance));
        Assert.Null(provenance);
    }

    [Fact]
    public void Malformed_execution_claim_fails_with_a_provenance_validation_error()
    {
        var context = Request();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("token_use", "execution"),
            new Claim("execution_identity", """
                {"schemaVersion":1,"executionId":"not-a-guid","app":null,
                 "actor":null,"beneficiary":null,"context":[]}
                """),
        ], "test"));

        var error = Assert.Throws<JobProvenanceValidationException>(() =>
            ExecutionJobProvenance.TryResolve(context, "/image/generate", out _));

        Assert.Contains("executionId must be a GUID", error.Message);
    }

    private static DefaultHttpContext Request(ExecutionIdentity? identity = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.TraceIdentifier = "request-1";
        context.Request.Headers["X-Correlation-Id"] = "correlation-1";
        if (identity is not null)
        {
            var options = new JwtOptions
            {
                SigningKey = "execution-job-provenance-tests-require-a-long-key-1234567890",
                ClockSkew = TimeSpan.Zero,
            };
            var jwt = new JwtService(options);
            var token = new ExecutionTokenIssuer(jwt, options).Issue(identity,
                new ExecutionPrincipal("user-1", "user@example.test", "Laurent", ["admin"]));
            context.User = jwt.ValidateToken(token.AccessToken)!;
        }
        return context;
    }

    private static ExecutionIdentity Identity() => new(
        ExecutionIdentity.CurrentSchemaVersion,
        Guid.NewGuid().ToString(),
        new ExecutionAppIdentity("nova", "Nova", "app-entity", "ph-star", "nova"),
        new ExecutionActorIdentity("agent", "nova", "Nova", "agent-entity", "/nova.png"),
        new ExecutionBeneficiaryIdentity("user", "user-1", "Laurent", "/user.png"),
        [new ExecutionContextReference("discussion", "discussion-1", "discussion-entity", "Chat")],
        Trace: new ExecutionTrace(
            "signed-request",
            "signed-correlation",
            "11111111-1111-1111-1111-111111111111"));
}
