using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public sealed record StagedInputAttachment(
    string Id, string Kind, string Name, string MediaType, long Size, string Sha256,
    string StoredPath, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    string OwnerUserId, string? ClaimedSessionId, string? ClaimedMessageUid)
{
    public InputAttachment ToProviderAttachment() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        MediaType = MediaType,
        Size = Size,
        Sha256 = Sha256,
        StoredPath = StoredPath,
        DownloadUrl = $"/ai-session/input-attachments/{Uri.EscapeDataString(Id)}",
    };
}

public sealed class AttachmentStoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Durable staged bytes for provider input. Paths are generated IDs under LocalAppData;
/// user filenames are display metadata only and never participate in storage paths.</summary>
public sealed class InputAttachmentStore
{
    private static readonly Regex MediaTypePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]{0,126}/[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]{0,126}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> NativeImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    private readonly string _root;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly RedComputeConfig _config;

    public InputAttachmentStore(RedComputeConfig config, string? root = null, string? databasePath = null)
    {
        _config = config;
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "input-attachments"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.GetFullPath(databasePath ?? Path.Combine(_root, "attachments.db"));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString();
        Initialize();
    }

    public string Root => _root;
    internal string DatabasePath => _databasePath;

    public async Task<StagedInputAttachment> UploadAsync(
        Stream source, string? fileName, string? mediaType, string ownerUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new AttachmentStoreException("unauthorized", "Authentication is required to upload attachments");

        var name = SanitizeFileName(fileName);
        var normalizedMediaType = NormalizeMediaType(mediaType);
        var id = "att_" + Guid.NewGuid().ToString("N");
        var shard = id.Substring(4, 2);
        var directory = Path.Combine(_root, shard);
        Directory.CreateDirectory(directory);
        var storedPath = Path.GetFullPath(Path.Combine(directory, id + StorageExtension(normalizedMediaType)));
        EnsureUnderRoot(storedPath);
        var tempPath = storedPath + ".upload";

        long size = 0;
        string sha256;
        byte[] sniff = new byte[16];
        var sniffLength = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > _config.InputAttachmentMaxFileSizeBytes)
                        throw new AttachmentStoreException("attachment_too_large",
                            $"Attachment exceeds the {_config.InputAttachmentMaxFileSizeBytes} byte limit");
                    var copy = Math.Min(read, sniff.Length - sniffLength);
                    if (copy > 0)
                    {
                        Buffer.BlockCopy(buffer, 0, sniff, sniffLength, copy);
                        sniffLength += copy;
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
            }

            if (size == 0)
                throw new AttachmentStoreException("empty_attachment", "Attachments cannot be empty");
            ValidateImageSignature(normalizedMediaType, sniff.AsSpan(0, sniffLength));
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            File.Move(tempPath, storedPath);
            File.SetAttributes(storedPath, File.GetAttributes(storedPath) | FileAttributes.ReadOnly);
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(storedPath);
            throw;
        }

        var createdAt = DateTimeOffset.UtcNow;
        var expiresAt = createdAt.AddMinutes(_config.InputAttachmentTtlMinutes);
        var kind = NativeImageTypes.Contains(normalizedMediaType) ? "image" : "file";
        var record = new StagedInputAttachment(id, kind, name, normalizedMediaType, size, sha256,
            storedPath, createdAt, expiresAt, ownerUserId, null, null);

        try
        {
            await using var connection = Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO InputAttachments
                    (Id, Kind, Name, MediaType, Size, Sha256, StoredPath, CreatedAt, ExpiresAt, OwnerUserId)
                VALUES
                    ($id, $kind, $name, $mediaType, $size, $sha256, $storedPath, $createdAt, $expiresAt, $ownerUserId)
                """;
            Add(cmd, "$id", id); Add(cmd, "$kind", kind); Add(cmd, "$name", name);
            Add(cmd, "$mediaType", normalizedMediaType); Add(cmd, "$size", size); Add(cmd, "$sha256", sha256);
            Add(cmd, "$storedPath", storedPath); Add(cmd, "$createdAt", createdAt.ToString("O"));
            Add(cmd, "$expiresAt", expiresAt.ToString("O")); Add(cmd, "$ownerUserId", ownerUserId);
            await cmd.ExecuteNonQueryAsync(ct);
            return record;
        }
        catch
        {
            TryDelete(storedPath);
            throw;
        }
    }

    public Task<StagedInputAttachment> UploadBytesAsync(
        byte[] bytes, string? fileName, string? mediaType, string ownerUserId, CancellationToken ct = default) =>
        UploadAsync(new MemoryStream(bytes, writable: false), fileName, mediaType, ownerUserId, ct);

    public async Task<IReadOnlyList<StagedInputAttachment>> ClaimAsync(
        IReadOnlyList<string> ids, string ownerUserId, string sessionId, string messageUid,
        CancellationToken ct = default)
    {
        var distinct = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != ids.Count)
            throw new AttachmentStoreException("duplicate_attachment", "An attachment can appear only once in a turn");
        if (distinct.Length > _config.InputAttachmentMaxCount)
            throw new AttachmentStoreException("too_many_attachments",
                $"A turn can contain at most {_config.InputAttachmentMaxCount} attachments");

        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var records = new List<StagedInputAttachment>(distinct.Length);
        foreach (var id in distinct)
        {
            var record = await GetCoreAsync(connection, id, ct)
                ?? throw new AttachmentStoreException("attachment_not_found", $"Attachment '{id}' was not found");
            Authorize(record, ownerUserId);
            if (record.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                throw new AttachmentStoreException("attachment_expired", $"Attachment '{id}' has expired");
            if (record.ClaimedSessionId is not null
                && (record.ClaimedSessionId != sessionId || record.ClaimedMessageUid != messageUid))
                throw new AttachmentStoreException("attachment_already_claimed", $"Attachment '{id}' has already been sent");
            if (!File.Exists(record.StoredPath))
                throw new AttachmentStoreException("attachment_missing", $"Attachment '{id}' is no longer available");
            records.Add(record);
        }

        if (records.Sum(r => r.Size) > _config.InputAttachmentMaxTurnSizeBytes)
            throw new AttachmentStoreException("attachments_too_large",
                $"Turn attachments exceed the {_config.InputAttachmentMaxTurnSizeBytes} byte total limit");

        foreach (var record in records)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE InputAttachments
                SET ClaimedSessionId = $sessionId, ClaimedMessageUid = $messageUid, ExpiresAt = NULL
                WHERE Id = $id AND (ClaimedSessionId IS NULL OR (ClaimedSessionId = $sessionId AND ClaimedMessageUid = $messageUid))
                """;
            Add(update, "$sessionId", sessionId); Add(update, "$messageUid", messageUid); Add(update, "$id", record.Id);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new AttachmentStoreException("attachment_already_claimed", $"Attachment '{record.Id}' was claimed concurrently");
        }
        await transaction.CommitAsync(ct);
        return records.Select(r => r with { ClaimedSessionId = sessionId, ClaimedMessageUid = messageUid, ExpiresAt = null }).ToArray();
    }

    public async Task ReleaseClaimAsync(string sessionId, string messageUid, CancellationToken ct = default)
    {
        await using var connection = Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE InputAttachments
            SET ClaimedSessionId = NULL, ClaimedMessageUid = NULL, ExpiresAt = $expiresAt
            WHERE ClaimedSessionId = $sessionId AND ClaimedMessageUid = $messageUid
            """;
        Add(cmd, "$expiresAt", DateTimeOffset.UtcNow.AddMinutes(_config.InputAttachmentTtlMinutes).ToString("O"));
        Add(cmd, "$sessionId", sessionId); Add(cmd, "$messageUid", messageUid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StagedInputAttachment?> GetAuthorizedAsync(string id, string ownerUserId, CancellationToken ct = default)
    {
        await using var connection = Open();
        var record = await GetCoreAsync(connection, id, ct);
        if (record is null) return null;
        Authorize(record, ownerUserId);
        if (record.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new AttachmentStoreException("attachment_expired", $"Attachment '{id}' has expired");
        return record;
    }

    public async Task DeleteDraftAsync(string id, string ownerUserId, CancellationToken ct = default)
    {
        await using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var record = await GetCoreAsync(connection, id, ct)
            ?? throw new AttachmentStoreException("attachment_not_found", $"Attachment '{id}' was not found");
        Authorize(record, ownerUserId);
        if (record.ClaimedSessionId is not null)
            throw new AttachmentStoreException("attachment_claimed", "Sent attachments cannot be deleted as drafts");
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM InputAttachments WHERE Id = $id AND ClaimedSessionId IS NULL";
        Add(cmd, "$id", id);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new AttachmentStoreException("attachment_claimed", "The attachment was claimed concurrently");
        await transaction.CommitAsync(ct);
        TryDelete(record.StoredPath);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        var select = connection.CreateCommand();
        select.CommandText = "SELECT StoredPath FROM InputAttachments WHERE ClaimedSessionId IS NULL AND ExpiresAt <= $now";
        Add(select, "$now", DateTimeOffset.UtcNow.ToString("O"));
        var paths = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) paths.Add(reader.GetString(0));
        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM InputAttachments WHERE ClaimedSessionId IS NULL AND ExpiresAt <= $now";
        Add(delete, "$now", DateTimeOffset.UtcNow.ToString("O"));
        var count = await delete.ExecuteNonQueryAsync(ct);
        foreach (var path in paths) TryDelete(path);
        return count;
    }

    public static string SanitizeFileName(string? fileName)
    {
        var leaf = Path.GetFileName((fileName ?? "attachment").Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(leaf.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or "..") clean = "attachment";
        return clean.Length <= 255 ? clean : clean[..255];
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        var normalized = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return "application/octet-stream";
        if (!MediaTypePattern.IsMatch(normalized))
            throw new AttachmentStoreException("invalid_media_type", $"Invalid media type '{mediaType}'");
        return normalized;
    }

    private static void ValidateImageSignature(string mediaType, ReadOnlySpan<byte> bytes)
    {
        if (!NativeImageTypes.Contains(mediaType)) return;
        var valid = mediaType switch
        {
            "image/png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid)
            throw new AttachmentStoreException("media_type_mismatch", $"File bytes do not match declared media type '{mediaType}'");
    }

    private static string StorageExtension(string mediaType) => mediaType switch
    {
        "application/json" or "application/ld+json" => ".json",
        "application/pdf" => ".pdf",
        "application/rtf" => ".rtf",
        "application/sql" => ".sql",
        "application/xml" or "text/xml" => ".xml",
        "application/zip" => ".zip",
        "application/gzip" => ".gz",
        "application/x-tar" => ".tar",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "text/plain" => ".txt",
        "text/csv" => ".csv",
        "text/html" => ".html",
        "text/css" => ".css",
        "text/markdown" => ".md",
        "text/javascript" or "application/javascript" => ".js",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin",
    };

    private void EnsureUnderRoot(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new AttachmentStoreException("invalid_storage_path", "Generated attachment path escaped the attachment root");
    }

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
            CREATE TABLE IF NOT EXISTS InputAttachments (
                Id TEXT PRIMARY KEY,
                Kind TEXT NOT NULL,
                Name TEXT NOT NULL,
                MediaType TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Sha256 TEXT NOT NULL,
                StoredPath TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NULL,
                OwnerUserId TEXT NOT NULL,
                ClaimedSessionId TEXT NULL,
                ClaimedMessageUid TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_InputAttachments_Expiry ON InputAttachments(ExpiresAt);
            CREATE INDEX IF NOT EXISTS IX_InputAttachments_Claim ON InputAttachments(ClaimedSessionId, ClaimedMessageUid);
            """;
        cmd.ExecuteNonQuery();
    }

    private static async Task<StagedInputAttachment?> GetCoreAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Kind, Name, MediaType, Size, Sha256, StoredPath, CreatedAt, ExpiresAt,
                   OwnerUserId, ClaimedSessionId, ClaimedMessageUid
            FROM InputAttachments WHERE Id = $id
            """;
        Add(cmd, "$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StagedInputAttachment(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static void Authorize(StagedInputAttachment record, string ownerUserId)
    {
        if (!string.Equals(record.OwnerUserId, ownerUserId, StringComparison.Ordinal))
            throw new AttachmentStoreException("forbidden", "You do not have access to this attachment");
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { }
    }
}
