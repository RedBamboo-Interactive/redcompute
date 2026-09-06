using System.Text.Json;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Reads provider-wide ChatGPT quota state from Codex App Server and keeps the relevant account
/// snapshot warm with the incremental notifications emitted by interactive connections.
/// </summary>
internal sealed class CodexAccountUsageService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private const string SparkLimitId = "codex_bengalfox";

    private readonly CodexConfig _config;
    private readonly Action<string, Guid?> _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _cacheLock = new();
    private ProviderUsageSnapshot? _cached;
    private DateTimeOffset _cacheExpiresAt;
    private bool _cacheIsComplete;

    public CodexAccountUsageService(CodexConfig config, Action<string, Guid?> log)
    {
        _config = config;
        _log = log;
    }

    public async Task<ProviderUsageSnapshot?> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && TryGetCompleteCache(out var cached)) return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && TryGetCompleteCache(out cached)) return cached;

            await using var connection = await CodexAppServerConnection.StartAsync(
                _config.CodexPath, workingDirectory: null, _log, ct: ct);
            var result = await connection.SendRequestAsync(
                "account/rateLimits/read", timeoutSeconds: 30, ct: ct);
            var snapshot = ParseSnapshot(result);
            lock (_cacheLock)
            {
                _cached = snapshot;
                _cacheExpiresAt = DateTimeOffset.UtcNow + CacheLifetime;
                _cacheIsComplete = true;
            }
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void ApplyNotification(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("rateLimits", out var rateLimits)
            || rateLimits.ValueKind != JsonValueKind.Object)
            return;

        var updated = ParseBucket(rateLimits);
        if (updated is null || !ShouldExposeBucket(updated.Id)) return;

        lock (_cacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is null)
            {
                _cached = new ProviderUsageSnapshot(
                    "codex", "Codex", ReadString(rateLimits, "planType"), now, [updated]);
                _cacheIsComplete = false;
            }
            else
            {
                var buckets = _cached.Buckets
                    .Where(bucket => !bucket.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase))
                    .Append(updated)
                    .OrderBy(bucket => bucket.Id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _cached = _cached with
                {
                    PlanType = ReadString(rateLimits, "planType") ?? _cached.PlanType,
                    FetchedAt = now,
                    Buckets = buckets,
                };
            }
            _cacheExpiresAt = now + CacheLifetime;
        }
    }

    private bool TryGetCompleteCache(out ProviderUsageSnapshot? snapshot)
    {
        lock (_cacheLock)
        {
            snapshot = _cached;
            return _cacheIsComplete && snapshot is not null && DateTimeOffset.UtcNow < _cacheExpiresAt;
        }
    }

    internal static ProviderUsageSnapshot ParseSnapshot(JsonElement result)
    {
        var buckets = new List<ProviderUsageBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId)
            && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject())
                if (ParseBucket(property.Value) is { } bucket)
                    if (ShouldExposeBucket(bucket.Id))
                        buckets.Add(bucket);
        }
        else if (result.TryGetProperty("rateLimits", out var rateLimits)
                 && rateLimits.ValueKind == JsonValueKind.Object
                 && ParseBucket(rateLimits) is { } bucket)
        {
            buckets.Add(bucket);
        }

        buckets = buckets
            .OrderBy(item => item.Id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var planType = buckets
            .Select(bucket => ReadBucketPlan(result, bucket.Id))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (planType is null
            && result.TryGetProperty("rateLimits", out var legacyRateLimits)
            && legacyRateLimits.ValueKind == JsonValueKind.Object)
            planType = ReadString(legacyRateLimits, "planType");

        int? resetCredits = null;
        if (result.TryGetProperty("rateLimitResetCredits", out var reset)
            && reset.ValueKind == JsonValueKind.Object)
            resetCredits = ReadInt(reset, "availableCount");

        return new ProviderUsageSnapshot(
            "codex", "Codex", planType, DateTimeOffset.UtcNow, buckets, resetCredits);
    }

    private static string? ReadBucketPlan(JsonElement result, string id)
    {
        if (!result.TryGetProperty("rateLimitsByLimitId", out var byId)
            || byId.ValueKind != JsonValueKind.Object
            || !byId.TryGetProperty(id, out var bucket)
            || bucket.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(bucket, "planType");
    }

    private static bool ShouldExposeBucket(string id)
        => !id.Equals(SparkLimitId, StringComparison.OrdinalIgnoreCase);

    private static ProviderUsageBucket? ParseBucket(JsonElement value)
    {
        var id = ReadString(value, "limitId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var windows = new List<ProviderUsageWindow>();
        AddWindow(value, "primary", windows);
        AddWindow(value, "secondary", windows);

        ProviderUsageCredits? credits = null;
        if (value.TryGetProperty("credits", out var creditValue)
            && creditValue.ValueKind == JsonValueKind.Object)
        {
            credits = new ProviderUsageCredits(
                ReadBool(creditValue, "hasCredits"),
                ReadBool(creditValue, "unlimited"),
                ReadString(creditValue, "balance"));
        }

        var name = ReadString(value, "limitName");
        if (string.IsNullOrWhiteSpace(name))
            name = id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : id;

        return new ProviderUsageBucket(
            id, name, windows, credits, ReadString(value, "rateLimitReachedType"));
    }

    private static void AddWindow(
        JsonElement bucket,
        string id,
        ICollection<ProviderUsageWindow> target)
    {
        if (!bucket.TryGetProperty(id, out var value) || value.ValueKind != JsonValueKind.Object)
            return;
        var usedPercent = ReadDouble(value, "usedPercent");
        var duration = ReadInt(value, "windowDurationMins");
        if (usedPercent is null || duration is null) return;

        DateTimeOffset? resetsAt = null;
        if (ReadLong(value, "resetsAt") is { } seconds)
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        }

        target.Add(new ProviderUsageWindow(id, usedPercent.Value, duration.Value, resetsAt));
    }

    private static string? ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadBool(JsonElement value, string name)
        => value.TryGetProperty(name, out var property)
           && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static int? ReadInt(JsonElement value, string name)
        => value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var number)
            ? number
            : null;

    private static long? ReadLong(JsonElement value, string name)
        => value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt64(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement value, string name)
        => value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetDouble(out var number)
            ? number
            : null;
}
