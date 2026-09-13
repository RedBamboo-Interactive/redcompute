using System.Collections.Concurrent;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>Writes one complete native prefix before publishing its tool results.
/// Retries reuse native part identity and transport stamps, including after an
/// ambiguous acknowledgement. Only the current turn's receipts are retained.</summary>
internal sealed class CompletedTranscriptPublisher(
    TranscriptCheckpointCoordinator checkpoints,
    ITranscriptSequenceStore sequences,
    Func<AiMessageSnapshot, TranscriptStamp, TranscriptPayloadRef?> persist,
    Action<string, string, UnifiedStreamEvent> broadcast)
{
    private sealed class Receipt(TranscriptStamp stamp)
    {
        public TranscriptStamp Stamp { get; } = stamp;
        public bool Written { get; set; }
        public bool Notified { get; set; }
        public TranscriptPayloadRef? Payload { get; set; }
    }

    private sealed class SessionState
    {
        public IReadOnlyList<AiMessageSnapshot>? Pending { get; set; }
        public string? TurnUid { get; set; }
        public Dictionary<(string PartId, string Type), Receipt> Receipts { get; } = new();
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public void Publish(IReadOnlyList<AiMessageSnapshot> messages)
    {
        if (messages.Count == 0) return;
        var first = messages[0];
        if (messages.Any(m => m.Provider != first.Provider || m.SessionId != first.SessionId
            || m.MessageUid != first.MessageUid || string.IsNullOrEmpty(m.ProviderPartId)))
            throw new ArgumentException("A completed checkpoint must contain one turn's identified native parts");

        checkpoints.Write(first.SessionId, () =>
        {
            var state = _sessions.GetOrAdd(first.SessionId, _ => new SessionState());
            // A failed earlier prefix must finish before any newer prefix can
            // make canonical history readable again.
            if (state.Pending is { } pending) Write(state, pending);
            Write(state, messages);
        });
    }

    private void Write(SessionState state, IReadOnlyList<AiMessageSnapshot> messages)
    {
        var first = messages[0];
        if (state.TurnUid != first.MessageUid)
        {
            state.Receipts.Clear();
            state.TurnUid = first.MessageUid;
        }
        state.Pending = messages;
        var notifications = new List<(AiMessageSnapshot Message, Receipt Receipt)>();
        foreach (var message in messages)
        {
            var key = (message.ProviderPartId!, message.EventType);
            if (!state.Receipts.TryGetValue(key, out var receipt))
                state.Receipts[key] = receipt = new Receipt(sequences.Next(message.Provider, message.SessionId));
            if (!receipt.Written)
            {
                receipt.Payload = persist(message, receipt.Stamp);
                receipt.Written = true;
            }
            if (message.EventType == "tool_result" && !receipt.Notified) notifications.Add((message, receipt));
        }
        state.Pending = null;
        // These events have exactly their durable record's stamp. An overlapping
        // snapshot therefore consumes them once, whichever arrives first.
        foreach (var (message, receipt) in notifications)
        {
            broadcast(message.Provider, message.SessionId, new UnifiedStreamEvent
            {
                Type = message.EventType,
                Content = receipt.Payload is null ? message.Content : null,
                ToolResult = receipt.Payload is null ? message.ToolResult : null,
                ToolName = message.ToolName,
                MessageId = message.MessageId,
                MessageUid = message.MessageUid,
                PayloadRef = receipt.Payload,
                Phase = message.Phase,
                Epoch = receipt.Stamp.Epoch,
                Sequence = receipt.Stamp.Sequence,
                Attachments = message.AttachmentsJson is { } json
                    ? JsonSerializer.Deserialize<List<UnifiedAttachment>>(json) : null,
            });
            receipt.Notified = true;
        }
    }
}
