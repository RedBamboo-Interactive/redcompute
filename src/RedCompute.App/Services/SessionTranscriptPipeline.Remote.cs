using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

public sealed partial class SessionTranscriptPipeline
{
    private static readonly JsonSerializerOptions RemoteRecordJson = new(JsonSerializerDefaults.Web);

    internal long RemoteReplayPosition(string provider, string logicalId, RemoteTranscriptBinding binding)
        => _sequences is TranscriptSequenceStore sequences
            ? sequences.RemotePersistedThrough(provider, logicalId, binding)
            : throw new InvalidOperationException("Remote replay requires durable transcript sequences");

    /// <summary>Returns an ACK position only after the canonical RedLeaf append and local receipt both commit.</summary>
    internal long ImportRemoteReplay(string storageProvider, string logicalId, string originProvider, string runtimeSessionId,
        RemoteTranscriptBinding binding, RemoteSessionEventPage page)
    {
        if (_sequences is not TranscriptSequenceStore sequences)
            throw new InvalidOperationException("Remote replay requires durable transcript sequences");
        if (page.Gap || page.AcknowledgedThrough < 0 || page.LastSequence < page.AcknowledgedThrough || page.Events.Count > 128)
            throw new InvalidOperationException("The remote transcript cursor is unavailable");

        var through = 0L;
        Checkpoints.Write(logicalId, () =>
        {
            through = sequences.RemotePersistedThrough(storageProvider, logicalId, binding);
            if (page.AcknowledgedThrough > through || page.LastSequence < through)
                throw new InvalidOperationException("The remote cursor differs from the canonical transcript receipt");

            long? previous = null;
            foreach (var item in page.Events)
            {
                if (item.Sequence <= page.AcknowledgedThrough || item.Sequence > page.LastSequence
                    || previous is { } last && item.Sequence != last + 1)
                    throw new InvalidOperationException("The remote transcript page is not contiguous");
                previous = item.Sequence;

                var row = JsonSerializer.Deserialize<UnifiedMessageRecord>(item.Event.Body, RemoteRecordJson);
                if (row is null || row.SessionId != runtimeSessionId || row.PayloadRef is not null
                    || string.IsNullOrWhiteSpace(row.Role) || string.IsNullOrWhiteSpace(row.EventType) || row.Timestamp == default)
                    throw new InvalidDataException("The remote transcript record does not belong to the active native thread");

                var record = new RemoteTranscriptRecord(item.Sequence, item.Event.EventId, Convert.ToHexString(
                    SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item.Event, RemoteRecordJson))));
                var reserved = sequences.ReserveRemote(storageProvider, logicalId, binding, record);
                if (reserved.Persisted) continue;

                PersistCompletedMessage(new AiMessageSnapshot
                {
                    Provider = storageProvider,
                    OriginProvider = originProvider,
                    SessionId = logicalId,
                    Role = row.Role,
                    EventType = row.EventType,
                    Content = row.Content,
                    ToolName = row.ToolName,
                    ToolInput = row.ToolInput,
                    ToolResult = row.ToolResult,
                    MessageId = row.MessageId,
                    MessageUid = row.MessageUid,
                    ParentToolUseId = row.ParentToolUseId,
                    Phase = row.Phase,
                    TurnOrigin = row.TurnOrigin,
                    Timestamp = row.Timestamp,
                    AttachmentsJson = row.AttachmentsJson,
                }, reserved.Stamp);
                sequences.CompleteRemote(storageProvider, logicalId, binding, record, reserved.Stamp);
                through = item.Sequence;
            }
        });
        return through;
    }
}
