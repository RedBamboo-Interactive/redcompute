using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ControllerRemoteSessionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "controller-remote-sessions-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly RemoteSessionPlacement Placement = new("node", "sessions", "fixture", "revision",
        "probe", "probe-local", "model", "low");
    private static readonly RemoteSessionPlacementPreview Preview = new("node", "sessions", "fixture", "revision",
        "probe", "probe-local", "ask", "ask");
    private static readonly RemoteSessionLifecycleThread Thread = new("runtime-session", "native-thread", "probe-local", "revision");

    [Fact]
    public async Task A_lost_start_can_only_replay_the_same_destination_and_resumes_the_same_native_thread()
    {
        var deliveries = new HashSet<(SessionRuntimeScope Scope, string Id, string Payload)>();
        var resumes = 0;
        var caller = Caller("owner");
        var controller = Open();
        Assert.Equal(Preview, await controller.ValidateAsync(caller, "key", Placement, default));
        Assert.Empty(deliveries);
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.StartAsync(caller, "key", Placement, default));
        var attempt = Assert.Single(deliveries);

        controller = Open();
        var conflict = await Assert.ThrowsAsync<ControllerRemoteSessionException>(() =>
            controller.StartAsync(caller, "key", Placement with { Model = "different" }, default));
        Assert.Equal("remote_start_conflict", conflict.Code);
        Assert.Single(deliveries);

        var started = await controller.StartAsync(caller, "key", Placement, default);
        Assert.Equal(attempt.Scope, started.Scope);
        Assert.Equal(Thread, started.Thread);
        Assert.Single(deliveries);

        controller = Open();
        var forbidden = await Assert.ThrowsAsync<ControllerRemoteSessionException>(() =>
            controller.ResumeAsync(Caller("somebody-else"), started.Scope.LogicalSessionId, "resume", default));
        Assert.Equal(404, forbidden.Status);
        Assert.Equal(0, resumes);
        Assert.Equal(started, await controller.ResumeAsync(caller, started.Scope.LogicalSessionId, "resume", default));
        Assert.Equal(1, resumes);

        ControllerRemoteSessions Open() => new(Queue(), (principal, scope, operation, requestId, payload, _) =>
        {
            Assert.Same(caller, principal);
            switch (operation)
            {
                case SessionRuntimeOperation.Inspect:
                    return Reply(HttpStatusCode.OK, Preview);
                case SessionRuntimeOperation.Start:
                    if (deliveries.Add((scope, requestId, Convert.ToBase64String(payload))))
                        throw new HttpRequestException("The destination started a native thread but the link dropped.");
                    return Reply(HttpStatusCode.Created, Thread);
                case SessionRuntimeOperation.Resume:
                    resumes++;
                    Assert.Equal(Thread.NativeThreadId, JsonSerializer.Deserialize<RemoteSessionLifecycleRequest>(payload, Json)!.NativeThreadId);
                    return Reply(HttpStatusCode.OK, Thread);
                default:
                    throw new InvalidOperationException();
            }
        });
    }

    [Fact]
    public async Task A_preview_with_a_different_provider_cannot_open_a_thread()
    {
        var commands = new List<SessionRuntimeOperation>();
        var controller = new ControllerRemoteSessions(Queue(), (_, _, operation, _, _, _) =>
        {
            commands.Add(operation);
            return Reply(HttpStatusCode.OK, Preview with { ProviderId = "different" });
        });

        var failure = await Assert.ThrowsAsync<ControllerRemoteSessionException>(() =>
            controller.StartAsync(Caller("owner"), "key", Placement, default));
        Assert.Equal("remote_placement_mismatch", failure.Code);
        Assert.Equal([SessionRuntimeOperation.Inspect], commands);
    }

    [Fact]
    public async Task A_remote_delivery_stays_the_fifo_head_across_a_controller_restart()
    {
        using var fixture = new SessionRecoveryTests.Fixture("maintenance_restart");
        var scope = new SessionRuntimeScope(Placement.NodeId, Placement.RuntimeId, "remote-logical", 0, "owner", Placement.Workspace);
        var binding = new ControllerRemoteSession(scope, Placement, Preview, Thread);
        var store = fixture.Store;
        await store.ReserveControllerRemoteStartAsync(scope, Placement, Preview, default);
        await store.CommitControllerRemoteStartAsync(binding, default);
        var first = (await store.EnqueueAsync(fixture.Submission("first") with { SessionId = scope.LogicalSessionId })).Item;
        var second = (await store.EnqueueAsync(fixture.Submission("second") with { SessionId = scope.LogicalSessionId })).Item;

        Assert.Null(await store.ClaimControllerRemoteInputAsync(binding with { Scope = scope with { OwnerId = "different" } }, default));
        var admitted = await store.ClaimControllerRemoteInputAsync(binding, default);
        Assert.Equal(first.Id, admitted!.Id);
        Assert.Equal(SessionInputQueueState.Delivering, admitted.State);
        Assert.Equal(1, admitted.AttemptCount);

        store = new SessionInputQueueStore(fixture.Config, fixture.Attachments);
        Assert.Equal(0, await store.RecoverExpiredLeasesAsync());
        Assert.Contains(scope.LogicalSessionId, await store.GetRunnableSessionIdsAsync());
        var again = await store.ClaimControllerRemoteInputAsync(binding, default);
        Assert.Equal(first.Id, again!.Id);
        Assert.Equal(1, again.AttemptCount);
        var cancel = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() => store.CancelAsync(scope.LogicalSessionId, first.Id, "owner"));
        Assert.Equal("delivery_in_progress", cancel.Code);
        Assert.Equal(SessionInputQueueState.Pending, (await store.GetAsync(scope.LogicalSessionId, second.Id, "owner"))!.State);

        await store.MarkDeliveredAsync([again], first.MessageUid);
        Assert.Equal(second.Id, (await store.ClaimControllerRemoteInputAsync(binding, default))!.Id);
        Assert.Contains(scope.LogicalSessionId, await store.GetRunnableSessionIdsAsync());
    }

    private SessionInputQueueStore Queue()
    {
        Directory.CreateDirectory(_root);
        var config = new RedComputeConfig();
        return new(config, new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db")));
    }

    private static ClaimsPrincipal Caller(string owner) => new(new ClaimsIdentity([new Claim("sub", owner)], "test-signed"));

    private static Task<(HttpStatusCode Status, byte[] Body)> Reply<T>(HttpStatusCode status, T body) =>
        Task.FromResult((status, JsonSerializer.SerializeToUtf8Bytes(body, Json)));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
