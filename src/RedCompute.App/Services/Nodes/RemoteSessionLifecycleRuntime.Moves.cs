using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Nodes;

internal sealed record RemoteSessionMoveRequest(string MoveId, long SourceGeneration, long DestinationGeneration,
    string Revision, string ProviderInstanceId, string? RuntimeSessionId = null, string? NativeThreadId = null,
    long? PreviousGeneration = null, string? Model = null, string? Effort = null, long HistoryThrough = 0,
    IReadOnlyList<BindingHandoffText>? History = null, IReadOnlyList<BindingHandoffArtifact>? Artifacts = null,
    RemoteSessionStartFence? Fence = null);

internal sealed partial class RemoteSessionLifecycleRuntime
{
    private async Task<RemoteLifecycleReceipt> ApplyMoveAsync(SessionRuntimeGrant grant, CancellationToken ct)
    {
        RemoteSessionMoveRequest? request;
        try { request = JsonSerializer.Deserialize<RemoteSessionMoveRequest>(grant.Payload, Json); }
        catch (JsonException) { return Error(400, "invalid_move_request"); }
        if (request is null || !ValidMoveRequest(grant, request))
            return Error(400, "invalid_move_request");

        await _lifecycle.WaitAsync(ct);
        try
        {
            if (!Authorized(grant.Operation, grant))
                return Error(403, "invalid_runtime_grant");
            var scope = grant.Scope;
            var bindingRequest = new RemoteSessionLifecycleRequest(request.Revision, request.ProviderInstanceId,
                RuntimeSessionId: request.RuntimeSessionId, NativeThreadId: request.NativeThreadId);
            var workspace = grant.Operation == SessionRuntimeOperation.DiscardMove && configuredWorkspace is not null
                ? configuredWorkspace(scope, bindingRequest) : await resolve(scope, bindingRequest, ct);
            if (workspace is null || workspace.OwnerId != scope.OwnerId || workspace.Reference != scope.AccessScope
                || workspace.Revision != request.Revision || workspace.ProviderInstanceId != request.ProviderInstanceId
                || !_host.Files.DirectoryExists(_host.Path(workspace.ProjectPath)))
                return Error(422, "workspace_unavailable");
            if (request.Fence is { } fence && (workspace.Provider.ProviderId != fence.ProviderId
                || workspace.Access.AccessLevel != fence.AccessLevel || workspace.Access.MaxAccessLevel != fence.MaxAccessLevel))
                return Error(409, "remote_placement_changed");

            var reservation = await queue.ReserveRemoteLifecycleAsync(grant, ct);
            if (reservation.State == RemoteLifecycleState.Conflict)
                return Error(409, "lifecycle_request_conflict");
            if (reservation.Receipt is { } receipt)
                return receipt;

            // A lost network response never cancels a reserved provider operation.
            var result = grant.Operation switch
            {
                SessionRuntimeOperation.PrepareMove when await IsSealedAsync(scope, CancellationToken.None)
                    => Error(409, "binding_generation_sealed"),
                SessionRuntimeOperation.ActivateMove when await IsSealedAsync(scope, CancellationToken.None)
                    => Error(409, "binding_generation_sealed"),
                SessionRuntimeOperation.PrepareMove when await IsSealedAsync(scope, CancellationToken.None)
                    => Error(409, "binding_generation_sealed"),
                SessionRuntimeOperation.ActivateMove when await IsSealedAsync(scope, CancellationToken.None)
                    => Error(409, "binding_generation_sealed"),
                SessionRuntimeOperation.PrepareMove => await PrepareMoveAsync(grant, request, workspace),
                SessionRuntimeOperation.SealMoveSource => await SealMoveSourceAsync(scope, bindingRequest, workspace),
                SessionRuntimeOperation.ActivateMove => await ActivateMoveAsync(scope, request),
                SessionRuntimeOperation.DiscardMove => await DiscardMoveAsync(scope, request, workspace),
                _ => throw new InvalidOperationException("Expected a session move phase."),
            };
            if (result.Status >= 500 || await queue.CompleteRemoteLifecycleAsync(grant, result)) return result;
            return (await queue.ReserveRemoteLifecycleAsync(grant)).Receipt ?? Error(503, "lifecycle_outcome_unknown");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static bool ValidMoveRequest(SessionRuntimeGrant grant, RemoteSessionMoveRequest request)
    {
        var operation = grant.Operation;
        if (request.MoveId is not { Length: > 0 and <= 160 } || string.IsNullOrWhiteSpace(request.MoveId)
            || grant.RequestId != SessionRuntimeProtocol.MoveRequestId(operation, request.MoveId)
            || string.IsNullOrWhiteSpace(request.Revision) || string.IsNullOrWhiteSpace(request.ProviderInstanceId)
            || request.SourceGeneration < 0 || request.DestinationGeneration <= request.SourceGeneration
            || request.DestinationGeneration - request.SourceGeneration != 1
            || grant.Scope.Generation != (operation == SessionRuntimeOperation.SealMoveSource
                ? request.SourceGeneration : request.DestinationGeneration))
            return false;

        var hasThread = !string.IsNullOrWhiteSpace(request.RuntimeSessionId) && !string.IsNullOrWhiteSpace(request.NativeThreadId);
        var noThread = request.RuntimeSessionId is null && request.NativeThreadId is null;
        if (operation != SessionRuntimeOperation.PrepareMove)
            return (operation == SessionRuntimeOperation.DiscardMove ? noThread : hasThread)
                && request.PreviousGeneration is null && request.Model is null && request.Effort is null
                && request.HistoryThrough == 0 && request.History is null && request.Artifacts is null && request.Fence is null;

        return request.Fence is not null && !string.IsNullOrWhiteSpace(request.Model) && request.HistoryThrough >= 0
            && (request.PreviousGeneration is { } previous ? previous >= 0 && previous < request.DestinationGeneration && hasThread : noThread)
            && (request.History is null || request.History.Count <= 24
                && request.History.All(row => row is { Role: "user" or "assistant", Content: not null } && row.Content.Length <= 12_000)
                && request.History.Sum(row => row.Content.Length) <= 12_000)
            && (request.Artifacts is null || request.Artifacts.Count <= 12
                && request.Artifacts.All(artifact => artifact is { Kind: { Length: <= 32 }, Name: { Length: <= 120 } }
                    && artifact.RecordId > 0 && artifact.RecordId <= request.HistoryThrough));
    }

    private async Task<RemoteLifecycleReceipt> PrepareMoveAsync(SessionRuntimeGrant grant, RemoteSessionMoveRequest request,
        RemoteSessionRuntimeWorkspace workspace)
    {
        var scope = grant.Scope;
        if (await ReadMoveThreadAsync(scope, SessionRuntimeOperation.DiscardMove, request.MoveId) is not null
            || await queue.ReadRemoteMoveReceiptAsync(scope, SessionRuntimeOperation.DiscardMove, request.MoveId, CancellationToken.None) is not null)
            return Error(409, "binding_preparation_discarded");

        if (await queue.ReadRemoteLifecycleStartAsync(scope) is not null)
            return Error(409, "binding_generation_already_active");

        var target = new BindingTarget(workspace.Provider.ProviderId, request.ProviderInstanceId, scope.NodeId, request.Model!, request.Effort);
        SessionBinding? previous = null;
        if (request.PreviousGeneration is { } generation)
        {
            var priorScope = scope with { Generation = generation };
            var started = await queue.ReadRemoteLifecycleStartAsync(priorScope);
            var thread = started is null ? null : JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(started.Body, Json);
            if (!MatchesMoveThread(thread, request) || !await IsSealedAsync(priorScope, CancellationToken.None))
                return Error(409, "binding_previous_thread_missing");
            previous = new(generation, new(thread!.RuntimeSessionId, thread.NativeThreadId, target), 0);
        }

        var identity = JsonSerializer.Serialize(new { scope.NodeId, scope.RuntimeId, scope.LogicalSessionId }, Json);
        var move = new SessionBindingMove(request.MoveId, request.SourceGeneration, target, request.HistoryThrough, BindingMoveState.Pending);
        var source = new SessionBinding(request.SourceGeneration, new("", "", target), 0);
        var preparation = new BindingPreparation(identity, move, source, previous, request.History ?? []);
        var nativeSource = new UnifiedSessionInfo
        {
            Id = scope.LogicalSessionId, Provider = target.ProviderId, ProjectName = workspace.Reference,
            UserId = scope.OwnerId, ProjectPath = workspace.ProjectPath, RepositoryId = workspace.RepositoryId,
            AccessLevel = workspace.Access.AccessLevel, MaxAccessLevel = workspace.Access.MaxAccessLevel,
            AccessLevelSource = workspace.Access.Source,
        };
        var prepared = await NativeSessionBindingPreparer.PrepareAsync(preparation, workspace.Provider, nativeSource,
            workspace.ProviderInstanceId, Provenance(grant), null, request.Artifacts ?? [], CancellationToken.None);
        if (prepared.Binding is not { } binding)
            return Error(409, prepared.Refusal ?? "binding_prepare_refused");
        var (info, history) = workspace.Provider.GetSession(binding.RuntimeSessionId);
        if (info is null || !Matches(info, binding.RuntimeSessionId, workspace) || info.ProviderSessionId != binding.NativeThreadId)
            return Error(409, "runtime_identity_mismatch");

        var boundary = history.Where(record => record.SessionId == binding.RuntimeSessionId).Select(record => record.Id).DefaultIfEmpty().Max();
        return Reply(201, new RemoteSessionLifecycleThread(binding.RuntimeSessionId, binding.NativeThreadId,
            request.ProviderInstanceId, request.Revision, request.Model, request.Effort, info.StartedAt, boundary));
    }

    private async Task<RemoteLifecycleReceipt> SealMoveSourceAsync(SessionRuntimeScope scope,
        RemoteSessionLifecycleRequest request, RemoteSessionRuntimeWorkspace workspace)
    {
        if (await ReadThreadAsync(scope, request, CancellationToken.None) is not { } thread)
            return Error(409, "runtime_identity_mismatch");
        if (await IsSealedAsync(scope, CancellationToken.None))
            return Error(409, "binding_generation_sealed");
        if (await queue.GetHeadAsync(thread.RuntimeSessionId) is not null)
            return Error(409, "remote_session_busy");
        var provider = workspace.Provider;
        var info = provider.GetSessionInfo(thread.RuntimeSessionId);
        if (info is null || !Matches(info, thread.RuntimeSessionId, workspace) || info.ProviderSessionId != thread.NativeThreadId)
            return Error(409, "runtime_identity_mismatch");
        if (info.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(info.StopReason))
            return Reply(200, thread);
        return info.Status == SessionStatus.Idle && provider is IIdleSessionParkingProvider parking
            && await parking.TryParkIdleSessionAsync(thread.RuntimeSessionId, SessionStopReasons.IdleTimeout)
            ? Reply(200, thread) : Error(409, "remote_session_busy");
    }

    private async Task<RemoteLifecycleReceipt> ActivateMoveAsync(SessionRuntimeScope scope, RemoteSessionMoveRequest request)
    {
        if (await queue.ReadRemoteMoveReceiptAsync(scope, SessionRuntimeOperation.DiscardMove, request.MoveId, CancellationToken.None) is not null)
            return Error(409, "binding_preparation_discarded");
        if (await queue.ReadRemoteLifecycleStartAsync(scope) is not null)
            return Error(409, "binding_generation_already_active");
        var prepared = await ReadMoveThreadAsync(scope, SessionRuntimeOperation.PrepareMove, request.MoveId);
        return MatchesMoveThread(prepared, request) ? Reply(200, prepared!) : Error(409, "binding_preparation_mismatch");
    }

    private async Task<RemoteLifecycleReceipt> DiscardMoveAsync(SessionRuntimeScope scope, RemoteSessionMoveRequest request,
        RemoteSessionRuntimeWorkspace workspace)
    {
        if (await ReadMoveThreadAsync(scope, SessionRuntimeOperation.ActivateMove, request.MoveId) is not null)
            return Error(409, "binding_generation_already_active");
        var prepared = await ReadMoveThreadAsync(scope, SessionRuntimeOperation.PrepareMove, request.MoveId);
        if (prepared is not null && queue.FindRemoteRuntimeBinding(prepared.RuntimeSessionId) is null
            && workspace.Provider.GetSessionInfo(prepared.RuntimeSessionId) is { Status: SessionStatus.Idle } info
            && Matches(info, prepared.RuntimeSessionId, workspace) && info.ProviderSessionId == prepared.NativeThreadId
            && workspace.Provider is IIdleSessionParkingProvider parking)
            await parking.TryParkIdleSessionAsync(prepared.RuntimeSessionId, SessionStopReasons.IdleTimeout);
        return Reply(200, new { request.MoveId });
    }

    private async Task<RemoteSessionLifecycleThread?> ReadMoveThreadAsync(SessionRuntimeScope scope,
        SessionRuntimeOperation operation, string moveId)
    {
        var receipt = await queue.ReadRemoteMoveReceiptAsync(scope, operation, moveId, CancellationToken.None);
        return receipt is null ? null : JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(receipt.Body, Json);
    }

    private Task<bool> IsSealedAsync(SessionRuntimeScope scope, CancellationToken ct)
        => queue.IsRemoteRuntimeGenerationRetiredAsync(scope, ct);

    private static bool MatchesMoveThread(RemoteSessionLifecycleThread? thread, RemoteSessionMoveRequest request)
        => thread is not null && thread.RuntimeSessionId == request.RuntimeSessionId && thread.NativeThreadId == request.NativeThreadId
            && thread.ProviderInstanceId == request.ProviderInstanceId && thread.Revision == request.Revision;
}
