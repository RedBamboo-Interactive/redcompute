using System.Net;
using System.Security.Claims;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;

namespace RedCompute.App.Services.Nodes;

internal sealed partial class ControllerRemoteSessions
{
    internal async Task<IReadOnlyList<RemoteMoveWorkspace>> ListMoveWorkspacesAsync(ClaimsPrincipal caller, string logicalId,
        long generation, string nodeId, string runtimeId, CancellationToken ct)
    {
        var owner = NodeAccess.Subject(caller) ?? throw new ControllerRemoteSessionException(401, "signed_owner_required");
        var scope = new SessionRuntimeScope(nodeId, runtimeId, logicalId, generation, owner, SessionRuntimeProtocol.MoveCatalogueScope);
        var catalogue = await ApplyAsync<RemoteMoveCatalogue>(caller, scope, SessionRuntimeOperation.Inspect,
            Guid.NewGuid().ToString("N"), new RemoteSessionLifecycleRequest("", "", MoveCatalogue: true), HttpStatusCode.OK, ct);
        if (catalogue.Workspaces is not { Count: <= 32 } || catalogue.Workspaces.Any(workspace => workspace is null
            || !Identifier(workspace.Workspace) || !Identifier(workspace.Revision) || !Identifier(workspace.ProviderId)
            || !Identifier(workspace.ProviderInstanceId) || !SessionAccessLevels.IsKnown(workspace.AccessLevel)
            || !SessionAccessLevels.IsKnown(workspace.MaxAccessLevel)
            || SessionAccessLevels.Rank(workspace.AccessLevel) > SessionAccessLevels.Rank(workspace.MaxAccessLevel)))
            throw new ControllerRemoteSessionException(502, "invalid_remote_runtime_reply");
        return catalogue.Workspaces;
    }

    internal async Task<RemoteSessionPlacementPreview> ValidateMoveAsync(ClaimsPrincipal caller, string logicalId,
        long generation, string requestId, RemoteSessionPlacement placement, string accessCap, CancellationToken ct)
    {
        var scope = GetScope(caller, requestId, placement) with { LogicalSessionId = logicalId, Generation = generation };
        var preview = await InspectAsync(caller, scope, requestId, placement, ct, movePreview: true);
        if (!preview.SupportsBindingMoves)
            throw new ControllerRemoteSessionException(422, "session_runtime_move_unsupported");
        if (!SessionAccessLevels.IsKnown(accessCap)
            || SessionAccessLevels.Rank(preview.MaxAccessLevel) > SessionAccessLevels.Rank(accessCap))
            throw new ControllerRemoteSessionException(422, "remote_session_access_unavailable");
        return preview;
    }

    internal async Task<bool> IsMoveSourceIdleAsync(ClaimsPrincipal caller, ControllerRemoteSession source,
        string requestId, CancellationToken ct)
    {
        EnsureMoveOwner(caller, source.Scope);
        var thread = source.Thread;
        var state = await ApplyAsync<RemoteMoveSourceState>(caller, source.Scope, SessionRuntimeOperation.Inspect,
            requestId, ThreadRequest(thread), HttpStatusCode.OK, ct);
        if (state.RuntimeSessionId != thread.RuntimeSessionId || state.NativeThreadId != thread.NativeThreadId)
            throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");
        return state.Idle;
    }

    internal async Task<NativeSessionBinding> PrepareMoveAsync(ClaimsPrincipal caller, BindingPreparation preparation,
        IReadOnlyList<BindingHandoffArtifact> artifacts, CancellationToken ct)
    {
        var move = preparation.Move;
        var target = move.Destination;
        var (scope, placement, preview) = MoveTarget(caller, preparation.LogicalId, move);
        var prior = preparation.PreviousDestination?.Native.Remote;
        if (preparation.PreviousDestination is not null && prior is null)
            throw new ControllerRemoteSessionException(409, "binding_previous_thread_missing");
        var request = new RemoteSessionMoveRequest(move.Id, move.SourceGeneration, scope.Generation,
            placement.Revision, placement.ProviderInstanceId, prior?.Thread.RuntimeSessionId,
            prior?.Thread.NativeThreadId, prior?.Scope.Generation, target.Model, target.Effort,
            move.HistoryThrough, preparation.History, artifacts, Fence(preview), PreviousMoveId: prior?.ActivationMoveId);
        var thread = await ApplyMovePhaseAsync<RemoteSessionLifecycleThread>(caller, scope, SessionRuntimeOperation.PrepareMove,
            request, HttpStatusCode.Created, ct);
        if (!Identifier(thread.RuntimeSessionId) || !Identifier(thread.NativeThreadId)
            || thread.ProviderInstanceId != target.ProviderInstanceId || thread.Revision != placement.Revision
            || thread.Model != target.Model || thread.Effort != target.Effort || thread.NativeHistoryThrough < 0
            || prior is not null && (prior.Thread.RuntimeSessionId != thread.RuntimeSessionId
                || prior.Thread.NativeThreadId != thread.NativeThreadId))
            throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");
        var remote = new ControllerRemoteSession(scope, placement, preview, thread, move.Id);
        return new(thread.RuntimeSessionId, thread.NativeThreadId, target, thread.StartedAt, Remote: remote);
    }

    internal async Task<bool> SealMoveSourceAsync(ClaimsPrincipal caller, ControllerRemoteSession source,
        SessionBindingMove move, CancellationToken ct)
    {
        EnsureMoveOwner(caller, source.Scope);
        var thread = source.Thread;
        var request = MoveForThread(move.Id, move.SourceGeneration, thread);
        try
        {
            var sealedThread = await ApplyMovePhaseAsync<RemoteSessionLifecycleThread>(caller, source.Scope,
                SessionRuntimeOperation.SealMoveSource, request, HttpStatusCode.OK, ct);
            if (sealedThread != thread)
                throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");
            return true;
        }
        catch (ControllerRemoteSessionException error) when (error.Status == 409 && error.Code == "remote_session_busy")
        {
            // This exact durable refusal certifies that the requested seal did not apply.
            return false;
        }
    }

    internal async Task<ControllerRemoteSession?> ActivateCommittedMoveAsync(ClaimsPrincipal caller, string logicalId,
        CancellationToken ct)
    {
        var (_, session) = await ReadOwnedAsync(caller, logicalId, ct);
        if (!session.ActivationPending) return session;
        var request = MoveForThread(session.ActivationMoveId!, session.Scope.Generation - 1, session.Thread);
        var activated = await ApplyMovePhaseAsync<RemoteSessionLifecycleThread>(caller, session.Scope,
            SessionRuntimeOperation.ActivateMove, request, HttpStatusCode.OK, ct);
        if (activated != session.Thread)
            throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");
        return await queue.ConfirmControllerRemoteActivationAsync(session, CancellationToken.None);
    }

    internal async Task<bool> RetireRedirectedSourceAsync(ClaimsPrincipal caller, ControllerRemoteSession retired,
        string retirementId, CancellationToken ct)
    {
        EnsureMoveOwner(caller, retired.Scope);
        if (retired.ActivationPending)
        {
            var tombstone = new RemoteSessionMoveRequest(retired.ActivationMoveId!, retired.Scope.Generation - 1,
                retired.Scope.Generation, retired.Thread.Revision, retired.Thread.ProviderInstanceId);
            try
            {
                var discarded = await ApplyMovePhaseAsync<RemoteDiscardedMove>(caller, retired.Scope,
                    SessionRuntimeOperation.DiscardMove, tombstone, HttpStatusCode.OK, ct);
                if (discarded.MoveId != retired.ActivationMoveId)
                    throw new ControllerRemoteSessionException(502, "remote_move_receipt_mismatch");
                return true;
            }
            catch (ControllerRemoteSessionException ex) when (ex.Status == 409 && ex.Code == "binding_generation_already_active") { }
        }
        try
        {
            var request = MoveForThread(retirementId, retired.Scope.Generation, retired.Thread);
            var sealedThread = await ApplyMovePhaseAsync<RemoteSessionLifecycleThread>(caller, retired.Scope,
                SessionRuntimeOperation.SealMoveSource, request, HttpStatusCode.OK, ct);
            return sealedThread == retired.Thread;
        }
        catch (ControllerRemoteSessionException ex) when (ex.Status == 409 && ex.Code == "binding_generation_sealed")
        {
            return true;
        }
    }

    internal async Task DiscardMoveAsync(ClaimsPrincipal caller, string logicalId, SessionBindingMove move, CancellationToken ct)
    {
        var (scope, placement, _) = MoveTarget(caller, logicalId, move);
        var request = new RemoteSessionMoveRequest(move.Id, move.SourceGeneration, scope.Generation,
            placement.Revision, placement.ProviderInstanceId);
        var result = await ApplyMovePhaseAsync<RemoteDiscardedMove>(caller, scope, SessionRuntimeOperation.DiscardMove,
            request, HttpStatusCode.OK, ct);
        if (result.MoveId != move.Id)
            throw new ControllerRemoteSessionException(502, "remote_move_receipt_mismatch");
    }

    private Task<T> ApplyMovePhaseAsync<T>(ClaimsPrincipal caller, SessionRuntimeScope scope, SessionRuntimeOperation phase,
        RemoteSessionMoveRequest request, HttpStatusCode expected, CancellationToken ct) where T : class
        => ApplyAsync<T>(caller, scope, phase, SessionRuntimeProtocol.MoveRequestId(phase, request.MoveId), request, expected, ct);

    private static RemoteSessionMoveRequest MoveForThread(string moveId, long sourceGeneration, RemoteSessionLifecycleThread thread)
        => new(moveId, sourceGeneration, checked(sourceGeneration + 1), thread.Revision, thread.ProviderInstanceId,
            thread.RuntimeSessionId, thread.NativeThreadId);

    private static (SessionRuntimeScope Scope, RemoteSessionPlacement Placement, RemoteSessionPlacementPreview Preview)
        MoveTarget(ClaimsPrincipal caller, string logicalId, SessionBindingMove move)
    {
        var target = move.Destination;
        if (target.Runtime is not { } runtime)
            throw new ControllerRemoteSessionException(422, "invalid_remote_placement");
        var placement = new RemoteSessionPlacement(target.NodeId, runtime.RuntimeId, runtime.Workspace, runtime.Revision,
            target.ProviderId, target.ProviderInstanceId, target.Model, target.Effort);
        var scope = GetScope(caller, move.Id, placement) with
        {
            LogicalSessionId = logicalId, Generation = checked(move.SourceGeneration + 1),
        };
        return (scope, placement, new(target.NodeId, runtime.RuntimeId, runtime.Workspace, runtime.Revision,
            target.ProviderId, target.ProviderInstanceId, runtime.AccessLevel, runtime.MaxAccessLevel, SupportsBindingMoves: true));
    }

    private static void EnsureMoveOwner(ClaimsPrincipal caller, SessionRuntimeScope scope)
    {
        if (NodeAccess.Subject(caller) != scope.OwnerId)
            throw new ControllerRemoteSessionException(404, "session_not_found");
    }

    private sealed record RemoteMoveCatalogue(IReadOnlyList<RemoteMoveWorkspace> Workspaces);
    private sealed record RemoteMoveSourceState(bool Idle, string RuntimeSessionId, string NativeThreadId);
    private sealed record RemoteDiscardedMove(string MoveId);
}
