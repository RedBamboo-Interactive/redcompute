using System.Text.Json;

namespace RedCompute.App.Services;

internal enum TranscriptPageCursorEdge
{
    Oldest,
    Newest,
}

internal readonly record struct TranscriptPageCursor(
    string SessionId,
    string Epoch,
    long RecordId,
    TranscriptPageCursorEdge Edge);

internal sealed class TranscriptPageCursorException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class TranscriptPageCursorCodec
{
    private const int CurrentVersion = 1;
    private const int MaxEncodedLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(TranscriptPageCursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload
        {
            Version = CurrentVersion,
            SessionId = cursor.SessionId,
            Epoch = cursor.Epoch,
            RecordId = cursor.RecordId,
            Edge = cursor.Edge == TranscriptPageCursorEdge.Oldest ? "oldest" : "newest",
        }, JsonOptions);
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static TranscriptPageCursor Decode(
        string encoded,
        string expectedSessionId,
        string expectedEpoch,
        TranscriptPageCursorEdge expectedEdge)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength)
            throw Invalid();

        CursorPayload? payload;
        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(normalized), JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException)
        {
            throw Invalid();
        }

        if (payload is null
            || payload.Version != CurrentVersion
            || string.IsNullOrWhiteSpace(payload.SessionId)
            || string.IsNullOrWhiteSpace(payload.Epoch)
            || payload.RecordId <= 0
            || payload.Edge is not ("oldest" or "newest"))
            throw Invalid();

        if (!string.Equals(payload.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(payload.Edge,
                expectedEdge == TranscriptPageCursorEdge.Oldest ? "oldest" : "newest",
                StringComparison.Ordinal))
            throw new TranscriptPageCursorException(
                "transcript_cursor_mismatch",
                "The transcript cursor does not belong to this session or requested page edge");

        if (!string.Equals(payload.Epoch, expectedEpoch, StringComparison.Ordinal))
            throw new TranscriptPageCursorException(
                "transcript_cursor_stale",
                "The transcript cursor belongs to a stale transcript epoch; restart from the newest page");

        return new TranscriptPageCursor(
            payload.SessionId,
            payload.Epoch,
            payload.RecordId,
            expectedEdge);
    }

    private static TranscriptPageCursorException Invalid() => new(
        "invalid_cursor",
        "The transcript cursor is malformed or uses an unsupported version");

    private sealed class CursorPayload
    {
        public int Version { get; init; }
        public string? SessionId { get; init; }
        public string? Epoch { get; init; }
        public long RecordId { get; init; }
        public string? Edge { get; init; }
    }
}
