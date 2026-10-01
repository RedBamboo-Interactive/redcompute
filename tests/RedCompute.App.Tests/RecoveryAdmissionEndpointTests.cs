using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class RecoveryAdmissionEndpointTests
{
    [Theory]
    [InlineData("app", false, "maintenance_restart", false)]
    [InlineData("user", false, "maintenance_restart", false)]
    [InlineData("app", true, "maintenance_restart", false)]
    [InlineData("user", true, "maintenance_restart", false)]
    [InlineData("app", false, "user_stopped", false)]
    [InlineData("app", true, "maintenance_restart", true)]
    [InlineData("plain", false, "maintenance_restart", false)]
    [InlineData("plain", true, "maintenance_restart", false)]
    public async Task Mapped_message_preserves_real_caller_recovery_even_after_automatic_block(string actorKind, bool previouslyBlocked, string stopReason, bool otherScope)
    {
        using var f = new SessionRecoveryTests.Fixture(stopReason);
        var dbPath = Path.Combine(f.Root, "jobs.db");
        RedComputeDbContext Db() => new(dbPath);
        using (var db = Db()) db.Database.EnsureCreated();
        var jobs = new JobTrackingService(Db); f.Jobs = jobs;
        var options = new JwtOptions { SigningKey = "endpoint-recovery-identity-secret-1234567890123456789" };
        var jwt = new JwtService(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(options).AddSingleton<IExecutionTokenIssuer>(new ExecutionTokenIssuer(jwt, options));
        await using var app = builder.Build();
        var ctx = new DefaultHttpContext { RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("roles", "[\"operator\"]")], "signed")) };
        var parent = new ExecutionIdentity(1, Guid.NewGuid().ToString(), new(actorKind == "app" ? "codered" : "direct", "Caller"),
            new(actorKind, actorKind == "app" ? "codered" : "owner", "Caller"), new("user", "owner"), []);
        ctx.User = actorKind == "plain" ? jwt.ValidateToken(jwt.GenerateAccessToken("owner", "owner@test", "Owner", ["operator"]))!
            : jwt.ValidateToken(new ExecutionTokenIssuer(jwt, options).Issue(parent, new ExecutionPrincipal("owner", "owner@test", "Owner", ["operator"])).AccessToken)!;
        ctx.Request.Method = "POST";
        using var scope = actorKind == "plain" ? null : ExecutionContextScope.Push(parent);
        if (actorKind == "plain") Assert.Null(ExecutionContextScope.Current);
        var provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions/{id}/message");
        var job = jobs.CreateJob(new JobSubmission("ai-session", "test", "{}", provenance)); f.Runtime.Info.JobId = job.Id;
        f.Authority = (_, _, _) => Task.FromResult(new SessionRecoveryAuthority(null, "recovery_actor_app_unverified", false));
        var queue = f.Service();
        if (previouslyBlocked)
        {
            var previous = otherScope ? provenance with { Actor = provenance.Actor with { Id = "other-app" } } : provenance;
            await f.Store.EnqueueAsync(f.Submission(otherScope ? "earlier" : "prompt") with { Provenance = previous });
            await queue.ProcessSessionAsync("session", default);
            Assert.Equal("recovery_actor_app_unverified", (await f.Store.GetHeadAsync("session"))!.ErrorCode);
        }
        f.Runtime.OnDelivery = () => {
            var principal = jwt.ValidateToken(SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"]);
            Assert.True(ExecutionIdentityClaims.TryRead(principal!, out var child, out _));
            Assert.Equal(actorKind == "plain" ? "app" : actorKind, child!.Actor.Kind);
            Assert.Equal(actorKind == "plain" ? "direct-redcompute-api" : parent.Actor.Id, child.Actor.Id);
            Assert.Equal(actorKind == "plain" ? null : parent.ExecutionId, child.ParentExecutionId);
        };
        var registry = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(registry, f.Registry, jobs, (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: queue);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/ai-session/sessions/{id}/message");
        ctx.SetEndpoint(endpoint); ctx.Request.RouteValues["id"] = "session";
        ctx.Request.Method = "POST"; ctx.Request.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { content = "prompt", messageUid = "prompt" }));
        ctx.Request.Body = new MemoryStream(body); ctx.Request.ContentLength = body.Length;
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        if (stopReason == "user_stopped" || otherScope)
        {
            Assert.Equal(202, ctx.Response.StatusCode);
            Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.Runtime.Deliveries);
            return;
        }
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(1, f.Runtime.Resumes); Assert.Equal(1, f.Runtime.Deliveries);
        var item = (await f.Store.FindPromptAsync("session", "prompt"))!;
        Assert.False(await f.Store.RequiresRecoveryAsync(item.Id, default));
        Assert.Null(SessionScratch.Environment(null));
    }
    private sealed class BodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
