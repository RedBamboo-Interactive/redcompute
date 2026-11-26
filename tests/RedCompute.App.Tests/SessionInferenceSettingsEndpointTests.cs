using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
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

    [Fact]
    public async Task Dry_run_validates_the_target_without_sending_or_queuing_anything()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { model = "model-b" }, "owner", validateOnly: true);
        Assert.Equal(200, status);
        Assert.Equal("model-a", body.GetProperty("model").GetString());
        Assert.Equal("model-b", body.GetProperty("proposed").GetProperty("model").GetString());
        Assert.Equal("medium", body.GetProperty("proposed").GetProperty("effort").GetString());
        Assert.Equal(0, f.Runtime.InferenceWrites);
        Assert.Null(await f.Store.GetHeadAsync("session"));
        var invalid = await CallAsync(app, "PUT", new { model = "no-such-model" }, "owner", key: "invalid");
        Assert.Equal(422, invalid.Status);
        Assert.Null(await f.Store.GetHeadAsync("session"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_change_survives_queue_reopen_and_separates_earlier_and_later_prompts(bool savedBeforeReceipt)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active;
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        var earlier = (await f.Store.EnqueueAsync(f.Submission("earlier"))).Item;
        await using var app = Map(f);

        var change = new { model = "model-b" };
        var (status, body) = await CallAsync(app, "PUT", change, "owner", key: "settings-request");
        Assert.True(status == 202, $"Expected queued settings, received {status}: {body}");
        var id = body.GetProperty("operation").GetProperty("id").GetString()!;
        Assert.Equal("model-a", body.GetProperty("model").GetString());
        Assert.Equal(id, body.GetProperty("pendingChanges")[0].GetProperty("id").GetString());
        var replay = await CallAsync(app, "PUT", change, "owner", key: "settings-request");
        Assert.Equal(202, replay.Status);
        Assert.Equal(id, replay.Body.GetProperty("operation").GetProperty("id").GetString());
        var conflict = await CallAsync(app, "PUT", new { effort = "high" }, "owner", key: "settings-request");
        Assert.Equal(409, conflict.Status);

        var later = await f.Store.EnqueueAsync(f.Submission("later"));
        f.Store = new(f.Config, f.Attachments);
        var queue = f.Service();
        var observed = new List<string?>();
        f.Runtime.OnDelivery = () => observed.Add(f.Runtime.Info.Model);

        f.Runtime.Info.Status = SessionStatus.Idle;
        await queue.ProcessSessionAsync("session", default);
        Assert.Equal(["model-a"], observed);
        Assert.Equal(earlier.MessageUid, await f.Store.CurrentDeliveryUidAsync("session"));

        f.Runtime.Info.Status = SessionStatus.Idle;
        if (savedBeforeReceipt)
        {
            f.Runtime.Info.Model = "model-b";
            f.Runtime.Info.Effort = "medium";
            f.Runtime.InferenceWrites = 1;
        }
        await queue.ProcessSessionAsync("session", default);
        var settled = Assert.IsType<SessionInputQueueItem>(await f.Store.GetAsync("session", id, "owner"));
        Assert.Equal(SessionInputQueueState.Delivered, settled.State);
        Assert.Null(settled.TurnId);
        Assert.Null(settled.DeliveredMessageUid);
        var exact = await CallAsync(app, "PUT", change, "owner", key: "settings-request");
        Assert.Equal(200, exact.Status);
        var operation = exact.Body.GetProperty("operation");
        Assert.Equal(id, operation.GetProperty("id").GetString());
        Assert.Equal("model-b", operation.GetProperty("applied").GetProperty("model").GetString());
        Assert.Equal("medium", operation.GetProperty("applied").GetProperty("effort").GetString());
        Assert.Equal(earlier.MessageUid, await f.Store.CurrentDeliveryUidAsync("session"));
        Assert.Equal(1, f.Runtime.InferenceWrites);

        await queue.ProcessSessionAsync("session", default);
        Assert.Equal(["model-a", "model-b"], observed);
        Assert.Equal(later.Item.MessageUid, await f.Store.CurrentDeliveryUidAsync("session"));
        Assert.Equal(1, f.Runtime.InferenceWrites);
    }

    [Fact]
    public async Task Interrupt_and_steer_cannot_overtake_an_accepted_change()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var control = (await f.Store.EnqueueAsync(f.Submission("control") with
        {
            Input = [],
            InferenceChange = new(null, "high"),
        })).Item;
        await f.Store.EnqueueAsync(f.Submission("interrupt") with { DeliveryPolicy = SessionInputDeliveryPolicy.InterruptCurrent });
        await f.Store.EnqueueAsync(f.Submission("steer") with { DeliveryPolicy = SessionInputDeliveryPolicy.SteerCurrent });

        Assert.Equal(control.Id, (await f.Store.GetHeadAsync("session"))?.Id);
        Assert.Null(await f.Store.GetSteerHeadAsync("session"));
        Assert.Empty(await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1)));
        Assert.Empty(await f.Store.ClaimSteerBatchAsync("session", "test", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_harness_that_controls_its_live_process_waits_for_its_first_idle_boundary()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active;
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        f.Runtime.InferenceRequiresIdle = true;
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { effort = "high" }, "owner");
        Assert.Equal(202, status);
        var id = body.GetProperty("operation").GetProperty("id").GetString()!;
        Assert.Equal("low", f.Runtime.Info.Effort);
        Assert.Equal(0, f.Runtime.InferenceWrites);

        f.Runtime.Info.Status = SessionStatus.Idle;
        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal("high", f.Runtime.Info.Effort);
        Assert.Equal(SessionInputQueueState.Delivered, (await f.Store.GetAsync("session", id, "owner"))?.State);
    }

    [Fact]
    public async Task A_parked_native_control_resumes_with_authority_and_records_the_confirmed_default()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Model = "model-a";
        f.Runtime.Info.Effort = "low";
        f.Runtime.InferenceRequiresResident = true;
        f.Runtime.NativeDefaultEffort = "medium";
        await using var app = Map(f);

        var (status, body) = await CallAsync(app, "PUT", new { model = "model-b" }, "owner");
        Assert.Equal(202, status);
        var id = body.GetProperty("operation").GetProperty("id").GetString()!;
        Assert.Equal(0, f.Runtime.InferenceWrites);

        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Resumes);
        Assert.True(f.Runtime.ResumeHadFreshIdentity);
        Assert.Equal(1, f.Runtime.InferenceWrites);
        Assert.Equal(0, f.Runtime.Deliveries);
        var exact = await CallAsync(app, "GET", null, "owner");
        Assert.Equal("model-b", exact.Body.GetProperty("model").GetString());
        Assert.Equal("medium", exact.Body.GetProperty("effort").GetString());
        Assert.Equal(SessionInputQueueState.Delivered, (await f.Store.GetAsync("session", id, "owner"))?.State);
    }

    private static WebApplication Map(SessionRecoveryTests.Fixture f)
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), f.Registry, null!, (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service());
        return app;
    }

    private static async Task<(int Status, JsonElement Body)> CallAsync(
        WebApplication app, string method, object? body, string subject, bool validateOnly = false, string? key = null)
    {
        const string pattern = "/ai-session/sessions/{id}/inference-settings";
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        SessionRollbackTests.Identify(ctx, "agent", subject);
        var identity = (ExecutionIdentity)ctx.Items[ExecutionIdentityClaims.HttpContextItemKey]!;
        ((ClaimsIdentity)ctx.User.Identity!).AddClaim(new Claim("execution_identity",
            JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        ctx.SetEndpoint(endpoint);
        ctx.Request.Method = method;
        ctx.Request.Path = "/ai-session/sessions/session/inference-settings";
        ctx.Request.RouteValues["id"] = "session";
        if (validateOnly) ctx.Request.QueryString = new("?validateOnly=true");
        if (key is not null) ctx.Request.Headers["X-Idempotency-Key"] = key;
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
