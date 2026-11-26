using System.Net.Http;
using System.Security.Claims;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public sealed partial class SessionInputQueueService
{
    private RemoteControllerInput? _remoteController;

    internal void AttachRemoteController(ControllerRemoteSessions sessions, SessionTranscriptPipeline transcript,
        Func<string, ClaimsPrincipal?> validateToken)
        => _remoteController = new(sessions, transcript, validateToken);

    private async Task<bool> ProcessControllerRemoteInputAsync(string sessionId, CancellationToken ct)
    {
        if (_remoteController is not { } controller || !ControllerRemoteSessions.IsLogicalId(sessionId)
            || (await _store.ReadBindingSnapshotAsync(sessionId, ct))?.Active.Native.Remote is not { } binding)
            return false;

        var head = await _store.GetHeadAsync(sessionId, ct);
        if (head is null || head.OwnerUserId != binding.Scope.OwnerId
            || head.State is not (SessionInputQueueState.Pending or SessionInputQueueState.Delivering)
            || SessionInputQueueStore.IsDeferred(head.NextAttemptAt, DateTimeOffset.UtcNow))
            return true;

        var session = new UnifiedSessionInfo
        {
            Id = sessionId,
            Provider = binding.Placement.ProviderId,
            UserId = binding.Scope.OwnerId,
            ProjectName = binding.Placement.Workspace,
            ProjectPath = "",
            NodeId = binding.Scope.NodeId,
        };
        var recovery = await ReadRecoveryAuthorityAsync(session, head, ct);
        var principal = recovery.AccessToken is { } token ? controller.ValidateToken(token) : null;
        if (principal is null || NodeAccess.Subject(principal) != head.OwnerUserId)
        {
            var error = recovery.ErrorCode ?? "recovery_identity_invalid";
            if (head.State == SessionInputQueueState.Pending)
                await _store.BlockPendingAsync(head, error, recovery.Retryable, ct);
            else
                await _store.SetControllerRemoteInputErrorAsync(head, error, ct);
            if (head.ErrorCode != error)
                await PublishAsync(sessionId, [head.Id], "blocked", ct, errorCode: error);
            return true;
        }

        if (_deliveryPaused() || HasStopIntent(sessionId)) return true;
        try
        {
            var applied = await controller.Sessions.AdvanceInputAsync(principal, binding, head.Id, controller.Transcript, ct);
            if (applied is null) return true;
            if (applied.TranscriptChanged)
            {
                _broadcaster.Broadcast("ai-session.changed", new
                {
                    sessionId,
                    provider = binding.Placement.ProviderId,
                    confidential = _isConfidential(sessionId),
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                });
            }
            if (applied.State == "delivered")
            {
                await PublishAsync(sessionId, [head.Id], "delivered", ct, deliveredMessageUid: head.MessageUid);
                Signal(sessionId);
                return true;
            }

            var hold = applied.State is "unknown" or "failed" or "cancelled"
                ? applied.ErrorCode ?? "delivery_outcome_unknown" : null;
            if (head.ErrorCode != hold)
            {
                await _store.SetControllerRemoteInputErrorAsync(head, hold, ct);
                await PublishAsync(sessionId, [head.Id], hold is null ? "delivering" : "blocked", ct, errorCode: hold);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is NodeHubException or ControllerRemoteSessionException or HttpRequestException)
        {
            var error = ex switch
            {
                NodeHubException failure => failure.Code,
                ControllerRemoteSessionException failure => failure.Code,
                _ => "remote_runtime_unavailable",
            };
            await _store.SetControllerRemoteInputErrorAsync(head, error, ct);
            if (head.ErrorCode != error)
                await PublishAsync(sessionId, [head.Id], "blocked", ct, errorCode: error);
        }
        return true;
    }

    private sealed record RemoteControllerInput(ControllerRemoteSessions Sessions, SessionTranscriptPipeline Transcript,
        Func<string, ClaimsPrincipal?> ValidateToken);
}
