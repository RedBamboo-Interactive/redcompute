using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services.Nodes;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RemoteSessionEndpointsTests
{
    [Fact]
    public async Task Ordinary_routes_validate_start_resume_and_admit_a_remote_session_with_signed_authority()
    {
        await using var leaf = await TranscriptMirrorOrderTests.FakeRedLeaf.Start();
        using var fixture = new SessionRecoveryTests.Fixture("maintenance_restart");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var preview = new RemoteSessionPlacementPreview("remote-one", "sessions", "workspace", "revision", "codex", "probe", "ask", "ask");
        var thread = new RemoteSessionLifecycleThread("native-session", "native-thread", "probe", "revision",
            "resolved-probe-model", "low", DateTimeOffset.UnixEpoch);
        var starts = 0;
        var resumes = 0;
        var controller = new ControllerRemoteSessions(fixture.Store, (_, scope, operation, _, bytes, _) =>
        {
            Assert.Equal("owner", scope.OwnerId);
            switch (operation)
            {
                case SessionRuntimeOperation.Inspect: return Reply(HttpStatusCode.OK, preview);
                case SessionRuntimeOperation.Start:
                    starts++;
                    return Reply(HttpStatusCode.Created, thread);
                case SessionRuntimeOperation.Resume:
                    resumes++;
                    Assert.Equal(thread.NativeThreadId, JsonSerializer.Deserialize<RemoteSessionLifecycleRequest>(bytes, json)!.NativeThreadId);
                    return Reply(HttpStatusCode.OK, thread);
                default:
                    throw new InvalidOperationException();
            }
        });
        var jobs = new ClaudeDeveloperInstructionsTests.MemoryJobTracker();
        var queue = fixture.Service();
        var reader = leaf.SessionReader(fixture);
        var remote = new UnifiedSessionEndpoints.RemoteSessions(controller, reader, leaf.StreamClient, jobs, queue);
        await using var app = WebApplication.CreateSlimBuilder().Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), fixture.Registry, jobs, (_, _) => { }, fixture.Config,
            redLeafReader: reader, attachmentStore: fixture.Attachments, inputQueue: queue, remote: remote);

        var denied = await Call("POST", "/ai-session/sessions", Start(false), key: "start", signed: false);
        Assert.Equal(401, denied.Status);
        var validation = await Call("POST", "/ai-session/sessions", Start(true), key: "start");
        Assert.Equal(200, validation.Status);
        Assert.True(validation.Body.GetProperty("eligible").GetBoolean());
        Assert.Equal(0, starts);

        var creation = await Call("POST", "/ai-session/sessions", Start(false), key: "start");
        Assert.Equal(201, creation.Status);
        var id = creation.Body.GetProperty("id").GetString()!;
        Assert.Equal("remote-one", creation.Body.GetProperty("nodeId").GetString());
        Assert.Equal("native-thread", creation.Body.GetProperty("providerSessionId").GetString());
        Assert.Equal("resolved-probe-model", creation.Body.GetProperty("model").GetString());
        Assert.Equal("low", creation.Body.GetProperty("effort").GetString());
        Assert.Equal(1, starts);
        Assert.Equal(404, (await Call("POST", "/ai-session/sessions/{id}/resume", null, id, owner: "someone-else")).Status);
        Assert.Equal(0, resumes);
        Assert.Equal(200, (await Call("POST", "/ai-session/sessions/{id}/resume", null, id)).Status);
        Assert.Equal(1, resumes);
        Assert.Equal(1, starts);

        var input = await Call("POST", "/ai-session/sessions/{id}/message", new { content = "one prompt", messageUid = "first" }, id);
        Assert.Equal(202, input.Status);
        var itemId = input.Body.GetProperty("queueItemId").GetString()!;
        var receipt = await Call("GET", "/ai-session/sessions/{id}/input-queue/{itemId}", null, id, itemId);
        Assert.Equal(200, receipt.Status);
        Assert.Equal("first", receipt.Body.GetProperty("messageUid").GetString());
        Assert.Equal(404, (await Call("GET", "/ai-session/sessions/{id}/input-queue/{itemId}", null, id, itemId,
            owner: "someone-else")).Status);

        object Start(bool validateOnly) => new
        {
            placement = new { nodeId = "remote-one", sessionRuntime = new
            {
                id = "sessions", workspace = "workspace", revision = "revision", providerInstanceId = "probe",
            } },
            provider = "codex", validateOnly,
        };

        Task<(HttpStatusCode Status, byte[] Body)> Reply<T>(HttpStatusCode status, T body)
            => Task.FromResult((status, JsonSerializer.SerializeToUtf8Bytes(body, json)));

        async Task<(int Status, JsonElement Body)> Call(string method, string path, object? body, string? id = null,
            string? itemId = null, string? key = null, bool signed = true, string owner = "owner")
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(value => value.RoutePattern.RawText == path
                    && value.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var context = new DefaultHttpContext { RequestServices = app.Services };
            ExecutionIdentity? identity = null;
            if (signed)
            {
                SessionRollbackTests.Identify(context, "agent", owner);
                identity = (ExecutionIdentity)context.Items[ExecutionIdentityClaims.HttpContextItemKey]!;
                ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("execution_identity", JsonSerializer.Serialize(identity, json)));
            }

            context.SetEndpoint(endpoint);
            context.Request.Method = method;
            context.Request.Path = path.Replace("{id}", id ?? "").Replace("{itemId}", itemId ?? "");
            if (id is not null) context.Request.RouteValues["id"] = id;
            if (itemId is not null) context.Request.RouteValues["itemId"] = itemId;
            if (key is not null) context.Request.Headers["X-Idempotency-Key"] = key;
            using var request = body is null ? null : new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body, json));
            if (request is not null)
            {
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = request.Length;
                context.Request.Body = request;
            }
            using var response = new MemoryStream();
            context.Response.Body = response;
            using var scope = identity is null ? null : ExecutionContextScope.Push(identity);
            await endpoint.RequestDelegate!(context);
            response.Position = 0;
            using var result = await JsonDocument.ParseAsync(response);
            return (context.Response.StatusCode, result.RootElement.Clone());
        }
    }
}
