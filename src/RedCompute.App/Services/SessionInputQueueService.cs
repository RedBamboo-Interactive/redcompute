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
    SessionInputQueueSummary Queue,
    // interrupt-current only: what asking the running turn to stop returned (SessionInterruptResults).
    // A replay repeats the original admission's result; it never interrupts again.
    string? InterruptResult = null);

/// <summary>send-now: whether anything was queued to send, and what interrupting the turn returned.</summary>
public sealed record SessionSendNowResult(bool Requested, string? InterruptResult);

/// <summary>What asking one queued input to steer the running turn came to.</summary>
public static class SessionSteerOutcomes
{
    /// <summary>It is waiting to join the running turn.</summary>
    public const string Steering = "steering";
    /// <summary>It is being handed to the provider.</summary>
    public const string Delivering = "delivering";
    /// <summary>The provider has it. The item's deliveryReason says if it ran as the next turn.</summary>
    public const string Delivered = "delivered";
    /// <summary>Steering could not happen: the item stays queued, and the reason says why.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>Why an input could not be asked to steer, beyond the item's own error.</summary>
public static class SessionSteerRefusals
{
    public const string Unsupported = "steering_unsupported";
    public const string SessionNotRunning = "session_not_running";
    public const string UserStopped = "user_stopped";
    public const string Maintenance = "maintenance_drain";
    public const string FailedHead = "failed_head";
    public const string RecoveryRequired = "recovery_required";
}

public sealed record SessionSteerPromotion(SessionInputQueueItem Item, string Outcome, string? Reason, SessionInputQueueSummary Queue);

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
    private static readonly TimeSpan SteeringLease = TimeSpan.FromMinutes(30);
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
    internal bool IsDeliveryPaused => _deliveryPaused();
    /// <summary>True while this session must not receive input (an unfinished rollback).</summary>
    internal Func<string, CancellationToken, Task<bool>>? SettleRollback { get; set; }
    /// <summary>Runs before a recovery resume launches a provider: resolves a legacy session's access level, demotes a revoked Full.</summary>
    internal Func<ISessionProvider, string, CancellationToken, Task>? BeforeResume { get; set; }
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
        await RecoverSteeringDeliveriesAsync(ct);
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

        if (_deliveryPaused() && admitted.Item.DeliveryPolicy == SessionInputDeliveryPolicy.SteerCurrent)
        {
            var demoted = await _store.DemotePendingSteerAsync(submission.SessionId, ct);
            if (demoted.Count > 0)
                await PublishAsync(submission.SessionId, demoted.Select(item => item.Id).ToArray(),
                    "steer_unavailable", ct, errorCode: "steer_unavailable");
        }

        // interrupt-current interrupts the running turn on a fresh admission only; the input itself
        // then overtakes older queued input (SelectDeliveryHead). The result is kept with the input
        // so a replay reports what the original admission did.
        var interruptResult = admitted.Existing ? admitted.Item.InterruptResult : null;
        if (!admitted.Existing && submission.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent)
        {
            // A maintenance drain lets running turns finish rather than cutting them short.
            interruptResult = _deliveryPaused() ? SessionInterruptResults.Maintenance
                : InterruptRunningTurn(submission.SessionId, SessionInterruptSources.InterruptCurrent,
                    admitted.Item.MessageUid, requireActive: true).Result;
            await _store.SetInterruptResultAsync(admitted.Item.Id, interruptResult, ct);
        }

        await ProcessSessionAsync(submission.SessionId, ct, authenticatedCallerToken is null ? null
            : new CallerRecovery(authenticatedCallerToken, admitted.Item.OwnerUserId, admitted.Item.ProvenanceScope));
        var current = await _store.GetAsync(submission.SessionId, admitted.Item.Id, submission.OwnerUserId, ct)
            ?? admitted.Item;
        var summary = await GetSummaryAsync(submission.SessionId, submission.OwnerUserId, ct);
        return new SessionInputAdmissionResult(current, admitted.Existing,
            current.State == SessionInputQueueState.Delivered ? "delivered" : "queued", summary, interruptResult);
    }

    /// <summary>
    /// Asks the session's running turn to stop on behalf of <paramref name="source"/> and the input
    /// (if any) that wants to run instead. The request is recorded on the turn before the provider is
    /// asked, so a turn that ends at once is still attributed, and withdrawn if the provider reports
    /// it interrupted nothing. Whether the turn really ended by interruption is the turn's own
    /// outcome, reported when it ends: an interrupt can race a turn that completes anyway.
    /// </summary>
    /// <param name="requireActive">Ask only a session that reports a running turn (input delivery);
    /// a plain interrupt request leaves that judgement to the provider, as it always has.</param>
    internal (InterruptResult? Provider, string Result) InterruptRunningTurn(string sessionId, string source, string? messageUid,
        bool requireActive = false)
    {
        var (provider, info) = FindSession(sessionId);
        if (provider is null || info is null) return (null, SessionInterruptResults.NotFound);
        if (!provider.Capabilities.HasFlag(SessionCapabilities.Interrupt)) return (null, SessionInterruptResults.Unsupported);
        if (requireActive && info.Status != SessionStatus.Active) return (InterruptResult.NotActive, SessionInterruptResults.NotActive);
        var recordedOn = info.Status == SessionStatus.Active ? _store.MarkInterruptRequested(sessionId, source, messageUid) : null;
        InterruptResult result;
        try { result = provider.InterruptSession(sessionId); }
        catch (Exception ex)
        {
            _log($"[InputQueue] Interrupt failed for session {sessionId}: {ex.Message}", info.JobId);
            result = InterruptResult.Error;
        }
        if (result != InterruptResult.Interrupted && recordedOn is not null)
            _store.ClearInterruptRequest(sessionId, recordedOn);
        return (result, result switch
        {
            InterruptResult.Interrupted => SessionInterruptResults.Interrupted,
            InterruptResult.NotActive => SessionInterruptResults.NotActive,
            InterruptResult.NotFound => SessionInterruptResults.NotFound,
            _ => SessionInterruptResults.Error,
        });
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

    /// <summary>
    /// Interrupts the running turn and lets the queue deliver in its own order as soon as the session
    /// is idle: interrupt-current inputs first, then the rest FIFO. It reorders nothing; what it adds
    /// is the interrupt, attributed to the input that will run next.
    /// </summary>
    public async Task<SessionSendNowResult> SendNowAsync(string sessionId, string ownerUserId, CancellationToken ct = default)
    {
        var items = await _store.ListAsync(sessionId, ownerUserId, includeTerminal: false, ct);
        if (items.Count == 0) return new(false, null);
        var (provider, info) = FindSession(sessionId);
        if (provider is null || info is null) return new(false, null);
        var next = await _store.GetHeadAsync(sessionId, ct);
        var interrupt = InterruptRunningTurn(sessionId, SessionInterruptSources.SendNow, next?.MessageUid, requireActive: true).Result;
        Signal(sessionId);
        await PublishAsync(sessionId, items.Select(item => item.Id).ToArray(), "send_now_requested", ct);
        return new(true, interrupt);
    }

    /// <summary>
    /// Asks one pending input to join the running turn. It never interrupts: the existing steer path
    /// delivers it, so a parked question, a maintenance pause or a turn that ended meanwhile demotes
    /// it or runs it as the next turn exactly as for an input admitted as a steer. Asking again is
    /// harmless. When steering cannot happen at all the row is left as it was and the answer says so.
    /// </summary>
    /// <returns>Null when the session has no such item for this owner.</returns>
    public async Task<SessionSteerPromotion?> PromoteToSteerAsync(
        string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        var item = await _store.GetAsync(sessionId, itemId, ownerUserId, ct);
        if (item is null) return null;
        if (item.State is SessionInputQueueState.Failed or SessionInputQueueState.Cancelled
            || (item.State == SessionInputQueueState.Pending && item.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent))
            throw new SessionInputQueueStoreException("not_promotable", "Only a pending queued input can be asked to steer the running turn");

        if (item.State == SessionInputQueueState.Pending && item.DeliveryPolicy == SessionInputDeliveryPolicy.AfterCurrent)
        {
            if (await SteerRefusalAsync(sessionId, item, ownerUserId, ct) is { } refusal)
                return new(item, SessionSteerOutcomes.Unavailable, refusal, await GetSummaryAsync(sessionId, ownerUserId, ct));
            // Null means a claim or a cancel won the race; the re-read below reports what happened.
            if (await _store.PromoteToSteerAsync(sessionId, itemId, ownerUserId, ct) is not null)
                await PublishAsync(sessionId, [itemId], "steer_requested", ct);
        }
        if (item.State == SessionInputQueueState.Pending)
        {
            await ProcessSessionAsync(sessionId, ct);
            // The session gate may be held by a pass that already read the queue.
            Signal(sessionId);
        }

        var current = await _store.GetAsync(sessionId, itemId, ownerUserId, ct) ?? item;
        var summary = await GetSummaryAsync(sessionId, ownerUserId, ct);
        return current.State switch
        {
            SessionInputQueueState.Delivered => new(current, SessionSteerOutcomes.Delivered, current.DeliveryReason, summary),
            SessionInputQueueState.Delivering => new(current, SessionSteerOutcomes.Delivering, current.DeliveryReason, summary),
            SessionInputQueueState.Pending when current.DeliveryPolicy == SessionInputDeliveryPolicy.SteerCurrent =>
                new(current, SessionSteerOutcomes.Steering, null, summary),
            _ => new(current, SessionSteerOutcomes.Unavailable, current.ErrorCode ?? current.DeliveryReason, summary),
        };
    }

    private async Task<string?> SteerRefusalAsync(string sessionId, SessionInputQueueItem item, string ownerUserId, CancellationToken ct)
    {
        if (HasStopIntent(sessionId)) return SessionSteerRefusals.UserStopped;
        if (_deliveryPaused()) return SessionSteerRefusals.Maintenance;
        var (provider, info) = FindSession(sessionId);
        if (provider is null || info is null || info.Status is not (SessionStatus.Active or SessionStatus.Idle))
            return SessionSteerRefusals.SessionNotRunning;
        if (!provider.Capabilities.HasFlag(SessionCapabilities.SupportsSteering)) return SessionSteerRefusals.Unsupported;
        // The steer path checks neither: a failed head would block it silently, and an input waiting
        // for its authority to be revalidated after a restart must not get past that by steering.
        var items = await _store.ListAsync(sessionId, ownerUserId, includeTerminal: false, ct);
        if (items.Any(active => active.State == SessionInputQueueState.Failed)) return SessionSteerRefusals.FailedHead;
        if (await _store.RequiresRecoveryAsync(item.Id, ct)) return SessionSteerRefusals.RecoveryRequired;
        return null;
    }

    public async Task<SessionInputQueueSummary> GetSummaryAsync(
        string sessionId, string ownerUserId, CancellationToken ct = default)
    {
        var items = await _store.ListAsync(sessionId, ownerUserId, includeTerminal: false, ct);
        var head = SessionInputQueueStore.SelectDeliveryHead(items);
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
            if (SettleRollback is not null) await SettleRollback(sessionId, ct);
            var (_, info) = FindSession(sessionId);
            if (info?.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(info.StopReason))
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

    internal async Task<bool> HasQueuedInputAsync(string sessionId, CancellationToken ct = default)
        => await _store.GetHeadAsync(sessionId, ct) is not null;

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

    private static bool TranscriptHoldsInput(ISessionProvider provider, string sessionId, string messageUid)
    {
        var (_, history) = provider.GetSession(sessionId);
        return history.Any(message => message.Role == "user" && message.MessageUid == messageUid);
    }

    /// <summary>
    /// A blocked session comes back on every sweep. A failed input is only recorded before its
    /// failure, so one look per process is enough and a long transcript isn't reread every few seconds.
    /// </summary>
    private bool FailedInputIsInTranscript(ISessionProvider provider, SessionInputQueueItem head)
    {
        if (_failuresOutsideTranscript.ContainsKey(head.Id)) return false;
        if (TranscriptHoldsInput(provider, head.SessionId, head.MessageUid)) return true;
        _failuresOutsideTranscript.TryAdd(head.Id, 0);
        return false;
    }

    internal async Task RecoverSteeringDeliveriesAsync(CancellationToken ct)
    {
        var delivering = await _store.GetDeliveringSteersAsync(ct);
        foreach (var sessionItems in delivering.GroupBy(item => item.SessionId))
        {
            var provider = FindSession(sessionItems.Key).Provider;
            var messages = provider?.GetSession(sessionItems.Key).History ?? [];
            foreach (var item in sessionItems)
            {
                // Coalesced queue items share the provider message UID recorded at claim time.
                // It is durable before stdin delivery, so restart recovery can reconcile without
                // ever blindly calling the provider a second time.
                var messageUid = item.DeliveredMessageUid ?? item.MessageUid;
                var persisted = messages.Any(message =>
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(message.MessageUid, messageUid, StringComparison.Ordinal));
                if (persisted)
                {
                    await _store.MarkDeliveredAsync([item], messageUid, ct);
                    await PublishAsync(item.SessionId, [item.Id], "delivered", ct,
                        deliveredMessageUid: messageUid);
                }
                else
                {
                    await _store.DemoteSteerAsync([item], ct);
                    await PublishAsync(item.SessionId, [item.Id], "steer_unavailable", ct,
                        errorCode: "steer_unavailable");
                }
            }
        }
    }

    private readonly ConcurrentDictionary<string, byte> _failuresOutsideTranscript = new(StringComparer.Ordinal);

    private async Task ProcessSessionAsync(string sessionId, CancellationToken ct, CallerRecovery? caller)
    {
        if (HasStopIntent(sessionId)) return;
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            if (HasStopIntent(sessionId)) return;
            // An unfinished rollback goes first. If it still can't finish, input stays queued for the next sweep.
            if (SettleRollback is not null && await SettleRollback(sessionId, ct)) return;
            if (_deliveryPaused())
            {
                var demoted = await _store.DemotePendingSteerAsync(sessionId, ct);
                if (demoted.Count > 0)
                    await PublishAsync(sessionId, demoted.Select(item => item.Id).ToArray(),
                        "steer_unavailable", ct, errorCode: "steer_unavailable");
                return;
            }
            var (provider, info) = FindSession(sessionId);
            if (provider is null || info is null) return;

            if (info.Status is SessionStatus.Active or SessionStatus.Starting)
            {
                var steerHead = await _store.GetSteerHeadAsync(sessionId, ct);
                if (steerHead is null) return;
                if (info.Status != SessionStatus.Active
                    || !provider.Capabilities.HasFlag(SessionCapabilities.SupportsSteering))
                {
                    var demoted = await _store.DemotePendingSteerAsync(sessionId, ct);
                    if (demoted.Count > 0)
                        await PublishAsync(sessionId, demoted.Select(item => item.Id).ToArray(),
                            "steer_unavailable", ct, errorCode: "steer_unavailable");
                    return;
                }
                if (DeploymentVerificationTarget.FromMetadata(steerHead.MetadataJson) is { } steerTarget)
                {
                    var targetState = _deploymentTarget?.Invoke(steerTarget)
                        ?? new("unknown", "deployment_target_unknown");
                    if (targetState.State != "succeeded")
                    {
                        await _store.BlockPendingAsync(steerHead,
                            targetState.ErrorCode ?? "deployment_target_pending",
                            targetState.State == "pending", ct);
                        await PublishAsync(sessionId, [steerHead.Id], "blocked", ct);
                        return;
                    }
                }

                var steerBatch = await _store.ClaimSteerBatchAsync(
                    sessionId, _leaseOwner, SteeringLease, ct);
                if (steerBatch.Count == 0) return;
                await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(), "delivering", ct);
                try
                {
                    var payload = await BuildDeliveryPayloadAsync(steerBatch, ct);
                    var deliveredMessageUid = steerBatch[0].MessageUid;
                    var result = await provider.TrySteerInputAsync(sessionId, payload.Input,
                        payload.AttachmentsJson, deliveredMessageUid, payload.DisplayContent);
                    switch (result.Status)
                    {
                        case SessionInputDeliveryStatus.Accepted:
                            await _store.MarkDeliveredAsync(steerBatch, deliveredMessageUid, CancellationToken.None);
                            await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(),
                                "delivered", CancellationToken.None, deliveredMessageUid: deliveredMessageUid);
                            break;
                        case SessionInputDeliveryStatus.NotSteerable:
                        case SessionInputDeliveryStatus.Unsupported:
                            await _store.DemoteSteerAsync(steerBatch, CancellationToken.None);
                            await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(),
                                "steer_unavailable", CancellationToken.None, errorCode: "steer_unavailable");
                            break;
                        case SessionInputDeliveryStatus.Busy:
                            await _store.RequeueBusyAsync(steerBatch, CancellationToken.None);
                            await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(),
                                "waiting_for_session", CancellationToken.None);
                            break;
                        default:
                            await _store.FailAsync(steerBatch, result.ErrorCode ?? "delivery_failed",
                                result.ErrorMessage ?? "Provider steering failed", result.Retryable,
                                CancellationToken.None);
                            await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(),
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
                    await _store.FailAsync(steerBatch, code, ex.Message, retryable, CancellationToken.None);
                    await PublishAsync(sessionId, steerBatch.Select(item => item.Id).ToArray(),
                        retryable ? "retry_scheduled" : "failed", CancellationToken.None, errorCode: code);
                    _log($"[InputQueue] Steering failed for session {sessionId}: {ex.Message}", info.JobId);
                }
                return;
            }

            var restart = info.Status == SessionStatus.Stopped
                && SessionStopReasons.IsAutoResumable(info.StopReason);
            if (info.Status != SessionStatus.Idle && !restart) return;
            // Every pre-restart accepted authority must be revalidated, including later FIFO scopes.
            if (restart) await _store.RequireSessionRecoveryAsync(sessionId, ct);
            var head = await _store.GetHeadAsync(sessionId, ct);
            if (head?.State == SessionInputQueueState.Failed && FailedInputIsInTranscript(provider, head))
            {
                // Only one batch can be failed at a time, because nothing is claimed behind a failed
                // head. Every failed row therefore belongs to the batch the transcript already holds.
                var failed = (await _store.ListAsync(sessionId, head.OwnerUserId, includeTerminal: false, ct))
                    .Where(item => item.State == SessionInputQueueState.Failed).ToArray();
                await _store.SettleFailedAsDeliveredAsync(failed, head.MessageUid, ct);
                await PublishAsync(sessionId, failed.Select(item => item.Id).ToArray(), "delivered", ct,
                    deliveredMessageUid: head.MessageUid);
                _log($"[InputQueue] Settled failed input {head.Id} for session {sessionId}: the transcript already holds it", info.JobId);
                head = await _store.GetHeadAsync(sessionId, ct);
            }
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
                    || !SessionStopReasons.IsAutoResumable(current.StopReason)
                    || !provider.Capabilities.HasFlag(SessionCapabilities.Resume)) return;
                try
                {
                    if (BeforeResume is not null) await BeforeResume(provider, sessionId, ct);
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
                    // A removed working folder (e.g. a deleted worktree) cannot heal by retrying.
                    var missing = provider.LastStartErrorCode == SessionStopReasons.ProjectPathMissing;
                    await _store.BlockPendingAsync(head,
                        missing ? SessionStopReasons.ProjectPathMissing : "recovery_resume_failed", !missing, ct);
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

            // The session is idle: whatever turn ran last has ended. Close it from this state before
            // the batch opens the next one, whatever order the provider's events arrive in.
            if (current is not null) _store.CloseTurn(sessionId, SessionTurnEnd.From(current));
            else if (info is not null) _store.CloseTurn(sessionId, SessionTurnEnd.From(info));
            var batch = await _store.ClaimBatchAsync(sessionId, _leaseOwner, DeliveryLease, ct);
            if (batch.Count == 0) return;
            if (authority is not null && !callerMatches)
                foreach (var item in batch) await _store.RequireRecoveryAsync(item, ct);
            await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(), "delivering", ct);

            try
            {
                var payload = await BuildDeliveryPayloadAsync(batch, ct);

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
                var result = await provider.TrySendInputAsync(
                    sessionId, payload.Input, payload.AttachmentsJson, deliveredMessageUid,
                    payload.DisplayContent);
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
                    // A provider can record the input and then report failure (Codex when turn/start
                    // fails). The input is in the conversation with any error beside it, and failing
                    // it would leave a blocker behind a message that was already sent.
                    case var _ when !result.Retryable && TranscriptHoldsInput(provider, sessionId, deliveredMessageUid):
                        await _store.MarkDeliveredAsync(batch, deliveredMessageUid, CancellationToken.None);
                        await PublishAsync(sessionId, batch.Select(item => item.Id).ToArray(), "delivered",
                            CancellationToken.None, deliveredMessageUid: deliveredMessageUid);
                        _log($"[InputQueue] Provider refused input it had already recorded for session {sessionId}: {result.ErrorCode}", info.JobId);
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

    private sealed record DeliveryPayload(
        IReadOnlyList<SessionInputPart> Input,
        string? AttachmentsJson,
        string DisplayContent);

    private async Task<DeliveryPayload> BuildDeliveryPayloadAsync(
        IReadOnlyList<SessionInputQueueItem> batch, CancellationToken ct)
    {
        var input = new List<SessionInputPart>();
        var publicAttachments = new List<object>();
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
                    throw new SessionInputQueueStoreException(
                        "unsupported_input_part", $"Unsupported queued input part '{part.Type}'");
                var attachment = await _attachments.GetAuthorizedAsync(part.Value, item.OwnerUserId, ct)
                    ?? throw new SessionInputQueueStoreException(
                        "attachment_not_found", $"Attachment '{part.Value}' was not found");
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

        var displayContent = batch.Count == 1
            ? batch[0].DisplayContent
            : string.Join("\n", batch.Select(item => item.DisplayContent)
                .Where(content => !string.IsNullOrWhiteSpace(content)));
        return new DeliveryPayload(input, attachmentsJson, displayContent);
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
            // Metadata only: this runs on every queue pass and every summary read, and a
            // transcript can hold tens of thousands of messages.
            var info = provider.GetSessionInfo(sessionId);
            if (info is not null) return (provider, info);
        }
        return (null, null);
    }

    /// <summary>
    /// Follows the provider's turns for the turn ledger, synchronously and in the order the provider
    /// raises them, then wakes delivery on Idle.
    /// </summary>
    internal void OnProviderEvent(string type, object data)
    {
        try
        {
            if (type == "session.updated" && data is UnifiedSessionInfo updated)
            {
                if (updated.Status == SessionStatus.Active) _store.MarkTurnActive(updated.Id);
                else if (updated.Status is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)
                    _store.CloseTurn(updated.Id, SessionTurnEnd.From(updated));
            }
            else if (type == "session.ended" && JsonSerializer.SerializeToElement(data) is { ValueKind: JsonValueKind.Object } ended
                && ended.TryGetProperty("id", out var endedId) && endedId.ValueKind == JsonValueKind.String)
            {
                var stopReason = ended.TryGetProperty("stopReason", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString() : null;
                _store.CloseTurn(endedId.GetString()!, new SessionTurnEnd("Ended", stopReason, null, null, 0));
            }
        }
        catch (Exception ex)
        {
            _log($"[InputQueue] Turn ledger update failed for {type}: {ex.Message}", null);
        }
        if (type == "session.updated" && data is UnifiedSessionInfo { Status: SessionStatus.Idle } session)
            Signal(session.Id);
    }

    /// <summary>The session's most recently ended turn, for the session read and event overlay.</summary>
    internal SessionTurnSummary? LastTurn(string sessionId)
    {
        try { return _store.LastEndedTurn(sessionId)?.Summary; }
        catch (Exception ex)
        {
            _log($"[InputQueue] Last turn unavailable for {sessionId}: {ex.Message}", null);
            return null;
        }
    }
}
