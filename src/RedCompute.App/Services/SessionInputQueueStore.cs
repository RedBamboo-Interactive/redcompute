using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public enum SessionInputQueueState
{
    Pending,
    Delivering,
    Failed,
    Delivered,
    Cancelled,
}

public enum SessionInputDeliveryPolicy
{
    AfterCurrent,
    InterruptCurrent,
    SteerCurrent,
}

public sealed record QueuedSessionInputPart(string Type, string Value);

public sealed record SessionInputQueueItem(
    long Sequence,
    string Id,
    string? ClientId,
    string SessionId,
    string OwnerUserId,
    IReadOnlyList<QueuedSessionInputPart> Input,
    string DisplayContent,
    string? MetadataJson,
    JobProvenance Provenance,
    string ProvenanceScope,
    SessionInputDeliveryPolicy DeliveryPolicy,
    SessionInputQueueState State,
    string MessageUid,
    IReadOnlyList<string> AttachmentIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LeaseExpiresAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool ErrorRetryable,
    string? DeliveredMessageUid,
    DateTimeOffset? CompletedAt,
    // The policy the caller asked for. DeliveryPolicy is the effective one: a steer demoted for
    // want of a steerable turn, or delivered to a session with no turn running, is after-current,
    // with DeliveryReason saying which.
    SessionInputDeliveryPolicy? RequestedDeliveryPolicy = null,
    string? DeliveryReason = null,
    // The provider turn that carried this input (SessionTurns), shared by every input it carried.
    string? TurnId = null,
    // For interrupt-current: what asking the running turn to stop returned (SessionInterruptResults).
    string? InterruptResult = null)
{
    public SessionInputDeliveryPolicy Requested => RequestedDeliveryPolicy ?? DeliveryPolicy;
}

/// <summary>Why an input's effective delivery differs from the one requested.</summary>
public static class SessionInputDeliveryReasons
{
    /// <summary>Steering was not possible (starting session, unsupported provider or turn, maintenance).</summary>
    public const string SteerUnavailable = "steer_unavailable";
    /// <summary>No turn was running: the steer ran as the session's next turn.</summary>
    public const string NoActiveTurn = "no_active_turn";
}

/// <summary>What asking a running turn to stop returned.</summary>
public static class SessionInterruptResults
{
    public const string Interrupted = "interrupted";
    public const string NotActive = "not_active";
    public const string NotFound = "not_found";
    public const string Unsupported = "unsupported";
    public const string Maintenance = "maintenance";
    public const string Error = "error";
}

/// <summary>
/// One provider turn as RedCompute observed it. A turn opens when a queued batch is handed to an
/// idle session (TurnId = the batch's delivered message UID) or when the session goes active with
/// nothing handed over (an unprompted turn); a steer joins the open turn. It closes when the session
/// leaves Active, recording the outcome and the session state at that moment, so the turn can be
/// reported truthfully even after the session has moved on to the next one.
/// </summary>
public sealed record SessionTurnRecord(
    string SessionId, string TurnId, DateTimeOffset OpenedAt, bool ActiveSeen, DateTimeOffset? EndedAt,
    string? Status, string? StopReason, string? Outcome, int BackgroundTasksRunning,
    string? InterruptSource, string? InterruptedByMessageUid)
{
    public bool Interrupted => Outcome == SessionTurnOutcomes.Interrupted;

    /// <summary>The turn for clients; interrupt attribution only when the interrupt ended it.</summary>
    public SessionTurnSummary Summary => new(TurnId, Outcome, EndedAt,
        Interrupted ? InterruptSource : null, Interrupted ? InterruptedByMessageUid : null);
}

/// <summary>The session state a turn ended in, as handed to <see cref="SessionInputQueueStore.CloseTurn"/>.</summary>
public sealed record SessionTurnEnd(string Status, string? StopReason, string? ProviderOutcome,
    DateTimeOffset? ProviderEndedAt, int BackgroundTasksRunning)
{
    /// <param name="endedStatus">"Ended" when the session itself ended; the live status otherwise.</param>
    public static SessionTurnEnd From(UnifiedSessionInfo session, string? endedStatus = null) => new(
        endedStatus ?? session.Status.ToString(), session.StopReason, session.LastTurnOutcome, session.LastTurnEndedAt,
        endedStatus is null && session.Status == SessionStatus.Idle
            ? SessionWorkStates.RunningBackgroundTasks(session.BackgroundTasks) : 0);
}

public sealed record SessionInputQueueSubmission(
    string SessionId,
    string OwnerUserId,
    IReadOnlyList<QueuedSessionInputPart> Input,
    string DisplayContent,
    string? MetadataJson,
    JobProvenance Provenance,
    SessionInputDeliveryPolicy DeliveryPolicy,
    string MessageUid,
    IReadOnlyList<string> AttachmentIds,
    string? IdempotencyKey);

public sealed record SessionInputQueueAdmission(SessionInputQueueItem Item, bool Existing);

public sealed class SessionInputQueueStoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Durable provider-neutral session input storage. It shares the attachment SQLite database so
/// admitting an input and reserving its staged bytes is one transaction.
/// </summary>
public sealed class SessionInputQueueStore
{
    /// <summary>
    /// The longest wait this queue ever schedules is the 30-minute steering lease; retries and
    /// recovery waits are seconds to minutes. A due time further out than this horizon can only
    /// come from a wall-clock jump (the 2026-10-08 jump to 24 December parked recovery for two
    /// months), so it is treated as due instead of waiting for the clock to catch up.
    /// </summary>
    internal static readonly TimeSpan MaxScheduleHorizon = TimeSpan.FromHours(2);

    internal static bool IsDeferred(DateTimeOffset? due, DateTimeOffset now)
        => due is { } at && at > now && at - now <= MaxScheduleHorizon;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly RedComputeConfig _config;

    public SessionInputQueueStore(RedComputeConfig config, InputAttachmentStore attachments)
    {
        _config = config;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = attachments.DatabasePath,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    internal async Task<SessionInputQueueItem?> FindPromptAsync(string sessionId, string messageUid, CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$session AND MessageUid=$uid ORDER BY Sequence DESC LIMIT 1";
        Add(command, "$session", sessionId); Add(command, "$uid", messageUid);
        return (await ReadItemsAsync(command, ct)).FirstOrDefault();
    }

    /// <summary>The input delivered next (see <see cref="SelectDeliveryHead"/>).</summary>
    internal async Task<SessionInputQueueItem?> GetHeadAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId = $id AND State IN ('Pending','Delivering','Failed') ORDER BY Sequence";
        Add(command, "$id", sessionId);
        return SelectDeliveryHead(await ReadItemsAsync(command, ct));
    }

    /// <summary>
    /// The next input to deliver among a session's active inputs (Pending, Delivering or Failed, in
    /// sequence order). FIFO, except that interrupt-current overtakes: the earliest pending
    /// interrupt-current input goes before older pending inputs, which stay queued in their own order
    /// behind it. Nothing overtakes while an input is in flight or failed (as for steering); the
    /// FIFO head then stays the head.
    /// </summary>
    internal static SessionInputQueueItem? SelectDeliveryHead(IReadOnlyList<SessionInputQueueItem> active)
    {
        var head = active.FirstOrDefault();
        if (head is null || head.State != SessionInputQueueState.Pending) return head;
        if (active.Any(item => item.State is SessionInputQueueState.Delivering or SessionInputQueueState.Failed)) return head;
        return active.FirstOrDefault(item => item.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent) ?? head;
    }

    internal async Task<SessionInputQueueItem?> GetSteerHeadAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$id AND State='Pending' AND DeliveryPolicy='SteerCurrent' ORDER BY Sequence LIMIT 1";
        Add(command, "$id", sessionId);
        return (await ReadItemsAsync(command, ct)).FirstOrDefault();
    }

    internal async Task<IReadOnlyList<SessionInputQueueItem>> GetDeliveringSteersAsync(
        CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionQueuedInputs WHERE State='Delivering' AND DeliveryPolicy='SteerCurrent' ORDER BY SessionId, Sequence";
        return await ReadItemsAsync(command, ct);
    }

    internal async Task<string?> CurrentDeliveryUidAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        // Delivery order, not admission order: steering and interrupt-current overtake.
        command.CommandText = "SELECT COALESCE(DeliveredMessageUid,MessageUid) FROM SessionQueuedInputs WHERE SessionId=$id AND State IN ('Delivering','Delivered') ORDER BY COALESCE(DeliveryOrdinal,Sequence) DESC LIMIT 1";
        Add(command, "$id", sessionId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Inputs of the session still waiting for, or in, delivery to the provider.</summary>
    /// <summary>
    /// The latest delivery position ever handed to the provider for this session (Delivering or
    /// Delivered): its delivery ordinal, or the sequence of an input delivered before ordinals
    /// existed. Ordinals are global, never reused and above every sequence ever issued, so this
    /// moves with every delivery even when an input overtakes older ones; a turn that starts while
    /// it has not moved since the session was last idle was not started by any queued input.
    /// </summary>
    internal async Task<long> LatestDeliverySequenceAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(COALESCE(DeliveryOrdinal,Sequence)),0) FROM SessionQueuedInputs WHERE SessionId=$id AND State IN ('Delivering','Delivered')";
        Add(command, "$id", sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    internal async Task<int> CountPendingAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionQueuedInputs WHERE SessionId=$id AND State IN ('Pending','Delivering')";
        Add(command, "$id", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    internal async Task RequireRecoveryAsync(SessionInputQueueItem item, CancellationToken ct)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO SessionInputRecovery(ItemId) SELECT Id FROM SessionQueuedInputs WHERE Id=$id AND State IN ('Pending','Delivering')";
        Add(command, "$id", item.Id); await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<bool> RequiresRecoveryAsync(string itemId, CancellationToken ct)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SessionInputRecovery WHERE ItemId=$id";
        Add(command, "$id", itemId); return await command.ExecuteScalarAsync(ct) is not null;
    }

    internal async Task RequireSessionRecoveryAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = Open(); var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO SessionInputRecovery(ItemId) SELECT Id FROM SessionQueuedInputs WHERE SessionId=$id AND State='Pending'";
        Add(command, "$id", sessionId); await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task AuthorizePendingScopeAsync(SessionInputQueueItem item, CancellationToken ct)
    {
        await using var connection = Open(); using var transaction = connection.BeginTransaction();
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM SessionInputRecovery WHERE ItemId IN (SELECT Id FROM SessionQueuedInputs WHERE SessionId=$session AND OwnerUserId=$owner AND ProvenanceScope=$scope AND State='Pending'); UPDATE SessionQueuedInputs SET NextAttemptAt=NULL, ErrorCode=NULL, ErrorMessage=NULL WHERE SessionId=$session AND OwnerUserId=$owner AND ProvenanceScope=$scope AND State='Pending' AND ErrorCode LIKE 'recovery_%'";
        Add(command, "$session", item.SessionId); Add(command, "$owner", item.OwnerUserId); Add(command, "$scope", item.ProvenanceScope);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }

    // Authorization/deployment waits never claim a delivery lease or count as a provider attempt.
    internal async Task BlockPendingAsync(SessionInputQueueItem item, string code, bool retryable,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SessionQueuedInputs SET ErrorCode = $code, ErrorMessage = $code,
                ErrorRetryable = $retryable, NextAttemptAt = $next, UpdatedAt = $now
            WHERE Id = $id AND State = 'Pending'
            """;
        Add(command, "$id", item.Id); Add(command, "$code", code); Add(command, "$retryable", retryable ? 1 : 0);
        Add(command, "$next", DateTimeOffset.UtcNow.Add(retryable ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5)).ToString("O"));
        Add(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<SessionInputQueueAdmission> EnqueueAsync(SessionInputQueueSubmission submission, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = "q_" + Guid.NewGuid().ToString("N");
        var inputJson = JsonSerializer.Serialize(submission.Input, JsonOptions);
        var attachmentIds = submission.AttachmentIds.Distinct(StringComparer.Ordinal).ToArray();
        if (attachmentIds.Length != submission.AttachmentIds.Count)
            throw new SessionInputQueueStoreException("duplicate_attachment", "An attachment can appear only once in a queued input");
        if (attachmentIds.Length > _config.InputAttachmentMaxCount)
            throw new SessionInputQueueStoreException("too_many_attachments", $"A turn can contain at most {_config.InputAttachmentMaxCount} attachments");

        var attachmentsJson = JsonSerializer.Serialize(attachmentIds, JsonOptions);
        var provenanceJson = submission.Provenance.ToJson();
        var provenanceScope = ComputeProvenanceScope(submission.Provenance);
        var fingerprint = ComputeFingerprint(submission, attachmentsJson, provenanceScope);

        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        if (!string.IsNullOrWhiteSpace(submission.IdempotencyKey))
        {
            var existing = await FindByIdempotencyAsync(connection, transaction, submission.SessionId,
                submission.OwnerUserId, provenanceScope, submission.IdempotencyKey!, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new SessionInputQueueStoreException("idempotency_conflict", "The idempotency key was already used for a different input");
                transaction.Commit();
                return new SessionInputQueueAdmission(existing.Value.Item, true);
            }
        }

        long totalAttachmentSize = 0;
        foreach (var attachmentId in attachmentIds)
        {
            var attachment = connection.CreateCommand();
            attachment.Transaction = transaction;
            attachment.CommandText = """
                SELECT Size, StoredPath, ExpiresAt, OwnerUserId, ClaimedSessionId, ClaimedMessageUid
                FROM InputAttachments WHERE Id = $id
                """;
            Add(attachment, "$id", attachmentId);
            await using var reader = await attachment.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new SessionInputQueueStoreException("attachment_not_found", $"Attachment '{attachmentId}' was not found");
            if (!string.Equals(reader.GetString(3), submission.OwnerUserId, StringComparison.Ordinal))
                throw new SessionInputQueueStoreException("forbidden", "You do not have access to this attachment");
            if (!reader.IsDBNull(2) && DateTimeOffset.Parse(reader.GetString(2)) <= now)
                throw new SessionInputQueueStoreException("attachment_expired", $"Attachment '{attachmentId}' has expired");
            if (!reader.IsDBNull(4)
                && (!string.Equals(reader.GetString(4), submission.SessionId, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(5), submission.MessageUid, StringComparison.Ordinal)))
                throw new SessionInputQueueStoreException("attachment_already_claimed", $"Attachment '{attachmentId}' has already been sent");
            if (!File.Exists(reader.GetString(1)))
                throw new SessionInputQueueStoreException("attachment_missing", $"Attachment '{attachmentId}' is no longer available");
            totalAttachmentSize += reader.GetInt64(0);
        }
        if (totalAttachmentSize > _config.InputAttachmentMaxTurnSizeBytes)
            throw new SessionInputQueueStoreException("attachments_too_large", $"Turn attachments exceed the {_config.InputAttachmentMaxTurnSizeBytes} byte total limit");

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO SessionQueuedInputs
                (Id, SessionId, OwnerUserId, InputJson, DisplayContent, MetadataJson, ProvenanceJson,
                 ProvenanceScope, DeliveryPolicy, RequestedDeliveryPolicy, State, MessageUid, AttachmentIdsJson, CreatedAt,
                 UpdatedAt, AttemptCount, IdempotencyKey, IdempotencyFingerprint)
            VALUES
                ($id, $sessionId, $ownerUserId, $inputJson, $displayContent, $metadataJson, $provenanceJson,
                 $provenanceScope, $deliveryPolicy, $deliveryPolicy, 'Pending', $messageUid, $attachmentIdsJson, $createdAt,
                 $updatedAt, 0, $idempotencyKey, $idempotencyFingerprint)
            """;
        Add(insert, "$id", id);
        Add(insert, "$sessionId", submission.SessionId);
        Add(insert, "$ownerUserId", submission.OwnerUserId);
        Add(insert, "$inputJson", inputJson);
        Add(insert, "$displayContent", submission.DisplayContent);
        AddNullable(insert, "$metadataJson", submission.MetadataJson);
        Add(insert, "$provenanceJson", provenanceJson);
        Add(insert, "$provenanceScope", provenanceScope);
        Add(insert, "$deliveryPolicy", submission.DeliveryPolicy.ToString());
        Add(insert, "$messageUid", submission.MessageUid);
        Add(insert, "$attachmentIdsJson", attachmentsJson);
        Add(insert, "$createdAt", now.ToString("O"));
        Add(insert, "$updatedAt", now.ToString("O"));
        AddNullable(insert, "$idempotencyKey", submission.IdempotencyKey);
        Add(insert, "$idempotencyFingerprint", fingerprint);
        await insert.ExecuteNonQueryAsync(ct);

        foreach (var attachmentId in attachmentIds)
        {
            var reserve = connection.CreateCommand();
            reserve.Transaction = transaction;
            reserve.CommandText = """
                UPDATE InputAttachments
                SET ClaimedSessionId = $sessionId, ClaimedMessageUid = $messageUid, ExpiresAt = NULL
                WHERE Id = $id AND (ClaimedSessionId IS NULL OR
                    (ClaimedSessionId = $sessionId AND ClaimedMessageUid = $messageUid))
                """;
            Add(reserve, "$sessionId", submission.SessionId);
            Add(reserve, "$messageUid", submission.MessageUid);
            Add(reserve, "$id", attachmentId);
            if (await reserve.ExecuteNonQueryAsync(ct) != 1)
                throw new SessionInputQueueStoreException("attachment_already_claimed", $"Attachment '{attachmentId}' was claimed concurrently");
        }

        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.Transaction = transaction;
        sequenceCommand.CommandText = "SELECT last_insert_rowid()";
        var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);
        var item = new SessionInputQueueItem(sequence, id, submission.IdempotencyKey, submission.SessionId, submission.OwnerUserId,
            submission.Input, submission.DisplayContent, submission.MetadataJson, submission.Provenance,
            provenanceScope, submission.DeliveryPolicy, SessionInputQueueState.Pending, submission.MessageUid,
            attachmentIds, now, now, 0, null, null, null, null, false, null, null,
            RequestedDeliveryPolicy: submission.DeliveryPolicy);
        return new SessionInputQueueAdmission(item, false);
    }

    public async Task<IReadOnlyList<SessionInputQueueItem>> ListAsync(
        string sessionId, string ownerUserId, bool includeTerminal, CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = includeTerminal
            ? "SELECT * FROM SessionQueuedInputs WHERE SessionId = $sessionId AND OwnerUserId = $ownerUserId ORDER BY Sequence"
            : "SELECT * FROM SessionQueuedInputs WHERE SessionId = $sessionId AND OwnerUserId = $ownerUserId AND State IN ('Pending','Delivering','Failed') ORDER BY Sequence";
        Add(cmd, "$sessionId", sessionId);
        Add(cmd, "$ownerUserId", ownerUserId);
        return await ReadItemsAsync(cmd, ct);
    }

    public async Task<SessionInputQueueItem?> GetAsync(string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM SessionQueuedInputs WHERE Id = $id AND SessionId = $sessionId AND OwnerUserId = $ownerUserId";
        Add(cmd, "$id", itemId); Add(cmd, "$sessionId", sessionId); Add(cmd, "$ownerUserId", ownerUserId);
        var items = await ReadItemsAsync(cmd, ct);
        return items.FirstOrDefault();
    }

    public async Task<SessionInputQueueItem?> CancelAsync(string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await GetCoreAsync(connection, transaction, sessionId, itemId, ownerUserId, ct);
        if (item is null) return null;
        if (item.State is SessionInputQueueState.Delivering)
            throw new SessionInputQueueStoreException("delivery_in_progress", "The input is already being delivered");
        if (item.State is SessionInputQueueState.Delivered or SessionInputQueueState.Cancelled)
            return item;

        var now = DateTimeOffset.UtcNow;
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE SessionQueuedInputs SET State = 'Cancelled', UpdatedAt = $now, CompletedAt = $now WHERE Id = $id";
        Add(update, "$now", now.ToString("O")); Add(update, "$id", item.Id);
        await update.ExecuteNonQueryAsync(ct);
        await ReleaseAttachmentsAsync(connection, transaction, item.SessionId, item.MessageUid, now, ct);
        await transaction.CommitAsync(ct);
        return item with { State = SessionInputQueueState.Cancelled, UpdatedAt = now, CompletedAt = now };
    }

    internal async Task<SessionInputQueueItem?> SupersedeVerificationAsync(string sessionId, string itemId,
        string ownerUserId, DeploymentVerificationTarget target, DeploymentVerificationTarget replacement,
        CancellationToken ct)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await GetCoreAsync(connection, transaction, sessionId, itemId, ownerUserId, ct);
        if (item is null) return null;
        if (item.State != SessionInputQueueState.Pending || item.AttemptCount != 0
            || item.DeliveredMessageUid is not null || DeploymentVerificationTarget.FromMetadata(item.MetadataJson) != target
            || target == replacement)
            throw new SessionInputQueueStoreException("verification_not_supersedable", "Only the exact never-delivered pending verification can be explicitly superseded");
        var now = DateTimeOffset.UtcNow;
        var reason = JsonSerializer.Serialize(new { target, replacement, reason = "explicit_supersession" }, JsonOptions);
        var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE SessionQueuedInputs SET State='Cancelled', ErrorCode='deployment_target_superseded', ErrorMessage=$reason, CompletedAt=$now, UpdatedAt=$now WHERE Id=$id AND State='Pending' AND AttemptCount=0";
        Add(update, "$id", item.Id); Add(update, "$reason", reason); Add(update, "$now", now.ToString("O"));
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new SessionInputQueueStoreException("queue_race", "Verification changed during supersession");
        await ReleaseAttachmentsAsync(connection, transaction, sessionId, item.MessageUid, now, ct);
        await transaction.CommitAsync(ct);
        return item with { State = SessionInputQueueState.Cancelled, ErrorCode = "deployment_target_superseded",
            ErrorMessage = reason, CompletedAt = now, UpdatedAt = now };
    }

    public async Task<IReadOnlyList<SessionInputQueueItem>> CancelSessionAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId = $sessionId AND State IN ('Pending', 'Delivering', 'Failed') ORDER BY Sequence";
        Add(select, "$sessionId", sessionId);
        var items = await ReadItemsAsync(select, ct);
        if (items.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return items;
        }

        var now = DateTimeOffset.UtcNow;
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE SessionQueuedInputs SET State = 'Cancelled', UpdatedAt = $now, CompletedAt = $now, LeaseOwner = NULL, LeaseExpiresAt = NULL WHERE SessionId = $sessionId AND State IN ('Pending', 'Delivering', 'Failed')";
        Add(update, "$now", now.ToString("O")); Add(update, "$sessionId", sessionId);
        await update.ExecuteNonQueryAsync(ct);
        foreach (var item in items)
            await ReleaseAttachmentsAsync(connection, transaction, item.SessionId, item.MessageUid, now, ct);
        await transaction.CommitAsync(ct);
        return items.Select(item => item with
        {
            State = SessionInputQueueState.Cancelled,
            UpdatedAt = now,
            CompletedAt = now,
            LeaseExpiresAt = null,
        }).ToArray();
    }

    public async Task<SessionInputQueueItem?> RetryAsync(string sessionId, string itemId, string ownerUserId, CancellationToken ct = default)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = await GetCoreAsync(connection, transaction, sessionId, itemId, ownerUserId, ct);
        if (item is null) return null;
        if (item.State != SessionInputQueueState.Failed)
            throw new SessionInputQueueStoreException("not_retryable_state", "Only a failed queue item can be retried");
        if (!item.ErrorRetryable)
            throw new SessionInputQueueStoreException("not_retryable", "This failure requires a corrected input rather than a retry");
        var now = DateTimeOffset.UtcNow;
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE SessionQueuedInputs
            SET State = 'Pending', UpdatedAt = $now, NextAttemptAt = NULL,
                LeaseOwner = NULL, LeaseExpiresAt = NULL, ErrorCode = NULL,
                ErrorMessage = NULL, ErrorRetryable = 0
            WHERE Id = $id
            """;
        Add(update, "$now", now.ToString("O")); Add(update, "$id", item.Id);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return item with
        {
            State = SessionInputQueueState.Pending,
            UpdatedAt = now,
            NextAttemptAt = null,
            LeaseExpiresAt = null,
            ErrorCode = null,
            ErrorMessage = null,
            ErrorRetryable = false,
        };
    }

    public async Task<IReadOnlyList<SessionInputQueueItem>> ClaimBatchAsync(
        string sessionId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var headCmd = connection.CreateCommand();
        headCmd.Transaction = transaction;
        headCmd.CommandText = """
            SELECT * FROM SessionQueuedInputs
            WHERE SessionId = $sessionId AND State IN ('Pending','Delivering','Failed')
            ORDER BY Sequence
            """;
        Add(headCmd, "$sessionId", sessionId);
        var head = SelectDeliveryHead(await ReadItemsAsync(headCmd, ct));
        if (head is null || head.State == SessionInputQueueState.Failed) return [];
        if (head.State == SessionInputQueueState.Delivering)
        {
            if (head.LeaseExpiresAt is null || IsDeferred(head.LeaseExpiresAt, now)) return [];
            await MarkUnknownOutcomeAsync(connection, transaction, head.Id, now, ct);
            await transaction.CommitAsync(ct);
            return [];
        }
        if (IsDeferred(head.NextAttemptAt, now)) return [];

        var candidates = connection.CreateCommand();
        candidates.Transaction = transaction;
        candidates.CommandText = """
            SELECT * FROM SessionQueuedInputs
            WHERE SessionId = $sessionId AND State = 'Pending' AND Sequence >= $sequence
            ORDER BY Sequence
            """;
        Add(candidates, "$sessionId", sessionId); Add(candidates, "$sequence", head.Sequence);
        var pending = await ReadItemsAsync(candidates, ct);
        var batch = new List<SessionInputQueueItem>();
        var overtaking = head.DeliveryPolicy == SessionInputDeliveryPolicy.InterruptCurrent;
        foreach (var item in pending)
        {
            // An overtaking batch carries the later interrupt-current inputs and passes over the
            // older inputs between them, which keep their place behind it.
            if (overtaking && item.DeliveryPolicy != SessionInputDeliveryPolicy.InterruptCurrent) continue;
            if (!string.Equals(item.ProvenanceScope, head.ProvenanceScope, StringComparison.Ordinal)) break;
            if (item.DeliveryPolicy != head.DeliveryPolicy) break;
            if (DeploymentVerificationTarget.FromMetadata(item.MetadataJson) != DeploymentVerificationTarget.FromMetadata(head.MetadataJson)) break;
            if (IsDeferred(item.NextAttemptAt, now)) break;
            batch.Add(item);
        }
        if (batch.Count == 0) return [];

        // This path hands the batch to an idle session as its next turn. A steer has no turn to
        // join here, so it is delivered as after-current, and says why.
        var ranAsNextTurn = head.DeliveryPolicy == SessionInputDeliveryPolicy.SteerCurrent;
        var turnId = batch[0].MessageUid;
        var ordinal = await NextDeliveryOrdinalAsync(connection, transaction, batch.Count, ct);
        await OpenDeliveredTurnAsync(connection, transaction, sessionId, turnId, now, ct);
        var leaseExpires = now.Add(leaseDuration);
        var claimed = new List<SessionInputQueueItem>(batch.Count);
        foreach (var item in batch)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE SessionQueuedInputs
                SET State = 'Delivering', UpdatedAt = $now, AttemptCount = AttemptCount + 1,
                    LeaseOwner = $leaseOwner, LeaseExpiresAt = $leaseExpires,
                    DeliveryOrdinal = $ordinal, TurnId = $turnId,
                    DeliveryPolicy = CASE WHEN $ranAsNextTurn = 1 THEN 'AfterCurrent' ELSE DeliveryPolicy END,
                    DeliveryReason = CASE WHEN $ranAsNextTurn = 1 THEN $reason ELSE DeliveryReason END
                WHERE Id = $id AND State = 'Pending'
                """;
            Add(update, "$now", now.ToString("O")); Add(update, "$leaseOwner", leaseOwner);
            Add(update, "$leaseExpires", leaseExpires.ToString("O")); Add(update, "$id", item.Id);
            Add(update, "$ordinal", ordinal); Add(update, "$turnId", turnId);
            Add(update, "$ranAsNextTurn", ranAsNextTurn ? 1 : 0);
            Add(update, "$reason", SessionInputDeliveryReasons.NoActiveTurn);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new SessionInputQueueStoreException("queue_race", "The queue changed while claiming a delivery batch");
            claimed.Add(item with
            {
                State = SessionInputQueueState.Delivering,
                UpdatedAt = now,
                AttemptCount = item.AttemptCount + 1,
                LeaseExpiresAt = leaseExpires,
                TurnId = turnId,
                DeliveryPolicy = ranAsNextTurn ? SessionInputDeliveryPolicy.AfterCurrent : item.DeliveryPolicy,
                DeliveryReason = ranAsNextTurn ? SessionInputDeliveryReasons.NoActiveTurn : item.DeliveryReason,
            });
            ordinal++;
        }
        await transaction.CommitAsync(ct);
        _turnStates[sessionId] = TurnState.Opening;
        _lastEnded.TryRemove(sessionId, out _);
        return claimed;
    }

    /// <summary>
    /// Claims the earliest steer item even when ordinary after-current input precedes it. A failed
    /// head or another live lease still blocks the session; steering is an overtaking policy, not a
    /// second delivery lane.
    /// </summary>
    public async Task<IReadOnlyList<SessionInputQueueItem>> ClaimSteerBatchAsync(
        string sessionId, string leaseOwner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var blockerCommand = connection.CreateCommand();
        blockerCommand.Transaction = transaction;
        blockerCommand.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$sessionId AND State IN ('Delivering','Failed') ORDER BY Sequence LIMIT 1";
        Add(blockerCommand, "$sessionId", sessionId);
        var blocker = (await ReadItemsAsync(blockerCommand, ct)).FirstOrDefault();
        if (blocker?.State == SessionInputQueueState.Failed) return [];
        if (blocker?.State == SessionInputQueueState.Delivering)
        {
            if (blocker.LeaseExpiresAt is null || IsDeferred(blocker.LeaseExpiresAt, now)) return [];
            await MarkUnknownOutcomeAsync(connection, transaction, blocker.Id, now, ct);
            await transaction.CommitAsync(ct);
            return [];
        }

        var headCommand = connection.CreateCommand();
        headCommand.Transaction = transaction;
        headCommand.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$sessionId AND State='Pending' AND DeliveryPolicy='SteerCurrent' ORDER BY Sequence LIMIT 1";
        Add(headCommand, "$sessionId", sessionId);
        var head = (await ReadItemsAsync(headCommand, ct)).FirstOrDefault();
        if (head is null || IsDeferred(head.NextAttemptAt, now)) return [];

        var candidates = connection.CreateCommand();
        candidates.Transaction = transaction;
        candidates.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$sessionId AND State='Pending' AND Sequence >= $sequence ORDER BY Sequence";
        Add(candidates, "$sessionId", sessionId);
        Add(candidates, "$sequence", head.Sequence);
        var pending = await ReadItemsAsync(candidates, ct);
        var batch = new List<SessionInputQueueItem>();
        foreach (var item in pending)
        {
            if (item.DeliveryPolicy != SessionInputDeliveryPolicy.SteerCurrent) break;
            if (!string.Equals(item.ProvenanceScope, head.ProvenanceScope, StringComparison.Ordinal)) break;
            if (DeploymentVerificationTarget.FromMetadata(item.MetadataJson) != DeploymentVerificationTarget.FromMetadata(head.MetadataJson)) break;
            if (IsDeferred(item.NextAttemptAt, now)) break;
            batch.Add(item);
        }
        if (batch.Count == 0) return [];

        // A steer joins the turn that is running: it shares that turn's id, so every input the turn
        // carried reports the same turn. A running turn RedCompute has no record of (one already
        // under way when this process started) is recorded now under the steer's own UID.
        var turnId = await OpenTurnIdAsync(connection, transaction, sessionId, ct);
        if (turnId is null)
        {
            turnId = head.MessageUid;
            await InsertTurnAsync(connection, transaction, sessionId, turnId, now, activeSeen: true, ct);
            _turnStates[sessionId] = TurnState.Active;
        }
        var ordinal = await NextDeliveryOrdinalAsync(connection, transaction, batch.Count, ct);
        var leaseExpires = now.Add(leaseDuration);
        var claimed = new List<SessionInputQueueItem>(batch.Count);
        foreach (var item in batch)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SessionQueuedInputs SET State='Delivering', UpdatedAt=$now, AttemptCount=AttemptCount+1, LeaseOwner=$leaseOwner, LeaseExpiresAt=$leaseExpires, DeliveredMessageUid=$deliveredMessageUid, DeliveryOrdinal=$ordinal, TurnId=$turnId WHERE Id=$id AND State='Pending' AND DeliveryPolicy='SteerCurrent'";
            Add(update, "$now", now.ToString("O"));
            Add(update, "$leaseOwner", leaseOwner);
            Add(update, "$leaseExpires", leaseExpires.ToString("O"));
            Add(update, "$deliveredMessageUid", head.MessageUid);
            Add(update, "$ordinal", ordinal++);
            Add(update, "$turnId", turnId);
            Add(update, "$id", item.Id);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new SessionInputQueueStoreException("queue_race", "The steer queue changed while claiming a delivery batch");
            claimed.Add(item with
            {
                State = SessionInputQueueState.Delivering,
                UpdatedAt = now,
                AttemptCount = item.AttemptCount + 1,
                LeaseExpiresAt = leaseExpires,
                DeliveredMessageUid = head.MessageUid,
                TurnId = turnId,
            });
        }
        await transaction.CommitAsync(ct);
        return claimed;
    }

    public Task DemoteSteerAsync(IReadOnlyList<SessionInputQueueItem> batch, CancellationToken ct = default) =>
        DemoteSteerCoreAsync(batch.Select(item => item.Id).ToArray(), "Delivering", ct);

    public async Task<IReadOnlyList<SessionInputQueueItem>> DemotePendingSteerAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var connection = Open();
        var select = connection.CreateCommand();
        select.CommandText = "SELECT * FROM SessionQueuedInputs WHERE SessionId=$sessionId AND State='Pending' AND DeliveryPolicy='SteerCurrent' ORDER BY Sequence";
        Add(select, "$sessionId", sessionId);
        var items = await ReadItemsAsync(select, ct);
        await DemoteSteerCoreAsync(items.Select(item => item.Id).ToArray(), "Pending", ct);
        return items.Select(item => item with
        {
            DeliveryPolicy = SessionInputDeliveryPolicy.AfterCurrent,
            ErrorCode = "steer_unavailable",
            ErrorMessage = "Steering was unavailable; the input will send after the current turn",
            ErrorRetryable = false,
            DeliveryReason = SessionInputDeliveryReasons.SteerUnavailable,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ToArray();
    }

    private async Task DemoteSteerCoreAsync(IReadOnlyList<string> itemIds, string expectedState, CancellationToken ct)
    {
        if (itemIds.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var id in itemIds)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SessionQueuedInputs SET DeliveryPolicy='AfterCurrent', State='Pending', UpdatedAt=$now, NextAttemptAt=NULL, LeaseOwner=NULL, LeaseExpiresAt=NULL, ErrorCode='steer_unavailable', ErrorMessage='Steering was unavailable; the input will send after the current turn', ErrorRetryable=0, DeliveredMessageUid=NULL, DeliveryReason='steer_unavailable', TurnId=NULL, DeliveryOrdinal=NULL WHERE Id=$id AND State=$state AND DeliveryPolicy='SteerCurrent'";
            Add(update, "$now", now.ToString("O"));
            Add(update, "$id", id);
            Add(update, "$state", expectedState);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public Task MarkDeliveredAsync(IReadOnlyList<SessionInputQueueItem> batch, string deliveredMessageUid, CancellationToken ct = default) =>
        CompleteBatchAsync(batch, SessionInputQueueState.Delivered, deliveredMessageUid, null, null, false, null, ct);

    public Task RequeueBusyAsync(IReadOnlyList<SessionInputQueueItem> batch, CancellationToken ct = default) =>
        CompleteBatchAsync(batch, SessionInputQueueState.Pending, null, null, null, false, null, ct);

    public Task FailAsync(IReadOnlyList<SessionInputQueueItem> batch, string code, string message, bool retryable, CancellationToken ct = default)
    {
        var attempts = batch.Count == 0 ? 0 : batch.Max(item => item.AttemptCount);
        var shouldRetry = retryable && attempts < 3;
        var delay = attempts switch { <= 1 => TimeSpan.FromSeconds(1), 2 => TimeSpan.FromSeconds(5), _ => TimeSpan.FromSeconds(30) };
        return CompleteBatchAsync(batch, shouldRetry ? SessionInputQueueState.Pending : SessionInputQueueState.Failed,
            null, code, message, retryable, shouldRetry ? DateTimeOffset.UtcNow.Add(delay) : null, ct);
    }

    public async Task<IReadOnlyList<string>> GetRunnableSessionIdsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT SessionId FROM SessionQueuedInputs
            WHERE State = 'Pending' AND (NextAttemptAt IS NULL OR NextAttemptAt <= $now OR NextAttemptAt > $horizon)
            """;
        Add(cmd, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(cmd, "$horizon", DateTimeOffset.UtcNow.Add(MaxScheduleHorizon).ToString("O"));
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SessionQueuedInputs
            SET State = 'Failed', UpdatedAt = $now, ErrorCode = 'delivery_outcome_unknown',
                ErrorMessage = 'RedCompute restarted before provider delivery was acknowledged',
                ErrorRetryable = 1, LeaseOwner = NULL, LeaseExpiresAt = NULL
            WHERE State = 'Delivering' AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= $now OR LeaseExpiresAt > $horizon)
            """;
        Add(cmd, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(cmd, "$horizon", DateTimeOffset.UtcNow.Add(MaxScheduleHorizon).ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CleanupTerminalAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SessionQueuedInputs WHERE State IN ('Delivered','Cancelled') AND CompletedAt <= $cutoff
                AND COALESCE(ErrorCode,'') != 'deployment_target_superseded'
                AND NOT EXISTS (SELECT 1 FROM SessionPromptCallbacks callback
                    WHERE callback.SessionId=SessionQueuedInputs.SessionId AND json_extract(callback.EntryJson,'$.PromptMessageUid')=SessionQueuedInputs.MessageUid)
            """;
        Add(cmd, "$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
        var removed = await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "DELETE FROM SessionInputRecovery WHERE NOT EXISTS (SELECT 1 FROM SessionQueuedInputs input WHERE input.Id=SessionInputRecovery.ItemId)";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = """
            DELETE FROM SessionTurns WHERE EndedAt IS NOT NULL AND EndedAt <= $cutoff
                AND OpenedAt < (SELECT MAX(latest.OpenedAt) FROM SessionTurns latest WHERE latest.SessionId=SessionTurns.SessionId)
                AND NOT EXISTS (SELECT 1 FROM SessionQueuedInputs input
                    WHERE input.SessionId=SessionTurns.SessionId AND input.TurnId=SessionTurns.TurnId)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        return removed;
    }

    private async Task CompleteBatchAsync(
        IReadOnlyList<SessionInputQueueItem> batch, SessionInputQueueState state,
        string? deliveredMessageUid, string? errorCode, string? errorMessage,
        bool retryable, DateTimeOffset? nextAttemptAt, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var item in batch)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE SessionQueuedInputs
                SET State = $state, UpdatedAt = $now, NextAttemptAt = $nextAttemptAt,
                    LeaseOwner = NULL, LeaseExpiresAt = NULL, ErrorCode = $errorCode,
                    ErrorMessage = $errorMessage, ErrorRetryable = $errorRetryable,
                    DeliveredMessageUid = $deliveredMessageUid, CompletedAt = $completedAt
                WHERE Id = $id AND State = 'Delivering'
                """;
            Add(cmd, "$state", state.ToString()); Add(cmd, "$now", now.ToString("O"));
            AddNullable(cmd, "$nextAttemptAt", nextAttemptAt?.ToString("O"));
            AddNullable(cmd, "$errorCode", errorCode); AddNullable(cmd, "$errorMessage", errorMessage);
            Add(cmd, "$errorRetryable", retryable ? 1 : 0);
            AddNullable(cmd, "$deliveredMessageUid", deliveredMessageUid);
            AddNullable(cmd, "$completedAt", state is SessionInputQueueState.Delivered or SessionInputQueueState.Cancelled ? now.ToString("O") : null);
            Add(cmd, "$id", item.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (state != SessionInputQueueState.Delivered && batch[0].TurnId is { } openedTurn && openedTurn == batch[0].MessageUid)
        {
            var unopened = connection.CreateCommand();
            unopened.Transaction = transaction;
            unopened.CommandText = "DELETE FROM SessionTurns WHERE SessionId=$sessionId AND TurnId=$turnId AND ActiveSeen=0 AND EndedAt IS NULL";
            Add(unopened, "$sessionId", batch[0].SessionId); Add(unopened, "$turnId", openedTurn);
            if (await unopened.ExecuteNonQueryAsync(ct) > 0) _turnStates.TryRemove(batch[0].SessionId, out _);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task ReleaseAttachmentsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, string messageUid, DateTimeOffset now, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE InputAttachments
            SET ClaimedSessionId = NULL, ClaimedMessageUid = NULL, ExpiresAt = $expiresAt
            WHERE ClaimedSessionId = $sessionId AND ClaimedMessageUid = $messageUid
            """;
        Add(cmd, "$expiresAt", now.AddMinutes(_config.InputAttachmentTtlMinutes).ToString("O"));
        Add(cmd, "$sessionId", sessionId); Add(cmd, "$messageUid", messageUid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkUnknownOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, DateTimeOffset now, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE SessionQueuedInputs
            SET State = 'Failed', UpdatedAt = $now, ErrorCode = 'delivery_outcome_unknown',
                ErrorMessage = 'The delivery lease expired before provider acknowledgement', ErrorRetryable = 1,
                LeaseOwner = NULL, LeaseExpiresAt = NULL
            WHERE Id = $id
            """;
        Add(cmd, "$now", now.ToString("O")); Add(cmd, "$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(SessionInputQueueItem Item, string Fingerprint)?> FindByIdempotencyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId, string ownerUserId,
        string provenanceScope, string idempotencyKey, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT * FROM SessionQueuedInputs
            WHERE SessionId = $sessionId AND OwnerUserId = $ownerUserId
              AND IdempotencyKey = $idempotencyKey
            ORDER BY Sequence
            """;
        Add(cmd, "$sessionId", sessionId); Add(cmd, "$ownerUserId", ownerUserId);
        Add(cmd, "$idempotencyKey", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = ReadItem(reader);
            if (!string.Equals(ComputeProvenanceScope(item.Provenance), provenanceScope, StringComparison.Ordinal))
                continue;

            // Recompute with the stable scope instead of trusting a fingerprint produced by an
            // older build whose scope included request-unique execution lineage.
            var normalizedSubmission = new SessionInputQueueSubmission(
                item.SessionId, item.OwnerUserId, item.Input, item.DisplayContent, item.MetadataJson,
                item.Provenance, item.Requested, item.MessageUid, item.AttachmentIds, item.ClientId);
            var attachmentsJson = JsonSerializer.Serialize(item.AttachmentIds, JsonOptions);
            return (item, ComputeFingerprint(normalizedSubmission, attachmentsJson, provenanceScope));
        }
        return null;
    }

    private static async Task<SessionInputQueueItem?> GetCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        string itemId, string ownerUserId, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT * FROM SessionQueuedInputs WHERE Id = $id AND SessionId = $sessionId AND OwnerUserId = $ownerUserId";
        Add(cmd, "$id", itemId); Add(cmd, "$sessionId", sessionId); Add(cmd, "$ownerUserId", ownerUserId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    private static async Task<IReadOnlyList<SessionInputQueueItem>> ReadItemsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var items = new List<SessionInputQueueItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadItem(reader));
        return items;
    }

    private static SessionInputQueueItem ReadItem(SqliteDataReader reader)
    {
        T EnumValue<T>(string name) where T : struct, Enum => Enum.Parse<T>(reader.GetString(reader.GetOrdinal(name)));
        string? NullableString(string name) { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal); }
        DateTimeOffset? NullableDate(string name) => NullableString(name) is { } value ? DateTimeOffset.Parse(value) : null;
        var provenance = JobProvenance.FromJson(reader.GetString(reader.GetOrdinal("ProvenanceJson")))
            ?? throw new InvalidDataException("Queued input provenance is missing");
        return new SessionInputQueueItem(
            reader.GetInt64(reader.GetOrdinal("Sequence")),
            reader.GetString(reader.GetOrdinal("Id")),
            NullableString("IdempotencyKey"),
            reader.GetString(reader.GetOrdinal("SessionId")),
            reader.GetString(reader.GetOrdinal("OwnerUserId")),
            JsonSerializer.Deserialize<QueuedSessionInputPart[]>(reader.GetString(reader.GetOrdinal("InputJson")), JsonOptions) ?? [],
            reader.GetString(reader.GetOrdinal("DisplayContent")),
            NullableString("MetadataJson"), provenance,
            reader.GetString(reader.GetOrdinal("ProvenanceScope")),
            EnumValue<SessionInputDeliveryPolicy>("DeliveryPolicy"),
            EnumValue<SessionInputQueueState>("State"),
            reader.GetString(reader.GetOrdinal("MessageUid")),
            JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("AttachmentIdsJson")), JsonOptions) ?? [],
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
            reader.GetInt32(reader.GetOrdinal("AttemptCount")),
            NullableDate("NextAttemptAt"), NullableDate("LeaseExpiresAt"),
            NullableString("ErrorCode"), NullableString("ErrorMessage"),
            reader.GetInt32(reader.GetOrdinal("ErrorRetryable")) != 0,
            NullableString("DeliveredMessageUid"), NullableDate("CompletedAt"),
            NullableString("RequestedDeliveryPolicy") is { } requested ? Enum.Parse<SessionInputDeliveryPolicy>(requested) : null,
            NullableString("DeliveryReason"), NullableString("TurnId"), NullableString("InterruptResult"));
    }

    private static string ComputeProvenanceScope(JobProvenance provenance)
    {
        var scope = new
        {
            provenance.Origin.Service,
            App = new { provenance.Origin.App.Kind, provenance.Origin.App.Id, provenance.Origin.App.EntityId },
            Actor = new { provenance.Actor.Kind, provenance.Actor.EntityId, provenance.Actor.Id },
            Beneficiary = new { provenance.OnBehalfOf.Kind, provenance.OnBehalfOf.Id },
            // Signed execution and parent-execution IDs identify this HTTP attempt, not the
            // logical browser outbox entry. Keeping them here defeats idempotency on retry while
            // the immutable provenance stored on the queue item still retains the full lineage.
            // An event ID likewise identifies one logical input, as a message UID does, not a
            // caller boundary: keeping it made every queued event its own scope, so events from
            // one caller never coalesced into a turn. Events still differ from ordinary messages
            // by their origin-entrypoint route, and the stored provenance keeps the event ID.
            Context = provenance.Context
                .Where(c => c.Kind is not ("execution" or "parent-execution" or "event"))
                .Select(c => new { c.Kind, c.Id, c.EntityId, c.Route })
                .ToArray(),
        };
        return Sha256(JsonSerializer.Serialize(scope, JsonOptions));
    }

    private static string ComputeFingerprint(SessionInputQueueSubmission submission,
        string attachmentsJson, string provenanceScope) => Sha256(JsonSerializer.Serialize(new
        {
            submission.SessionId,
            submission.OwnerUserId,
            // The provider input may contain a freshly generated context envelope on
            // an HTTP retry. DisplayContent is the stable logical user turn; callers
            // that omit it receive the joined text input as their default.
            submission.DisplayContent,
            attachmentsJson,
            submission.MetadataJson,
            provenanceScope,
            deliveryPolicy = submission.DeliveryPolicy.ToString(),
        }, JsonOptions));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionQueuedInputs (
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                SessionId TEXT NOT NULL,
                OwnerUserId TEXT NOT NULL,
                InputJson TEXT NOT NULL,
                DisplayContent TEXT NOT NULL,
                MetadataJson TEXT NULL,
                ProvenanceJson TEXT NOT NULL,
                ProvenanceScope TEXT NOT NULL,
                DeliveryPolicy TEXT NOT NULL,
                State TEXT NOT NULL,
                MessageUid TEXT NOT NULL,
                AttachmentIdsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT NULL,
                LeaseOwner TEXT NULL,
                LeaseExpiresAt TEXT NULL,
                ErrorCode TEXT NULL,
                ErrorMessage TEXT NULL,
                ErrorRetryable INTEGER NOT NULL DEFAULT 0,
                IdempotencyKey TEXT NULL,
                IdempotencyFingerprint TEXT NOT NULL,
                DeliveredMessageUid TEXT NULL,
                CompletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SessionQueuedInputs_Active
                ON SessionQueuedInputs(SessionId, State, Sequence);
            CREATE INDEX IF NOT EXISTS IX_SessionQueuedInputs_Due
                ON SessionQueuedInputs(State, NextAttemptAt);
            CREATE INDEX IF NOT EXISTS IX_SessionQueuedInputs_Prompt
                ON SessionQueuedInputs(SessionId, MessageUid);
            CREATE TABLE IF NOT EXISTS SessionPromptCallbacks (
                SessionId TEXT NOT NULL, CallbackId TEXT NOT NULL, EntryJson TEXT NOT NULL,
                PRIMARY KEY(SessionId, CallbackId)
            );
            CREATE TABLE IF NOT EXISTS SessionInputRecovery (ItemId TEXT PRIMARY KEY NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionQueuedInputs_Idempotency
                ON SessionQueuedInputs(SessionId, OwnerUserId, ProvenanceScope, IdempotencyKey)
                WHERE IdempotencyKey IS NOT NULL;
            CREATE TABLE IF NOT EXISTS SessionTurns (
                SessionId TEXT NOT NULL,
                TurnId TEXT NOT NULL,
                OpenedAt TEXT NOT NULL,
                ActiveSeen INTEGER NOT NULL DEFAULT 0,
                EndedAt TEXT NULL,
                Status TEXT NULL,
                StopReason TEXT NULL,
                Outcome TEXT NULL,
                BackgroundTasksRunning INTEGER NOT NULL DEFAULT 0,
                InterruptSource TEXT NULL,
                InterruptedByMessageUid TEXT NULL,
                InterruptRequestedAt TEXT NULL,
                PRIMARY KEY(SessionId, TurnId)
            );
            CREATE INDEX IF NOT EXISTS IX_SessionTurns_Open ON SessionTurns(SessionId, EndedAt);
            CREATE TABLE IF NOT EXISTS SessionQueueCounters (Name TEXT PRIMARY KEY NOT NULL, Value INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();

        // Columns added after the table first shipped. SQLite has no ADD COLUMN IF NOT EXISTS.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText = "PRAGMA table_info(SessionQueuedInputs)";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (column, type) in new[]
        {
            ("RequestedDeliveryPolicy", "TEXT NULL"), ("DeliveryReason", "TEXT NULL"),
            ("DeliveryOrdinal", "INTEGER NULL"), ("TurnId", "TEXT NULL"), ("InterruptResult", "TEXT NULL"),
        })
        {
            if (columns.Contains(column)) continue;
            cmd.CommandText = $"ALTER TABLE SessionQueuedInputs ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }
    }

    // ===== Turn ledger =====================================================================
    // Writes are idempotent and safe from any observer; this per-process cache only spares the
    // database the session.updated stream (token and title updates fire many times per turn).
    private enum TurnState { Opening, Active, Closed }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TurnState> _turnStates = new(StringComparer.Ordinal);

    /// <summary>
    /// The session is running a turn. The turn a delivery opened is now seen running; with no open
    /// turn, the provider started one of its own (unprompted) and it is recorded under a fresh id.
    /// </summary>
    internal void MarkTurnActive(string sessionId)
    {
        if (_turnStates.TryGetValue(sessionId, out var state) && state == TurnState.Active) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE SessionTurns SET ActiveSeen=1 WHERE SessionId=$sessionId AND EndedAt IS NULL";
        Add(update, "$sessionId", sessionId);
        if (update.ExecuteNonQuery() == 0)
            InsertTurnAsync(connection, transaction, sessionId, "unprompted:" + Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow, activeSeen: true, default).GetAwaiter().GetResult();
        transaction.Commit();
        _turnStates[sessionId] = TurnState.Active;
    }

    /// <summary>
    /// The session left Active: close its running turn with the state it ended in. The provider's
    /// outcome counts only if it was reported after the turn opened. Without one, a turn RedCompute
    /// asked to interrupt is interrupted (the provider process was replaced, or the provider does not
    /// report outcomes), a stopped or failed session says so, and anything else stays unknown.
    /// A turn opened by a delivery the provider has not started yet is left open.
    /// </summary>
    internal void CloseTurn(string sessionId, SessionTurnEnd end)
    {
        if (_turnStates.TryGetValue(sessionId, out var state) && state == TurnState.Closed) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT * FROM SessionTurns WHERE SessionId=$sessionId AND EndedAt IS NULL AND ActiveSeen=1";
        Add(select, "$sessionId", sessionId);
        var open = ReadTurns(select).FirstOrDefault();
        if (open is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var providerOutcome = end.ProviderOutcome is not null && end.ProviderEndedAt is { } at && at >= open.OpenedAt
                ? end.ProviderOutcome : null;
            var interruptRequested = open.InterruptSource is not null;
            var outcome = providerOutcome
                ?? (interruptRequested ? SessionTurnOutcomes.Interrupted
                    : end.Status == nameof(SessionStatus.Error) ? SessionTurnOutcomes.Failed
                    : end.Status is nameof(SessionStatus.Stopped) or "Ended" ? SessionTurnOutcomes.Stopped
                    : null);
            var close = connection.CreateCommand();
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE SessionTurns SET EndedAt=$now, Status=$status, StopReason=$stopReason, Outcome=$outcome,
                    BackgroundTasksRunning=$background
                WHERE SessionId=$sessionId AND TurnId=$turnId AND EndedAt IS NULL
                """;
            Add(close, "$now", now.ToString("O")); Add(close, "$status", end.Status);
            AddNullable(close, "$stopReason", end.StopReason); AddNullable(close, "$outcome", outcome);
            Add(close, "$background", end.BackgroundTasksRunning);
            Add(close, "$sessionId", sessionId); Add(close, "$turnId", open.TurnId);
            close.ExecuteNonQuery();
        }
        transaction.Commit();
        if (open is not null) _lastEnded.TryRemove(sessionId, out _);
        // A delivery opened but not yet started keeps its turn open.
        if (open is not null || !HasOpenTurn(sessionId)) _turnStates[sessionId] = TurnState.Closed;
    }

    private bool HasOpenTurn(string sessionId)
    {
        using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SessionTurns WHERE SessionId=$sessionId AND EndedAt IS NULL LIMIT 1";
        Add(command, "$sessionId", sessionId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Records that RedCompute is asking the running turn to stop, and for whom, before it asks, so
    /// a turn that ends at once is still attributed. The first request on a turn is the one that ends
    /// it; later ones change nothing. Returns the turn when this call recorded it, for
    /// <see cref="ClearInterruptRequest"/> if the provider then reports nothing was interrupted.
    /// </summary>
    internal string? MarkInterruptRequested(string sessionId, string source, string? messageUid)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var turnId = OpenTurnIdAsync(connection, transaction, sessionId, default).GetAwaiter().GetResult();
        if (turnId is null)
        {
            turnId = "unprompted:" + Guid.NewGuid().ToString("N");
            InsertTurnAsync(connection, transaction, sessionId, turnId, DateTimeOffset.UtcNow, activeSeen: true, default)
                .GetAwaiter().GetResult();
            _turnStates[sessionId] = TurnState.Active;
        }
        var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            UPDATE SessionTurns SET InterruptSource=$source, InterruptedByMessageUid=$messageUid, InterruptRequestedAt=$now
            WHERE SessionId=$sessionId AND TurnId=$turnId AND InterruptSource IS NULL
            """;
        Add(mark, "$source", source); AddNullable(mark, "$messageUid", messageUid);
        Add(mark, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(mark, "$sessionId", sessionId); Add(mark, "$turnId", turnId);
        var recorded = mark.ExecuteNonQuery() == 1;
        transaction.Commit();
        return recorded ? turnId : null;
    }

    internal void ClearInterruptRequest(string sessionId, string turnId)
    {
        using var connection = Open();
        var clear = connection.CreateCommand();
        clear.CommandText = "UPDATE SessionTurns SET InterruptSource=NULL, InterruptedByMessageUid=NULL, InterruptRequestedAt=NULL WHERE SessionId=$sessionId AND TurnId=$turnId AND EndedAt IS NULL";
        Add(clear, "$sessionId", sessionId); Add(clear, "$turnId", turnId);
        clear.ExecuteNonQuery();
    }

    internal SessionTurnRecord? GetTurn(string sessionId, string turnId)
    {
        using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionTurns WHERE SessionId=$sessionId AND TurnId=$turnId";
        Add(command, "$sessionId", sessionId); Add(command, "$turnId", turnId);
        return ReadTurns(command).FirstOrDefault();
    }

    // Last ended turn per session for the read and event overlay; dropped whenever a turn closes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionTurnRecord?> _lastEnded = new(StringComparer.Ordinal);

    /// <summary>The session's most recently ended turn, cached until the next turn closes.</summary>
    internal SessionTurnRecord? LastEndedTurn(string sessionId)
        => _lastEnded.GetOrAdd(sessionId, GetLastEndedTurn);

    /// <summary>The session's most recently ended turn.</summary>
    internal SessionTurnRecord? GetLastEndedTurn(string sessionId)
    {
        using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SessionTurns WHERE SessionId=$sessionId AND EndedAt IS NOT NULL ORDER BY EndedAt DESC, OpenedAt DESC LIMIT 1";
        Add(command, "$sessionId", sessionId);
        return ReadTurns(command).FirstOrDefault();
    }

    internal async Task SetInterruptResultAsync(string itemId, string result, CancellationToken ct = default)
    {
        await using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE SessionQueuedInputs SET InterruptResult=$result WHERE Id=$id AND InterruptResult IS NULL";
        Add(command, "$result", result); Add(command, "$id", itemId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> OpenTurnIdAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TurnId FROM SessionTurns WHERE SessionId=$sessionId AND EndedAt IS NULL ORDER BY OpenedAt DESC LIMIT 1";
        Add(command, "$sessionId", sessionId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task InsertTurnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, string turnId, DateTimeOffset now, bool activeSeen, CancellationToken ct)
    {
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO SessionTurns (SessionId, TurnId, OpenedAt, ActiveSeen)
            VALUES ($sessionId, $turnId, $now, $activeSeen)
            """;
        Add(insert, "$sessionId", sessionId); Add(insert, "$turnId", turnId);
        Add(insert, "$now", now.ToString("O")); Add(insert, "$activeSeen", activeSeen ? 1 : 0);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A batch is handed to an idle session: it opens that session's next turn. Any turn still open
    /// either never started (its delivery was not taken) and is dropped, or ended without a recorded
    /// close and is closed now with no outcome rather than left to absorb this turn.
    /// </summary>
    private static async Task OpenDeliveredTurnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, string turnId, DateTimeOffset now, CancellationToken ct)
    {
        var stale = connection.CreateCommand();
        stale.Transaction = transaction;
        stale.CommandText = """
            DELETE FROM SessionTurns WHERE SessionId=$sessionId AND EndedAt IS NULL AND ActiveSeen=0;
            UPDATE SessionTurns SET EndedAt=$now WHERE SessionId=$sessionId AND EndedAt IS NULL;
            """;
        Add(stale, "$sessionId", sessionId); Add(stale, "$now", now.ToString("O"));
        await stale.ExecuteNonQueryAsync(ct);
        await InsertTurnAsync(connection, transaction, sessionId, turnId, now, activeSeen: false, ct);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive delivery ordinals. They are global, never reused
    /// (a counter row, not MAX over rows that cleanup deletes) and start above every sequence ever
    /// issued, so they extend the order of inputs delivered before ordinals existed.
    /// </summary>
    private static async Task<long> NextDeliveryOrdinalAsync(SqliteConnection connection, SqliteTransaction transaction,
        int count, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO SessionQueueCounters (Name, Value) VALUES ('delivery', 0);
            UPDATE SessionQueueCounters
            SET Value = max(Value, COALESCE((SELECT seq FROM sqlite_sequence WHERE name='SessionQueuedInputs'), 0)) + $count
            WHERE Name='delivery';
            SELECT Value FROM SessionQueueCounters WHERE Name='delivery';
            """;
        Add(command, "$count", count);
        var last = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        return last - count + 1;
    }

    private static IReadOnlyList<SessionTurnRecord> ReadTurns(SqliteCommand command)
    {
        var turns = new List<SessionTurnRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? Text(string name) { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal); }
            turns.Add(new SessionTurnRecord(
                Text("SessionId")!, Text("TurnId")!, DateTimeOffset.Parse(Text("OpenedAt")!),
                reader.GetInt32(reader.GetOrdinal("ActiveSeen")) != 0,
                Text("EndedAt") is { } ended ? DateTimeOffset.Parse(ended) : null,
                Text("Status"), Text("StopReason"), Text("Outcome"),
                reader.GetInt32(reader.GetOrdinal("BackgroundTasksRunning")),
                Text("InterruptSource"), Text("InterruptedByMessageUid")));
        }
        return turns;
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
