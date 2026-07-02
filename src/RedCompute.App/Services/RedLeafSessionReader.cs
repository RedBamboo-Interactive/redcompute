using System.Net.Http;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

/// <summary>
/// RedLeaf-backed session reads (read-path cutover): the UI's session list
/// and history come from ai-session entities + session-messages records
/// instead of the plugin SQLite DBs. RedLeaf is a hard dependency here by
/// decision — failures propagate to the caller, no local fallback.
/// </summary>
public sealed class RedLeafSessionReader
{
    private readonly HttpClient _http;

    public RedLeafSessionReader(string redLeafBaseUrl, JwtService jwtService)
    {
        var token = jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
        _http = new HttpClient
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    public async Task<List<UnifiedSessionInfo>> GetSessionsAsync(string? provider, int limit, bool includeDismissed)
    {
        // Entities can't be server-sorted by a data key; recently-started is a
        // subset of recently-updated (upserts bump UpdatedAt), so fetch the
        // most recently updated page and order client-side.
        var url = "api/entities?type=ai-session&sort_by=updated_at&sort_dir=desc&limit=500";
        if (provider != null)
            url += $"&data.provider={Uri.EscapeDataString(provider)}";
        if (!includeDismissed)
            url += "&data.dismissed=false";

        using var doc = await GetJsonAsync(url);
        var sessions = new List<UnifiedSessionInfo>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var info = MapSession(item);
            if (info != null) sessions.Add(info);
        }

        sessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return sessions.Count > limit ? sessions.Take(limit).ToList() : sessions;
    }

    public async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetSessionAsync(string sessionId)
        => await GetByDataFilterAsync($"data.session_id={Uri.EscapeDataString(sessionId)}");

    public async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetSessionByJobIdAsync(Guid jobId)
    {
        // Migrated entities carry the SQLite text form (uppercase), the live
        // mirror writes lowercase — try both.
        var result = await GetByDataFilterAsync($"data.job_id={jobId.ToString().ToLowerInvariant()}");
        if (result.Info == null)
            result = await GetByDataFilterAsync($"data.job_id={jobId.ToString().ToUpperInvariant()}");
        return result;
    }

    private async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetByDataFilterAsync(string filter)
    {
        using var doc = await GetJsonAsync($"api/entities?type=ai-session&{filter}&limit=1");
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0)
            return (null, []);

        var entity = items[0];
        var info = MapSession(entity);
        if (info == null)
            return (null, []);

        var entityId = entity.GetProperty("id").GetString()!;
        return (info, await GetHistoryAsync(entityId, info.Id));
    }

    private async Task<List<UnifiedMessageRecord>> GetHistoryAsync(string entityId, string sessionId)
    {
        var history = new List<UnifiedMessageRecord>();
        long afterId = 0;
        while (true)
        {
            using var doc = await GetJsonAsync(
                $"api/streams/session-messages/records?entity_id={entityId}&order=asc&limit=1000&after_id={afterId}");
            var items = doc.RootElement.GetProperty("items");
            foreach (var rec in items.EnumerateArray())
            {
                var id = rec.GetProperty("id").GetInt64();
                afterId = id;
                using var data = JsonDocument.Parse(rec.GetProperty("data").GetString()!);
                var d = data.RootElement;
                history.Add(new UnifiedMessageRecord
                {
                    Id = id,
                    SessionId = Str(d, "session_id") ?? sessionId,
                    Role = Str(d, "role") ?? "",
                    EventType = Str(d, "event_type") ?? "",
                    Content = Str(d, "content"),
                    ToolName = Str(d, "tool_name"),
                    ToolInput = Str(d, "tool_input"),
                    ToolResult = Str(d, "tool_result"),
                    MessageId = Str(d, "message_id"),
                    MessageUid = Str(d, "message_uid"),
                    Timestamp = Str(d, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                        ? t : default,
                    AttachmentsJson = Str(d, "attachments_json"),
                });
            }
            if (items.GetArrayLength() < 1000) break;
        }
        return history;
    }

    private static UnifiedSessionInfo? MapSession(JsonElement entity)
    {
        using var data = JsonDocument.Parse(entity.GetProperty("data").GetString()!);
        var d = data.RootElement;

        var sessionId = Str(d, "session_id");
        if (sessionId == null) return null;

        return new UnifiedSessionInfo
        {
            Id = sessionId,
            Provider = Str(d, "provider") ?? "",
            ProjectName = Str(d, "project_name") ?? "",
            ProjectPath = Str(d, "project_path") ?? "",
            Status = Enum.TryParse<SessionStatus>(Str(d, "status"), ignoreCase: true, out var s) ? s : SessionStatus.Stopped,
            StopReason = Str(d, "stop_reason"),
            StartedAt = Str(d, "started_at") is { } sa && DateTimeOffset.TryParse(sa, out var t) ? t : default,
            Model = Str(d, "model"),
            ProviderSessionId = Str(d, "external_session_id"),
            Title = entity.GetProperty("name").GetString(),
            MessageCount = Int(d, "message_count") ?? 0,
            CostUsd = Dbl(d, "cost_usd"),
            InputTokens = Int(d, "input_tokens"),
            OutputTokens = Int(d, "output_tokens"),
            CachedInputTokens = Int(d, "cache_read_input_tokens"),
            ContextTokens = Int(d, "context_tokens"),
            ContextWindow = Int(d, "context_window"),
            Effort = Str(d, "effort"),
            JobId = Str(d, "job_id") is { } j && Guid.TryParse(j, out var g) ? g : null,
            Source = Str(d, "source"),
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? Dbl(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
