using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Api.Endpoints;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionExecutionTokenTests
{
    [Fact]
    public void Provider_process_preserves_the_initiating_actor_in_its_signed_child_identity()
    {
        var options = new JwtOptions
        {
            SigningKey = "session-child-token-tests-require-a-long-signing-key-1234567890",
            ExecutionTokenLifetime = TimeSpan.FromMinutes(30),
            ClockSkew = TimeSpan.Zero,
        };
        var jwt = new JwtService(options);
        var services = new ServiceCollection()
            .AddSingleton<IExecutionTokenIssuer>(new ExecutionTokenIssuer(jwt, options))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim("email", "user@example.test"),
                new Claim("name", "Laurent"),
                new Claim("roles", "[\"admin\"]"),
            ], "test")),
        };
        var parent = new ExecutionIdentity(
            ExecutionIdentity.CurrentSchemaVersion,
            Guid.NewGuid().ToString(),
            new ExecutionAppIdentity("nova", "Nova"),
            new ExecutionActorIdentity(
                "agent",
                "nova",
                "Nova",
                "nova-agent-entity",
                "/api/assets/nova.png"),
            new ExecutionBeneficiaryIdentity("user", "user-1", "Laurent"),
            [new ExecutionContextReference("discussion", "7861ea0d")]);

        using (ExecutionContextScope.Push(parent))
        using (SessionExecutionToken.Push(context, "codex", "Codex"))
        {
            var token = SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"];
            var principal = jwt.ValidateToken(token);
            Assert.NotNull(principal);
            Assert.True(ExecutionIdentityClaims.TryRead(principal!, out var child, out var error), error);
            Assert.Equal(parent.ExecutionId, child!.ParentExecutionId);
            Assert.NotEqual(parent.ExecutionId, child.ExecutionId);
            Assert.Equal("nova", child.App.Id);
            Assert.Equal(parent.Actor, child.Actor);
            Assert.Equal("nova-agent-entity", child.Actor.EntityId);
            Assert.Equal("/api/assets/nova.png", child.Actor.Avatar);
            Assert.Equal("user-1", child.Beneficiary.Id);
            Assert.Contains(child.Context,
                item => item.Kind == "ai-session-provider" && item.Id == "codex");
            Assert.Equal(["admin"], ExecutionIdentityClaims.ReadRoles(principal!));
        }

        Assert.Null(SessionScratch.Environment(null));
    }
}
