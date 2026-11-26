using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class SessionInferenceSettingsEndpointTests
{
    [Fact]
    public async Task Signed_agent_can_change_thinking_for_subsequent_turns()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active;
        f.Runtime.Info.ProviderSessionId = "native";
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { effort = "high" }, "owner");
        Assert.Equal(200, status);
        Assert.Equal("high", body.GetProperty("effort").GetString());
        Assert.Equal(SessionStatus.Active, f.Runtime.Info.Status);
        Assert.Equal("native", f.Runtime.Info.ProviderSessionId);
        Assert.Equal(1, f.Runtime.InferenceWrites);
        Assert.Equal(0, f.Runtime.Resumes);
        Assert.Equal(0, f.Runtime.Deliveries);

        var options = await CallAsync(app, "GET", null, "owner");
        Assert.Equal(200, options.Status);
        Assert.Equal("high", options.Body.GetProperty("effort").GetString());
        Assert.Equal(["low", "high"], options.Body.GetProperty("models")[0]
            .GetProperty("supportedEfforts").Deserialize<string[]>()!);

        var repeated = await CallAsync(app, "PUT", new { effort = "high" }, "owner");
        Assert.Equal(200, repeated.Status);
        Assert.Equal(1, f.Runtime.InferenceWrites);
    }

    [Theory]
    [InlineData("low", "medium")]
    [InlineData("high", "high")]
    public async Task Model_change_preserves_compatible_effort_and_keeps_the_original_thread(string previous, string expected)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active;
        f.Runtime.Info.ProviderSessionId = "native";
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = previous;
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { model = "model-b" }, "owner");
        Assert.Equal(200, status);
        Assert.Equal("model-b", body.GetProperty("model").GetString());
        Assert.Equal(expected, body.GetProperty("effort").GetString());
        Assert.Equal("native", f.Runtime.Info.ProviderSessionId);
        Assert.Equal(SessionStatus.Active, f.Runtime.Info.Status);
        Assert.Equal(0, f.Runtime.Resumes);
    }

    [Theory]
    [InlineData("owner", "unavailable", false, 422, "unsupported_effort")]
    [InlineData("other-user", "high", false, 403, "forbidden")]
    [InlineData("owner", "high", true, 409, "input_queued")]
    public async Task Refusal_leaves_the_session_and_queued_input_intact(
        string subject, string effort, bool enqueue, int expected, string code)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        if (enqueue) await f.Store.EnqueueAsync(f.Submission());
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { effort }, subject);
        Assert.Equal(expected, status);
        Assert.Equal(code, body.GetProperty("error").GetString());
        Assert.Equal("low", f.Runtime.Info.Effort);
        Assert.Equal(0, f.Runtime.InferenceWrites);
        Assert.Equal(enqueue, await f.Store.GetHeadAsync("session") is not null);
    }

    private static WebApplication Map(SessionRecoveryTests.Fixture f)
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), f.Registry, null!, (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service());
        return app;
    }

    private static async Task<(int Status, JsonElement Body)> CallAsync(
        WebApplication app, string method, object? body, string subject)
    {
        const string pattern = "/ai-session/sessions/{id}/inference-settings";
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        SessionRollbackTests.Identify(ctx, "agent", subject);
        ctx.SetEndpoint(endpoint);
        ctx.Request.Method = method;
        ctx.Request.Path = "/ai-session/sessions/session/inference-settings";
        ctx.Request.RouteValues["id"] = "session";
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.Body = new MemoryStream(bytes);
        }
        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        ctx.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, json.RootElement.Clone());
    }
}

