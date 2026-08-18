using System.IdentityModel.Tokens.Jwt;
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
        IExecutionTokenIssuer issuer = new ExecutionTokenIssuer(jwt, options);
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<IExecutionTokenIssuer>(issuer)
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

    [Fact]
    public void Provider_process_token_uses_the_session_lifetime_instead_of_the_short_execution_lifetime()
    {
        var options = new JwtOptions
        {
            SigningKey = "session-child-token-tests-require-a-long-signing-key-1234567890",
            ExecutionTokenLifetime = TimeSpan.FromMinutes(5),
            SessionExecutionTokenLifetime = TimeSpan.FromDays(14),
            ClockSkew = TimeSpan.Zero,
        };
        var jwt = new JwtService(options);
        IExecutionTokenIssuer issuer = new ExecutionTokenIssuer(jwt, options);
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<IExecutionTokenIssuer>(issuer)
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
            new ExecutionActorIdentity("agent", "nova", "Nova"),
            new ExecutionBeneficiaryIdentity("user", "user-1", "Laurent"),
            [new ExecutionContextReference("discussion", "7861ea0d")]);

        var ordinaryToken = issuer.Issue(parent, context.User).AccessToken;
        var ordinaryExpiry = new JwtSecurityTokenHandler().ReadJwtToken(ordinaryToken).ValidTo;
        Assert.True(ordinaryExpiry < DateTime.UtcNow.AddMinutes(6),
            $"Ordinary execution token lived too long, until {ordinaryExpiry:O}");

        using (ExecutionContextScope.Push(parent))
        using (SessionExecutionToken.Push(context, "codex", "Codex"))
        {
            var token = SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"];
            var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

            Assert.True(parsed.ValidTo > DateTime.UtcNow.AddDays(13),
                $"Provider-process token expired too early at {parsed.ValidTo:O}");
        }
    }

    [Fact]
    public async Task Stateless_provider_environment_materializes_a_child_token_for_the_admitted_timeout()
    {
        var (context, jwt, parent) = CreateFixture();
        Dictionary<string, string>? environment;
        var before = DateTime.UtcNow;

        using (ExecutionContextScope.Push(parent))
        {
            environment = SessionExecutionToken.CreateStatelessEnvironment(
                context, "codex", "Codex", timeoutSeconds: 7200, environment: null);
        }

        var token = await Task.Run(() => environment!["REDLEAF_EXECUTION_TOKEN"]);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var principal = jwt.ValidateToken(token);

        Assert.NotNull(principal);
        Assert.True(ExecutionIdentityClaims.TryRead(principal!, out var child, out var error), error);
        Assert.Equal(parent.ExecutionId, child!.ParentExecutionId);
        Assert.InRange(
            parsed.ValidTo,
            before.AddSeconds(7200).Add(SessionExecutionToken.StatelessCompletionGrace).AddSeconds(-5),
            DateTime.UtcNow.AddSeconds(7200).Add(SessionExecutionToken.StatelessCompletionGrace).AddSeconds(5));
        Assert.True(parsed.ValidTo < before.AddHours(3),
            $"Stateless provider token inherited the persistent-session lifetime, until {parsed.ValidTo:O}");
        Assert.Null(SessionScratch.Environment(null));
    }

    [Fact]
    public void Stateless_provider_environment_preserves_caller_keys_and_replaces_reserved_token_case_insensitively()
    {
        var (context, jwt, parent) = CreateFixture();
        var callerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "windows-path",
            ["Path"] = "container-path",
            ["redleaf_execution_token"] = "caller-controlled-token",
        };

        Dictionary<string, string>? environment;
        using (ExecutionContextScope.Push(parent))
        {
            environment = SessionExecutionToken.CreateStatelessEnvironment(
                context, "codex", "Codex", timeoutSeconds: 1800, callerEnvironment);
        }

        Assert.NotSame(callerEnvironment, environment);
        Assert.Equal("windows-path", environment!["PATH"]);
        Assert.Equal("container-path", environment["Path"]);
        Assert.DoesNotContain(environment.Keys,
            key => string.Equals(key, "redleaf_execution_token", StringComparison.Ordinal));
        Assert.NotNull(jwt.ValidateToken(environment["REDLEAF_EXECUTION_TOKEN"]));
        Assert.Equal("caller-controlled-token", callerEnvironment["redleaf_execution_token"]);
    }

    [Fact]
    public void Unsigned_stateless_execution_leaves_the_caller_environment_unchanged()
    {
        var (context, _, _) = CreateFixture();
        var callerEnvironment = new Dictionary<string, string>
        {
            ["CUSTOM_SETTING"] = "kept",
            ["REDLEAF_EXECUTION_TOKEN"] = "caller-value",
        };

        var environment = SessionExecutionToken.CreateStatelessEnvironment(
            context, "codex", "Codex", timeoutSeconds: 1800, callerEnvironment);

        Assert.Same(callerEnvironment, environment);
        Assert.Equal("caller-value", environment!["REDLEAF_EXECUTION_TOKEN"]);
        Assert.Null(SessionExecutionToken.CreateStatelessEnvironment(
            context, "codex", "Codex", timeoutSeconds: 1800, environment: null));
    }

    private static (DefaultHttpContext Context, JwtService Jwt, ExecutionIdentity Parent) CreateFixture()
    {
        var options = new JwtOptions
        {
            SigningKey = "session-child-token-tests-require-a-long-signing-key-1234567890",
            ExecutionTokenLifetime = TimeSpan.FromMinutes(5),
            SessionExecutionTokenLifetime = TimeSpan.FromDays(365),
            ClockSkew = TimeSpan.Zero,
        };
        var jwt = new JwtService(options);
        IExecutionTokenIssuer issuer = new ExecutionTokenIssuer(jwt, options);
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<IExecutionTokenIssuer>(issuer)
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
            new ExecutionActorIdentity("agent", "nova", "Nova", "nova-agent-entity"),
            new ExecutionBeneficiaryIdentity("user", "user-1", "Laurent"),
            [new ExecutionContextReference("discussion", "7861ea0d")]);

        return (context, jwt, parent);
    }
}
