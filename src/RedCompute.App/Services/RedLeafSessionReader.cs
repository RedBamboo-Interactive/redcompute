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
    private readonly QualityModeService _qualityModes;

    public RedLeafSessionReader(string redLeafBaseUrl, JwtService jwtService, QualityModeService qualityModes)
    {
        _qualityModes = qualityModes;
        var token = jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
        _http = new HttpClient
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    internal RedLeafSessionReader(HttpClient http, QualityModeService qualityModes)
    {
        _http = http;
        _qualityModes = qualityModes;
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

    public async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetSessionAsync(
        string sessionId, int? tail = null)
        => await GetByDataFilterAsync($"data.session_id={Uri.EscapeDataString(sessionId)}", tail);

    public async Task<(UnifiedSessionInfo? Info, string? EntityId)> GetSessionInfoAsync(string sessionId)
    {
        using var doc = await GetJsonAsync(
            $"api/entities?type=ai-session&data.session_id={Uri.EscapeDataString(sessionId)}&limit=1");
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return (null, null);
        var entity = items[0];
        return (MapSession(entity), entity.GetProperty("id").GetString());
    }

    public async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetSessionByJobIdAsync(Guid jobId)
    {
        // Migrated entities carry the SQLite text form (uppercase), the live
        // mirror writes lowercase — try both.
        var result = await GetByDataFilterAsync($"data.job_id={jobId.ToString().ToLowerInvariant()}");
        if (result.Info == null)
            result = await GetByDataFilterAsync($"data.job_id={jobId.ToString().ToUpperInvariant()}");
        return result;
    }

    private async Task<(UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History)> GetByDataFilterAsync(
        string filter, int? tail = null)
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
        return (info, await GetHistoryAsync(entityId, info.Id, tail));
    }

    private async Task<List<UnifiedMessageRecord>> GetHistoryAsync(string entityId, string sessionId, int? tail)
    {
        var history = new List<UnifiedMessageRecord>();

        void AddRecord(JsonElement rec)
        {
            var id = rec.GetProperty("id").GetInt64();
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
                PayloadRef = MapPayloadRef(rec, id),
                MessageId = Str(d, "message_id"),
                MessageUid = Str(d, "message_uid"),
                Timestamp = Str(d, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t : default,
                AttachmentsJson = Str(d, "attachments_json"),
            });
        }

        if (tail is { } requestedTail)
        {
            var pageSize = Math.Clamp(requestedTail, 1, 10_000);
            using var doc = await GetJsonAsync(
                $"api/streams/session-messages/records?entity_id={entityId}&order=desc&limit={pageSize}");
            var records = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            for (var i = records.Length - 1; i >= 0; i--)
                AddRecord(records[i]);
            return history;
        }

        long afterId = 0;
        while (true)
        {
            using var doc = await GetJsonAsync(
                $"api/streams/session-messages/records?entity_id={entityId}&order=asc&limit=1000&after_id={afterId}");
            var items = doc.RootElement.GetProperty("items");
            foreach (var rec in items.EnumerateArray())
            {
                afterId = rec.GetProperty("id").GetInt64();
                AddRecord(rec);
            }
            if (items.GetArrayLength() < 1000) break;
        }
        return history;
    }

    public async Task<HttpResponseMessage> OpenPayloadAsync(
        string entityId, long recordId, string? range, CancellationToken ct)
    {
        var afterId = Math.Max(0, recordId - 1);
        using var doc = await GetJsonAsync(
            $"api/streams/session-messages/records?entity_id={Uri.EscapeDataString(entityId)}&order=asc&limit=1&after_id={afterId}");
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0 || items[0].GetProperty("id").GetInt64() != recordId)
            throw new KeyNotFoundException("Transcript payload record was not found in this session");

        var record = items[0];
        if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new KeyNotFoundException("Transcript record has no payload");
        using (var data = JsonDocument.Parse(record.GetProperty("data").GetString()!))
        {
            if (Str(data.RootElement, "event_type") != "tool_result")
                throw new KeyNotFoundException("Transcript payload is not a tool result");
        }

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/streams/session-messages/records/{recordId}/payload");
        if (!string.IsNullOrWhiteSpace(range))
            request.Headers.TryAddWithoutValidation("Range", range);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private UnifiedSessionInfo? MapSession(JsonElement entity)
    {
        using var data = JsonDocument.Parse(entity.GetProperty("data").GetString()!);
        var d = data.RootElement;

        var sessionId = Str(d, "session_id");
        if (sessionId == null) return null;

        var provider = Str(d, "provider") ?? "";
        var model = Str(d, "model");
        var inputTokens = Int(d, "input_tokens");
        var outputTokens = Int(d, "output_tokens");
        var cachedInputTokens = Int(d, "cache_read_input_tokens");
        var reportedCost = Dbl(d, "cost_usd");
        var hasUsage = inputTokens.GetValueOrDefault() > 0 || outputTokens.GetValueOrDefault() > 0;
        // Codex subscription sessions may report no monetary charge (or a literal zero). Their
        // token usage still has an API-equivalent value, which is the useful number for suite
        // statistics. Never replace a positive provider-reported charge.
        var shouldEstimateCost = !reportedCost.HasValue
            || (provider.Equals("codex", StringComparison.OrdinalIgnoreCase)
                && reportedCost <= 0
                && hasUsage);
        var estimatedCost = shouldEstimateCost
            ? _qualityModes.EstimateCostUsd(model, inputTokens, cachedInputTokens, outputTokens)
            : null;

        return new UnifiedSessionInfo
        {
            Id = sessionId,
            Provider = provider,
            ProviderEntity = Str(d, "provider_entity"),
            ProjectName = Str(d, "project_name") ?? "",
            ProjectPath = Str(d, "project_path") ?? "",
            RepositoryId = Str(d, "repository") is { } repositoryId
                && Guid.TryParse(repositoryId, out var repositoryGuid)
                    ? repositoryGuid
                    : null,
            Status = Enum.TryParse<SessionStatus>(Str(d, "status"), ignoreCase: true, out var s) ? s : SessionStatus.Stopped,
            StopReason = Str(d, "stop_reason"),
            StartedAt = Str(d, "started_at") is { } sa && DateTimeOffset.TryParse(sa, out var t) ? t : default,
            Model = model,
            ProviderSessionId = Str(d, "external_session_id"),
            Title = entity.GetProperty("name").GetString(),
            MessageCount = Int(d, "message_count") ?? 0,
            CostUsd = estimatedCost ?? reportedCost,
            CostEstimated = estimatedCost.HasValue,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedInputTokens,
            ContextTokens = Int(d, "context_tokens"),
            // Provider runtime metadata is authoritative. The quality mode is only a fallback for
            // sessions whose provider did not report a live account/model-specific window.
            ContextWindow = Int(d, "context_window") ?? _qualityModes.GetContextWindow(model),
            Effort = Str(d, "effort"),
            QualityTier = Str(d, "quality_tier"),
            JobId = Str(d, "job_id") is { } j && Guid.TryParse(j, out var g) ? g : null,
            Source = Str(d, "source"),
            UserId = Str(d, "user_id"),
            OwnerAgentId = Str(d, "owner_agent_id"),
            Confidential = Bool(d, "confidential") ?? false,
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

    private static bool? Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? Dbl(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static TranscriptPayloadRef? MapPayloadRef(JsonElement record, long recordId)
    {
        if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return new TranscriptPayloadRef
        {
            RecordId = recordId,
            Kind = "tool-output",
            Available = payload.TryGetProperty("available", out var available) && available.GetBoolean(),
            Length = payload.TryGetProperty("length", out var length) ? length.GetInt64() : 0,
            ContentType = Str(payload, "contentType") ?? "text/plain; charset=utf-8",
            Encoding = Str(payload, "encoding") ?? "utf-8",
            Sha256 = Str(payload, "sha256") ?? "",
        };
    }
}
