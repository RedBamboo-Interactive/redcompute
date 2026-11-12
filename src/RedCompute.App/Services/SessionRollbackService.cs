using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>What the rollback needs from canonical storage. RedLeaf owns the records.</summary>
internal interface ISessionRollbackTranscript
{
    Task<(string? EntityId, List<UnifiedMessageRecord> History)> ReadAsync(string sessionId, CancellationToken ct);

    /// <summary>False when the kernel cannot mark records, so nothing may start.</summary>
    Task<bool> CanMarkAsync(string entityId, CancellationToken ct);

    Task MarkAsync(string entityId, long fromRecordId, string rollbackId, CancellationToken ct);
}

public sealed record SessionRollbackOutcome(
    bool RolledBack,
    int StatusCode,
    string? Error,
    string? Message,
    IReadOnlyList<string>? Reasons = null,
    string? RollbackId = null,
    string? MessageUid = null,
    string? Epoch = null,
    long? ThroughSequence = null,
    IReadOnlyList<string>? RemovedMessageUids = null,
    string? RestoredText = null,
    DateTimeOffset? FromCreatedAt = null,
    DateTimeOffset? RolledBackAt = null,
    bool AlreadyRolledBack = false)
{
    internal static SessionRollbackOutcome Refused(string error, string message, int statusCode = 409,
        IReadOnlyList<string>? reasons = null) => new(false, statusCode, error, message, reasons);
}

/// <summary>
/// Rewinds a session to just before one of its user messages. The steps cross three owners (provider,
/// RedLeaf, the local epoch), so a ledger row is written first and each step is idempotent: a retry or
/// a restart finishes the same rollback. Input is held while a row is incomplete. Never automatic:
/// the only caller is the explicit route, and a refusal is reported, not retried.
/// </summary>
public sealed class SessionRollbackService
{
    internal const string TranscriptResetEvent = "transcript_reset";
    private const string CompactionTool = "ContextCompaction";

    private readonly CapabilityRegistry _registry;
    private readonly SessionInputQueueService _queue;
    private readonly SessionTranscriptPipeline _pipeline;
    private readonly ISessionRollbackTranscript _transcript;
    private readonly SessionRollbackLedger _ledger;
    private readonly Action<string, Guid?> _log;

    internal SessionRollbackService(
        CapabilityRegistry registry,
        SessionInputQueueService queue,
        SessionTranscriptPipeline pipeline,
        ISessionRollbackTranscript transcript,
        SessionRollbackLedger ledger,
        Action<string, Guid?> log)
    {
        _registry = registry;
        _queue = queue;
        _pipeline = pipeline;
        _transcript = transcript;
        _ledger = ledger;
        _log = log;
    }

    public bool HasIncomplete(string sessionId) => _ledger.HasIncomplete(sessionId);

    public async Task<SessionRollbackOutcome> RollbackAsync(ISessionProvider provider, string sessionId, string messageUid, CancellationToken ct)
    {
        SessionRollbackOutcome outcome = null!;
        await _queue.WithSessionLockAsync(sessionId,
            async () => outcome = await RollbackLockedAsync(provider, sessionId, messageUid, ct), ct);
        if (outcome.RolledBack) _queue.Signal(sessionId);
        return outcome;
    }

    /// <summary>
    /// One pass at startup over rollbacks a restart interrupted. What cannot finish yet waits for the
    /// next thing that needs it: the input queue before it delivers, a resume, or the person asking again.
    /// </summary>
    /// <summary>Context-free: continuations never run on the caller's SynchronizationContext (see DetachedWork).</summary>
    public Task RecoverAsync(CancellationToken ct) => DetachedWork.Start(() => RecoverCoreAsync(ct));

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        foreach (var sessionId in _ledger.ListIncomplete().Select(row => row.SessionId).Distinct(StringComparer.Ordinal).ToList())
        {
            await _queue.WithSessionLockAsync(sessionId, async () => await SettleAsync(sessionId, ct), ct);
            _queue.Signal(sessionId);
        }
    }

    /// <summary>
    /// Finishes this session's unfinished rollback if it can, holding the session's lock. True while one
    /// remains, which holds input: the queue's own sweep comes back for it, so nothing here polls.
    /// </summary>
    public async Task<bool> SettleAsync(string sessionId, CancellationToken ct)
    {
        if (!_ledger.HasIncomplete(sessionId)) return false;
        foreach (var row in _ledger.ListIncomplete().Where(row => row.SessionId == sessionId))
        {
            var provider = _registry.FindProviders<ISessionProvider>().FirstOrDefault(candidate =>
                candidate.GetSessionInfo(sessionId) is not null);
            if (provider is null) continue;
            try
            {
                await ContinueAsync(provider, row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"[Rollback] Finishing {row.RollbackId} for {sessionId} failed: {ex.Message}", null);
            }
        }
        return _ledger.HasIncomplete(sessionId);
    }

    private async Task<SessionRollbackOutcome> RollbackLockedAsync(
        ISessionProvider provider, string sessionId, string messageUid, CancellationToken ct)
    {
        var row = _ledger.Find(sessionId, messageUid);
        if (row is { State: SessionRollbackState.Completed })
            return Completed(row, already: true);

        if (!provider.Capabilities.HasFlag(SessionCapabilities.SupportsRollback) || !provider.SupportsRollback(sessionId))
            return SessionRollbackOutcome.Refused(SessionRollbackRefusals.Unsupported,
                $"Provider '{provider.ProviderId}' cannot roll this conversation back");

        if (row is null)
        {
            var blockers = await BlockersAsync(provider, sessionId, ct);
            if (blockers.Count > 0)
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.NotIdle,
                    $"This conversation can't be rewound right now: {string.Join("; ", blockers.Select(DescribeBlocker))}.",
                    reasons: blockers);

            var (entityId, history) = await _transcript.ReadAsync(sessionId, ct);
            if (entityId is null)
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.TargetNotFound,
                    $"Session '{sessionId}' has no transcript", 404);
            var targetIndex = history.FindIndex(record => record.MessageUid == messageUid);
            if (targetIndex < 0)
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.TargetNotFound,
                    $"Message '{messageUid}' is not in this conversation", 404);
            targetIndex = history.FindIndex(record => record.MessageUid == messageUid && record.Role == "user");
            if (targetIndex < 0)
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.TargetNotUserMessage,
                    "Only a message of the person's can be rewound to");

            var after = history.Skip(targetIndex).ToList();
            if (after.Any(record => record.ToolName == CompactionTool))
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.TargetBeforeCompaction,
                    "This message is before a compaction: the summary already absorbed it, so it cannot be removed");
            if (!await _transcript.CanMarkAsync(entityId, ct))
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.Unsupported,
                    "RedLeaf cannot mark records as rolled back yet");

            var target = history[targetIndex];
            row = new SessionRollbackRow
            {
                SessionId = sessionId,
                MessageUid = messageUid,
                RollbackId = Guid.NewGuid().ToString("N"),
                Provider = provider.ProviderId,
                TranscriptProvider = TranscriptProviderOf(provider, sessionId),
                EntityId = entityId,
                FromRecordId = target.Id,
                FromSequence = after.Select(record => record.Sequence).FirstOrDefault(sequence => sequence.HasValue),
                FromCreatedAt = target.RecordCreatedAt ?? target.Timestamp,
                PreparedAt = DateTimeOffset.UtcNow,
                RestoredText = target.Content,
                KeptTurns = UserTurns(history.Take(targetIndex)),
                DroppedTurns = UserTurns(after),
                RemovedMessageUids = after.Select(record => record.MessageUid).OfType<string>()
                    .Distinct(StringComparer.Ordinal).ToList(),
                State = SessionRollbackState.Prepared,
            };
            _ledger.Insert(row);
        }

        return await ContinueAsync(provider, row);
    }

    private async Task<SessionRollbackOutcome> ContinueAsync(ISessionProvider provider, SessionRollbackRow row)
    {
        // From here the caller going away must not leave a half-done rollback behind.
        var ct = CancellationToken.None;
        if (row.State == SessionRollbackState.Prepared)
        {
            SessionRollbackResult result;
            try
            {
                result = await provider.RollbackAsync(new SessionRollbackRequest(
                    row.SessionId, row.RollbackId, row.MessageUid, row.KeptTurns, row.DroppedTurns), ct);
            }
            catch (Exception ex)
            {
                _log($"[Rollback] Provider failed for {row.SessionId}: {ex.Message}", null);
                result = SessionRollbackResult.Unavailable(ex.Message);
            }

            if (result.Status != SessionRollbackStatus.Applied)
            {
                // The provider changed nothing, so nothing is pending. A refusal is final.
                _ledger.Delete(row);
                return result.Status == SessionRollbackStatus.Unavailable
                    ? SessionRollbackOutcome.Refused(result.Code ?? SessionRollbackRefusals.ProviderUnavailable,
                        result.Message ?? "The provider is unavailable", 503)
                    : SessionRollbackOutcome.Refused(result.Code ?? SessionRollbackRefusals.ProviderRejected,
                        result.Message ?? "The provider refused to rewind to this message");
            }
            _ledger.Advance(row, SessionRollbackState.ProviderApplied);
        }

        if (row.State == SessionRollbackState.ProviderApplied)
        {
            try
            {
                await _transcript.MarkAsync(row.EntityId, row.FromRecordId, row.RollbackId, ct);
            }
            catch (Exception ex)
            {
                _log($"[Rollback] Marking records failed for {row.SessionId}; recovery will finish it: {ex.Message}", null);
                return SessionRollbackOutcome.Refused(SessionRollbackRefusals.Incomplete,
                    "The rewind is applied but its records are not marked yet. Input is held; retrying finishes it.", 503);
            }
            _ledger.Advance(row, SessionRollbackState.Marked);
        }

        if (row.State == SessionRollbackState.Marked)
        {
            var stamp = _pipeline.ResetTranscript(row.TranscriptProvider, row.SessionId, row.RollbackId, row.MessageUid, row.FromSequence);
            row.Epoch = stamp.Epoch;
            row.ThroughSequence = stamp.Sequence;
            _ledger.Complete(row);
        }

        _log($"[Rollback] Session {row.SessionId} rewound to before {row.MessageUid} (rollback {row.RollbackId}, {row.RemovedMessageUids.Count} messages)", null);
        return Completed(row, already: false);
    }

    private static SessionRollbackOutcome Completed(SessionRollbackRow row, bool already) => new(
        true, 200, null, null,
        RollbackId: row.RollbackId,
        MessageUid: row.MessageUid,
        Epoch: row.Epoch,
        ThroughSequence: row.ThroughSequence,
        RemovedMessageUids: row.RemovedMessageUids,
        RestoredText: row.RestoredText,
        FromCreatedAt: row.FromCreatedAt,
        RolledBackAt: row.PreparedAt,
        AlreadyRolledBack: already);

    private async Task<IReadOnlyList<string>> BlockersAsync(ISessionProvider provider, string sessionId, CancellationToken ct)
    {
        var blockers = new List<string>();
        var info = provider.GetSessionInfo(sessionId);
        if (info?.Status is SessionStatus.Active or SessionStatus.Starting)
            blockers.Add(SessionRollbackBlockers.TurnActive);
        else if (info?.Status is SessionStatus.Error
                 || (info?.Status == SessionStatus.Stopped && !SessionStopReasons.IsAutoResumable(info.StopReason)))
            blockers.Add(SessionRollbackBlockers.SessionStopped);
        if (provider.GetPendingQuestions(sessionId).Count > 0)
            blockers.Add(SessionRollbackBlockers.QuestionParked);
        if (provider.GetBackgroundTasks(sessionId).Any(task => !BackgroundTaskStatuses.IsTerminal(task.Status)))
            blockers.Add(SessionRollbackBlockers.BackgroundTasksRunning);
        if (await _queue.HasQueuedInputAsync(sessionId, ct))
            blockers.Add(SessionRollbackBlockers.InputQueued);
        return blockers;
    }

    private static string DescribeBlocker(string blocker) => blocker switch
    {
        SessionRollbackBlockers.TurnActive => "a turn is running",
        SessionRollbackBlockers.QuestionParked => "a question is waiting for an answer",
        SessionRollbackBlockers.BackgroundTasksRunning => "background work is running",
        SessionRollbackBlockers.InputQueued => "a message is waiting to be delivered",
        SessionRollbackBlockers.SessionStopped => "the session has stopped",
        _ => blocker,
    };

    private static string TranscriptProviderOf(ISessionProvider provider, string sessionId) =>
        provider.GetSessionInfo(sessionId)?.Provider ?? provider.ProviderId;

    private static List<SessionRollbackUserTurn> UserTurns(IEnumerable<UnifiedMessageRecord> records) =>
        records.Where(record => record.Role == "user" && !string.IsNullOrWhiteSpace(record.MessageUid))
            .Select(record => new SessionRollbackUserTurn(record.MessageUid!, record.Content))
            .ToList();
}

internal static class SessionRollbackState
{
    public const string Prepared = "prepared";
    public const string ProviderApplied = "provider_applied";
    public const string Marked = "marked";
    public const string Completed = "completed";
}

internal sealed class SessionRollbackRow
{
    public required string SessionId { get; init; }
    public required string MessageUid { get; init; }
    public required string RollbackId { get; init; }
    public required string Provider { get; init; }
    public required string TranscriptProvider { get; init; }
    public required string EntityId { get; init; }
    public required long FromRecordId { get; init; }
    public long? FromSequence { get; init; }
    public DateTimeOffset FromCreatedAt { get; init; }
    public DateTimeOffset PreparedAt { get; init; }
    public string? RestoredText { get; init; }
    public required IReadOnlyList<SessionRollbackUserTurn> KeptTurns { get; init; }
    public required IReadOnlyList<SessionRollbackUserTurn> DroppedTurns { get; init; }
    public required IReadOnlyList<string> RemovedMessageUids { get; init; }
    public string State { get; set; } = SessionRollbackState.Prepared;
    public string? Epoch { get; set; }
    public long? ThroughSequence { get; set; }
}

/// <summary>The durable step a rollback has reached, in RedCompute's own database.</summary>
internal sealed class SessionRollbackLedger
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public SessionRollbackLedger(string? databasePath = null)
    {
        var usePooling = databasePath is null;
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "redcompute.db");
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = usePooling,
            DefaultTimeout = 5,
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionRollbacks (
                SessionId TEXT NOT NULL,
                MessageUid TEXT NOT NULL,
                RollbackId TEXT NOT NULL,
                State TEXT NOT NULL,
                Body TEXT NOT NULL,
                PRIMARY KEY (SessionId, MessageUid)
            );
            """;
        command.ExecuteNonQuery();
    }

    public SessionRollbackRow? Find(string sessionId, string messageUid)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Body FROM SessionRollbacks WHERE SessionId = $session AND MessageUid = $uid";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$uid", messageUid);
            return command.ExecuteScalar() is string body ? Deserialize(body) : null;
        }
    }

    public bool HasIncomplete(string sessionId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM SessionRollbacks WHERE SessionId = $session AND State <> $done LIMIT 1";
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$done", SessionRollbackState.Completed);
            return command.ExecuteScalar() is not null;
        }
    }

    public IReadOnlyList<SessionRollbackRow> ListIncomplete()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Body FROM SessionRollbacks WHERE State <> $done";
            command.Parameters.AddWithValue("$done", SessionRollbackState.Completed);
            using var reader = command.ExecuteReader();
            var rows = new List<SessionRollbackRow>();
            while (reader.Read()) rows.Add(Deserialize(reader.GetString(0)));
            return rows;
        }
    }

    public void Insert(SessionRollbackRow row) => Write(row,
        "INSERT INTO SessionRollbacks (SessionId, MessageUid, RollbackId, State, Body) VALUES ($session, $uid, $rollback, $state, $body)");

    public void Advance(SessionRollbackRow row, string state)
    {
        row.State = state;
        Update(row);
    }

    public void Complete(SessionRollbackRow row)
    {
        row.State = SessionRollbackState.Completed;
        Update(row);
    }

    public void Delete(SessionRollbackRow row)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SessionRollbacks WHERE SessionId = $session AND MessageUid = $uid";
            command.Parameters.AddWithValue("$session", row.SessionId);
            command.Parameters.AddWithValue("$uid", row.MessageUid);
            command.ExecuteNonQuery();
        }
    }

    private void Update(SessionRollbackRow row) => Write(row,
        "UPDATE SessionRollbacks SET State = $state, Body = $body, RollbackId = $rollback WHERE SessionId = $session AND MessageUid = $uid");

    private void Write(SessionRollbackRow row, string sql)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$session", row.SessionId);
            command.Parameters.AddWithValue("$uid", row.MessageUid);
            command.Parameters.AddWithValue("$rollback", row.RollbackId);
            command.Parameters.AddWithValue("$state", row.State);
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(row));
            command.ExecuteNonQuery();
        }
    }

    private static SessionRollbackRow Deserialize(string body) =>
        JsonSerializer.Deserialize<SessionRollbackRow>(body)
        ?? throw new InvalidDataException("An unreadable rollback row");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
