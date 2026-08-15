using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Jobs;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RerunExecutionIdentityTests
{
    private const string SigningKey =
        "rerun-execution-identity-tests-require-a-long-signing-key-1234567890";

    [Fact]
    public void Signed_rerun_derives_a_child_token_without_a_legacy_provenance_header()
    {
        var options = new JwtOptions { SigningKey = SigningKey, ClockSkew = TimeSpan.Zero };
        var jwt = new JwtService(options);
        var issuer = new ExecutionTokenIssuer(jwt, options);
        var services = new ServiceCollection()
            .AddSingleton<IExecutionTokenIssuer>(issuer)
            .BuildServiceProvider();
        var parent = ParentIdentity();
        var parentToken = issuer.Issue(parent,
            new ExecutionPrincipal("user-1", "user@example.test", "Laurent", ["admin"]));
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = jwt.ValidateToken(parentToken.AccessToken)!,
        };
        var parentJobId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/image/generate");

        using (ExecutionContextScope.Push(parent))
            GlobalEndpoints.ApplyRerunCredentials(
                context, request, AssertedProvenance(), parentJobId);

        Assert.False(request.Headers.Contains("X-Compute-Provenance"));
        var bearer = Assert.Single(request.Headers.GetValues("Authorization"));
        Assert.StartsWith("Bearer ", bearer);
        var principal = jwt.ValidateToken(bearer["Bearer ".Length..]);
        Assert.NotNull(principal);
        Assert.True(ExecutionIdentityClaims.TryRead(principal!, out var child, out var error), error);
        Assert.NotEqual(parent.ExecutionId, child!.ExecutionId);
        Assert.Equal(parent.ExecutionId, child.ParentExecutionId);
        Assert.Equal(parentJobId.ToString(), child.Trace?.ParentJobId);
        Assert.Contains(child.Context,
            item => item.Kind == "compute-job" && item.Id == parentJobId.ToString());
        Assert.Equal(["admin"], ExecutionIdentityClaims.ReadRoles(principal!));
    }

    [Fact]
    public void Legacy_rerun_keeps_the_asserted_header_fallback()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer legacy-service-token";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/image/generate");

        GlobalEndpoints.ApplyRerunCredentials(
            context, request, AssertedProvenance(), Guid.NewGuid());

        Assert.True(request.Headers.Contains("X-Compute-Provenance"));
        Assert.Equal("Bearer legacy-service-token",
            Assert.Single(request.Headers.GetValues("Authorization")));
    }

    private static ExecutionIdentity ParentIdentity() => new(
        ExecutionIdentity.CurrentSchemaVersion,
        Guid.NewGuid().ToString(),
        new ExecutionAppIdentity("nova", "Nova"),
        new ExecutionActorIdentity("agent", "nova", "Nova"),
        new ExecutionBeneficiaryIdentity("user", "user-1", "Laurent"),
        [new ExecutionContextReference("discussion", "discussion-1")],
        Trace: new ExecutionTrace("request-1", "correlation-1"));

    private static JobProvenance AssertedProvenance() => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin(
            "redcompute",
            new JobAppReference("app", "nova", null, "Nova", null, null),
            new JobEntrypoint("http", "/jobs/{id}/rerun", "POST")),
        new JobActor("agent", "Nova", Id: "nova"),
        new JobBeneficiary("user", "user-1", "Laurent"),
        [new JobContextReference("discussion", "discussion-1")],
        new JobTrace("request-1"),
        JobProvenanceAssurance.Asserted,
        DateTimeOffset.UtcNow);
}
