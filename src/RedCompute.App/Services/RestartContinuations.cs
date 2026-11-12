using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// After a restart, continues every delegated session the restart stopped in the middle of its
/// work, so no supervisor has to write the continuation by hand.
/// </summary>
/// <remarks>
/// Eligible: a durable prompt registration still waiting for its completion (nothing final was
/// built for it), whose prompt was delivered, on a session now Stopped with a restart-recoverable
/// reason that this boot recovered (stopped within <see cref="BootWindow"/> of it). Such a
/// registration only exists while the delegated turn is running, parked or working in the
/// background, so it is exactly the work the restart cut. An explicit user stop is never eligible.
///
/// The continuation is one ordinary queued input, admitted under the delegated prompt's own owner
/// and provenance: the queue resumes the provider through RedLeaf's revalidated recovery token for
/// that exact item, at the session's persisted access level. Its idempotency key is the session and
/// its stop, so a second boot, or a second pass, admits nothing new. The registrations move to the
/// continuation, so the supervisor still hears the completion, now with trigger restart-continuation.
/// Sessions owned by a Nova discussion carry no durable prompt registration and keep Nova's own
/// RestartRecovery waker; nothing here touches them.
/// </remarks>
internal sealed class RestartContinuations
{
    internal const string Trigger = "restart-continuation";
    internal static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(20);

    private readonly SessionPromptCallbacks _callbacks;
    private readonly SessionInputQueueStore _store;
    private readonly Func<SessionInputQueueSubmission, CancellationToken, Task<SessionInputAdmissionResult>> _admit;
    private readonly Func<string, UnifiedSessionInfo?> _session;
    private readonly PlannedRestartData? _checkpoint;
    private readonly DateTimeOffset _bootedAt;
    private readonly Action<string, Guid?> _log;

    internal RestartContinuations(SessionPromptCallbacks callbacks, SessionInputQueueStore store,
        Func<SessionInputQueueSubmission, CancellationToken, Task<SessionInputAdmissionResult>> admit,
        Func<string, UnifiedSessionInfo?> session, PlannedRestartData? checkpoint, DateTimeOffset bootedAt,
        Action<string, Guid?> log)
    {
        _callbacks = callbacks; _store = store; _admit = admit; _session = session;
        _checkpoint = checkpoint; _bootedAt = bootedAt; _log = log;
    }

    /// <summary>
    /// Two passes: once providers have recovered and started, and once more for a late one.
    /// Context-free: continuations never run on the caller's SynchronizationContext (see DetachedWork).
    /// </summary>
    internal Task RunAfterBootAsync(CancellationToken ct) => DetachedWork.Start(() => RunAfterBootCoreAsync(ct));

    private async Task RunAfterBootCoreAsync(CancellationToken ct)
    {
        foreach (var delay in new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3) })
        {
            try
            {
                await Task.Delay(delay, ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log($"[Restart] Continuation pass failed: {ex.Message}", null); }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var admitted = 0;
        foreach (var group in (await _callbacks.AwaitingCompletionAsync(ct)).GroupBy(p => p.SessionId))
        {
            var sessionId = group.Key;
            if (_session(sessionId) is not { Status: SessionStatus.Stopped } info
                || !SessionStopReasons.IsRestartRecoverable(info.StopReason)
                || info.StoppedAt is not { } stoppedAt || stoppedAt < _bootedAt - BootWindow)
                continue;

            // The delegated prompt this continues: its owner and provenance carry the continuation.
            SessionInputQueueItem? prompt = null;
            foreach (var registration in group)
                if (await _store.FindPromptAsync(sessionId, registration.PromptMessageUid, ct) is
                        { State: SessionInputQueueState.Delivered } found && found.OwnerUserId == registration.UserId)
                { prompt = found; break; }
            if (prompt is null) continue;

            var planned = _checkpoint?.Sessions.FirstOrDefault(s => s.SessionId == sessionId
                && string.Equals(s.Provider, info.Provider, StringComparison.OrdinalIgnoreCase));
            var key = $"{Trigger}:{sessionId}:{stoppedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
            var uid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
            var text = Text(info, stoppedAt, planned, _checkpoint?.RunId);
            var metadata = JsonSerializer.Serialize(new
            {
                source = Trigger,
                stopReason = info.StopReason,
                stoppedAt,
                deploymentRunId = planned is null ? null : _checkpoint?.RunId,
                forcedStop = planned?.ForcedStop ?? false,
                workState = planned?.WorkState,
                lostBackgroundTasks = planned?.BackgroundTasks,
                continues = prompt.MessageUid,
            });
            var submission = new SessionInputQueueSubmission(sessionId, prompt.OwnerUserId,
                [new QueuedSessionInputPart("text", text)], text, metadata, prompt.Provenance,
                SessionInputDeliveryPolicy.AfterCurrent, uid, [], key);
            try
            {
                var result = await _admit(submission, ct);
                await _callbacks.HandOverAsync(sessionId, group.Select(p => p.CallbackId).ToArray(),
                    result.Item.MessageUid, Trigger);
                admitted++;
                _log($"[Restart] Continued {info.Provider}/{sessionId} after {info.StopReason} at {stoppedAt:O}"
                    + (planned is null ? "" : $" (deployment {_checkpoint!.RunId}{(planned.ForcedStop ? ", forced stop" : "")})")
                    + $": input {result.Item.Id} {result.Disposition}{(result.Existing ? ", already admitted" : "")}", null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"[Restart] Could not continue {info.Provider}/{sessionId}: {ex.Message}", null);
            }
        }
        return admitted;
    }

    /// <summary>What the interrupted session reads: when, by what, why, what it lost, what to do.</summary>
    internal static string Text(UnifiedSessionInfo info, DateTimeOffset stoppedAt, PlannedRestartSession? planned, string? runId)
    {
        var at = stoppedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var by = planned is not null && runId is not null
            ? $"a planned RedCompute restart (deployment {runId})"
            : info.StopReason == SessionStopReasons.MaintenanceRestart
                ? "a planned RedCompute restart"
                : "an unplanned RedCompute restart (the service stopped without a planned drain)";
        var why = planned switch
        {
            { ForcedStop: true, WorkState: SessionWorkStates.AwaitingAnswer } =>
                "Your turn was parked on a question when the drain ran out of time, so it was stopped mid-turn; the question is gone.",
            { ForcedStop: true } =>
                "Your turn was still running when the drain's wait ran out, so it was stopped mid-turn.",
            { WorkState: SessionWorkStates.Background } =>
                "Your turn had ended, but the restart stopped the background work it was waiting on.",
            null => "Your work was interrupted; whatever turn or background work was running stopped with the process.",
            _ => "Your work was interrupted by the restart.",
        };
        var lost = planned?.BackgroundTasks is { Count: > 0 } tasks
            ? "Lost with the old process: " + string.Join("; ", tasks.Select(t => $"background task \"{Clip(t)}\"")) + "."
            : planned is null
                ? "Any background task that was running was lost with the old process."
                : "No background task was running.";
        var access = info.AccessLevel is { Length: > 0 } level ? $", same access level ({level})" : "";
        return $"""
            <restart-continuation stoppedAt="{stoppedAt.UtcDateTime:O}" reason="{info.StopReason}">
            {by} stopped this session at {at}. {why}
            {lost}
            You are resumed in the same working directory with the same transcript{access}. Processes you started are gone; files they wrote are still there.
            Check what the interrupted step left behind, rerun what was lost, and carry on with the task you were given. Nobody asked for this message; RedCompute sends it after every restart that cuts delegated work. Report as usual when you are done.
            </restart-continuation>
            """;
    }

    private static string Clip(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Replace("\"", "'").Trim();
        return flat.Length <= 120 ? flat : flat[..117] + "...";
    }
}
