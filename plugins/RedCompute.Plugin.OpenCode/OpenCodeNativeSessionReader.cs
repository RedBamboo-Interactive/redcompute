using System.Globalization;
using System.Text;
using System.Text.Json;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

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
    DateTimeOffset Timestamp,
    bool IsTerminalMessage = false);

internal interface IOpenCodeNativeSessionReader
{
    OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId);
    IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId);
}

/// <summary>
/// OpenCode's own session database (opencode.db), read where OpenCode writes it: on the host, through
/// <see cref="IHostQueries.QuerySqliteReadOnly"/>, with the connection this reader always opened
/// (read-only, unpooled, a 2 s busy timeout) and the same statements and parameters.
/// </summary>
internal sealed class OpenCodeNativeSessionReader : IOpenCodeNativeSessionReader
{
    private readonly string _dbPath;
    private readonly IExecutionHost _host;

    /// <summary>The connection the reader always opened: <c>Mode=ReadOnly;Pooling=False</c>, then <c>PRAGMA busy_timeout=2000</c>.</summary>
    internal static readonly SqliteReadOptions ReadOptions = new() { Pooling = false, BusyTimeoutMilliseconds = 2000 };

    public OpenCodeNativeSessionReader(string? dbPath = null, IExecutionHost? host = null)
    {
        _host = host ?? LocalExecutionHost.Shared;
        _dbPath = dbPath ?? ResolveDbPath(_host);
    }

    internal string DbPath => _dbPath;

    public OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId)
    {
        if (!_host.Files.FileExists(_host.Path(_dbPath))) return OpenCodeNativeMessageVisibility.Missing;

        // ExecuteScalar: the first column of the first row, NULL and no row alike reading as no data.
        var rows = Query("SELECT data FROM message WHERE session_id=$session AND id=$message LIMIT 1",
            new Dictionary<string, object?> { ["$session"] = sessionId, ["$message"] = messageId });
        var json = rows.Count > 0 ? rows[0]["data"] as string : null;
        if (json == null) return OpenCodeNativeMessageVisibility.Missing;

        using var document = JsonDocument.Parse(json);
        return IsCompaction(document.RootElement)
            ? OpenCodeNativeMessageVisibility.Compaction
            : OpenCodeNativeMessageVisibility.Visible;
    }

    public IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId)
    {
        if (!_host.Files.FileExists(_host.Path(_dbPath)))
            throw new FileNotFoundException("OpenCode native session database was not found", _dbPath);

        var session = new Dictionary<string, object?> { ["$session"] = sessionId };
        var messages = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in Query("SELECT id, time_created, data FROM message WHERE session_id=$session ORDER BY time_created, id", session))
        {
            using var document = JsonDocument.Parse(GetString(row, "data", 2));
            var data = document.RootElement;
            if (String(data, "role") != "assistant" || IsCompaction(data) || !IsCompleted(data))
                continue;

            messages.Add(GetString(row, "id", 0), String(data, "finish") != "tool-calls");
        }

        if (messages.Count == 0) return [];

        var parts = new List<OpenCodeNativePart>();
        foreach (var row in Query("SELECT id, message_id, time_created, data FROM part WHERE session_id=$session ORDER BY time_created, id", session))
        {
            var providerPartId = GetString(row, "id", 0);
            var messageId = GetString(row, "message_id", 1);
            if (!messages.TryGetValue(messageId, out var terminal)) continue;
            using var document = JsonDocument.Parse(GetString(row, "data", 3));
            if (ParsePart(providerPartId, messageId, document.RootElement,
                    DateTimeOffset.FromUnixTimeMilliseconds(GetInt64(row, "time_created", 2)), out var part))
                parts.Add(part! with { IsTerminalMessage = terminal });
        }

        return parts;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, IReadOnlyDictionary<string, object?> parameters)
        => _host.Queries.QuerySqliteReadOnly(_host.Path(_dbPath), sql, parameters, ReadOptions);

    /// <summary>
    /// <c>SqliteDataReader.GetString</c> over a returned row: SQLite's text of the stored value, and on
    /// NULL the provider's own exception and message.
    /// </summary>
    internal static string GetString(IReadOnlyDictionary<string, object?> row, string column, int ordinal)
        => row[column] switch
        {
            null => throw CalledOnNull(ordinal),
            string text => text,
            byte[] blob => Encoding.UTF8.GetString(blob),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            var value => value.ToString()!,
        };

    /// <summary><c>SqliteDataReader.GetInt64</c> over a returned row; NULL throws as the provider does.</summary>
    internal static long GetInt64(IReadOnlyDictionary<string, object?> row, string column, int ordinal)
        => row[column] switch
        {
            null => throw CalledOnNull(ordinal),
            long integer => integer,
            double real => (long)real,
            var value => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };

    private static InvalidOperationException CalledOnNull(int ordinal) => new(
        $"The data is NULL at ordinal {ordinal}. This method can't be called on NULL values. Check using IsDBNull before calling.");

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

    internal static string ResolveDbPath(string? configuredDataHome = null)
        => ResolveDbPath(LocalExecutionHost.Shared, configuredDataHome);

    /// <summary>opencode.db on <paramref name="host"/>: under the configured data home, the host's XDG_DATA_HOME, or ~/.local/share.</summary>
    internal static string ResolveDbPath(IExecutionHost host, string? configuredDataHome = null)
    {
        var dataHome = NormalizeConfiguredDataHome(host, configuredDataHome)
            ?? host.Platform.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
            return host.PathRules.Combine(dataHome, "opencode", "opencode.db");
        return host.PathRules.Combine(host.Platform.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "opencode", "opencode.db");
    }

    internal static string? NormalizeConfiguredDataHome(IExecutionHost host, string? configuredDataHome)
    {
        if (string.IsNullOrWhiteSpace(configuredDataHome)) return null;
        if (!host.PathRules.IsPathFullyQualified(configuredDataHome))
            throw new ArgumentException("OpenCodeDataHome must be an absolute path", nameof(configuredDataHome));
        return host.PathRules.GetFullPath(configuredDataHome);
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
