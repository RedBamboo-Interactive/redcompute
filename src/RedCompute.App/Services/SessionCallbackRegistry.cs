using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public class SessionCallbackRegistry
{
    // BackgroundDeliveries counts turns already reported Idle with background work still running;
    // AwaitingTurn holds the registration until the turn that work wakes has been seen executing.
    private record CallbackEntry(string Url, string? UserId, string CallbackId, string? QuestionUrl = null,
        int BackgroundDeliveries = 0, bool AwaitingTurn = false);

    private readonly ConcurrentDictionary<string, CallbackEntry> _callbacks = new();
    // In-memory registrations' question notifications already sent, by session, request and URL.
    private readonly ConcurrentDictionary<(string, string, string), byte> _questionsNotified = new();
    private readonly HttpClient _http;
    private readonly Action<string, Guid?> _log;
    private SessionPromptCallbacks? _durable;
    private SessionInputQueueStore? _queue;
    private Func<string, IReadOnlyList<PendingSessionQuestion>> _pendingQuestions = _ => [];
    private Func<string, UnifiedSessionInfo?> _currentSession = _ => null;

    internal void AttachDurableStore(string databasePath, SessionInputQueueStore queue,
        Func<string, UnifiedSessionInfo?> currentSession)
    {
        _queue = queue;
        _durable = new(databasePath, queue, currentSession, _http, _log)
        {
            PendingQuestions = id => _pendingQuestions(id),
            HasLiveRegistration = id => _callbacks.ContainsKey(id),
        };
    }

    /// <summary>Where parked questions are read from: the live session providers.</summary>
    internal void AttachQuestionSource(Func<string, IReadOnlyList<PendingSessionQuestion>> pendingQuestions,
        Func<string, UnifiedSessionInfo?> currentSession)
    {
        _pendingQuestions = pendingQuestions;
        _currentSession = currentSession;
    }

    internal Task ReconcileAsync(CancellationToken ct)
        => _durable?.SweepAsync(ct) ?? Task.CompletedTask;

    public SessionCallbackRegistry(Action<string, Guid?> log, AuthenticatedHttpClientFactory? authFactory = null)
    {
        _log = log;
        _http = authFactory?.CreateClient("http://localhost", TimeSpan.FromSeconds(10))
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal SessionCallbackRegistry(HttpClient http, Action<string, Guid?> log)
    {
        _http = http;
        _log = log;
    }

    // One delivery identity per registration, shared by every HTTP retry. Callers
    // with an existing invocation identity (e.g. a delegated prompt UID) reuse it.
    // questionUrl opts this registration in to one POST per question the session parks on;
    // registrants that omit it never receive question payloads.
    public void Register(string sessionId, string callbackUrl, string? userId = null, string? callbackId = null, string? promptMessageUid = null,
        string? questionUrl = null)
    {
        if (_durable is not null && callbackId is not null && promptMessageUid is not null)
        {
            _durable.RegisterAsync(sessionId, callbackId, callbackUrl, userId, promptMessageUid, questionUrl).GetAwaiter().GetResult();
            return;
        }
        // With the durable store attached the session-scoped subscription owns question notifications.
        _callbacks[sessionId] = new CallbackEntry(callbackUrl, userId, callbackId ?? Guid.NewGuid().ToString("N"), _durable is null ? questionUrl : null);
        _log($"[Callbacks] Registered callback for session {sessionId}", null);
        if (questionUrl is not null) Subscribe(sessionId, callbackUrl, userId, questionUrl);
    }

    public bool RegisterIfStillActive(string sessionId, string callbackUrl, SessionStatus currentStatus,
        string? userId = null, string? stopReason = null, bool force = false, string? callbackId = null, string? promptMessageUid = null,
        string? questionUrl = null, IReadOnlyList<BackgroundTaskInfo>? backgroundTasks = null)
    {
        if (_durable is not null && callbackId is not null && promptMessageUid is not null)
        {
            Register(sessionId, callbackUrl, userId, callbackId, promptMessageUid, questionUrl);
            return true;
        }
        if (!force && currentStatus is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)
        {
            var identity = callbackId ?? Guid.NewGuid().ToString("N");
            var tasks = backgroundTasks ?? [];
            var running = currentStatus == SessionStatus.Idle ? SessionWorkStates.RunningBackgroundTasks(tasks) : 0;
            var workState = SessionWorkStates.Derive(currentStatus, _pendingQuestions(sessionId).Count, tasks);
            if (running > 0)
            {
                // Already Idle with background work running: report this turn now and stay armed
                // for the turn that work wakes, exactly as a live registration would.
                _callbacks[sessionId] = new CallbackEntry(callbackUrl, userId, identity, _durable is null ? questionUrl : null,
                    BackgroundDeliveries: 1, AwaitingTurn: true);
                if (questionUrl is not null && _durable is not null) Subscribe(sessionId, callbackUrl, userId, questionUrl);
                _ = FireAsync(callbackUrl, sessionId, currentStatus.ToString(), identity, userId: userId, stopReason: stopReason,
                    workState: workState, backgroundTasksRunning: running, deliverySequence: 1);
                return true;
            }
            _ = FireAsync(callbackUrl, sessionId, currentStatus.ToString(), identity, userId: userId, stopReason: stopReason, workState: workState);
            // The question opt-in is session-scoped: it outlives this already-finished completion.
            if (questionUrl is not null) Subscribe(sessionId, callbackUrl, userId, questionUrl);
            return false;
        }

        Register(sessionId, callbackUrl, userId, callbackId, promptMessageUid, questionUrl);
        return true;
    }

    /// <summary>
    /// A session parked on a question. Notifies opted-in registrants; never touches completion
    /// subscriptions, and a failed notification is logged and never blocks the session.
    /// </summary>
    public void OnQuestionParked(string sessionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                NotifyLegacyQuestions(sessionId);
                if (_durable is not null) await _durable.QuestionsChangedAsync(sessionId);
            }
            catch (Exception ex) { _log($"[Callbacks] Question notification for session {sessionId} failed: {ex.Message}", null); }
        });
    }

    // A question opt-in becomes a session-scoped subscription, durable when the store is attached.
    // Without it (no durable store) the in-memory registration keeps its own question opt-in.
    private void Subscribe(string sessionId, string callbackUrl, string? userId, string questionUrl)
    {
        if (_durable is not null)
            _durable.SubscribeSessionAsync(sessionId, callbackUrl, userId, questionUrl).GetAwaiter().GetResult();
        else
            NotifyLegacyQuestions(sessionId);
    }

    /// <summary>Routes the provider stream events that open or close a parked question.</summary>
    public void OnStreamEvent(string sessionId, UnifiedStreamEvent evt)
    {
        if (evt.Type == "question") OnQuestionParked(sessionId);
        else if (evt.Type == "question_resolved" && evt.RequestId is { } requestId) OnQuestionResolved(sessionId, requestId);
    }

    /// <summary>
    /// The session has settled: Idle, nothing running, nothing queued, for the settle window. A
    /// registration still waiting for the turn its background work would wake reports now
    /// instead of waiting forever, since that work ended without waking one. A woken turn would
    /// have left the settled state first and reported through the ordinary Idle path.
    /// </summary>
    public void OnSessionSettled(string sessionId)
    {
        if (_callbacks.TryGetValue(sessionId, out var entry) && entry.AwaitingTurn
            && _currentSession(sessionId) is { Status: SessionStatus.Idle } session
            && SessionWorkStates.Derive(session.Status, _pendingQuestions(sessionId).Count, session.BackgroundTasks) == SessionWorkStates.Idle
            && _callbacks.TryRemove(KeyValuePair.Create(sessionId, entry)))
        {
            _log($"[Callbacks] Session {sessionId} settled without a woken turn; reporting completion", null);
            _ = FireAsync(entry.Url, sessionId, session.Status.ToString(), entry.CallbackId, session.ProjectPath, session.Title,
                userId: session.UserId ?? entry.UserId, stopReason: session.StopReason, workState: SessionWorkStates.Idle,
                deliverySequence: entry.BackgroundDeliveries + 1);
        }
        if (_durable is not null) _ = ObserveSafelyAsync(sessionId, settled: true);
    }

    public void OnQuestionResolved(string sessionId, string requestId)
    {
        foreach (var key in _questionsNotified.Keys.Where(k => k.Item1 == sessionId && k.Item2 == requestId))
            _questionsNotified.TryRemove(key, out _);
        if (_durable is null) return;
        _ = Task.Run(async () =>
        {
            try { await _durable.QuestionResolvedAsync(sessionId, requestId); }
            catch (Exception ex) { _log($"[Callbacks] Question {requestId} cleanup for session {sessionId} failed: {ex.Message}", null); }
        });
    }

    private void NotifyLegacyQuestions(string sessionId)
    {
        if (!_callbacks.TryGetValue(sessionId, out var entry) || entry.QuestionUrl is not { } url) return;
        var pending = _pendingQuestions(sessionId);
        if (pending.Count == 0) return;
        var session = _currentSession(sessionId);
        foreach (var question in pending)
        {
            if (!_questionsNotified.TryAdd((sessionId, question.RequestId, url), 0)) continue;
            var payload = SessionPromptCallbacks.QuestionPayload(sessionId, entry.CallbackId, session, question);
            _ = PostQuestionAsync(url, sessionId, question.RequestId, payload);
        }
    }

    private async Task PostQuestionAsync(string url, string sessionId, string requestId, string payload)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!_pendingQuestions(sessionId).Any(q => q.RequestId == requestId))
            {
                _log($"[Callbacks] Question {requestId} on session {sessionId} resolved before notification; dropped", null);
                return;
            }
            try
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    _log($"[Callbacks] Question {requestId} notified for session {sessionId}", null);
                    return;
                }
                _log($"[Callbacks] Question {requestId} notification attempt {attempt + 1} failed for {sessionId}: HTTP {(int)response.StatusCode}", null);
            }
            catch (Exception ex)
            {
                _log($"[Callbacks] Question {requestId} notification attempt {attempt + 1} failed for {sessionId}: {ex.Message}", null);
            }

            if (attempt < 2)
                await Task.Delay(500 * (attempt + 1));
        }

        _log($"[Callbacks] All 3 question notification attempts failed for session {sessionId} (request {requestId}), giving up", null);
    }

    public void OnSessionEvent(string eventType, object data)
    {
        if (_durable is not null && eventType == "session.updated" && data is UnifiedSessionInfo current)
            _ = ObserveSafelyAsync(current.Id, current.Status == SessionStatus.Active);
        if (_durable is not null && eventType == "session.ended")
        {
            var ended = JsonSerializer.SerializeToElement(data);
            if (ended.TryGetProperty("id", out var endedId) && endedId.ValueKind == JsonValueKind.String)
                _ = ObserveSafelyAsync(endedId.GetString()!, endedReason:
                    ended.TryGetProperty("reason", out var endedReason) && endedReason.ValueKind == JsonValueKind.String
                        ? endedReason.GetString() : "unknown",
                    endedStopReason: ended.TryGetProperty("stopReason", out var endedStop) && endedStop.ValueKind == JsonValueKind.String
                        ? endedStop.GetString() : null);
        }
        if (eventType == "session.updated" && data is UnifiedSessionInfo session)
        {
            if (session.Status is SessionStatus.Active or SessionStatus.Starting)
            {
                if (_callbacks.TryGetValue(session.Id, out var waiting) && waiting.AwaitingTurn)
                    _callbacks.TryUpdate(session.Id, waiting with { AwaitingTurn = false }, waiting);
                return;
            }
            if (session.Status is not (SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error))
                return;
            if (!_callbacks.TryGetValue(session.Id, out var entry))
                return;

            var running = session.Status == SessionStatus.Idle ? SessionWorkStates.RunningBackgroundTasks(session.BackgroundTasks) : 0;
            var workState = SessionWorkStates.Derive(session.Status, _pendingQuestions(session.Id).Count, session.BackgroundTasks);
            if (session.Status == SessionStatus.Idle && entry.AwaitingTurn)
                return; // Still the reported turn's idle, or its background progress: wait for the woken turn.
            if (running > 0)
            {
                // Report the turn, keep the registration for the turn the background work wakes.
                var kept = entry with { BackgroundDeliveries = entry.BackgroundDeliveries + 1, AwaitingTurn = true };
                if (_callbacks.TryUpdate(session.Id, kept, entry))
                    _ = FireAsync(entry.Url, session.Id, session.Status.ToString(), entry.CallbackId, session.ProjectPath, session.Title,
                        userId: session.UserId ?? entry.UserId, stopReason: session.StopReason, workState: workState,
                        backgroundTasksRunning: running, deliverySequence: kept.BackgroundDeliveries);
                return;
            }

            if (_callbacks.TryRemove(KeyValuePair.Create(session.Id, entry)))
                _ = FireAsync(entry.Url, session.Id, session.Status.ToString(), entry.CallbackId, session.ProjectPath, session.Title,
                    userId: session.UserId ?? entry.UserId, stopReason: session.StopReason, workState: workState,
                    deliverySequence: entry.BackgroundDeliveries > 0 ? entry.BackgroundDeliveries + 1 : null);
        }
        else if (eventType == "session.ended")
        {
            try
            {
                var json = JsonSerializer.Serialize(data);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString()!;
                    var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "ended";
                    var stopReason = doc.RootElement.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
                    foreach (var key in _questionsNotified.Keys.Where(k => k.Item1 == id))
                        _questionsNotified.TryRemove(key, out _);
                    if (_durable is not null) _ = _durable.SessionEndedAsync(id);
                    if (_callbacks.TryRemove(id, out var entry))
                        _ = FireAsync(entry.Url, id, "Ended", entry.CallbackId, reason: reason, userId: entry.UserId, stopReason: stopReason,
                            workState: SessionWorkStates.Idle,
                            deliverySequence: entry.BackgroundDeliveries > 0 ? entry.BackgroundDeliveries + 1 : null);
                }
            }
            catch { }
        }
    }

    internal Task<bool> RemoveUnacceptedAsync(string sessionId, string callbackId, string promptMessageUid, string owner, CancellationToken ct)
        => _durable?.RemoveUnacceptedAsync(sessionId, callbackId, promptMessageUid, owner, ct) ?? Task.FromResult(false);
    internal Task DrainAsync() => _durable?.DrainAsync() ?? Task.CompletedTask;
    private async Task ObserveSafelyAsync(string id, bool observedActive = false, string? endedReason = null, bool settled = false,
        string? endedStopReason = null)
    {
        try { await _durable!.ObserveAsync(id, observedActive: observedActive, endedReason: endedReason, settled: settled, endedStopReason: endedStopReason); }
        catch (Exception) { _log("[Callbacks] Observation failed; subscription retained for owning sweep", null); }
    }

    /// <summary>
    /// Serializes a completion payload. Unchanged from the classic shape unless the turn ended with
    /// background work running or follows such a turn; then it carries backgroundTasksRunning and a
    /// 1-based deliverySequence that distinguishes each turn reported under one callbackId.
    /// </summary>
    internal static string WithTurnIdentity(object completion, int backgroundTasksRunning, int priorBackgroundDeliveries)
    {
        if (backgroundTasksRunning <= 0 && priorBackgroundDeliveries <= 0)
            return JsonSerializer.Serialize(completion);
        var node = JsonSerializer.SerializeToNode(completion)!.AsObject();
        node["backgroundTasksRunning"] = backgroundTasksRunning;
        node["deliverySequence"] = priorBackgroundDeliveries + 1;
        return node.ToJsonString();
    }

    private async Task FireAsync(string url, string sessionId, string status, string callbackId,
        string? projectPath = null, string? title = null, string? reason = null, string? userId = null, string? stopReason = null,
        string workState = SessionWorkStates.Idle, int backgroundTasksRunning = 0, int? deliverySequence = null)
    {
        // Counted once, before the retry loop, so every attempt posts the same payload.
        var pendingInputs = 0;
        if (_queue is not null)
        {
            try { pendingInputs = await _queue.CountPendingAsync(sessionId); }
            catch (Exception ex) { _log($"[Callbacks] Pending input count for {sessionId} unavailable: {ex.Message}", null); }
        }
        var completion = new
        {
            sessionId,
            callbackId,
            status,
            projectPath,
            title,
            reason,
            userId,
            stopReason,
            workState,
            pendingInputs,
        };
        var payload = deliverySequence is { } sequence
            ? WithTurnIdentity(completion, backgroundTasksRunning, sequence - 1)
            : JsonSerializer.Serialize(completion);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    _log($"[Callbacks] Fired for session {sessionId} (status={status})", null);
                    return;
                }

                _log($"[Callbacks] Attempt {attempt + 1} failed for {sessionId}: HTTP {(int)response.StatusCode}", null);
            }
            catch (Exception ex)
            {
                _log($"[Callbacks] Attempt {attempt + 1} failed for {sessionId}: {ex.Message}", null);
            }

            if (attempt < 2)
                await Task.Delay(500 * (attempt + 1));
        }

        _log($"[Callbacks] All 3 attempts failed for session {sessionId}, giving up", null);
    }
}
