using System.Collections.Concurrent;
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

    private static readonly TimeSpan DedupLifetime = TimeSpan.FromMinutes(10);
    private readonly RedLeafStreamClient _streams;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly Action<string, Guid?> _log;
    private readonly Func<string, string, bool> _isConfidential;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _livePayloads = new();

    public SessionTranscriptPipeline(
        RedLeafStreamClient streams,
        WebSocketBroadcaster broadcaster,
        Action<string, Guid?> log,
        Func<string, string, bool>? isConfidential = null)
    {
        _streams = streams;
        _broadcaster = broadcaster;
        _log = log;
        _isConfidential = isConfidential ?? ((_, _) => false);
    }

    public void HandleLiveEvent(string provider, string sessionId, UnifiedStreamEvent evt)
    {
        // Partial tool output is intentionally not transported. The provider's
        // completed event contains the authoritative output and becomes one
        // durable payload plus one lightweight completion event.
        if (evt.Type == "tool_result" && evt.IsPartial)
            return;

        if (evt.Type != "tool_result" || Output(evt) is not { Length: > 0 } output)
        {
            Broadcast(provider, sessionId, evt);
            return;
        }

        try
        {
            var payloadRef = PersistToolOutputAsync(
                    provider, sessionId, "assistant", evt.ToolName,
                    evt.ToolInput?.ToString(), evt.MessageId, evt.MessageUid,
                    DateTimeOffset.UtcNow,
                    evt.Attachments is { Count: > 0 } ? JsonSerializer.Serialize(evt.Attachments) : null,
                    output)
                .GetAwaiter().GetResult();

            _livePayloads[Fingerprint(provider, sessionId, evt.MessageUid, evt.MessageId, output)] = DateTimeOffset.UtcNow;
            Broadcast(provider, sessionId, new UnifiedStreamEvent
            {
                Type = evt.Type,
                Content = null,
                ToolName = evt.ToolName,
                ToolInput = evt.ToolInput,
                ToolResult = null,
                PayloadRef = payloadRef,
                IsPartial = false,
                MessageId = evt.MessageId,
                MessageUid = evt.MessageUid,
                RequestId = evt.RequestId,
                Attachments = evt.Attachments,
            });
        }
        catch (Exception ex)
        {
            _log($"[Transcript] Payload append failed for {provider}/{sessionId}; using inline fallback: {ex.Message}", null);
            Broadcast(provider, sessionId, evt);
        }
    }

    public void MirrorMessages(IReadOnlyList<AiMessageSnapshot> messages)
    {
        PruneDedupEntries();
        foreach (var message in messages)
        {
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
                    PersistToolOutputAsync(
                            message.Provider, message.SessionId, message.Role,
                            message.ToolName, message.ToolInput, message.MessageId,
                            message.MessageUid, message.Timestamp, message.AttachmentsJson, output)
                        .GetAwaiter().GetResult();
                    continue;
                }
                catch (Exception ex)
                {
                    _log($"[Transcript] Payload mirror failed for {message.Provider}/{message.SessionId}; retaining inline output: {ex.Message}", null);
                }
            }

            EnqueueInline(message);
        }
    }

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
        string output)
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

    private void EnqueueInline(AiMessageSnapshot message) =>
        _streams.EnqueueForEntity("session-messages", SessionEntitySlug(message.Provider, message.SessionId), new
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
            message_uid = message.MessageUid,
            timestamp = message.Timestamp.ToString("O"),
            attachments_json = message.AttachmentsJson,
        });

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

    private static string? Output(UnifiedStreamEvent evt) => evt.ToolResult ?? evt.Content;

    private static string Fingerprint(
        string provider, string sessionId, string? messageUid, string? messageId, string output)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)));
        return $"{provider}\n{sessionId}\n{messageUid ?? ""}\n{messageId ?? ""}\n{hash}";
    }

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
