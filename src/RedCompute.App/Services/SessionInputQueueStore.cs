using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;

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
    DateTimeOffset? CompletedAt);

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
                 ProvenanceScope, DeliveryPolicy, State, MessageUid, AttachmentIdsJson, CreatedAt,
                 UpdatedAt, AttemptCount, IdempotencyKey, IdempotencyFingerprint)
            VALUES
                ($id, $sessionId, $ownerUserId, $inputJson, $displayContent, $metadataJson, $provenanceJson,
                 $provenanceScope, $deliveryPolicy, 'Pending', $messageUid, $attachmentIdsJson, $createdAt,
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
            attachmentIds, now, now, 0, null, null, null, null, false, null, null);
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
            ORDER BY Sequence LIMIT 1
            """;
        Add(headCmd, "$sessionId", sessionId);
        var headItems = await ReadItemsAsync(headCmd, ct);
        var head = headItems.FirstOrDefault();
        if (head is null || head.State == SessionInputQueueState.Failed) return [];
        if (head.State == SessionInputQueueState.Delivering)
        {
            if (head.LeaseExpiresAt is null || head.LeaseExpiresAt > now) return [];
            await MarkUnknownOutcomeAsync(connection, transaction, head.Id, now, ct);
            await transaction.CommitAsync(ct);
            return [];
        }
        if (head.NextAttemptAt is { } next && next > now) return [];

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
        foreach (var item in pending)
        {
            if (!string.Equals(item.ProvenanceScope, head.ProvenanceScope, StringComparison.Ordinal)) break;
            if (item.NextAttemptAt is { } retryAt && retryAt > now) break;
            batch.Add(item);
        }
        if (batch.Count == 0) return [];

        var leaseExpires = now.Add(leaseDuration);
        foreach (var item in batch)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE SessionQueuedInputs
                SET State = 'Delivering', UpdatedAt = $now, AttemptCount = AttemptCount + 1,
                    LeaseOwner = $leaseOwner, LeaseExpiresAt = $leaseExpires
                WHERE Id = $id AND State = 'Pending'
                """;
            Add(update, "$now", now.ToString("O")); Add(update, "$leaseOwner", leaseOwner);
            Add(update, "$leaseExpires", leaseExpires.ToString("O")); Add(update, "$id", item.Id);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new SessionInputQueueStoreException("queue_race", "The queue changed while claiming a delivery batch");
        }
        await transaction.CommitAsync(ct);
        return batch.Select(item => item with
        {
            State = SessionInputQueueState.Delivering,
            UpdatedAt = now,
            AttemptCount = item.AttemptCount + 1,
            LeaseExpiresAt = leaseExpires,
        }).ToArray();
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
            WHERE State = 'Pending' AND (NextAttemptAt IS NULL OR NextAttemptAt <= $now)
            """;
        Add(cmd, "$now", DateTimeOffset.UtcNow.ToString("O"));
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
            WHERE State = 'Delivering' AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= $now)
            """;
        Add(cmd, "$now", DateTimeOffset.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CleanupTerminalAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM SessionQueuedInputs WHERE State IN ('Delivered','Cancelled') AND CompletedAt <= $cutoff";
        Add(cmd, "$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
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
              AND ProvenanceScope = $provenanceScope AND IdempotencyKey = $idempotencyKey
            LIMIT 1
            """;
        Add(cmd, "$sessionId", sessionId); Add(cmd, "$ownerUserId", ownerUserId);
        Add(cmd, "$provenanceScope", provenanceScope); Add(cmd, "$idempotencyKey", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (ReadItem(reader), reader.GetString(reader.GetOrdinal("IdempotencyFingerprint")));
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
            NullableString("DeliveredMessageUid"), NullableDate("CompletedAt"));
    }

    private static string ComputeProvenanceScope(JobProvenance provenance)
    {
        var scope = new
        {
            provenance.Origin.Service,
            App = new { provenance.Origin.App.Kind, provenance.Origin.App.Id, provenance.Origin.App.EntityId },
            Actor = new { provenance.Actor.Kind, provenance.Actor.EntityId, provenance.Actor.Id },
            Beneficiary = new { provenance.OnBehalfOf.Kind, provenance.OnBehalfOf.Id },
            Context = provenance.Context.Select(c => new { c.Kind, c.Id, c.EntityId, c.Route }).ToArray(),
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
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionQueuedInputs_Idempotency
                ON SessionQueuedInputs(SessionId, OwnerUserId, ProvenanceScope, IdempotencyKey)
                WHERE IdempotencyKey IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
