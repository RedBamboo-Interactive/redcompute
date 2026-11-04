using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.Codex;

public sealed partial class CodexInteractiveService
{
    private const string PaginatedHistory = "paginated";

    /// <summary>Whether a thread's history contract allows <c>thread/revert</c>. Fixed when the thread is created.</summary>
    internal bool ThreadSupportsRollback(string sessionId) =>
        SupportsRollback(_store.FindSession(sessionId)?.HistoryMode);

    internal static bool SupportsRollback(string? historyMode) =>
        string.Equals(historyMode, PaginatedHistory, StringComparison.Ordinal);

    /// <summary>
    /// <c>thread/revert</c> replaces the thread's persisted history with the prefix before one turn and
    /// leaves files alone. It takes effect at once and survives a restart, so nothing stays pending.
    /// Steered input joined a turn that had already started: it has no boundary of its own to revert to.
    /// </summary>
    public async Task<SessionRollbackResult> RollbackAsync(SessionRollbackRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId;
        var record = _store.FindSession(sessionId);
        if (record is null)
            return SessionRollbackResult.Refused(SessionRollbackRefusals.TargetNotFound, $"Session '{sessionId}' was not found");
        var turnId = _store.GetUserTurnId(sessionId, request.TargetMessageUid);
        if (string.IsNullOrWhiteSpace(turnId))
            return SessionRollbackResult.Refused(SessionRollbackRefusals.TargetNotMapped,
                "This message predates rollback support, so its provider turn is unknown");

        var earlier = _store.GetMessages(sessionId)
            .TakeWhile(message => !(message.Role == "user" && message.MessageUid == request.TargetMessageUid));
        if (earlier.Any(message => message.Role == "user" && message.ProviderTurnId == turnId))
            return SessionRollbackResult.Refused(SessionRollbackRefusals.TargetMidTurn,
                "This message joined a turn that was already running, so there is no boundary just before it");

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            if (await ResumeSessionAsync(sessionId) is null)
                return SessionRollbackResult.Unavailable(LastStartError ?? "The session could not be resumed");
            if (!_sessions.TryGetValue(sessionId, out session))
                return SessionRollbackResult.Unavailable("The session is not loaded");
        }

        if (!string.Equals(session.Info.HistoryMode, PaginatedHistory, StringComparison.Ordinal))
            return SessionRollbackResult.Refused(SessionRollbackRefusals.ProviderThreadLegacy,
                "This conversation was created before its provider could rewind");
        if (session.Info.Status != "Idle")
            return SessionRollbackResult.Refused(SessionRollbackRefusals.NotIdle, "The session is not idle");

        try
        {
            await session.Connection.SendRequestAsync("thread/revert",
                new { threadId = session.Info.ThreadId, beforeTurnId = turnId }, timeoutSeconds: 120);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A restart can interrupt a rollback after the provider applied it. The turn being
            // gone is the proof; anything else is a real refusal and is not retried.
            bool present;
            try { present = await ThreadHasTurnAsync(session, turnId, ct); }
            catch (Exception readError) when (readError is not OperationCanceledException)
            {
                return SessionRollbackResult.Unavailable($"Could not confirm the provider's history: {readError.Message}");
            }
            if (present)
                return SessionRollbackResult.Refused(SessionRollbackRefusals.ProviderRejected, ex.Message);
        }

        _store.MarkMessagesRolledBack(sessionId, request.TargetMessageUid);
        session.Info.MessageCount = request.KeptUserTurns.Count;
        session.Info.LastActivity = DateTimeOffset.UtcNow;
        session.CurrentTurnUid = null;
        Persist(session.Info);
        SessionUpdated?.Invoke(session.Info);
        _log($"[Codex] Session {sessionId} rolled back before {request.TargetMessageUid} (rollback {request.RollbackId})", null);
        return SessionRollbackResult.Applied();
    }

    private async Task<bool> ThreadHasTurnAsync(ManagedSession session, string turnId, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await session.Connection.SendRequestAsync("thread/turns/list",
                new { threadId = session.Info.ThreadId, limit = 100, sortDirection = "desc", cursor }, timeoutSeconds: 60);
            // "Gone" is what lets a failed revert count as applied, so only a readable list may say it.
            if (!page.TryGetProperty("data", out var turns) || turns.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("thread/turns/list did not return a list of turns");
            foreach (var turn in turns.EnumerateArray())
                if (turn.TryGetProperty("id", out var id) && id.GetString() == turnId)
                    return true;
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
        } while (cursor is not null);
        return false;
    }

    /// <summary>The thread's <c>historyMode</c> from a thread/start or thread/resume result.</summary>
    internal static string? HistoryModeOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("thread", out var thread) && thread.ValueKind == JsonValueKind.Object
        && thread.TryGetProperty("historyMode", out var mode) && mode.ValueKind == JsonValueKind.String
            ? mode.GetString()
            : null;

    private void RecordTurnId(ManagedSession session, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId) || session.PendingTurnUid is not { } uid) return;
        session.PendingTurnUid = null;
        _store.SetUserTurnId(session.Info.Id, uid, turnId);
    }
}
