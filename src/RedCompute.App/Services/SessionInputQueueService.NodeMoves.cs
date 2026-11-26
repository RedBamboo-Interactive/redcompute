using System.Net.Http;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public sealed partial class SessionInputQueueService
{
    private DateTimeOffset _nextNodeRetirementSweep;
    private Task? _nodeRetirementSweep;

    private void StartNodeRetirementSweep(CancellationToken ct)
    {
        if (_remoteController is not { } controller || _deliveryPaused() || DateTimeOffset.UtcNow < _nextNodeRetirementSweep
            || _nodeRetirementSweep is { IsCompleted: false })
            return;
        _nextNodeRetirementSweep = DateTimeOffset.UtcNow.AddSeconds(30);
        _nodeRetirementSweep = ReconcileNodeRetirementsAsync(controller, ct);
    }

    private async Task ReconcileNodeRetirementsAsync(RemoteControllerInput controller, CancellationToken ct)
    {
        try
        {
            foreach (var (sessionId, retired) in await _store.ReadPendingNodeRetirementsAsync(ct))
            {
                if (retired.Native.Remote is not { } remote || retired.RetirementMoveId is not { } moveId
                    || await _store.ReadBindingSnapshotAsync(sessionId, ct) is not { } snapshot
                    || snapshot.Active.Generation <= retired.Generation || !snapshot.Threads.Contains(retired))
                    continue;
                var item = await _store.GetAsync(sessionId, moveId, remote.Scope.OwnerId, ct);
                if (item is not { State: SessionInputQueueState.Delivered }) continue;
                var authority = await ReadRecoveryAuthorityAsync(RemoteQueueInfo(snapshot, remote), item, ct);
                var principal = authority.AccessToken is { } token ? controller.ValidateToken(token) : null;
                if (principal is null || NodeAccess.Subject(principal) != remote.Scope.OwnerId) continue;
                try
                {
                    if (await controller.Sessions.RetireRedirectedSourceAsync(principal, remote, moveId, ct))
                        await _store.ConfirmNodeRetirementAsync(sessionId, retired, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested
                    && ex is NodeHubException or ControllerRemoteSessionException or HttpRequestException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log($"[InputQueue] Node retirement reconciliation failed: {ex.Message}", null);
        }
    }

    private async Task<bool> ProcessNodeMoveAsync(string sessionId, CancellationToken ct)
    {
        if (_remoteController is not { } controller || BindingCommands is not { } commands || BindingHistory is not { } reader)
            return false;
        var head = await _store.ReadPendingHeldBindingReplacementAsync(sessionId, ct)
            ?? await _store.GetHeadAsync(sessionId, ct);
        if (head?.BindingChange is not { ChangesNode: true } change) return false;
        if (head.State != SessionInputQueueState.Pending || SessionInputQueueStore.IsDeferred(head.NextAttemptAt, DateTimeOffset.UtcNow))
            return true;

        var snapshot = await _store.ReadBindingSnapshotAsync(sessionId, ct);
        var initial = snapshot?.Active.Native;
        var (provider, info) = initial?.Remote is null ? FindSession(sessionId) : (null, (await reader.GetSessionInfoAsync(sessionId)).Info);
        if (info is null || info.UserId != head.OwnerUserId) return true;
        if (initial is null)
        {
            var instance = provider is null ? null : commands.InstanceFor(provider);
            if (instance is null || info.ProviderSessionId is null) return true;
            initial = new(sessionId, info.ProviderSessionId,
                new(info.Provider, instance, controller.LocalNodeId, info.Model ?? "", info.Effort), info.StartedAt);
        }
        var recovery = await ReadRecoveryAuthorityAsync(info, head, ct);
        var principal = recovery.AccessToken is { } token ? controller.ValidateToken(token) : null;
        if (principal is null || NodeAccess.Subject(principal) != head.OwnerUserId)
        {
            await BlockNodeMoveAsync(head, recovery.ErrorCode ?? "recovery_identity_invalid", recovery.Retryable, ct);
            return true;
        }

        if (_deliveryPaused() || HasStopIntent(sessionId)) return true;
        var creation = info.JobId is { } job ? _jobTracker.GetJob(job)?.CreationProvenance : null;
        var runtime = new NodeSessionBindingRuntime(controller.Sessions, principal, commands, reader, info, _store, head,
            recovery.AccessToken!, InvocationProvenance(head), creation);
        try
        {
            var switched = await new SessionBindingSwitch(_store, runtime).ApplyAsync(head, initial, ct);
            if (switched?.State == BindingMoveState.Rejected)
                await PublishAsync(sessionId, [head.Id], "failed", ct, errorCode: switched.Error);
            if (switched?.State == BindingMoveState.Committed)
            {
                await PublishAsync(sessionId, [head.Id], "delivered", CancellationToken.None);
                PublishNodePlacementChanged(sessionId, change.Destination.ProviderId);
                Signal(sessionId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is NodeHubException or ControllerRemoteSessionException or HttpRequestException)
        {
            await BlockNodeMoveAsync(head, NodeMoveError(ex), true, ct);
        }

        return true;
    }

    private async Task BlockNodeMoveAsync(SessionInputQueueItem item, string error, bool retryable, CancellationToken ct)
    {
        await _store.BlockPendingAsync(item, error, retryable, ct);
        if (item.ErrorCode != error)
            await PublishAsync(item.SessionId, [item.Id], "blocked", ct, errorCode: error);
    }

    private async Task ProcessNodeActivationAsync(RemoteControllerInput controller, SessionBindingSnapshot snapshot,
        ControllerRemoteSession binding, CancellationToken ct)
    {
        var sessionId = snapshot.LogicalId;
        var item = await _store.GetAsync(sessionId, binding.ActivationMoveId!, binding.Scope.OwnerId, ct);
        if (item is null || item.State != SessionInputQueueState.Delivered || item.BindingChange?.ChangesNode != true
            || SessionInputQueueStore.IsDeferred(item.NextAttemptAt, DateTimeOffset.UtcNow))
            return;

        var recovery = await ReadRecoveryAuthorityAsync(RemoteQueueInfo(snapshot, binding), item, ct);
        var principal = recovery.AccessToken is { } token ? controller.ValidateToken(token) : null;
        if (principal is null || NodeAccess.Subject(principal) != binding.Scope.OwnerId)
        {
            await DeferNodeActivationAsync(binding, item, recovery.ErrorCode ?? "recovery_identity_invalid", ct);
            return;
        }

        if (_deliveryPaused() || HasStopIntent(sessionId)) return;
        try
        {
            if (await controller.Sessions.ActivateCommittedMoveAsync(principal, sessionId, ct) is not null)
            {
                await PublishAsync(sessionId, [item.Id], "delivered", CancellationToken.None);
                PublishNodePlacementChanged(sessionId, binding.Placement.ProviderId);
                Signal(sessionId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is NodeHubException or ControllerRemoteSessionException or HttpRequestException)
        {
            await DeferNodeActivationAsync(binding, item, NodeMoveError(ex), ct);
        }
    }

    private async Task DeferNodeActivationAsync(ControllerRemoteSession binding, SessionInputQueueItem item, string error,
        CancellationToken ct)
    {
        await _store.DeferControllerRemoteActivationAsync(binding, error, ct);
        if (item.ErrorCode != error)
            await PublishAsync(binding.Scope.LogicalSessionId, [item.Id], "blocked", ct, errorCode: error);
    }

    private static UnifiedSessionInfo RemoteQueueInfo(SessionBindingSnapshot snapshot, ControllerRemoteSession binding)
        => new UnifiedSessionInfo
        {
            Id = snapshot.LogicalId,
            Provider = binding.Placement.ProviderId,
            UserId = binding.Scope.OwnerId,
            ProjectName = binding.Placement.Workspace,
            ProjectPath = "",
            NodeId = binding.Scope.NodeId,
        }.AsLogicalSession(snapshot.LogicalId, snapshot.Threads[0].Native.Target.ProviderId);

    private static string NodeMoveError(Exception error) => error switch
    {
        NodeHubException failure => failure.Code,
        ControllerRemoteSessionException failure => failure.Code,
        _ => "remote_runtime_unavailable",
    };

    private void PublishNodePlacementChanged(string sessionId, string provider)
        => _broadcaster.Broadcast("ai-session.changed", new
        {
            sessionId, provider, confidential = _isConfidential(sessionId),
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
}
