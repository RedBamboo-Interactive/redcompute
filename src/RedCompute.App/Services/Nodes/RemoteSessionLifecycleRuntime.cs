using System.IO;
using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Nodes;

internal sealed record RemoteSessionRuntimeIdentity(string NodeId, string RuntimeId, byte[] PinnedControllerKey);

internal sealed record RemoteSessionLifecycleRequest(string Revision, string ProviderInstanceId, string? Model = null,
    string? Effort = null, string? RuntimeSessionId = null, string? NativeThreadId = null);

internal sealed record RemoteSessionLifecycleThread(string RuntimeSessionId, string NativeThreadId);

internal sealed record RemoteSessionRuntimeWorkspace(string OwnerId, string Reference, string Revision,
    string ProjectPath, string ProviderInstanceId, ISessionProvider Provider, SessionAccessState Access, Guid? RepositoryId = null);

internal sealed class RemoteSessionLifecycleRuntime(SessionInputQueueStore queue,
    Func<RemoteSessionRuntimeIdentity?> currentPairing,
    Func<SessionRuntimeScope, RemoteSessionLifecycleRequest, CancellationToken, Task<RemoteSessionRuntimeWorkspace?>> resolve,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    internal async Task<RemoteLifecycleReceipt> ApplyAsync(SessionRuntimeOperation operation, SessionRuntimeGrant grant,
        CancellationToken ct = default)
    {
        if (!Authorized(operation, grant))
            return Error(403, "invalid_runtime_grant");
        if (operation is not (SessionRuntimeOperation.Start or SessionRuntimeOperation.Resume or SessionRuntimeOperation.Park))
            return Error(404, "unsupported_operation");
        if (grant.Scope.Generation != 0)
            return Error(409, "binding_generation_mismatch");

        RemoteSessionLifecycleRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RemoteSessionLifecycleRequest>(grant.Payload, Json);
        }
        catch (JsonException)
        {
            return Error(400, "invalid_lifecycle_request");
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Revision) || string.IsNullOrWhiteSpace(request.ProviderInstanceId)
            || (operation == SessionRuntimeOperation.Start
                ? request.RuntimeSessionId is not null || request.NativeThreadId is not null
                : string.IsNullOrWhiteSpace(request.RuntimeSessionId) || string.IsNullOrWhiteSpace(request.NativeThreadId)
                  || request.Model is not null || request.Effort is not null))
            return Error(400, "invalid_lifecycle_request");

        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!Authorized(operation, grant))
                return Error(403, "invalid_runtime_grant");

            var workspace = await resolve(grant.Scope, request, ct);
            if (workspace is null || workspace.OwnerId != grant.Scope.OwnerId || workspace.Reference != grant.Scope.AccessScope
                || workspace.Revision != request.Revision || workspace.ProviderInstanceId != request.ProviderInstanceId
                || !Directory.Exists(workspace.ProjectPath))
                return Error(422, "workspace_unavailable");
            if (!NativeSessionBindingPreparer.Supports(workspace.Provider)
                || !SessionAccessLevels.IsKnown(workspace.Access.AccessLevel)
                || !SessionAccessLevels.IsKnown(workspace.Access.MaxAccessLevel)
                || !workspace.Provider.SupportedAccessLevels.Contains(workspace.Access.AccessLevel!)
                || SessionAccessLevels.Rank(workspace.Access.AccessLevel!) > SessionAccessLevels.Rank(workspace.Access.MaxAccessLevel!))
                return Error(422, "runtime_provider_unavailable");

            var reservation = await queue.ReserveRemoteLifecycleAsync(grant, ct);
            if (reservation.State == RemoteLifecycleState.Conflict)
                return Error(409, "lifecycle_request_conflict");
            if (reservation.Receipt is { } receipt)
                return receipt;

            // Once reserved, a connection going away does not cancel the provider operation or its receipt.
            var result = operation == SessionRuntimeOperation.Start
                ? await StartAsync(grant, request, workspace)
                : await ReopenAsync(operation, grant, request, workspace);
            if (result.Status >= 500)
                return result;
            if (await queue.CompleteRemoteLifecycleAsync(grant, result))
                return result;
            return (await queue.ReserveRemoteLifecycleAsync(grant)).Receipt ?? Error(503, "lifecycle_outcome_unknown");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private bool Authorized(SessionRuntimeOperation operation, SessionRuntimeGrant grant)
        => currentPairing() is { } active
           && grant.Verify(active.PinnedControllerKey, active.NodeId, active.RuntimeId, operation, time.GetUtcNow());

    private async Task<RemoteLifecycleReceipt> StartAsync(SessionRuntimeGrant grant,
        RemoteSessionLifecycleRequest request, RemoteSessionRuntimeWorkspace workspace)
    {
        var scope = grant.Scope;
        var binding = JsonSerializer.Serialize(new { scope.NodeId, scope.RuntimeId, scope.LogicalSessionId, scope.Generation }, Json);
        var runtimeId = NativeSessionBindingPreparer.NewRuntimeId(binding, grant.RequestId);
        var provider = workspace.Provider;
        var session = provider.GetSessionInfo(runtimeId);
        if (session is null)
        {
            using var launch = SessionHandoffScope.Push(new(runtimeId, runtimeId, null));
            using var access = SessionAccessScope.Push(workspace.Access);
            session = await provider.StartSessionAsync(workspace.ProjectPath, request.Model, scope.OwnerId, null, null,
                request.Effort, null, null, null, null, workspace.ProviderInstanceId, workspace.RepositoryId, Provenance(grant));
        }
        if (session is null || session.Status == SessionStatus.Starting)
            return Error(503, "remote_start_pending");
        if (session.Status == SessionStatus.Error || session.Status == SessionStatus.Stopped && !SessionStopReasons.IsAutoResumable(session.StopReason))
            return Error(409, "remote_session_not_resumable");
        return Matches(session, runtimeId, workspace) && session.ProviderSessionId is { Length: > 0 } nativeId
            ? Thread(201, runtimeId, nativeId)
            : Error(409, "runtime_identity_mismatch");
    }

    private async Task<RemoteLifecycleReceipt> ReopenAsync(SessionRuntimeOperation operation, SessionRuntimeGrant grant,
        RemoteSessionLifecycleRequest request, RemoteSessionRuntimeWorkspace workspace)
    {
        var started = await queue.ReadRemoteLifecycleStartAsync(grant.Scope);
        var thread = started is null ? null : JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(started.Body, Json);
        if (thread is null || thread.RuntimeSessionId != request.RuntimeSessionId || thread.NativeThreadId != request.NativeThreadId)
            return Error(409, "runtime_identity_mismatch");

        var provider = workspace.Provider;
        var session = provider.GetSessionInfo(thread.RuntimeSessionId);
        if (session is null || !Matches(session, thread.RuntimeSessionId, workspace) || session.ProviderSessionId != thread.NativeThreadId)
            return Error(409, "runtime_identity_mismatch");

        if (operation == SessionRuntimeOperation.Park)
        {
            if (session.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(session.StopReason))
                return Thread(200, thread.RuntimeSessionId, thread.NativeThreadId);
            return session.Status == SessionStatus.Idle
                   && await ((IIdleSessionParkingProvider)provider).TryParkIdleSessionAsync(thread.RuntimeSessionId, SessionStopReasons.IdleTimeout)
                ? Thread(200, thread.RuntimeSessionId, thread.NativeThreadId)
                : Error(409, "remote_session_busy");
        }

        if (session.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(session.StopReason))
            session = await provider.ResumeSessionAsync(thread.RuntimeSessionId, Provenance(grant));
        if (session is null || session.Status == SessionStatus.Starting)
            return Error(503, "remote_resume_pending");
        if (session.Status is not (SessionStatus.Idle or SessionStatus.Active))
            return Error(409, "remote_session_not_resumable");
        return Matches(session, thread.RuntimeSessionId, workspace) && session.ProviderSessionId == thread.NativeThreadId
            ? Thread(200, thread.RuntimeSessionId, thread.NativeThreadId)
            : Error(409, "runtime_identity_mismatch");
    }

    private static bool Matches(UnifiedSessionInfo session, string runtimeId, RemoteSessionRuntimeWorkspace workspace) =>
        session.Id == runtimeId && session.Provider == workspace.Provider.ProviderId && session.UserId == workspace.OwnerId
        && session.ProviderEntity == workspace.ProviderInstanceId && session.RepositoryId == workspace.RepositoryId
        && session.ProjectPath == workspace.ProjectPath && SessionAccessLevels.IsKnown(session.AccessLevel)
        && SessionAccessLevels.Rank(session.AccessLevel!) <= SessionAccessLevels.Rank(workspace.Access.AccessLevel!)
        && SessionAccessLevels.IsKnown(session.MaxAccessLevel)
        && SessionAccessLevels.Rank(session.MaxAccessLevel!) <= SessionAccessLevels.Rank(workspace.Access.MaxAccessLevel!);

    private JobProvenance Provenance(SessionRuntimeGrant grant) => new(
        JobProvenance.CurrentSchemaVersion,
        new("redcompute", new("app", "redcompute", null, "RedCompute"), new("node-session-runtime", SessionRuntimeProtocol.PathFor(grant.Operation), "POST")),
        new("app", "Paired RedCompute controller", Id: "redcompute-node-controller"), new("user", grant.Scope.OwnerId),
        [new("ai-session", grant.Scope.LogicalSessionId), new("node", grant.Scope.NodeId)],
        new(grant.RequestId), JobProvenanceAssurance.Verified, time.GetUtcNow());

    private static RemoteLifecycleReceipt Thread(int status, string runtimeId, string nativeId) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(new RemoteSessionLifecycleThread(runtimeId, nativeId), Json));

    private static RemoteLifecycleReceipt Error(int status, string code) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(new { code }, Json));
}
