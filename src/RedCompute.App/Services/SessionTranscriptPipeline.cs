using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Owns the suite transcript representation for every session provider.
/// Providers may keep their local resume stores, but payload persistence and
/// browser transport are decided here exactly once.
/// </summary>
public sealed class SessionTranscriptPipeline
{
    internal const string StreamEventType = "session.stream";
    internal const int MaxImagePayloadBytes = 25 * 1024 * 1024;
    internal const long MaxImagePayloadBase64Chars = ((MaxImagePayloadBytes + 2L) / 3L * 4L) + 4L;

    private static readonly TimeSpan DedupLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions AttachmentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly RedLeafStreamClient _streams;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly Action<string, Guid?> _log;
    private readonly Func<string, string, bool> _isConfidential;
    private readonly ITranscriptSequenceStore _sequences;
    private readonly CompletedTranscriptPublisher _completed;
    internal TranscriptCheckpointCoordinator Checkpoints { get; } = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _livePayloads = new();

    public SessionTranscriptPipeline(
        RedLeafStreamClient streams,
        WebSocketBroadcaster broadcaster,
        Action<string, Guid?> log,
        Func<string, string, bool>? isConfidential = null)
        : this(streams, broadcaster, log, isConfidential, new TranscriptSequenceStore())
    {
    }

    internal SessionTranscriptPipeline(
        RedLeafStreamClient streams,
        WebSocketBroadcaster broadcaster,
        Action<string, Guid?> log,
        Func<string, string, bool>? isConfidential,
        ITranscriptSequenceStore sequences)
    {
        _streams = streams;
        _broadcaster = broadcaster;
        _log = log;
        _isConfidential = isConfidential ?? ((_, _) => false);
        _sequences = sequences;
        _completed = new CompletedTranscriptPublisher(Checkpoints, sequences, PersistCompletedMessage, Broadcast);
    }

    public void HandleLiveEvent(string provider, string sessionId, UnifiedStreamEvent evt)
    {
        // Partial tool output is intentionally not transported. The provider's
        // completed event contains the authoritative output and becomes one
        // durable payload plus one lightweight completion event.
        if (evt.Type == "tool_result" && evt.IsPartial)
            return;

        var stamp = _sequences.Next(provider, sessionId);
        evt = WithStamp(evt, stamp);

        if (evt.Type == "image")
        {
            try
            {
                var image = DecodeImage(evt.Attachments);
                var payloadRef = PersistSynchronously(() => PersistImageAsync(
                    provider, sessionId, "assistant", evt.Content, evt.MessageId, evt.MessageUid,
                    DateTimeOffset.UtcNow, image, stamp));

                _livePayloads[Fingerprint(provider, sessionId, evt.MessageUid, evt.MessageId, image.Bytes)] = DateTimeOffset.UtcNow;
                Broadcast(provider, sessionId, WithPayload(evt, payloadRef));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException)
            {
                _log($"[Transcript] Rejected image payload for {provider}/{sessionId}: {ex.Message}", null);
                Broadcast(provider, sessionId, ImageStorageError(evt));
            }
            catch (Exception ex)
            {
                _log($"[Transcript] Image append failed for {provider}/{sessionId}: {ex.Message}", null);
                Broadcast(provider, sessionId, ImageStorageError(evt));
            }
            return;
        }

        if (evt.Type != "tool_result" || Output(evt) is not { Length: > 0 } output)
        {
            Broadcast(provider, sessionId, evt);
            return;
        }

        try
        {
            var payloadRef = PersistSynchronously(() => PersistToolOutputAsync(
                    provider, sessionId, "assistant", evt.ToolName,
                    evt.ToolInput?.ToString(), evt.MessageId, evt.MessageUid,
                    DateTimeOffset.UtcNow,
                    evt.Attachments is { Count: > 0 } ? JsonSerializer.Serialize(evt.Attachments) : null,
                    output,
                    stamp));

            _livePayloads[Fingerprint(provider, sessionId, evt.MessageUid, evt.MessageId, output)] = DateTimeOffset.UtcNow;
            Broadcast(provider, sessionId, WithPayload(evt, payloadRef));
        }
        catch (Exception ex)
        {
            _log($"[Transcript] Payload append failed for {provider}/{sessionId}; using inline fallback: {ex.Message}", null);
            Broadcast(provider, sessionId, evt);
        }
    }

    public void MirrorCompletedMessages(IReadOnlyList<AiMessageSnapshot> messages) => _completed.Publish(messages);

    private TranscriptPayloadRef? PersistCompletedMessage(AiMessageSnapshot message, TranscriptStamp stamp)
    {
        if (message.EventType == "image")
        {
            var image = DecodeImage(message.AttachmentsJson);
            return PersistSynchronously(() => PersistImageAsync(
                message.Provider, message.SessionId, message.Role, message.Content,
                message.MessageId, message.MessageUid, message.Timestamp, image, stamp));
        }

        var output = message.EventType == "tool_result" ? message.ToolResult ?? message.Content : null;
        if (!string.IsNullOrEmpty(output))
            return PersistSynchronously(() => PersistToolOutputAsync(message.Provider, message.SessionId, message.Role,
                message.ToolName, message.ToolInput, message.MessageId, message.MessageUid,
                message.Timestamp, message.AttachmentsJson, output, stamp));

        PersistSynchronously(() => _streams.AppendForEntityAsync("session-messages", SessionEntitySlug(message.Provider, message.SessionId),
            InlineData(message, stamp), $"transcript:{stamp.Epoch}:{stamp.Sequence}",
            createdAt: message.Timestamp));
        return null;
    }

    public void MirrorMessages(IReadOnlyList<AiMessageSnapshot> messages)
    {
        PruneDedupEntries();
        foreach (var message in messages)
        {
            if (message.EventType == "image")
            {
                try
                {
                    var image = DecodeImage(message.AttachmentsJson);
                    var fingerprint = Fingerprint(
                        message.Provider, message.SessionId, message.MessageUid, message.MessageId, image.Bytes);
                    if (_livePayloads.TryRemove(fingerprint, out _))
                        continue;

                    PersistSynchronously(() => PersistImageAsync(
                        message.Provider, message.SessionId, message.Role, message.Content,
                        message.MessageId, message.MessageUid, message.Timestamp, image,
                        _sequences.Next(message.Provider, message.SessionId)));
                    continue;
                }
                catch (Exception ex)
                {
                    _log($"[Transcript] Image mirror failed for {message.Provider}/{message.SessionId}: {ex.Message}", null);
                    EnqueueInline(SanitizeImage(message), _sequences.Next(message.Provider, message.SessionId));
                    continue;
                }
            }

            var output = message.EventType == "tool_result"
                ? message.ToolResult ?? message.Content
                : null;

            if (!string.IsNullOrEmpty(output))
            {
                var fingerprint = Fingerprint(
                    message.Provider, message.SessionId, message.MessageUid, message.MessageId, output);
                if (_livePayloads.TryRemove(fingerprint, out _))
                    continue;

                try
                {
                    PersistSynchronously(() => PersistToolOutputAsync(
                            message.Provider, message.SessionId, message.Role,
                            message.ToolName, message.ToolInput, message.MessageId,
                            message.MessageUid, message.Timestamp, message.AttachmentsJson, output,
                            _sequences.Next(message.Provider, message.SessionId)));
                    continue;
                }
                catch (Exception ex)
                {
                    _log($"[Transcript] Payload mirror failed for {message.Provider}/{message.SessionId}; retaining inline output: {ex.Message}", null);
                }
            }

            EnqueueInline(message, _sequences.Next(message.Provider, message.SessionId));
        }
    }

    // Provider events may arrive on the WPF dispatcher. Start asynchronous I/O on
    // the pool before waiting, so its continuations never need the blocked caller.
    // The wait still preserves the durable checkpoint-before-publication contract.
    internal static void PersistSynchronously(Func<Task> persist) =>
        Task.Run(persist).GetAwaiter().GetResult();

    internal static T PersistSynchronously<T>(Func<Task<T>> persist) =>
        Task.Run(persist).GetAwaiter().GetResult();

    private async Task<TranscriptPayloadRef> PersistToolOutputAsync(
        string provider,
        string sessionId,
        string role,
        string? toolName,
        string? toolInput,
        string? messageId,
        string? messageUid,
        DateTimeOffset timestamp,
        string? attachmentsJson,
        string output,
        TranscriptStamp stamp)
    {
        var bytes = Encoding.UTF8.GetBytes(output);
        var descriptor = await _streams.AppendPayloadForEntityAsync(
            "session-messages",
            SessionEntitySlug(provider, sessionId),
            new
            {
                provider,
                session_id = sessionId,
                role,
                event_type = "tool_result",
                tool_name = toolName,
                tool_input = toolInput,
                message_id = messageId,
                message_uid = messageUid,
                epoch = stamp.Epoch,
                sequence = stamp.Sequence,
                timestamp = timestamp.ToString("O"),
                attachments_json = attachmentsJson,
                payload_kind = "tool-output",
            },
            bytes,
            "text/plain; charset=utf-8",
            "utf-8",
            createdAt: timestamp);

        return new TranscriptPayloadRef
        {
            RecordId = descriptor.RecordId,
            Kind = "tool-output",
            Length = descriptor.Length,
            ContentType = descriptor.ContentType,
            Encoding = descriptor.Encoding,
            Sha256 = descriptor.Sha256,
            Available = descriptor.Available,
        };
    }

    private async Task<TranscriptPayloadRef> PersistImageAsync(
        string provider,
        string sessionId,
        string role,
        string? content,
        string? messageId,
        string? messageUid,
        DateTimeOffset timestamp,
        ImagePayload image,
        TranscriptStamp stamp)
    {
        var descriptor = await _streams.AppendPayloadForEntityAsync(
            "session-messages",
            SessionEntitySlug(provider, sessionId),
            new
            {
                provider,
                session_id = sessionId,
                role,
                event_type = "image",
                content,
                message_id = messageId,
                message_uid = messageUid,
                epoch = stamp.Epoch,
                sequence = stamp.Sequence,
                timestamp = timestamp.ToString("O"),
                payload_kind = "assistant-image",
            },
            image.Bytes,
            image.ContentType,
            "binary",
            createdAt: timestamp);

        return new TranscriptPayloadRef
        {
            RecordId = descriptor.RecordId,
            Kind = "assistant-image",
            Length = descriptor.Length,
            ContentType = descriptor.ContentType,
            Encoding = descriptor.Encoding,
            Sha256 = descriptor.Sha256,
            Available = descriptor.Available,
        };
    }

    private void EnqueueInline(AiMessageSnapshot message, TranscriptStamp stamp) =>
        _streams.EnqueueForEntity("session-messages", SessionEntitySlug(message.Provider, message.SessionId), InlineData(message, stamp));

    private static object InlineData(AiMessageSnapshot message, TranscriptStamp stamp) => new
        {
            provider = message.Provider,
            session_id = message.SessionId,
            role = message.Role,
            event_type = message.EventType,
            content = message.Content,
            tool_name = message.ToolName,
            tool_input = message.ToolInput,
            tool_result = message.ToolResult,
            message_id = message.MessageId,
            phase = message.Phase,
            message_uid = message.MessageUid,
            epoch = stamp.Epoch,
            sequence = stamp.Sequence,
            timestamp = message.Timestamp.ToString("O"),
            attachments_json = message.AttachmentsJson,
        };

    private void Broadcast(string provider, string sessionId, UnifiedStreamEvent evt)
    {
        if (_isConfidential(provider, sessionId))
        {
            _broadcaster.Broadcast("ai-session.changed", new
            {
                provider,
                sessionId,
                confidential = true,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            return;
        }

        _broadcaster.Broadcast(StreamEventType, new
        {
            provider,
            sessionId,
            @event = evt,
            // RedLeaf merges this stream with its own ambient event stream.
            // Stamp observation at the source so browser receipt races cannot
            // rewrite the visible chronology.
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    internal static UnifiedStreamEvent WithPayload(UnifiedStreamEvent evt, TranscriptPayloadRef payloadRef) => new()
    {
        Type = evt.Type,
        Content = evt.Type == "image" ? evt.Content : null,
        ToolName = evt.ToolName,
        ToolInput = evt.ToolInput,
        ToolResult = null,
        PayloadRef = payloadRef,
        IsPartial = false,
        MessageId = evt.MessageId,
        Phase = evt.Phase,
        MessageUid = evt.MessageUid,
        RequestId = evt.RequestId,
        Epoch = evt.Epoch,
        Sequence = evt.Sequence,
        Attachments = evt.Type == "image" ? null : evt.Attachments,
    };

    private static string? Output(UnifiedStreamEvent evt) => evt.ToolResult ?? evt.Content;

    private static UnifiedStreamEvent WithStamp(UnifiedStreamEvent evt, TranscriptStamp stamp) => new()
    {
        Type = evt.Type,
        Content = evt.Content,
        ToolName = evt.ToolName,
        ToolInput = evt.ToolInput,
        ToolResult = evt.ToolResult,
        PayloadRef = evt.PayloadRef,
        IsPartial = evt.IsPartial,
        MessageId = evt.MessageId,
        Phase = evt.Phase,
        MessageUid = evt.MessageUid,
        RequestId = evt.RequestId,
        Epoch = stamp.Epoch,
        Sequence = stamp.Sequence,
        Attachments = evt.Attachments,
    };

    private static string Fingerprint(
        string provider, string sessionId, string? messageUid, string? messageId, string output)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)));
        return $"{provider}\n{sessionId}\n{messageUid ?? ""}\n{messageId ?? ""}\n{hash}";
    }

    private static string Fingerprint(
        string provider, string sessionId, string? messageUid, string? messageId, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return $"{provider}\n{sessionId}\n{messageUid ?? ""}\n{messageId ?? ""}\n{hash}";
    }

    internal static ImagePayload DecodeImage(string? attachmentsJson)
    {
        if (string.IsNullOrWhiteSpace(attachmentsJson))
            throw new InvalidDataException("The image event has no attachment metadata");

        List<UnifiedAttachment>? attachments;
        try
        {
            attachments = JsonSerializer.Deserialize<List<UnifiedAttachment>>(
                attachmentsJson, AttachmentJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The image attachment metadata is invalid", ex);
        }
        return DecodeImage(attachments);
    }

    internal static ImagePayload DecodeImage(IReadOnlyList<UnifiedAttachment>? attachments)
    {
        var images = attachments?.Where(a => string.Equals(a.Type, "image", StringComparison.OrdinalIgnoreCase)).ToList();
        if (images is not { Count: 1 })
            throw new InvalidDataException("An image event must contain exactly one image attachment");

        var attachment = images[0];
        if (string.IsNullOrWhiteSpace(attachment.Data))
            throw new InvalidDataException("The image attachment has no inline data");
        if (!IsImageBase64LengthWithinLimit(attachment.Data.Length))
            throw new InvalidDataException($"The image exceeds the {MaxImagePayloadBytes}-byte limit");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(attachment.Data);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The image attachment is not valid base64", ex);
        }
        if (bytes.Length == 0 || bytes.Length > MaxImagePayloadBytes)
            throw new InvalidDataException($"The image exceeds the {MaxImagePayloadBytes}-byte limit");

        var detected = DetectImageContentType(bytes)
            ?? throw new InvalidDataException("The image attachment has an unsupported or invalid signature");
        var declared = attachment.MimeType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(declared) && declared != detected)
            throw new InvalidDataException($"The declared image type '{declared}' does not match '{detected}'");

        return new ImagePayload(bytes, detected);
    }

    internal static bool IsImageBase64LengthWithinLimit(long length) =>
        length >= 0 && length <= MaxImagePayloadBase64Chars;

    private static string? DetectImageContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    private static UnifiedStreamEvent ImageStorageError(UnifiedStreamEvent evt) => new()
    {
        Type = "error",
        Content = "The generated image could not be stored safely.",
        MessageId = evt.MessageId,
        MessageUid = evt.MessageUid,
        Epoch = evt.Epoch,
        Sequence = evt.Sequence,
    };

    private static AiMessageSnapshot SanitizeImage(AiMessageSnapshot message) => new()
    {
        Provider = message.Provider,
        SessionId = message.SessionId,
        Role = message.Role,
        EventType = message.EventType,
        Content = message.Content,
        MessageId = message.MessageId,
        ProviderPartId = message.ProviderPartId,
        Phase = message.Phase,
        MessageUid = message.MessageUid,
        Timestamp = message.Timestamp,
    };

    internal sealed record ImagePayload(byte[] Bytes, string ContentType);

    private void PruneDedupEntries()
    {
        var cutoff = DateTimeOffset.UtcNow - DedupLifetime;
        foreach (var (key, createdAt) in _livePayloads)
            if (createdAt < cutoff) _livePayloads.TryRemove(key, out _);
    }

    private static string SessionEntitySlug(string provider, string sessionId)
    {
        var sanitized = new string(sessionId.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"ai-session-{provider}-{sanitized}";
    }
}
