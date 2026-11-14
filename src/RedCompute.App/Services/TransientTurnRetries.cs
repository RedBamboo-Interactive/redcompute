using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Retries a delegated turn that failed on a transient error (the network or the model service was
/// briefly unavailable), with bounded backoff, so a blip does not leave a worker dead until someone
/// notices. When the retries run out, the completion reports the failure and its error.
/// </summary>
/// <remarks>
/// 2026-11-14: a DNS outage of about an hour made the Claude CLI give up after its own ~4.5-minute
/// retry ("Can't reach the API server ... (ENOTFOUND)"). Two delegated workers ended their turns
/// failed, the completions went out as plain Idle, and both supervisors were woken into the same
/// outage and died the same way. Nothing tried again; Laurent restarted each thread by hand three
/// hours later.
///
/// Eligible: a durable prompt registration still waiting for its completion, whose delivered prompt
/// rode a turn the provider reported failed with a transient error (<see cref="SessionTurnErrors"/>),
/// while the session's latest ended turn is still such a failure (the outage did not end: a queued
/// prompt or a CLI self-wake that ran after it failed the same way, as on 14 Nov), on a session that
/// is idle, running or idle-parked (not stopped by anyone, not restart-stopped:
/// <see cref="RestartContinuations"/> owns those), with attempts left and the failure recent. While
/// that holds, <see cref="SessionPromptCallbacks"/> keeps the completion back.
///
/// A session gets one retry at a time: one ordinary queued input under a held prompt's own owner and
/// provenance, like a restart continuation, due a backoff step after the session's latest failure.
/// Its idempotency key is that failed turn, so a second pass or a restart admits nothing new; the
/// attempt number travels in its metadata, so the count survives restarts with no state of its own.
/// Every held registration of the session moves to the retry (trigger <see cref="Trigger"/>), so each
/// supervisor hears exactly one completion: the retried turn's, or the last failure's.
/// </remarks>
internal sealed class TransientTurnRetries
{
    internal const string Trigger = "transient-retry";

    /// <summary>Wait before each attempt, from the end of the failed turn. Six attempts over about 70
    /// minutes, each also covering the CLI's own few minutes of retrying: the 14 Nov outage was over
    /// within the window.</summary>
    internal static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30),
    ];

    /// <summary>A failure older than this is not retried (RedCompute was down, or the PC was off).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(3);

    internal static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(30);

    private readonly SessionPromptCallbacks _callbacks;
    private readonly SessionInputQueueStore _store;
    private readonly Func<SessionInputQueueSubmission, CancellationToken, Task<SessionInputAdmissionResult>> _admit;
    private readonly Func<string, UnifiedSessionInfo?> _session;
    private readonly Action<string, Guid?> _log;
    private readonly Func<DateTimeOffset> _now;

    internal TransientTurnRetries(SessionPromptCallbacks callbacks, SessionInputQueueStore store,
        Func<SessionInputQueueSubmission, CancellationToken, Task<SessionInputAdmissionResult>> admit,
        Func<string, UnifiedSessionInfo?> session, Action<string, Guid?> log, Func<DateTimeOffset>? now = null)
    {
        _callbacks = callbacks; _store = store; _admit = admit; _session = session; _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        callbacks.HoldForRetry = Holds;
    }

    /// <summary>The next retry: which attempt it is, when it is due, and the latest failure it answers.</summary>
    internal sealed record Plan(int Attempt, DateTimeOffset DueAt, SessionTurnRecord LatestFailure);

    /// <summary>The decision table, pure so it is tested exactly. Null: nothing will be retried.</summary>
    internal static Plan? Qualify(SessionInputQueueItem prompt, SessionTurnRecord? turn, SessionTurnRecord? lastEnded,
        UnifiedSessionInfo? session, DateTimeOffset now)
    {
        if (prompt.State != SessionInputQueueState.Delivered || prompt.TurnId is null) return null;
        if (turn is not { EndedAt: { } endedAt, Outcome: SessionTurnOutcomes.Failed } || turn.TurnId != prompt.TurnId) return null;
        if (!TransientFailure(turn)) return null;
        // The session's latest turn must still be failing the same way. Once anything after it ends
        // otherwise, the outage is over for this session and the prompt reports its own failure.
        if (lastEnded is not { EndedAt: { } lastAt } || !TransientFailure(lastEnded) || lastAt < endedAt) return null;
        if (session is null) return null;
        var live = session.Status is SessionStatus.Idle or SessionStatus.Active
            || session.Status == SessionStatus.Stopped && session.StopReason is "idle_timeout" or "idle_ttl_expired";
        if (!live) return null;
        var attempt = AttemptOf(prompt) + 1;
        if (attempt > Delays.Length) return null;
        if (now - lastAt > Window) return null;
        return new Plan(attempt, lastAt + Delays[attempt - 1], lastEnded);
    }

    private static bool TransientFailure(SessionTurnRecord turn)
        => turn is { EndedAt: not null, Outcome: SessionTurnOutcomes.Failed }
            && SessionTurnErrors.IsTransient(turn.Error)
            && turn.StopReason is not ("usage_limit" or "authentication_failed");

    /// <summary>Which retry this prompt itself is: 0 for an ordinary prompt.</summary>
    internal static int AttemptOf(SessionInputQueueItem prompt)
    {
        if (string.IsNullOrEmpty(prompt.MetadataJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(prompt.MetadataJson);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                && source.GetString() == Trigger
                && root.TryGetProperty("attempt", out var attempt) && attempt.TryGetInt32(out var n) && n > 0
                ? n : 0;
        }
        catch (JsonException) { return 0; }
    }

    private bool Holds(string sessionId, SessionInputQueueItem prompt, SessionTurnRecord? turn, UnifiedSessionInfo? session)
        => turn is { Outcome: SessionTurnOutcomes.Failed }
            && Qualify(prompt, turn, _store.LastEndedTurn(sessionId), session, _now()) is not null;

    /// <summary>A pass every <see cref="PassInterval"/>. Context-free (see DetachedWork).</summary>
    internal Task RunAsync(CancellationToken ct) => DetachedWork.Start(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PassInterval, ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log($"[Retry] Transient retry pass failed: {ex.Message}", null); }
        }
    });

    internal async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var admitted = 0;
        var now = _now();
        foreach (var group in (await _callbacks.AwaitingCompletionAsync(ct)).GroupBy(p => p.SessionId))
        {
            var sessionId = group.Key;
            var info = _session(sessionId);
            var lastEnded = _store.GetLastEndedTurn(sessionId);
            // Every held registration of the session moves to its one retry.
            var held = new List<(SessionInputQueueItem Prompt, Plan Plan, string CallbackId)>();
            var settle = false;
            foreach (var registration in group)
            {
                if (await _store.FindPromptAsync(sessionId, registration.PromptMessageUid, ct) is not { } prompt
                    || prompt.OwnerUserId != registration.UserId || prompt.TurnId is null)
                    continue;
                var turn = _store.GetTurn(sessionId, prompt.TurnId);
                var plan = Qualify(prompt, turn, lastEnded, info, now);
                if (plan is null)
                {
                    // A failure held back earlier and no longer retried (out of time): report it now.
                    if (turn is { Outcome: SessionTurnOutcomes.Failed }) settle = true;
                    continue;
                }
                held.Add((prompt, plan, registration.CallbackId));
            }
            if (held.Count > 0)
            {
                // The furthest-along prompt sets the attempt; every plan answers the same latest failure.
                var lead = held.MaxBy(h => h.Plan.Attempt);
                if (lead.Plan.DueAt <= now
                    && await AdmitAsync(sessionId, info!, lead.Prompt, lead.Plan, held.Select(h => h.CallbackId).ToArray(), ct))
                    admitted++;
            }
            if (settle) await _callbacks.ObserveAsync(sessionId, ct);
        }
        return admitted;
    }

    private async Task<bool> AdmitAsync(string sessionId, UnifiedSessionInfo info, SessionInputQueueItem prompt, Plan plan,
        IReadOnlyList<string> callbackIds, CancellationToken ct)
    {
        var failed = plan.LatestFailure;
        var key = $"{Trigger}:{sessionId}:{failed.TurnId}";
        var uid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        var text = Text(failed, plan.Attempt);
        var metadata = JsonSerializer.Serialize(new
        {
            source = Trigger,
            attempt = plan.Attempt,
            maxAttempts = Delays.Length,
            failedTurnId = failed.TurnId,
            failedAt = failed.EndedAt,
            error = failed.Error,
            continues = prompt.MessageUid,
        });
        var submission = new SessionInputQueueSubmission(sessionId, prompt.OwnerUserId,
            [new QueuedSessionInputPart("text", text)], text, metadata, prompt.Provenance,
            SessionInputDeliveryPolicy.AfterCurrent, uid, [], key);
        try
        {
            var result = await _admit(submission, ct);
            await _callbacks.HandOverAsync(sessionId, callbackIds, result.Item.MessageUid, Trigger);
            _log($"[Retry] Retrying {info.Provider}/{sessionId} after a transient failure at {failed.EndedAt:O}"
                + $" (attempt {plan.Attempt}/{Delays.Length}): input {result.Item.Id} {result.Disposition}"
                + $"{(result.Existing ? ", already admitted" : "")}", null);
            return !result.Existing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"[Retry] Could not retry {info.Provider}/{sessionId}: {ex.Message}", null);
            return false;
        }
    }

    /// <summary>What the session reads: what failed, when, and what to do.</summary>
    internal static string Text(SessionTurnRecord failed, int attempt)
    {
        var at = (failed.EndedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var error = Clip(failed.Error ?? "unknown error");
        return $"""
            <transient-retry attempt="{attempt}" of="{Delays.Length}" failedAt="{failed.EndedAt?.UtcDateTime:O}">
            Your previous turn stopped at {at} on a transient provider error: {error}
            This was the network or the model service being briefly unavailable, not something you did. You are in the same session, working directory and transcript; anything that completed before the error is still done.
            Check where the interrupted step got to, redo only what did not finish, and carry on with the task you were given. Nobody asked for this message; RedCompute sends it after a transient failure, at most {Delays.Length} times. Report as usual when you are done.
            </transient-retry>
            """;
    }

    private static string Clip(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Replace("\"", "'").Trim();
        return flat.Length <= 300 ? flat : flat[..297] + "...";
    }
}
