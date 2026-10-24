using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

/// <summary>Opt-in exact-prompt subscriptions and bounded durable outbox; the queue sweep never awaits HTTP.</summary>
/// <remarks>
/// A subscription may also opt in to question notifications (QuestionUrl). Each question the session
/// parks on becomes its own outbox row (QuestionRequestId set), keyed apart from the subscription, so
/// it never consumes or alters the terminal completion. A question row is sent once per request and
/// distinct URL, and dropped unsent if the question resolves first.
///
/// A turn that ends Idle while background tasks still run is reported (with backgroundTasksRunning)
/// but does not consume the subscription: it re-arms for the turn the background work wakes. Each
/// such delivery carries deliverySequence, so retries of one delivery stay identical while the next
/// turn's delivery is distinct under the same callbackId.
/// </remarks>
internal sealed class SessionPromptCallbacks
{
    private sealed record Entry(string SessionId, string CallbackId, string PromptMessageUid, string Url, string? UserId,
        bool ActiveSeen = false, string? PauseReason = null, string? Payload = null,
        int Attempts = 0, DateTimeOffset? NextAttemptAt = null,
        string? QuestionUrl = null, string? QuestionRequestId = null, int BackgroundDeliveries = 0);
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
    // Question rows already enqueued by this process. A row lives only until acknowledged; this keeps
    // a later registration from sending the same request to the same URL again.
    private readonly HashSet<(string, string)> _questionsEnqueued = new();

    /// <summary>Live parked questions by session id. None until the host attaches its providers.</summary>
    internal Func<string, IReadOnlyList<PendingSessionQuestion>> PendingQuestions { get; set; } = _ => [];

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
    internal async Task RegisterAsync(string sessionId, string callbackId, string url, string? userId, string promptMessageUid,
        string? questionUrl = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_entries.TryGetValue((sessionId, callbackId), out var existing))
            {
                if (existing.QuestionRequestId is not null || existing.PromptMessageUid != promptMessageUid
                    || existing.UserId != userId || existing.Url != url)
                    throw new SessionInputQueueStoreException("callback_identity_conflict", "Callback identity already refers to a different prompt or owner");
                // Opting in to questions is additive; it never changes the completion identity.
                if (questionUrl is not null && existing.QuestionUrl != questionUrl)
                    Save(existing with { QuestionUrl = questionUrl });
            }
            else Save(new(sessionId, callbackId, promptMessageUid, url, userId, QuestionUrl: questionUrl));
        }
        finally { _gate.Release(); }
        await ObserveAsync(sessionId);
        // A registrant arriving while the session is already parked still has to hear about it.
        if (questionUrl is not null) await QuestionsChangedAsync(sessionId);
    }

    /// <summary>
    /// Enqueues one notification per parked question and distinct opted-in URL not enqueued yet,
    /// then schedules delivery. Called when a session parks and when a registrant opts in.
    /// </summary>
    internal async Task QuestionsChangedAsync(string sessionId, CancellationToken ct = default)
    {
        var pending = PendingQuestions(sessionId);
        if (pending.Count == 0) return;
        var deliver = new List<Entry>();
        await _gate.WaitAsync(ct);
        try
        {
            var subscriptions = _entries.Values
                .Where(e => e.SessionId == sessionId && e.QuestionRequestId is null && e.QuestionUrl is not null)
                .GroupBy(e => e.QuestionUrl!, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToArray();
            if (subscriptions.Length == 0) return;
            var session = _current(sessionId);
            foreach (var question in pending)
            foreach (var subscription in subscriptions)
            {
                var rowId = QuestionRowId(question.RequestId, subscription.QuestionUrl!);
                if (_entries.ContainsKey((sessionId, rowId)) || !_questionsEnqueued.Add((sessionId, rowId))) continue;
                var row = new Entry(sessionId, rowId, subscription.PromptMessageUid, subscription.QuestionUrl!, subscription.UserId,
                    Payload: QuestionPayload(sessionId, subscription.CallbackId, session, question),
                    QuestionRequestId: question.RequestId);
                Save(row); deliver.Add(row);
            }
        }
        finally { _gate.Release(); }
        foreach (var entry in deliver) Schedule(entry, ct);
    }

    /// <summary>Drops undelivered notifications for a question the session no longer waits on.</summary>
    internal async Task QuestionResolvedAsync(string sessionId, string requestId)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var row in _entries.Values.Where(e => e.SessionId == sessionId && e.QuestionRequestId == requestId).ToArray())
            {
                _entries.Remove((sessionId, row.CallbackId)); _store.Remove(sessionId, row.CallbackId);
            }
            _questionsEnqueued.RemoveWhere(k => k.Item1 == sessionId && k.Item2.StartsWith(QuestionRowPrefix(requestId), StringComparison.Ordinal));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets the in-memory question bookkeeping of a session that ended.</summary>
    internal async Task SessionEndedAsync(string sessionId)
    {
        await _gate.WaitAsync();
        try { _questionsEnqueued.RemoveWhere(k => k.Item1 == sessionId); }
        finally { _gate.Release(); }
    }

    private static string QuestionRowPrefix(string requestId) => $"question:{requestId}:";
    private static string QuestionRowId(string requestId, string url)
        => QuestionRowPrefix(requestId) + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

    internal static string QuestionPayload(string sessionId, string callbackId, UnifiedSessionInfo? session, PendingSessionQuestion question)
        => JsonSerializer.Serialize(new
        {
            @event = "session.question",
            sessionId,
            callbackId,
            title = session?.Title,
            provider = session?.Provider,
            requestId = question.RequestId,
            toolName = question.ToolName,
            toolUseId = question.ToolUseId,
            questions = question.Questions,
            input = question.Input,
            askedAt = question.AskedAt,
            expiresAt = question.ExpiresAt,
            answer = new { method = "POST", path = $"/ai-session/sessions/{sessionId}/question" },
        });
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
    /// <param name="settled">The session has been Idle with nothing running and nothing queued for
    /// the settle window: a subscription re-armed for a woken turn that never came reports now.</param>
    internal async Task ObserveAsync(string sessionId, CancellationToken ct = default, bool observedActive = false,
        string? endedReason = null, bool settled = false)
    {
        var deliver = new List<Entry>();
        await _gate.WaitAsync(ct);
        try
        {
            var session = _current(sessionId);
            var currentDeliveryUid = await _queue.CurrentDeliveryUidAsync(sessionId, ct);
            foreach (var entry in _entries.Values.Where(e => e.SessionId == sessionId).ToArray())
            {
                if (entry.QuestionRequestId is not null)
                {
                    if (entry.NextAttemptAt is null || entry.NextAttemptAt <= _now()) deliver.Add(entry);
                    continue;
                }
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
                var restartPaused = !cancelled && session!.Status == SessionStatus.Stopped
                    && SessionStopReasons.IsAutoResumable(session.StopReason);
                var paused = restartPaused || !cancelled && session!.Status == SessionStatus.Stopped
                    && session.StopReason == "usage_limit";
                // A recoverable stop which durably predates this exact prompt did not interrupt it.
                // The prompt was admitted onto an already-stopped session and is waiting for the
                // queue's automatic resume. Never freeze that stale lifecycle state into the outbox.
                if (restartPaused && prompt.State == SessionInputQueueState.Pending && !entry.ActiveSeen
                    && session!.StoppedAt is { } stoppedAt && stoppedAt < prompt.CreatedAt) continue;
                if (paused && entry.PauseReason == session!.StopReason) continue;
                if (!cancelled && !paused && session!.Status is not (SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)) continue;
                if (!cancelled && !paused && session!.Status == SessionStatus.Idle
                    && (prompt.State != SessionInputQueueState.Delivered || !entry.ActiveSeen)
                    && !(settled && entry.BackgroundDeliveries > 0 && prompt.State == SessionInputQueueState.Delivered
                        && SessionWorkStates.Derive(session.Status, PendingQuestions(sessionId).Count, session.BackgroundTasks) == SessionWorkStates.Idle))
                    continue;
                var completion = new { sessionId, callbackId = entry.CallbackId, promptMessageUid = entry.PromptMessageUid,
                    status = cancelled ? "Cancelled" : !paused && endedReason is not null && session!.Status != SessionStatus.Error ? "Ended" : session!.Status.ToString(),
                    reason = cancelled ? prompt.ErrorCode == "deployment_target_superseded" ? "superseded" : "cancelled" : endedReason,
                    stopReason = cancelled ? null : session!.StopReason, userId = entry.UserId,
                    deliveredMessageUid = prompt.State == SessionInputQueueState.Delivered ? prompt.DeliveredMessageUid : null,
                    executionObserved = prompt.State == SessionInputQueueState.Delivered && entry.ActiveSeen,
                    workState = endedReason is not null || session is null ? SessionWorkStates.Idle
                        : SessionWorkStates.Derive(session.Status, PendingQuestions(sessionId).Count, session.BackgroundTasks) };
                var backgroundRunning = !cancelled && !paused && endedReason is null && session!.Status == SessionStatus.Idle
                    ? SessionWorkStates.RunningBackgroundTasks(session.BackgroundTasks) : 0;
                var payload = SessionCallbackRegistry.WithTurnIdentity(completion, backgroundRunning, entry.BackgroundDeliveries);
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
        var question = entry.QuestionRequestId;
        if (question is not null && !PendingQuestions(entry.SessionId).Any(q => q.RequestId == question))
        {
            await _gate.WaitAsync();
            try
            {
                if (_entries.TryGetValue((entry.SessionId, entry.CallbackId), out var stale) && stale.Payload == entry.Payload)
                { _entries.Remove((entry.SessionId, entry.CallbackId)); _store.Remove(entry.SessionId, entry.CallbackId); }
            }
            finally { _gate.Release(); }
            _log($"[Callbacks] Question {question} on session {entry.SessionId} resolved before notification; dropped", null);
            return;
        }
        var acknowledged = false;
        var subject = question is null ? "Outbox" : $"Question {question} notification for session {entry.SessionId}:";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, entry.Url) {
                Content = new StringContent(entry.Payload!, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, ct);
            acknowledged = response.IsSuccessStatusCode;
            if (!acknowledged) _log($"[Callbacks] {subject} HTTP {(int)response.StatusCode}; retry deferred", null);
        }
        catch (Exception) { _log($"[Callbacks] {subject} transport unavailable; retry deferred", null); }
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
            if (question is not null)
            {
                _entries.Remove(key); _store.Remove(entry.SessionId, entry.CallbackId);
                _log($"[Callbacks] Question {question} notified for session {entry.SessionId}", null);
                return;
            }
            using var doc = JsonDocument.Parse(entry.Payload!);
            // The turn ended with background work still running: keep the subscription for the turn
            // that work wakes. It must be observed executing again before it can report.
            if (doc.RootElement.TryGetProperty("backgroundTasksRunning", out var running) && running.GetInt32() > 0)
            {
                Save(latest with { Payload = null, ActiveSeen = false, BackgroundDeliveries = latest.BackgroundDeliveries + 1, Attempts = 0, NextAttemptAt = null });
                return;
            }
            var pauseReason = doc.RootElement.GetProperty("stopReason").GetString();
            var paused = doc.RootElement.GetProperty("status").GetString() == "Stopped"
                && (SessionStopReasons.IsAutoResumable(pauseReason) || pauseReason == "usage_limit");
            if (paused) Save(latest with { Payload = null, PauseReason = latest.ActiveSeen ? null : pauseReason, Attempts = 0, NextAttemptAt = null });
            else { _entries.Remove(key); _store.Remove(entry.SessionId, entry.CallbackId); }
        }
        finally { _gate.Release(); }
    }
}
