using System.Globalization;
using System.IO;
using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.Nodes;

internal sealed record RemoteSessionRuntimeIdentity(string NodeId, string RuntimeId, byte[] PinnedControllerKey);

internal sealed record RemoteSessionLifecycleRequest(string Revision, string ProviderInstanceId, string? Model = null,
    string? Effort = null, string? RuntimeSessionId = null, string? NativeThreadId = null,
    string? InputUid = null, string? Text = null, long? AfterSeq = null, int? Limit = null, long? ThroughSeq = null,
    RemoteSessionStartFence? Fence = null);

internal sealed record RemoteSessionStartFence(string ProviderId, string AccessLevel, string MaxAccessLevel);

internal sealed record RemoteSessionLifecycleThread(string RuntimeSessionId, string NativeThreadId, string ProviderInstanceId,
    string Revision, string? Model = null, string? Effort = null, DateTimeOffset? StartedAt = null, long NativeHistoryThrough = 0);
internal sealed record RemoteSessionRuntimeBinding(SessionRuntimeScope Scope, RemoteSessionLifecycleThread Thread);

internal sealed record RemoteSessionRuntimeWorkspace(string OwnerId, string Reference, string Revision,
    string ProjectPath, string ProviderInstanceId, ISessionProvider Provider, SessionAccessState Access, Guid? RepositoryId = null);

internal sealed partial class RemoteSessionLifecycleRuntime(SessionInputQueueStore queue,
    Func<RemoteSessionRuntimeIdentity?> currentPairing,
    Func<SessionRuntimeScope, RemoteSessionLifecycleRequest, CancellationToken, Task<RemoteSessionRuntimeWorkspace?>> resolve,
    TimeProvider time, Action<string>? signalInput = null,
    Func<SessionRuntimeScope, RemoteSessionLifecycleRequest, RemoteSessionRuntimeWorkspace?>? configuredWorkspace = null,
    IExecutionHost? host = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IExecutionHost _host = host ?? LocalExecutionHost.Shared;

    internal async Task<RemoteLifecycleReceipt> ApplyAsync(SessionRuntimeOperation operation, SessionRuntimeGrant grant,
        CancellationToken ct = default)
    {
        if (!Authorized(operation, grant))
            return Error(403, "invalid_runtime_grant");

        if (SessionRuntimeProtocol.IsMoveOperation(operation))
            return await ApplyMoveAsync(grant, ct);
        if (operation == SessionRuntimeOperation.Start && grant.Scope.Generation != 0)
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
        var inspectPlacement = operation == SessionRuntimeOperation.Inspect
            && request is { RuntimeSessionId: null, NativeThreadId: null, InputUid: null };
        if (request is null || string.IsNullOrWhiteSpace(request.Revision) || string.IsNullOrWhiteSpace(request.ProviderInstanceId)
            || (operation == SessionRuntimeOperation.Start || inspectPlacement
                ? request.RuntimeSessionId is not null || request.NativeThreadId is not null
                  || inspectPlacement && (request.Model is not null || request.Effort is not null)
                : string.IsNullOrWhiteSpace(request.RuntimeSessionId) || string.IsNullOrWhiteSpace(request.NativeThreadId)
                  || request.Model is not null || request.Effort is not null))
            return Error(400, "invalid_lifecycle_request");

        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!Authorized(operation, grant))
                return Error(403, "invalid_runtime_grant");

            var isRead = operation is SessionRuntimeOperation.Inspect or SessionRuntimeOperation.ReadEvents
                or SessionRuntimeOperation.AcknowledgeEvents;
            var workspace = isRead && !inspectPlacement && configuredWorkspace is not null
                ? configuredWorkspace(grant.Scope, request) : await resolve(grant.Scope, request, ct);
            if (workspace is null || workspace.OwnerId != grant.Scope.OwnerId || workspace.Reference != grant.Scope.AccessScope
                || workspace.Revision != request.Revision || workspace.ProviderInstanceId != request.ProviderInstanceId
                || !_host.Files.DirectoryExists(_host.Path(workspace.ProjectPath)))
                return Error(422, "workspace_unavailable");
            if (!NativeSessionBindingPreparer.Supports(workspace.Provider)
                || !SessionAccessLevels.IsKnown(workspace.Access.AccessLevel)
                || !SessionAccessLevels.IsKnown(workspace.Access.MaxAccessLevel)
                || !workspace.Provider.SupportedAccessLevels.Contains(workspace.Access.AccessLevel!)
                || SessionAccessLevels.Rank(workspace.Access.AccessLevel!) > SessionAccessLevels.Rank(workspace.Access.MaxAccessLevel!))
                return Error(422, "runtime_provider_unavailable");

            if (request.Fence is { } fence && (operation is not (SessionRuntimeOperation.Start or SessionRuntimeOperation.Resume
                    or SessionRuntimeOperation.SendInput)
                || workspace.Provider.ProviderId != fence.ProviderId || workspace.Access.AccessLevel != fence.AccessLevel
                || workspace.Access.MaxAccessLevel != fence.MaxAccessLevel))
                return Error(409, "remote_placement_changed");

            if (!isRead && await IsSealedAsync(grant.Scope, ct))
                return Error(409, "binding_generation_sealed");

            if (inspectPlacement)
                return Reply(200, new
                {
                    nodeId = grant.Scope.NodeId,
                    runtimeId = grant.Scope.RuntimeId,
                    workspace = workspace.Reference,
                    workspace.Revision,
                    workspace.ProviderInstanceId,
                    providerId = workspace.Provider.ProviderId,
                    workspace.Access.AccessLevel,
                    workspace.Access.MaxAccessLevel,
                    supportsBindingMoves = true,
                });

            if (operation is not (SessionRuntimeOperation.Start or SessionRuntimeOperation.Resume or SessionRuntimeOperation.Park))
            {
                var thread = await ReadThreadAsync(grant.Scope, request, ct);
                return thread is null ? Error(409, "runtime_identity_mismatch")
                    : await ApplyTurnCommandAsync(grant, request, workspace, thread, ct);
            }

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

    internal (ISessionProvider? Provider, UnifiedSessionInfo? Info) FindLocalSession(string runtimeId)
    {
        if (configuredWorkspace is null || !runtimeId.StartsWith("binding-", StringComparison.Ordinal)
            || queue.FindRemoteRuntimeBinding(runtimeId) is not { } binding
            || currentPairing() is not { } pairing || pairing.NodeId != binding.Scope.NodeId || pairing.RuntimeId != binding.Scope.RuntimeId)
            return (null, null);

        var thread = binding.Thread;
        var request = new RemoteSessionLifecycleRequest(thread.Revision, thread.ProviderInstanceId,
            RuntimeSessionId: thread.RuntimeSessionId, NativeThreadId: thread.NativeThreadId);
        var workspace = configuredWorkspace(binding.Scope, request);
        if (workspace is not null && workspace.Provider.GetSessionInfo(runtimeId) is { } info && Matches(info, runtimeId, workspace)
            && info.ProviderSessionId == thread.NativeThreadId)
            return (workspace.Provider, info);
        return (null, null);
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
            ? Reply(201, new RemoteSessionLifecycleThread(runtimeId, nativeId, request.ProviderInstanceId, request.Revision,
                session.Model, session.Effort, session.StartedAt == default ? null : session.StartedAt))
            : Error(409, "runtime_identity_mismatch");
    }

    private async Task<RemoteLifecycleReceipt> ReopenAsync(SessionRuntimeOperation operation, SessionRuntimeGrant grant,
        RemoteSessionLifecycleRequest request, RemoteSessionRuntimeWorkspace workspace)
    {
        var thread = await ReadThreadAsync(grant.Scope, request, CancellationToken.None);
        if (thread is null)
            return Error(409, "runtime_identity_mismatch");

        var provider = workspace.Provider;
        var session = provider.GetSessionInfo(thread.RuntimeSessionId);
        if (session is null || !Matches(session, thread.RuntimeSessionId, workspace) || session.ProviderSessionId != thread.NativeThreadId)
            return Error(409, "runtime_identity_mismatch");

        if (operation == SessionRuntimeOperation.Park)
        {
            if (session.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(session.StopReason))
                return Reply(200, thread);
            return session.Status == SessionStatus.Idle
                   && await ((IIdleSessionParkingProvider)provider).TryParkIdleSessionAsync(thread.RuntimeSessionId, SessionStopReasons.IdleTimeout)
                ? Reply(200, thread)
                : Error(409, "remote_session_busy");
        }

        if (session.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(session.StopReason))
            session = await provider.ResumeSessionAsync(thread.RuntimeSessionId, Provenance(grant));
        if (session is null || session.Status == SessionStatus.Starting)
            return Error(503, "remote_resume_pending");
        if (session.Status is not (SessionStatus.Idle or SessionStatus.Active))
            return Error(409, "remote_session_not_resumable");
        return Matches(session, thread.RuntimeSessionId, workspace) && session.ProviderSessionId == thread.NativeThreadId
            ? Reply(200, thread)
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

    private async Task<RemoteSessionLifecycleThread?> ReadThreadAsync(SessionRuntimeScope scope,
        RemoteSessionLifecycleRequest request, CancellationToken ct)
    {
        var started = await queue.ReadRemoteLifecycleStartAsync(scope, ct);
        var thread = started is null ? null : JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(started.Body, Json);
        return thread is not null && thread.RuntimeSessionId == request.RuntimeSessionId && thread.NativeThreadId == request.NativeThreadId
            && thread.ProviderInstanceId == request.ProviderInstanceId && thread.Revision == request.Revision ? thread : null;
    }

    private async Task<RemoteLifecycleReceipt> ApplyTurnCommandAsync(SessionRuntimeGrant grant, RemoteSessionLifecycleRequest request,
        RemoteSessionRuntimeWorkspace workspace, RemoteSessionLifecycleThread thread, CancellationToken ct)
    {
        switch (grant.Operation)
        {
            case SessionRuntimeOperation.SendInput:
                if (request.InputUid != grant.RequestId || string.IsNullOrWhiteSpace(request.Text))
                    return Error(400, "invalid_remote_input");
                return await AdmitInputAsync(grant, request.Text, workspace, thread, ct);
            case SessionRuntimeOperation.Inspect:
                if (request.InputUid is null)
                    return await InspectMoveSourceAsync(grant.Scope, thread, workspace, ct);
                if (string.IsNullOrWhiteSpace(request.InputUid) || request.InputUid.Length > 200)
                    return Error(400, "invalid_input_uid");
                var inspection = await queue.ReadRemoteInputAsync(grant.Scope, request.InputUid, ct);
                return inspection is null ? Reply(200, new { state = "absent" })
                    : Reply(200, new
                    {
                        state = inspection.ErrorCode == "delivery_outcome_unknown" ? "unknown"
                            : inspection.DeliveryState?.ToString().ToLowerInvariant() ?? "unknown",
                        receipt = inspection.Receipt,
                        payloadDigest = inspection.PayloadDigest,
                        turn = queue.GetTurn(thread.RuntimeSessionId, inspection.Receipt.TurnId)?.Summary,
                        errorCode = inspection.ErrorCode,
                    });
            case SessionRuntimeOperation.ReadEvents:
                if (request.AfterSeq is not >= 0 || request.Limit is not (>= 1 and <= 128))
                    return Error(400, "invalid_event_cursor");
                return await ReplayEventsAsync(grant.Scope, workspace, thread, request.AfterSeq.Value, request.Limit.Value, ct);
            case SessionRuntimeOperation.AcknowledgeEvents:
                if (request.ThroughSeq is not >= 0)
                    return Error(400, "invalid_event_cursor");
                var acknowledged = await queue.AcknowledgeRemoteEventsAsync(grant.Scope, request.ThroughSeq.Value, ct);
                return acknowledged is null ? Error(409, "event_cursor_not_available") : Reply(200, new { acknowledgedThrough = acknowledged.Value });
            default:
                return Error(404, "unsupported_operation");
        }
    }

    private async Task<RemoteLifecycleReceipt> ReplayEventsAsync(SessionRuntimeScope scope,
        RemoteSessionRuntimeWorkspace workspace, RemoteSessionLifecycleThread thread, long after, int limit, CancellationToken ct)
    {
        var page = await queue.ReadRemoteEventsAsync(scope, after, limit, ct);
        if (page.Gap || page.Events.Count > 0 || await IsSealedAsync(scope, ct))
            return Reply(page.Gap ? 409 : 200, page);

        // Native records are committed at the provider before they enter the spool.
        // Reading the same durable prefix after a crash converges on the same record IDs.
        var (info, history) = workspace.Provider.GetSession(thread.RuntimeSessionId);
        if (info is null || !Matches(info, thread.RuntimeSessionId, workspace) || info.ProviderSessionId != thread.NativeThreadId)
            return Reply(200, page);

        var through = Math.Max(thread.NativeHistoryThrough, await queue.ReadRemoteNativeWatermarkAsync(scope, ct));
        string? inputUid = null;
        var added = 0;
        foreach (var record in history.Where(record => record.SessionId == thread.RuntimeSessionId).OrderBy(record => record.Id))
        {
            if (record.Id > thread.NativeHistoryThrough && record.Role == "user" && !string.IsNullOrWhiteSpace(record.MessageUid))
                inputUid = record.MessageUid;
            if (record.Id <= through) continue;
            var body = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (body.Length > SessionInputQueueStore.MaxRemoteEventBytes && added == 0)
                return Error(413, "remote_event_too_large");
            if (body.Length > SessionInputQueueStore.MaxRemoteEventBytes) break;
            try
            {
                await queue.AppendRemoteEventAsync(scope,
                    new("native:" + record.Id.ToString(CultureInfo.InvariantCulture), inputUid, inputUid, body), ct);
            }
            catch (SessionInputQueueStoreException error) when (error.Code == "remote_event_backpressure")
            {
                break;
            }
            if (++added >= 128) break;
        }

        var replay = await queue.ReadRemoteEventsAsync(scope, after, limit, ct);
        return Reply(replay.Gap ? 409 : 200, replay);
    }

    private async Task<RemoteLifecycleReceipt> AdmitInputAsync(SessionRuntimeGrant grant, string text,
        RemoteSessionRuntimeWorkspace workspace, RemoteSessionLifecycleThread thread, CancellationToken ct)
    {
        if (await queue.ReadRemoteInputAsync(grant.Scope, grant.RequestId, ct) is null)
        {
            var current = workspace.Provider.GetSessionInfo(thread.RuntimeSessionId);
            if (current is null || !Matches(current, thread.RuntimeSessionId, workspace) || current.ProviderSessionId != thread.NativeThreadId)
                return Error(409, "runtime_identity_mismatch");
            if (current.Status == SessionStatus.Error || current.Status == SessionStatus.Stopped && !SessionStopReasons.IsAutoResumable(current.StopReason))
                return Error(409, "remote_session_not_writable");
        }

        try
        {
            var submission = new SessionInputQueueSubmission(thread.RuntimeSessionId, grant.Scope.OwnerId,
                [new("text", text)], text, null, Provenance(grant), SessionInputDeliveryPolicy.AfterCurrent, grant.RequestId, [], grant.RequestId);
            var receipt = await queue.EnqueueRemoteInputAsync(grant, submission, ct);
            signalInput?.Invoke(thread.RuntimeSessionId);
            return Reply(202, receipt);
        }
        catch (SessionInputQueueStoreException error) when (error.Code == "idempotency_conflict")
        {
            return Error(409, error.Code);
        }
    }

    private static RemoteLifecycleReceipt Reply(int status, object payload) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(payload, Json));

    private static RemoteLifecycleReceipt Error(int status, string code) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(new { code }, Json));
}
