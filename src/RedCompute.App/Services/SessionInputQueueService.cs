using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RedBamboo.AppHost.WebSockets;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

public sealed record SessionInputQueueSummary(
    int Depth,
    string State,
    string? BlockedReason,
    string? HeadItemId,
    string? ErrorCode);

public sealed record SessionInputAdmissionResult(
    SessionInputQueueItem Item,
    bool Existing,
    string Disposition,
    SessionInputQueueSummary Queue);

public sealed record SessionInputQueueChanged(
    string SessionId,
    IReadOnlyList<string> ItemIds,
    string Transition,
    int Depth,
    string State,
    string? BlockedReason = null,
    string? ErrorCode = null,
    string? DeliveredMessageUid = null);

/// <summary>
/// RedCompute-owned admission and delivery coordinator. Browser state is never used as a lease or
/// scheduling signal; provider session state and the durable queue are authoritative.
/// </summary>
public sealed class SessionInputQueueService
{
    private static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SessionInputQueueStore _store;
    private readonly InputAttachmentStore _attachments;
    private readonly CapabilityRegistry _registry;
    private readonly IJobTracker _jobTracker;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly Action<string, Guid?> _log;
    private readonly Func<string, bool> _isConfidential;
    private readonly Func<bool> _deliveryPaused;
    private readonly Func<UnifiedSessionInfo, SessionInputQueueItem, CancellationToken, Task<SessionRecoveryAuthority>>? _recoverAuthority;
    private readonly Func<DeploymentVerificationTarget, DeploymentVerificationState>? _deploymentTarget;
    private readonly Func<CancellationToken, Task>? _reconcileCallbacks;
    private readonly string _leaseOwner = $"redcompute:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _manualStops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _pendingStops = new(StringComparer.Ordinal);
    private bool HasStopIntent(string id) => _manualStops.ContainsKey(id) || _pendingStops.GetValueOrDefault(id) > 0;
    private sealed record CallerRecovery(string Token, string Owner, string Scope)
    {
        public override string ToString() => "CallerRecovery { token = [redacted] }";
    }
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public SessionInputQueueService(
        SessionInputQueueStore store,
        InputAttachmentStore attachments,
        CapabilityRegistry registry,
        IJobTracker jobTracker,
        WebSocketBroadcaster broadcaster,
        Action<string, Guid?> log,
        Func<string, bool>? isConfidential = null,
        Func<bool>? deliveryPaused = null,
        Func<UnifiedSessionInfo, SessionInputQueueItem, CancellationToken, Task<SessionRecoveryAuthority>>? recoverAuthority = null,
        Func<DeploymentVerificationTarget, DeploymentVerificationState>? deploymentTarget = null,
        Func<CancellationToken, Task>? reconcileCallbacks = null)
    {
        _store = store;
        _attachments = attachments;
        _registry = registry;
        _jobTracker = jobTracker;
        _broadcaster = broadcaster;
        _log = log;
        _isConfidential = isConfidential ?? (_ => false);
        _deliveryPaused = deliveryPaused ?? (() => false);
        _recoverAuthority = recoverAuthority;
        _deploymentTarget = deploymentTarget;
        _reconcileCallbacks = reconcileCallbacks;

        foreach (var source in _registry.FindProviders<IPluginEventSource>())
            source.PluginEvent += OnProviderEvent;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var recovered = await _store.RecoverExpiredLeasesAsync(ct);
        if (recovered > 0)
            _log($"[InputQueue] Marked {recovered} expired delivery lease(s) as outcome unknown", null);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wake = _signals.Reader.WaitToReadAsync(ct).AsTask();
                var sweep = Task.Delay(TimeSpan.FromSeconds(5), ct);
                await Task.WhenAny(wake, sweep);

                var sessions = new HashSet<string>(StringComparer.Ordinal);
                while (_signals.Reader.TryRead(out var sessionId)) sessions.Add(sessionId);
                if (sweep.IsCompleted)
                {
                    foreach (var sessionId in await _store.GetRunnableSessionIdsAsync(ct))
                        sessions.Add(sessionId);
                    await _store.CleanupTerminalAsync(TerminalRetention, ct);
                }

                foreach (var sessionId in sessions)
                    await ProcessSessionAsync(sessionId, ct);
                if (_reconcileCallbacks is not null) await _reconcileCallbacks(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[InputQueue] Coordinator sweep failed: {ex.Message}", null);
            }
        }
    }

    public Task<SessionInputAdmissionResult> AdmitAsync(SessionInputQueueSubmission submission, CancellationToken ct = default)
        => AdmitCoreAsync(submission, ct, null);
    internal Task<SessionInputAdmissionResult> AdmitWithCallerAsync(SessionInputQueueSubmission submission, string token, CancellationToken ct)
        => AdmitCoreAsync(submission, ct, token);
    private async Task<SessionInputAdmissionResult> AdmitCoreAsync(SessionInputQueueSubmission submission, CancellationToken ct,
        string? authenticatedCallerToken)
    {
        var admitted = await _store.EnqueueAsync(submission, ct);
        await PublishAsync(submission.SessionId, [admitted.Item.Id], admitted.Existing ? "idempotent_replay" : "queued", ct);

        if (!_deliveryPaused()
            && !admitted.Existing
            && submission.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent)
        {
            var (provider, info) = FindSession(submission.SessionId);
            if (provider is not null && info?.Status == SessionStatus.Active)
                provider.InterruptSession(submission.SessionId);
        }

        await ProcessSessionAsync(submission.SessionId, ct, authenticatedCallerToken is null ? null
            : new CallerRecovery(authenticatedCallerToken, admitted.Item.OwnerUserId, admitted.Item.ProvenanceScope));
        var current = await _store.GetAsync(submission.SessionId, admitted.Item.Id, submission.OwnerUserId, ct)
            ?? admitted.Item;
        var summary = await GetSummaryAsync(submission.SessionId, submission.OwnerUserId, ct);
        return new SessionInputAdmissionResult(current, admitted.Existing,
            current.State == SessionInputQueueState.Delivered ? "delivered" : "queued", summary);
    }

    public Task<IReadOnlyList<SessionInputQueueItem>> ListAsync(
        string sessionId, string ownerUserId, bool includeTerminal, CancellationToken ct = default) =>
        _store.ListAsync(sessionId, ownerUserId, includeTerminal, ct);

    public Task<SessionInputQueueItem?> GetAsync(
        string sessionId, string itemId, string ownerUserId, CancellationToken ct = default) =>
        _store.GetAsync(sessionId, itemId, ownerUserId, ct);

    public async Task<SessionInputQueueItem?> CancelAsync(
        string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        SessionInputQueueItem? item = null;
        await WithSessionLockAsync(sessionId, async () => item = await _store.CancelAsync(sessionId, itemId, ownerUserId, ct), ct);
        if (item is not null) await PublishAsync(sessionId, [item.Id], "cancelled", ct);
        return item;
    }

    public async Task<int> CancelSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var items = await _store.CancelSessionAsync(sessionId, ct);
            if (items.Count > 0)
                await PublishAsync(sessionId, items.Select(item => item.Id).ToArray(), "session_cancelled", ct);
            return items.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SessionInputQueueItem?> RetryAsync(
        string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        var item = await _store.RetryAsync(sessionId, itemId, ownerUserId, ct);
        if (item is not null)
        {
            await PublishAsync(sessionId, [item.Id], "retry_requested", ct);
            Signal(sessionId);
        }
        return item;
    }

    public async Task<bool> SendNowAsync(string sessionId, string ownerUserId, CancellationToken ct = default)
    {
        var items = await _store.ListAsync(sessionId, ownerUserId, includeTerminal: false, ct);
        if (items.Count == 0) return false;
        var (provider, info) = FindSession(sessionId);
        if (provider is null || info is null) return false;
        if (info.Status == SessionStatus.Active) provider.InterruptSession(sessionId);
        Signal(sessionId);
        await PublishAsync(sessionId, items.Select(item => item.Id).ToArray(), "send_now_requested", ct);
        return true;
    }

    public async Task<SessionInputQueueSummary> GetSummaryAsync(
        string sessionId, string ownerUserId, CancellationToken ct = default)
    {
        var items = await _store.ListAsync(sessionId, ownerUserId, includeTerminal: false, ct);
        var head = items.FirstOrDefault();
        var (_, info) = FindSession(sessionId);
        var blocked = head?.State == SessionInputQueueState.Failed
            ? "failed_head"
            : HasStopIntent(sessionId) ? "user_stopped"
            : _deliveryPaused() ? "maintenance_drain"
            : info?.Status switch
            {
                SessionStatus.Active => "active_turn",
                SessionStatus.Starting => "session_starting",
                SessionStatus.Stopped => info.StopReason ?? "session_stopped",
                SessionStatus.Error => info.StopReason ?? "session_error",
                _ => null,
            };
        var state = items.Count == 0 ? "empty"
            : head?.State == SessionInputQueueState.Failed ? "failed"
            : head?.State == SessionInputQueueState.Delivering ? "delivering"
            : blocked is null ? "ready" : "waiting_for_session";
        return new SessionInputQueueSummary(items.Count, state, blocked, head?.Id, head?.ErrorCode);
    }

    public async Task StopSessionAsync(string sessionId, Func<Task> stop, CancellationToken ct)
    {
        _pendingStops.AddOrUpdate(sessionId, 1, (_, count) => count + 1);
        try { await WithSessionLockAsync(sessionId, async () => { await stop(); _manualStops[sessionId] = 0; }, ct); }
        finally { _pendingStops.AddOrUpdate(sessionId, 0, (_, count) => Math.Max(0, count - 1)); }
    }

    public async Task<bool> ExplicitResumeAsync(string sessionId, Func<Task<bool>> resume, CancellationToken ct)
    {
        var resumed = false;
        await WithSessionLockAsync(sessionId, async () => {
            var (_, info) = FindSession(sessionId);
            if (info?.Status == SessionStatus.Stopped && info.StopReason is "maintenance_restart" or "orphaned_on_restart")
                await _store.RequireSessionRecoveryAsync(sessionId, ct);
            resumed = await resume();
            if (resumed) _manualStops.TryRemove(sessionId, out _);
        }, ct);
        return resumed;
    }

    public async Task WithSessionLockAsync(string sessionId, Func<Task> action, CancellationToken ct = default)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { await action(); } finally { gate.Release(); }
    }

    public async Task<SessionInputQueueItem?> SupersedeVerificationAsync(string sessionId, string itemId,
        string ownerUserId, DeploymentVerificationTarget target, DeploymentVerificationTarget replacement,
        CancellationToken ct)
    {
        target.Validate(); replacement.Validate();
        var replacementState = _deploymentTarget?.Invoke(replacement);
        if (replacementState is null || replacementState.State is "unknown" or "failed")
            throw new SessionInputQueueStoreException("replacement_target_unavailable", "An exact admitted non-failed replacement target is required");
        SessionInputQueueItem? result = null;
        await WithSessionLockAsync(sessionId, async () => result = await _store.SupersedeVerificationAsync(
            sessionId, itemId, ownerUserId, target, replacement, ct), ct);
        if (result is not null) await PublishAsync(sessionId, [result.Id], "verification_superseded", ct);
        return result;
    }

    public void Signal(string sessionId) => _signals.Writer.TryWrite(sessionId);

    /// <summary>
    /// Called only after delivery has been paused. Crossing every existing per-session gate proves
    /// that work which entered before the pause has either reached the provider or fully exited.
    /// </summary>
    public async Task WaitForDeliveryQuiescenceAsync(CancellationToken ct = default)
    {
        var gates = _sessionLocks.Values.ToArray();
        foreach (var gate in gates)
        {
            await gate.WaitAsync(ct);
            gate.Release();
        }
    }

    internal Task ProcessSessionAsync(string sessionId, CancellationToken ct) => ProcessSessionAsync(sessionId, ct, null);
    private async Task ProcessSessionAsync(string sessionId, CancellationToken ct, CallerRecovery? caller)
    {
        if (_deliveryPaused() || HasStopIntent(sessionId)) return;
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            if (_deliveryPaused() || HasStopIntent(sessionId)) return;
            var (provider, info) = FindSession(sessionId);
            if (provider is null || info is null) return;
            var restart = info.Status == SessionStatus.Stopped && info.StopReason is "maintenance_restart" or "orphaned_on_restart";
            if (info.Status != SessionStatus.Idle && !restart) return;
            // Every pre-restart accepted authority must be revalidated, including later FIFO scopes.
            if (restart) await _store.RequireSessionRecoveryAsync(sessionId, ct);
            var head = await _store.GetHeadAsync(sessionId, ct);
            if (head is null || head.State != SessionInputQueueState.Pending) return;
            var callerMatches = caller is not null && caller.Owner == info.UserId
                && caller.Owner == head.OwnerUserId && caller.Scope == head.ProvenanceScope;
            if (!callerMatches && head.NextAttemptAt is { } due && due > DateTimeOffset.UtcNow) return;
            if (DeploymentVerificationTarget.FromMetadata(head.MetadataJson) is { } target)
            {
                var state = _deploymentTarget?.Invoke(target) ?? new("unknown", "deployment_target_unknown");
                if (state.State != "succeeded")
                {
                    await _store.BlockPendingAsync(head, state.ErrorCode ?? "deployment_target_pending", state.State == "pending", ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
            }
            SessionRecoveryAuthority? authority = null;
            if (restart || await _store.RequiresRecoveryAsync(head.Id, ct))
            {
                authority = callerMatches ? new(caller!.Token, null, false) : await ReadRecoveryAuthorityAsync(info, head, ct);
                if (authority.AccessToken is null)
                {
                    await _store.BlockPendingAsync(head, authority.ErrorCode ?? "recovery_authority_blocked", authority.Retryable, ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
            }
            if (_deliveryPaused() || HasStopIntent(sessionId)) return;
            // Stop/resume endpoints cross this same gate. Re-read provider state after authorization.
            var (_, current) = FindSession(sessionId);
            if (restart)
            {
                if (current?.Status != SessionStatus.Stopped
                    || current.StopReason is not ("maintenance_restart" or "orphaned_on_restart")
                    || !provider.Capabilities.HasFlag(SessionCapabilities.Resume)) return;
                try
                {
                    using (SessionScratch.PushExecutionToken(authority!.AccessToken!))
                        info = await provider.ResumeSessionAsync(sessionId, InvocationProvenance(head));
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    await _store.BlockPendingAsync(head, "recovery_resume_failed", true, ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
                if (info is null)
                {
                    await _store.BlockPendingAsync(head, "recovery_resume_failed", true, ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
                if (callerMatches && info.Status is SessionStatus.Idle or SessionStatus.Active or SessionStatus.Starting)
                    await _store.AuthorizePendingScopeAsync(head, ct);
                if (_deliveryPaused() || HasStopIntent(sessionId) || info.Status != SessionStatus.Idle) return;
                // Current canonical authority must also hold after asynchronous provider startup.
                authority = callerMatches ? new(caller!.Token, null, false) : await ReadRecoveryAuthorityAsync(info, head, ct);
                if (authority.AccessToken is null)
                {
                    await _store.BlockPendingAsync(head, authority.ErrorCode ?? "recovery_authority_blocked", authority.Retryable, ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
            }
            else if (current?.Status != SessionStatus.Idle) return;
            else if (callerMatches) await _store.AuthorizePendingScopeAsync(head, ct);
            if (_deliveryPaused() || HasStopIntent(sessionId)) return;
            if ((await _store.GetHeadAsync(sessionId, ct))?.Id != head.Id) return;
            if (DeploymentVerificationTarget.FromMetadata(head.MetadataJson) is { } exactTarget)
            {
                var state = _deploymentTarget?.Invoke(exactTarget) ?? new("unknown", "deployment_target_unknown");
                if (state.State != "succeeded")
                {
                    await _store.BlockPendingAsync(head, state.ErrorCode ?? "deployment_target_pending", state.State == "pending", ct);
                    await PublishAsync(sessionId, [head.Id], "blocked", ct);
                    return;
                }
            }

            var batch = await _store.ClaimBatchAsync(sessionId, _leaseOwner, DeliveryLease, ct);
            if (batch.Count == 0) return;
            if (authority is not null && !callerMatches)
                foreach (var item in batch) await _store.RequireRecoveryAsync(item, ct);
            await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(), "delivering", ct);

            var input = new List<SessionInputPart>();
            var publicAttachments = new List<object>();
            try
            {
                foreach (var item in batch)
                {
                    foreach (var part in item.Input)
                    {
                        if (part.Type == "text")
                        {
                            if (!string.IsNullOrWhiteSpace(part.Value)) input.Add(SessionInputPart.TextPart(part.Value));
                            continue;
                        }
                        if (part.Type != "attachment")
                            throw new SessionInputQueueStoreException("unsupported_input_part", $"Unsupported queued input part '{part.Type}'");
                        var attachment = await _attachments.GetAuthorizedAsync(part.Value, item.OwnerUserId, ct)
                            ?? throw new SessionInputQueueStoreException("attachment_not_found", $"Attachment '{part.Value}' was not found");
                        input.Add(SessionInputPart.AttachmentPart(attachment.ToProviderAttachment()));
                        publicAttachments.Add(new
                        {
                            id = attachment.Id,
                            kind = attachment.Kind,
                            name = attachment.Name,
                            mediaType = attachment.MediaType,
                            size = attachment.Size,
                            sha256 = attachment.Sha256,
                            downloadUrl = $"/ai-session/input-attachments/{Uri.EscapeDataString(attachment.Id)}",
                        });
                    }
                }

                var metadata = batch.Select(item => item.MetadataJson)
                    .Where(json => !string.IsNullOrWhiteSpace(json))
                    .Select(json => JsonSerializer.Deserialize<JsonElement>(json!))
                    .ToArray();
                string? attachmentsJson = null;
                if (publicAttachments.Count > 0 || metadata.Length > 0 || batch.Count > 1)
                {
                    attachmentsJson = JsonSerializer.Serialize(new
                    {
                        // The user record can be mirrored before provider acknowledgement.
                        // Persist the represented inputs with that same record, not a later event.
                        inputMessageUids = batch.Select(item => item.MessageUid).ToArray(),
                        attachments = publicAttachments,
                        metadata = metadata.Length switch
                        {
                            0 => (object?)null,
                            1 => metadata[0],
                            _ => metadata,
                        },
                    }, JsonOptions);
                }

                if (info.JobId is { } jobId && _jobTracker.GetJob(jobId) is not null)
                {
                    // Provider resume already starts this same accepted invocation. Do not append
                    // a second start for immediate delivery, later Idle, or a busy retry.
                    var lastInvocation = _jobTracker.GetJobEvents(jobId).LastOrDefault(e =>
                        e.Kind is JobEventKind.Started or JobEventKind.Resumed or JobEventKind.Retried or JobEventKind.Rerun);
                    if (lastInvocation?.Provenance?.Context.Any(c => c.Kind == "accepted-input" && c.Id == batch[0].Id) != true)
                        _jobTracker.StartInvocation(jobId, InvocationProvenance(batch[0]), JobEventKind.Resumed);
                }

                var deliveredMessageUid = batch[0].MessageUid;
                using var recoveryScope = authority?.AccessToken is { } token ? SessionScratch.PushExecutionToken(token) : null;
                var result = await provider.TrySendInputAsync(sessionId, input, attachmentsJson, deliveredMessageUid);
                switch (result.Status)
                {
                    case SessionInputDeliveryStatus.Accepted:
                        await _store.MarkDeliveredAsync(batch, deliveredMessageUid, CancellationToken.None);
                        await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(), "delivered",
                            CancellationToken.None, deliveredMessageUid: deliveredMessageUid);
                        break;
                    case SessionInputDeliveryStatus.Busy:
                        await _store.RequeueBusyAsync(batch, CancellationToken.None);
                        await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(), "waiting_for_session", CancellationToken.None);
                        break;
                    default:
                        await _store.FailAsync(batch, result.ErrorCode ?? "delivery_failed",
                            result.ErrorMessage ?? "Provider delivery failed", result.Retryable, CancellationToken.None);
                        await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(),
                            result.Retryable ? "retry_scheduled" : "failed", CancellationToken.None,
                            errorCode: result.ErrorCode ?? "delivery_failed");
                        break;
                }
            }
            catch (Exception ex)
            {
                var retryable = ex is not SessionInputQueueStoreException and not AttachmentStoreException;
                var code = ex switch
                {
                    SessionInputQueueStoreException queue => queue.Code,
                    AttachmentStoreException attachment => attachment.Code,
                    _ => "delivery_failed",
                };
                await _store.FailAsync(batch, code, ex.Message, retryable, CancellationToken.None);
                await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(),
                    retryable ? "retry_scheduled" : "failed", CancellationToken.None, errorCode: code);
                _log($"[InputQueue] Delivery failed for session {sessionId}: {ex.Message}", info.JobId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PublishAsync(string sessionId, IReadOnlyList<string> itemIds, string transition,
        CancellationToken ct, string? errorCode = null, string? deliveredMessageUid = null)
    {
        var first = await FindAnyOwnerAsync(sessionId, ct);
        var summary = first is null
            ? new SessionInputQueueSummary(0, "empty", null, null, null)
            : await GetSummaryAsync(sessionId, first, ct);
        if (_isConfidential(sessionId))
            _broadcaster.Broadcast("ai-session.changed", new
            {
                sessionId,
                confidential = true,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
        else
            _broadcaster.Broadcast("session.input-queue.updated", new SessionInputQueueChanged(
                sessionId, itemIds, transition, summary.Depth, summary.State,
                summary.BlockedReason, errorCode ?? summary.ErrorCode, deliveredMessageUid));
    }

    private async Task<SessionRecoveryAuthority> ReadRecoveryAuthorityAsync(UnifiedSessionInfo session,
        SessionInputQueueItem item, CancellationToken ct)
    {
        try { return _recoverAuthority is null ? new(null, "recovery_authority_unavailable", true)
            : await _recoverAuthority(session, item, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(null, "recovery_host_unavailable", true); }
    }

    private static JobProvenance InvocationProvenance(SessionInputQueueItem item)
        => item.Provenance with { Context = [.. item.Provenance.Context,
            new JobContextReference("accepted-input", item.Id)] };

    private async Task<string?> FindAnyOwnerAsync(string sessionId, CancellationToken ct)
    {
        var (provider, info) = FindSession(sessionId);
        if (!string.IsNullOrWhiteSpace(info?.UserId)) return info.UserId;
        var local = await _store.ListAsync(sessionId, "local-user", includeTerminal: false, ct);
        return local.Count > 0 ? "local-user" : provider is null ? null : "local-user";
    }

    private (ISessionProvider? Provider, UnifiedSessionInfo? Info) FindSession(string sessionId)
    {
        foreach (var provider in _registry.FindProviders<ISessionProvider>())
        {
            var (info, _) = provider.GetSession(sessionId);
            if (info is not null) return (provider, info);
        }
        return (null, null);
    }

    private void OnProviderEvent(string type, object data)
    {
        if (type == "session.updated" && data is UnifiedSessionInfo { Status: SessionStatus.Idle } session)
            Signal(session.Id);
    }
}
