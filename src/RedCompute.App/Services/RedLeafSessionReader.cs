using System.Net.Http;
using System.Text;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

internal sealed record TranscriptPageReadResult(
    IReadOnlyList<UnifiedMessageRecord> Messages,
    long? OldestRecordId,
    long? NewestRecordId,
    bool HasEarlier,
    bool HasLater);

internal sealed class TranscriptPageTooLargeException : Exception
{
    public TranscriptPageTooLargeException(int recordCount, int inlineBytes)
        : base("One logical transcript message exceeds the safe page response budget")
    {
        RecordCount = recordCount;
        InlineBytes = inlineBytes;
    }

    public int RecordCount { get; }
    public int InlineBytes { get; }
}

/// <summary>
/// RedLeaf-backed session reads (read-path cutover): the UI's session list
/// and history come from ai-session entities + session-messages records
/// instead of the plugin SQLite DBs. RedLeaf is a hard dependency here by
/// decision — failures propagate to the caller, no local fallback.
/// </summary>
public sealed class RedLeafSessionReader
{
    internal const int MaxTranscriptPageLimit = 500;
    internal const int MaxTranscriptBoundaryRecords = 10_000;
    internal const int MaxTranscriptBoundaryBytes = 16 * 1024 * 1024;
    private const int RedLeafMaxPageSize = 1000;

    private readonly HttpClient _http;
    private readonly QualityModeService _qualityModes;

    public RedLeafSessionReader(string redLeafBaseUrl, JwtService jwtService, QualityModeService qualityModes)
    {
        _qualityModes = qualityModes;
        // RedLeaf deliberately permits its signed RedCompute projection owner to enumerate
        // confidential session entities. Keep the reader's existing admin permission while
        // emitting that service identity instead of the unrelated "system" user subject.
        var token = jwtService.GenerateServiceAccessToken(
            "redcompute", roles: ["service", "admin"]);
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

    internal async Task<TranscriptPageReadResult> GetTranscriptPageAsync(
        string entityId,
        string sessionId,
        int limit,
        long? beforeRecordId,
        long? afterRecordId,
        CancellationToken ct = default)
    {
        if (beforeRecordId.HasValue && afterRecordId.HasValue)
            throw new ArgumentException("before and after are mutually exclusive");

        limit = Math.Clamp(limit, 1, MaxTranscriptPageLimit);
        var ascending = afterRecordId.HasValue;
        var anchor = beforeRecordId ?? afterRecordId;
        var selected = new List<MappedTranscriptRecord>(limit);
        var batchAnchor = anchor;
        var hasOutsideBoundary = false;
        string? boundaryUid = null;
        var boundaryRecordCount = 0;
        var boundaryBytes = 0;
        var extendingBoundary = false;

        while (true)
        {
            var requestLimit = extendingBoundary
                ? RedLeafMaxPageSize
                : Math.Min(RedLeafMaxPageSize, limit + 1);
            var batch = await GetRecordBatchAsync(
                entityId,
                sessionId,
                ascending,
                requestLimit,
                beforeRecordId: ascending ? null : batchAnchor,
                afterRecordId: ascending ? batchAnchor : null,
                ct);

            if (batch.Count == 0)
                break;

            var stop = false;
            foreach (var record in batch)
            {
                if (selected.Count < limit)
                {
                    selected.Add(record);
                    continue;
                }

                if (!extendingBoundary)
                {
                    boundaryUid = selected[^1].Message.MessageUid;
                    if (string.IsNullOrWhiteSpace(boundaryUid))
                    {
                        hasOutsideBoundary = true;
                        stop = true;
                        break;
                    }

                    (boundaryRecordCount, boundaryBytes) = BoundarySize(selected, boundaryUid);
                    EnsureBoundaryBudget(boundaryRecordCount, boundaryBytes);
                    extendingBoundary = true;
                }

                if (!string.Equals(record.Message.MessageUid, boundaryUid, StringComparison.Ordinal))
                {
                    hasOutsideBoundary = true;
                    stop = true;
                    break;
                }

                boundaryRecordCount++;
                boundaryBytes += record.InlineBytes;
                EnsureBoundaryBudget(boundaryRecordCount, boundaryBytes);
                selected.Add(record);
            }

            if (stop || batch.Count < requestLimit)
                break;

            batchAnchor = batch[^1].Id;
        }

        if (selected.Count > 0)
        {
            var edgeUid = selected[^1].Message.MessageUid;
            if (!string.IsNullOrWhiteSpace(edgeUid))
            {
                var size = BoundarySize(selected, edgeUid);
                EnsureBoundaryBudget(size.Count, size.Bytes);
            }
            else
            {
                EnsureBoundaryBudget(1, selected[^1].InlineBytes);
            }
        }

        if (!ascending)
            selected.Reverse();

        var messages = selected.Select(record => record.Message).ToList();
        long? oldestRecordId = messages.Count == 0 ? null : messages[0].Id;
        long? newestRecordId = messages.Count == 0 ? null : messages[^1].Id;
        bool hasEarlier;
        bool hasLater;
        if (messages.Count > 0)
        {
            hasEarlier = ascending
                ? await HasRecordBeforeAsync(entityId, sessionId, oldestRecordId!.Value, ct)
                : hasOutsideBoundary;
            hasLater = ascending
                ? hasOutsideBoundary
                : await HasRecordAfterAsync(entityId, sessionId, newestRecordId!.Value, ct);
        }
        else if (anchor.HasValue)
        {
            hasEarlier = ascending
                ? await HasRecordAtOrBeforeAsync(entityId, sessionId, anchor.Value, ct)
                : await HasRecordBeforeAsync(entityId, sessionId, anchor.Value, ct);
            hasLater = ascending
                ? await HasRecordAfterAsync(entityId, sessionId, anchor.Value, ct)
                : await HasRecordAtOrAfterAsync(entityId, sessionId, anchor.Value, ct);
        }
        else
        {
            hasEarlier = false;
            hasLater = false;
        }

        return new TranscriptPageReadResult(
            messages,
            oldestRecordId,
            newestRecordId,
            hasEarlier,
            hasLater);
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

        if (tail is { } requestedTail)
        {
            var pageSize = Math.Clamp(requestedTail, 1, 10_000);
            using var doc = await GetJsonAsync(
                $"api/streams/session-messages/records?entity_id={entityId}&order=desc&limit={pageSize}");
            var records = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
            for (var i = records.Length - 1; i >= 0; i--)
                history.Add(MapRecord(records[i], sessionId).Message);
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
                history.Add(MapRecord(rec, sessionId).Message);
            }
            if (items.GetArrayLength() < 1000) break;
        }
        return history;
    }

    private async Task<List<MappedTranscriptRecord>> GetRecordBatchAsync(
        string entityId,
        string sessionId,
        bool ascending,
        int limit,
        long? beforeRecordId,
        long? afterRecordId,
        CancellationToken ct)
    {
        var url =
            $"api/streams/session-messages/records?entity_id={Uri.EscapeDataString(entityId)}" +
            $"&order={(ascending ? "asc" : "desc")}&limit={Math.Clamp(limit, 1, RedLeafMaxPageSize)}";
        if (beforeRecordId.HasValue)
            url += $"&before_id={beforeRecordId.Value}";
        if (afterRecordId.HasValue)
            url += $"&after_id={afterRecordId.Value}";

        using var doc = await GetJsonAsync(url, ct);
        return doc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(record => MapRecord(record, sessionId))
            .ToList();
    }

    private async Task<bool> HasRecordBeforeAsync(
        string entityId,
        string sessionId,
        long recordId,
        CancellationToken ct) =>
        (await GetRecordBatchAsync(
            entityId, sessionId, ascending: false, limit: 1,
            beforeRecordId: recordId, afterRecordId: null, ct)).Count > 0;

    private async Task<bool> HasRecordAfterAsync(
        string entityId,
        string sessionId,
        long recordId,
        CancellationToken ct) =>
        (await GetRecordBatchAsync(
            entityId, sessionId, ascending: true, limit: 1,
            beforeRecordId: null, afterRecordId: recordId, ct)).Count > 0;

    private async Task<bool> HasRecordAtAsync(
        string entityId,
        string sessionId,
        long recordId,
        CancellationToken ct)
    {
        if (recordId <= 0) return false;
        var records = await GetRecordBatchAsync(
            entityId, sessionId, ascending: true, limit: 1,
            beforeRecordId: null, afterRecordId: recordId - 1, ct);
        return records.Count == 1 && records[0].Id == recordId;
    }

    private async Task<bool> HasRecordAtOrBeforeAsync(
        string entityId,
        string sessionId,
        long recordId,
        CancellationToken ct) =>
        await HasRecordAtAsync(entityId, sessionId, recordId, ct)
        || await HasRecordBeforeAsync(entityId, sessionId, recordId, ct);

    private async Task<bool> HasRecordAtOrAfterAsync(
        string entityId,
        string sessionId,
        long recordId,
        CancellationToken ct) =>
        await HasRecordAtAsync(entityId, sessionId, recordId, ct)
        || await HasRecordAfterAsync(entityId, sessionId, recordId, ct);

    private static MappedTranscriptRecord MapRecord(JsonElement rec, string sessionId)
    {
        var id = rec.GetProperty("id").GetInt64();
        var dataJson = rec.GetProperty("data").GetString()!;
        using var data = JsonDocument.Parse(dataJson);
        var d = data.RootElement;
        return new MappedTranscriptRecord(
            id,
            new UnifiedMessageRecord
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
                Phase = Str(d, "phase"),
                MessageUid = Str(d, "message_uid"),
                Epoch = Str(d, "epoch"),
                Sequence = Long(d, "sequence"),
                Timestamp = Str(d, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t : default,
                RecordCreatedAt = rec.TryGetProperty("createdAt", out var createdAt)
                    && createdAt.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(createdAt.GetString(), out var recordCreatedAt)
                        ? recordCreatedAt
                        : null,
                AttachmentsJson = Str(d, "attachments_json"),
            },
            Encoding.UTF8.GetByteCount(dataJson));
    }

    private static (int Count, int Bytes) BoundarySize(
        IReadOnlyList<MappedTranscriptRecord> records,
        string boundaryUid)
    {
        var count = 0;
        var bytes = 0;
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            if (!string.Equals(record.Message.MessageUid, boundaryUid, StringComparison.Ordinal))
                break;
            count++;
            bytes = checked(bytes + record.InlineBytes);
        }
        return (count, bytes);
    }

    private static void EnsureBoundaryBudget(int recordCount, int inlineBytes)
    {
        if (recordCount > MaxTranscriptBoundaryRecords || inlineBytes > MaxTranscriptBoundaryBytes)
            throw new TranscriptPageTooLargeException(recordCount, inlineBytes);
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
            ExecutionProfile = Str(d, "execution_profile"),
            UserId = Str(d, "user_id"),
            OwnerAgentId = Str(d, "owner_agent_id"),
            Confidential = Bool(d, "confidential") ?? false,
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static long? Long(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

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

    private sealed record MappedTranscriptRecord(
        long Id,
        UnifiedMessageRecord Message,
        int InlineBytes);
}
