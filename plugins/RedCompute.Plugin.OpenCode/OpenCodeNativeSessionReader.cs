using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RedCompute.Plugin.OpenCode;

internal enum OpenCodeNativeMessageVisibility
{
    Missing,
    Visible,
    Compaction,
}

internal sealed record OpenCodeNativePart(
    string Id,
    string MessageId,
    string Type,
    string? Text,
    string? ToolName,
    string? CallId,
    string? ToolInput,
    string? ToolResult,
    string? ToolTitle,
    bool ToolFailed,
    DateTimeOffset Timestamp);

internal interface IOpenCodeNativeSessionReader
{
    OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId);
    IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId);
}

internal sealed class OpenCodeNativeSessionReader : IOpenCodeNativeSessionReader
{
    private readonly string _dbPath;

    public OpenCodeNativeSessionReader(string? dbPath = null)
    {
        _dbPath = dbPath ?? ResolveDbPath();
    }

    internal string DbPath => _dbPath;

    public OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId)
    {
        if (!File.Exists(_dbPath)) return OpenCodeNativeMessageVisibility.Missing;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM message WHERE session_id=$session AND id=$message LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$message", messageId);
        var json = command.ExecuteScalar() as string;
        if (json == null) return OpenCodeNativeMessageVisibility.Missing;

        using var document = JsonDocument.Parse(json);
        return IsCompaction(document.RootElement)
            ? OpenCodeNativeMessageVisibility.Compaction
            : OpenCodeNativeMessageVisibility.Visible;
    }

    public IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId)
    {
        if (!File.Exists(_dbPath))
            throw new FileNotFoundException("OpenCode native session database was not found", _dbPath);

        using var connection = Open();
        var messages = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, time_created, data FROM message WHERE session_id=$session ORDER BY time_created, id";
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                using var document = JsonDocument.Parse(reader.GetString(2));
                var data = document.RootElement;
                if (String(data, "role") != "assistant" || IsCompaction(data) || !IsCompleted(data))
                    continue;

                messages.Add(reader.GetString(0));
            }
        }

        if (messages.Count == 0) return [];

        var parts = new List<OpenCodeNativePart>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, message_id, time_created, data FROM part WHERE session_id=$session ORDER BY time_created, id";
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var providerPartId = reader.GetString(0);
                var messageId = reader.GetString(1);
                if (!messages.Contains(messageId)) continue;
                using var document = JsonDocument.Parse(reader.GetString(3));
                if (ParsePart(providerPartId, messageId, document.RootElement,
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), out var part))
                    parts.Add(part!);
            }
        }

        return parts;
    }

    internal static bool ParsePart(
        string providerPartId,
        string messageId,
        JsonElement data,
        DateTimeOffset fallbackTimestamp,
        out OpenCodeNativePart? part)
    {
        part = null;
        var type = String(data, "type");
        if (type is not ("text" or "reasoning" or "tool")) return false;

        if (type is "text" or "reasoning")
        {
            var text = String(data, "text");
            if (string.IsNullOrEmpty(text)) return false;
            part = new OpenCodeNativePart(providerPartId, messageId, type, text, null, null, null, null, null,
                false, Timestamp(data, fallbackTimestamp));
            return true;
        }

        if (!data.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            return false;
        var status = String(state, "status");
        if (status is not ("completed" or "error")) return false;

        var input = state.TryGetProperty("input", out var inputValue) ? inputValue.GetRawText() : null;
        var output = String(state, "output") ?? String(state, "error");
        part = new OpenCodeNativePart(
            providerPartId,
            messageId,
            type,
            null,
            String(data, "tool"),
            String(data, "callID"),
            input,
            output,
            String(state, "title"),
            status == "error",
            Timestamp(state, Timestamp(data, fallbackTimestamp)));
        return true;
    }

    internal static bool IsCompaction(JsonElement data) =>
        String(data, "mode") == "compaction"
        || (data.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.True);

    private static bool IsCompleted(JsonElement data) =>
        data.TryGetProperty("time", out var time)
        && time.ValueKind == JsonValueKind.Object
        && time.TryGetProperty("completed", out var completed)
        && completed.ValueKind == JsonValueKind.Number;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=2000";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static string ResolveDbPath()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
            return Path.Combine(dataHome, "opencode", "opencode.db");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "opencode", "opencode.db");
    }

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset Timestamp(JsonElement value, DateTimeOffset fallback)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("time", out var time)
            && time.ValueKind == JsonValueKind.Object)
        {
            if (time.TryGetProperty("start", out var start) && start.TryGetInt64(out var startMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(startMs);
            if (time.TryGetProperty("created", out var created) && created.TryGetInt64(out var createdMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(createdMs);
        }
        return fallback;
    }
}
