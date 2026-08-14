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
    private readonly string _leaseOwner = $"redcompute:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
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
        Action<string, Guid?> log)
    {
        _store = store;
        _attachments = attachments;
        _registry = registry;
        _jobTracker = jobTracker;
        _broadcaster = broadcaster;
        _log = log;

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

    public async Task<SessionInputAdmissionResult> AdmitAsync(SessionInputQueueSubmission submission, CancellationToken ct = default)
    {
        var admitted = await _store.EnqueueAsync(submission, ct);
        await PublishAsync(submission.SessionId, [admitted.Item.Id], admitted.Existing ? "idempotent_replay" : "queued", ct);

        if (!admitted.Existing && submission.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent)
        {
            var (provider, info) = FindSession(submission.SessionId);
            if (provider is not null && info?.Status == SessionStatus.Active)
                provider.InterruptSession(submission.SessionId);
        }

        await ProcessSessionAsync(submission.SessionId, ct);
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
        var item = await _store.CancelAsync(sessionId, itemId, ownerUserId, ct);
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

    public void Signal(string sessionId) => _signals.Writer.TryWrite(sessionId);

    private async Task ProcessSessionAsync(string sessionId, CancellationToken ct)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            var (provider, info) = FindSession(sessionId);
            if (provider is null || info is null || info.Status != SessionStatus.Idle) return;

            var batch = await _store.ClaimBatchAsync(sessionId, _leaseOwner, DeliveryLease, ct);
            if (batch.Count == 0) return;
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
                if (publicAttachments.Count > 0 || metadata.Length > 0)
                {
                    attachmentsJson = JsonSerializer.Serialize(new
                    {
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
                    _jobTracker.StartInvocation(jobId, batch[0].Provenance, JobEventKind.Resumed);

                var deliveredMessageUid = batch[0].MessageUid;
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
        _broadcaster.Broadcast("session.input-queue.updated", new SessionInputQueueChanged(
            sessionId, itemIds, transition, summary.Depth, summary.State,
            summary.BlockedReason, errorCode ?? summary.ErrorCode, deliveredMessageUid));
    }

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
