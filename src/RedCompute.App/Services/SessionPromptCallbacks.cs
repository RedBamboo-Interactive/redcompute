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
///
/// Every completion carries pendingInputs, the session's inputs still Pending or Delivering when the
/// payload is built, so a receiver can tell "this turn completed" from "nothing is left to do". It is
/// part of the persisted payload, so retries of one delivery repeat the same count.
///
/// A question opt-in is also a session-scoped subscription (Kind "session", keyed by session,
/// questionUrl and owner), independent of prompt rows: it keeps receiving question notifications
/// after every prompt row has completed, and it reports, once, the end of any turn the provider
/// started without a queued input (peer messages, CLI self-wakes) while no live prompt
/// registration covered it. Those reports are their own outbox rows (Kind "unprompted",
/// trigger "unprompted", deliverySequence per turn). The subscription lives until the session
/// ends for good; prompt-scoped completion semantics are unchanged.
///
/// Every completion names its turn (turnId, shared by every prompt one turn carried, steered ones
/// included), how it ended (turnOutcome: completed, interrupted, failed, stopped, or null when the
/// provider does not say) and, for an interrupted turn, who interrupted it (interruptedBy). These
/// come from the turn ledger, recorded when the turn ended. A prompt whose turn ended idle while the
/// session already runs the next turn reports from that record, not from the next turn's state.
/// </remarks>
internal sealed class SessionPromptCallbacks
{
    private sealed record Entry(string SessionId, string CallbackId, string PromptMessageUid, string Url, string? UserId,
        bool ActiveSeen = false, string? PauseReason = null, string? Payload = null,
        int Attempts = 0, DateTimeOffset? NextAttemptAt = null,
        string? QuestionUrl = null, string? QuestionRequestId = null, int BackgroundDeliveries = 0,
        // Session-scoped subscription state (Kind "session"): whether a turn is in progress, whether
        // it began without a queued input, the queue sequence it began at, and the last one seen.
        string? Kind = null, bool InTurn = false, bool UnpromptedOpen = false, int UnpromptedTurns = 0,
        long LastDeliverySequence = 0, long OpenDeliverySequence = 0);
    private const string KindSession = "session";
    private const string KindUnprompted = "unprompted";
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

    /// <summary>An in-memory (non-durable) completion registration still covers this session's turn.</summary>
    internal Func<string, bool> HasLiveRegistration { get; set; } = _ => false;

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
                if (existing.QuestionRequestId is not null || existing.Kind is not null || existing.PromptMessageUid != promptMessageUid
                    || existing.UserId != userId || existing.Url != url)
                    throw new SessionInputQueueStoreException("callback_identity_conflict", "Callback identity already refers to a different prompt or owner");
                // Opting in to questions is additive; it never changes the completion identity.
                if (questionUrl is not null && existing.QuestionUrl != questionUrl)
                    Save(existing with { QuestionUrl = questionUrl });
            }
            else Save(new(sessionId, callbackId, promptMessageUid, url, userId, QuestionUrl: questionUrl));
            if (questionUrl is not null) await SubscribeLockedAsync(sessionId, url, userId, questionUrl);
        }
        finally { _gate.Release(); }
        await ObserveAsync(sessionId);
        // A registrant arriving while the session is already parked still has to hear about it.
        if (questionUrl is not null) await QuestionsChangedAsync(sessionId);
    }

    /// <summary>
    /// The session-scoped subscription of a question opt-in that has no exact prompt (an in-memory
    /// registration). Same row and semantics as the one created with a prompt registration.
    /// </summary>
    internal async Task SubscribeSessionAsync(string sessionId, string completionUrl, string? owner, string questionUrl)
    {
        await _gate.WaitAsync();
        try { await SubscribeLockedAsync(sessionId, completionUrl, owner, questionUrl); }
        finally { _gate.Release(); }
        await QuestionsChangedAsync(sessionId);
    }

    // Idempotent per (session, questionUrl, owner): re-registering keeps the row and its turn state,
    // and only follows a changed completion URL.
    private async Task SubscribeLockedAsync(string sessionId, string completionUrl, string? owner, string questionUrl)
    {
        var rowId = SessionRowId(questionUrl, owner);
        if (_entries.TryGetValue((sessionId, rowId), out var existing))
        {
            if (existing.Url != completionUrl) Save(existing with { Url = completionUrl });
            return;
        }
        // A turn already running when the subscription starts is someone else's turn: it is joined,
        // never classified as unprompted.
        Save(new(sessionId, rowId, "", completionUrl, owner, QuestionUrl: questionUrl, Kind: KindSession,
            InTurn: _current(sessionId)?.Status == SessionStatus.Active,
            LastDeliverySequence: await _queue.LatestDeliverySequenceAsync(sessionId)));
    }

    private static string SessionRowId(string questionUrl, string? owner)
        => "session-subscription:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(questionUrl + "\n" + owner)))[..16];

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
        // Session subscriptions follow session events; sweeping them would read every subscribed
        // session on each pass for nothing. Sessions with prompt or outbox rows are swept as before.
        try { sessions = _entries.Values.Where(e => e.Kind != KindSession).Select(e => e.SessionId).Distinct().ToArray(); }
        finally { _gate.Release(); }
        foreach (var sessionId in sessions) await ObserveAsync(sessionId, ct);
    }
    /// <param name="settled">The session has been Idle with nothing running and nothing queued for
    /// the settle window: a subscription re-armed for a woken turn that never came reports now.</param>
    /// <param name="endedStopReason">With endedReason: the stop reason the session ended with. Session
    /// subscriptions outlive only a stop the session resumes from.</param>
    internal async Task ObserveAsync(string sessionId, CancellationToken ct = default, bool observedActive = false,
        string? endedReason = null, bool settled = false, string? endedStopReason = null)
    {
        var deliver = new List<Entry>();
        await _gate.WaitAsync(ct);
        try
        {
            if (!_entries.Values.Any(e => e.SessionId == sessionId)) return;
            var session = _current(sessionId);
            // Close the session's finished turn before reading it, whichever observer runs first.
            if (session?.Status is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)
                _queue.CloseTurn(sessionId, SessionTurnEnd.From(session));
            else if (session is null && endedReason is not null)
                _queue.CloseTurn(sessionId, new SessionTurnEnd("Ended", endedStopReason, null, null, 0));
            var currentDeliveryUid = await _queue.CurrentDeliveryUidAsync(sessionId, ct);
            int? pendingInputs = null; // Read once per observation, only when a payload is built.
            foreach (var entry in _entries.Values.Where(e => e.SessionId == sessionId).ToArray())
            {
                if (entry.QuestionRequestId is not null || entry.Kind == KindUnprompted)
                {
                    if (entry.NextAttemptAt is null || entry.NextAttemptAt <= _now()) deliver.Add(entry);
                    continue;
                }
                if (entry.Kind == KindSession) continue;
                var prompt = await _queue.FindPromptAsync(sessionId, entry.PromptMessageUid, ct);
                if (prompt is null || prompt.OwnerUserId != entry.UserId) continue;
                // The turn this prompt rode; for a report re-armed by background work, the turn
                // that work woke, which is the session's latest ended one when it reports.
                var turn = prompt.State != SessionInputQueueState.Delivered || prompt.TurnId is null ? null
                    : entry.BackgroundDeliveries > 0 ? _queue.GetLastEndedTurn(sessionId) ?? _queue.GetTurn(sessionId, prompt.TurnId)
                    : _queue.GetTurn(sessionId, prompt.TurnId);
                // The ledger saw this prompt's own turn run and end idle: the prompt executed, even
                // if this registration never observed it (a steer accepted as the turn finished).
                var turnRan = entry.PauseReason is null && entry.BackgroundDeliveries == 0
                    && turn is { ActiveSeen: true, EndedAt: not null, Status: nameof(SessionStatus.Idle) }
                    && turn.TurnId == prompt.TurnId;
                if (entry.Payload is null && turnRan && (observedActive || session?.Status == SessionStatus.Active))
                {
                    // The turn ended idle and the session is already running the next one (an
                    // interrupt-current input delivered ahead, a woken or unprompted turn). Report
                    // the ended turn from its record instead of waiting for, and borrowing, the next.
                    pendingInputs ??= await _queue.CountPendingAsync(sessionId, ct);
                    var ended = Completion(sessionId, entry, prompt, turn.Status!, null, turn.StopReason, true,
                        turn.BackgroundTasksRunning > 0 ? SessionWorkStates.Background : SessionWorkStates.Idle,
                        pendingInputs.Value, turn);
                    var recorded = entry with { Payload = SessionCallbackRegistry.WithTurnIdentity(ended, turn.BackgroundTasksRunning, entry.BackgroundDeliveries),
                        ActiveSeen = true, Attempts = 0, NextAttemptAt = null };
                    Save(recorded); deliver.Add(recorded);
                    continue;
                }
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
                    && (prompt.State != SessionInputQueueState.Delivered || !(entry.ActiveSeen || turnRan))
                    && !(settled && entry.BackgroundDeliveries > 0 && prompt.State == SessionInputQueueState.Delivered
                        && SessionWorkStates.Derive(session.Status, PendingQuestions(sessionId).Count, session.BackgroundTasks) == SessionWorkStates.Idle))
                    continue;
                pendingInputs ??= await _queue.CountPendingAsync(sessionId, ct);
                var completion = Completion(sessionId, entry, prompt,
                    status: cancelled ? "Cancelled" : !paused && endedReason is not null && session!.Status != SessionStatus.Error ? "Ended" : session!.Status.ToString(),
                    reason: cancelled ? prompt.ErrorCode == "deployment_target_superseded" ? "superseded" : "cancelled" : endedReason,
                    stopReason: cancelled ? null : session!.StopReason,
                    executionObserved: prompt.State == SessionInputQueueState.Delivered && (entry.ActiveSeen || turnRan),
                    workState: endedReason is not null || session is null ? SessionWorkStates.Idle
                        : SessionWorkStates.Derive(session.Status, PendingQuestions(sessionId).Count, session.BackgroundTasks),
                    pendingInputs: pendingInputs.Value, turn: cancelled ? null : turn);
                var backgroundRunning = !cancelled && !paused && endedReason is null && session!.Status == SessionStatus.Idle
                    ? SessionWorkStates.RunningBackgroundTasks(session.BackgroundTasks) : 0;
                var payload = SessionCallbackRegistry.WithTurnIdentity(completion, backgroundRunning, entry.BackgroundDeliveries);
                var pending = entry with { Payload = payload, ActiveSeen = paused ? false : entry.ActiveSeen, Attempts = 0, NextAttemptAt = null };
                Save(pending); deliver.Add(pending);
            }
            await ObserveSessionSubscriptionsAsync(sessionId, session, observedActive, endedReason, endedStopReason, deliver, ct);
        }
        finally { _gate.Release(); }
        foreach (var entry in deliver) Schedule(entry, ct);
    }

    /// <summary>
    /// Follows the session's turns for its session-scoped subscriptions. A turn that starts while the
    /// queue's delivery sequence has not moved since the session was last idle, and while no live
    /// prompt registration covers it, began without a queued input: its end is reported once. A
    /// queued input that joins the turn (steering) hands the report to that input's own registration.
    /// </summary>
    private async Task ObserveSessionSubscriptionsAsync(string sessionId, UnifiedSessionInfo? session, bool observedActive,
        string? endedReason, string? endedStopReason, List<Entry> deliver, CancellationToken ct)
    {
        var rows = _entries.Values.Where(e => e.SessionId == sessionId && e.Kind == KindSession).ToArray();
        if (rows.Length == 0) return;
        var delivered = await _queue.LatestDeliverySequenceAsync(sessionId, ct);
        var live = session?.Status == SessionStatus.Active;
        var covered = HasLiveRegistration(sessionId) || await HasCoveringPromptAsync(sessionId, ct);
        int? pendingInputs = null;
        foreach (var row in rows)
        {
            var next = row;
            if (endedReason is null && (observedActive || live) && !next.InTurn)
            {
                var unprompted = delivered <= next.LastDeliverySequence && !covered;
                next = next with { InTurn = true, UnpromptedOpen = unprompted, OpenDeliverySequence = delivered,
                    UnpromptedTurns = unprompted ? next.UnpromptedTurns + 1 : next.UnpromptedTurns,
                    LastDeliverySequence = Math.Max(next.LastDeliverySequence, delivered) };
            }
            if (next.InTurn && (!live || endedReason is not null))
            {
                if (next.UnpromptedOpen && delivered <= next.OpenDeliverySequence && !covered)
                {
                    pendingInputs ??= await _queue.CountPendingAsync(sessionId, ct);
                    var report = new Entry(sessionId, $"unprompted:{next.CallbackId}:{next.UnpromptedTurns}", "", next.Url, next.UserId,
                        Payload: UnpromptedPayload(sessionId, next, session, endedReason, pendingInputs.Value), Kind: KindUnprompted);
                    if (!_entries.ContainsKey((sessionId, report.CallbackId))) { Save(report); deliver.Add(report); }
                }
                next = next with { InTurn = false, UnpromptedOpen = false };
            }
            if (!next.InTurn) next = next with { LastDeliverySequence = Math.Max(next.LastDeliverySequence, delivered) };
            if (next != row) Save(next);
        }
        // Gone for good: the subscription ends with the session. A stop the session resumes from
        // (maintenance restart, idle parking) keeps it.
        // Codex reports no stop reason on the ended event; the session record carries it.
        if (endedReason is not null && !SessionStopReasons.IsAutoResumable(endedStopReason ?? session?.StopReason))
            foreach (var row in rows)
            {
                _entries.Remove((sessionId, row.CallbackId)); _store.Remove(sessionId, row.CallbackId);
            }
    }

    /// <summary>
    /// The completion payload of a prompt registration. The turn fields are additive: turnId names
    /// the provider turn (every prompt it carried shares it), turnOutcome how it ended, and
    /// interruptedBy the interrupt that ended it, when it was interrupted.
    /// </summary>
    private static object Completion(string sessionId, Entry entry, SessionInputQueueItem prompt, string status,
        string? reason, string? stopReason, bool executionObserved, string workState, int pendingInputs, SessionTurnRecord? turn)
        => new
        {
            sessionId, callbackId = entry.CallbackId, promptMessageUid = entry.PromptMessageUid,
            status, reason, stopReason, userId = entry.UserId,
            deliveredMessageUid = prompt.State == SessionInputQueueState.Delivered ? prompt.DeliveredMessageUid : null,
            executionObserved, workState, pendingInputs,
            turnId = turn?.TurnId,
            turnOutcome = turn?.EndedAt is null ? null : turn.Outcome,
            interruptedBy = InterruptedBy(turn),
        };

    private static object? InterruptedBy(SessionTurnRecord? turn)
        => turn is { Interrupted: true, InterruptSource: { } source }
            ? new { source, messageUid = turn.InterruptedByMessageUid }
            : null;

    // A delivered prompt whose registration still waits to report its turn (or is re-armed for a
    // woken one) covers the session's current turn; it will report it itself.
    private async Task<bool> HasCoveringPromptAsync(string sessionId, CancellationToken ct)
    {
        foreach (var entry in _entries.Values.Where(e => e.SessionId == sessionId && e.Kind is null
                     && e.QuestionRequestId is null && e.Payload is null).ToArray())
            if (await _queue.FindPromptAsync(sessionId, entry.PromptMessageUid, ct) is { State: SessionInputQueueState.Delivered } prompt
                && prompt.OwnerUserId == entry.UserId)
                return true;
        return false;
    }

    private string UnpromptedPayload(string sessionId, Entry subscription, UnifiedSessionInfo? session, string? endedReason, int pendingInputs)
    {
        var completion = new { sessionId, callbackId = subscription.CallbackId, promptMessageUid = (string?)null,
            status = endedReason is not null && session?.Status != SessionStatus.Error ? "Ended" : session?.Status.ToString() ?? "Ended",
            reason = endedReason, stopReason = session?.StopReason, userId = subscription.UserId,
            deliveredMessageUid = (string?)null, executionObserved = true,
            workState = endedReason is not null || session is null ? SessionWorkStates.Idle
                : SessionWorkStates.Derive(session.Status, PendingQuestions(sessionId).Count, session.BackgroundTasks),
            pendingInputs, trigger = "unprompted" };
        var node = JsonSerializer.SerializeToNode(completion)!.AsObject();
        // The ledger records a turn the provider started itself under an "unprompted:" id.
        if (_queue.GetLastEndedTurn(sessionId) is { } turn && turn.TurnId.StartsWith("unprompted:", StringComparison.Ordinal))
        {
            node["turnId"] = turn.TurnId;
            node["turnOutcome"] = turn.Outcome;
            node["interruptedBy"] = JsonSerializer.SerializeToNode(InterruptedBy(turn));
        }
        node["backgroundTasksRunning"] = endedReason is null && session?.Status == SessionStatus.Idle
            ? SessionWorkStates.RunningBackgroundTasks(session.BackgroundTasks) : 0;
        node["deliverySequence"] = subscription.UnpromptedTurns;
        return node.ToJsonString();
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
            if (latest.Kind == KindUnprompted)
            {
                _entries.Remove(key); _store.Remove(entry.SessionId, entry.CallbackId);
                _log($"[Callbacks] Unprompted turn reported for session {entry.SessionId} ({entry.CallbackId})", null);
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
