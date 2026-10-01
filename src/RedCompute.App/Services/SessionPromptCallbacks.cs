using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

/// <summary>Opt-in exact-prompt subscriptions and bounded durable outbox; the queue sweep never awaits HTTP.</summary>
internal sealed class SessionPromptCallbacks
{
    private sealed record Entry(string SessionId, string CallbackId, string PromptMessageUid, string Url, string? UserId,
        bool ActiveSeen = false, string? PauseReason = null, string? Payload = null,
        int Attempts = 0, DateTimeOffset? NextAttemptAt = null);
    private readonly SessionCallbackStore _store;
    private readonly SessionInputQueueStore _queue;
    private readonly Func<string, UnifiedSessionInfo?> _current;
    private readonly HttpClient _http;
    private readonly Action<string, Guid?> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<(string, string), Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliverySlots = new(4, 4);
    private readonly ConcurrentDictionary<(string, string), TaskCompletionSource> _deliveries = new();

    internal SessionPromptCallbacks(string path, SessionInputQueueStore queue,
        Func<string, UnifiedSessionInfo?> current, HttpClient http,
        Action<string, Guid?>? log = null, Func<DateTimeOffset>? now = null)
    {
        _store = new(path); _queue = queue; _current = current; _http = http;
        _log = log ?? ((_, _) => { }); _now = now ?? (() => DateTimeOffset.UtcNow);
        foreach (var (_, json) in _store.Load())
        {
            var entry = JsonSerializer.Deserialize<Entry>(json)!;
            _entries[(entry.SessionId, entry.CallbackId)] = entry;
        }
    }
    internal async Task RegisterAsync(string sessionId, string callbackId, string url, string? userId, string promptMessageUid)
    {
        await _gate.WaitAsync();
        try
        {
            if (_entries.TryGetValue((sessionId, callbackId), out var existing))
            {
                if (existing.PromptMessageUid != promptMessageUid || existing.UserId != userId || existing.Url != url)
                    throw new SessionInputQueueStoreException("callback_identity_conflict", "Callback identity already refers to a different prompt or owner");
            }
            else Save(new(sessionId, callbackId, promptMessageUid, url, userId));
        }
        finally { _gate.Release(); }
        await ObserveAsync(sessionId);
    }
    // Exact owner-authorized cleanup after definitive admission rejection. Accepted/unknown work is never removed.
    internal async Task<bool> RemoveUnacceptedAsync(string sessionId, string callbackId, string promptMessageUid, string owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var key = (sessionId, callbackId);
            if (!_entries.TryGetValue(key, out var entry) || entry.UserId != owner || entry.PromptMessageUid != promptMessageUid)
                return false;
            if (await _queue.FindPromptAsync(sessionId, promptMessageUid, ct) is not null) return false;
            _entries.Remove(key); _store.Remove(sessionId, callbackId); return true;
        }
        finally { _gate.Release(); }
    }
    private void Save(Entry entry)
    {
        _entries[(entry.SessionId, entry.CallbackId)] = entry;
        _store.Save(entry.SessionId, entry.CallbackId, JsonSerializer.Serialize(entry));
    }
    internal async Task SweepAsync(CancellationToken ct)
    {
        string[] sessions;
        await _gate.WaitAsync(ct);
        try { sessions = _entries.Values.Select(e => e.SessionId).Distinct().ToArray(); }
        finally { _gate.Release(); }
        foreach (var sessionId in sessions) await ObserveAsync(sessionId, ct);
    }
    internal async Task ObserveAsync(string sessionId, CancellationToken ct = default, bool observedActive = false,
        string? endedReason = null)
    {
        var deliver = new List<Entry>();
        await _gate.WaitAsync(ct);
        try
        {
            var session = _current(sessionId);
            var currentDeliveryUid = await _queue.CurrentDeliveryUidAsync(sessionId, ct);
            foreach (var entry in _entries.Values.Where(e => e.SessionId == sessionId).ToArray())
            {
                var prompt = await _queue.FindPromptAsync(sessionId, entry.PromptMessageUid, ct);
                if (prompt is null || prompt.OwnerUserId != entry.UserId) continue;
                var executionObserved = (observedActive || session?.Status == SessionStatus.Active)
                    && (prompt.State == SessionInputQueueState.Delivering
                        || prompt.State == SessionInputQueueState.Delivered && prompt.DeliveredMessageUid == currentDeliveryUid);
                if (entry.Payload is not null)
                {
                    var latest = executionObserved ? entry with { ActiveSeen = true, PauseReason = null } : entry;
                    if (latest != entry) Save(latest);
                    if (latest.NextAttemptAt is null || latest.NextAttemptAt <= _now()) deliver.Add(latest);
                    continue;
                }
                var cancelled = prompt.State == SessionInputQueueState.Cancelled;
                if (!cancelled && session is null) continue;
                if (!cancelled && executionObserved) { Save(entry with { ActiveSeen = true, PauseReason = null }); continue; }
                if (!cancelled && (observedActive || session!.Status == SessionStatus.Active)) continue;
                var paused = !cancelled && session!.Status == SessionStatus.Stopped
                    && session.StopReason is "maintenance_restart" or "orphaned_on_restart" or "usage_limit";
                if (paused && entry.PauseReason == session!.StopReason) continue;
                if (!cancelled && !paused && session!.Status is not (SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)) continue;
                if (!cancelled && !paused && session!.Status == SessionStatus.Idle
                    && (prompt.State != SessionInputQueueState.Delivered || !entry.ActiveSeen)) continue;
                var payload = JsonSerializer.Serialize(new { sessionId, callbackId = entry.CallbackId, promptMessageUid = entry.PromptMessageUid,
                    status = cancelled ? "Cancelled" : !paused && endedReason is not null && session!.Status != SessionStatus.Error ? "Ended" : session!.Status.ToString(),
                    reason = cancelled ? prompt.ErrorCode == "deployment_target_superseded" ? "superseded" : "cancelled" : endedReason,
                    stopReason = cancelled ? null : session!.StopReason, userId = entry.UserId,
                    deliveredMessageUid = prompt.State == SessionInputQueueState.Delivered ? prompt.DeliveredMessageUid : null,
                    executionObserved = prompt.State == SessionInputQueueState.Delivered && entry.ActiveSeen });
                var pending = entry with { Payload = payload, ActiveSeen = paused ? false : entry.ActiveSeen, Attempts = 0, NextAttemptAt = null };
                Save(pending); deliver.Add(pending);
            }
        }
        finally { _gate.Release(); }
        foreach (var entry in deliver) Schedule(entry, ct);
    }
    private void Schedule(Entry entry, CancellationToken ct)
    {
        if (!_deliverySlots.Wait(0)) return;
        var key = (entry.SessionId, entry.CallbackId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_deliveries.TryAdd(key, completion)) { _deliverySlots.Release(); return; }
        _ = Task.Run(async () => {
            try { await DeliverAsync(entry, ct); }
            catch (Exception) { _log("[Callbacks] Outbox coordination failed; durable payload retained", null); }
            finally { completion.TrySetResult(); _deliveries.TryRemove(key, out _); _deliverySlots.Release(); }
        });
    }
    internal Task DrainAsync() => Task.WhenAll(_deliveries.Values.Select(c => c.Task)); // Receipt only; never input scheduling.
    private async Task DeliverAsync(Entry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_entries.TryGetValue((entry.SessionId, entry.CallbackId), out var current)
                || current.Payload != entry.Payload || current.NextAttemptAt > _now()) return;
        }
        finally { _gate.Release(); }
        var acknowledged = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, entry.Url) {
                Content = new StringContent(entry.Payload!, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, ct);
            acknowledged = response.IsSuccessStatusCode;
            if (!acknowledged) _log($"[Callbacks] Outbox HTTP {(int)response.StatusCode}; retry deferred", null);
        }
        catch (Exception) { _log("[Callbacks] Outbox transport unavailable; retry deferred", null); }
        await _gate.WaitAsync();
        try
        {
            var key = (entry.SessionId, entry.CallbackId);
            if (!_entries.TryGetValue(key, out var latest) || latest.Payload != entry.Payload) return;
            if (!acknowledged)
            {
                var attempts = latest.Attempts + 1;
                Save(latest with { Attempts = attempts, NextAttemptAt = _now().AddSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(attempts - 1, 4)))) });
                return;
            }
            using var doc = JsonDocument.Parse(entry.Payload!);
            var pauseReason = doc.RootElement.GetProperty("stopReason").GetString();
            var paused = doc.RootElement.GetProperty("status").GetString() == "Stopped"
                && pauseReason is "maintenance_restart" or "orphaned_on_restart" or "usage_limit";
            if (paused) Save(latest with { Payload = null, PauseReason = latest.ActiveSeen ? null : pauseReason, Attempts = 0, NextAttemptAt = null });
            else { _entries.Remove(key); _store.Remove(entry.SessionId, entry.CallbackId); }
        }
        finally { _gate.Release(); }
    }
}
