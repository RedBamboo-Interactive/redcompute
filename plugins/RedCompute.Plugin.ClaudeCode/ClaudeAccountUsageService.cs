using System.Text.Json;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

/// <summary>
/// Keeps the latest provider-wide subscription quota snapshot emitted by Claude Code. The stream
/// event is passive account metadata: consuming it must not affect the owning session lifecycle.
/// </summary>
internal sealed class ClaudeAccountUsageService
{
    private ProviderUsageSnapshot? _cached;

    public Task<ProviderUsageSnapshot?> GetAsync(
        bool forceRefresh,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _cached));
    }

    public void ApplyEvent(JsonElement root)
    {
        if (ParseSnapshot(root) is { } snapshot)
            Volatile.Write(ref _cached, snapshot);
    }

    internal static ProviderUsageSnapshot? ParseSnapshot(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("unifiedWindows", out var unifiedWindows)
            || unifiedWindows.ValueKind != JsonValueKind.Object)
            return null;

        var windows = new List<ProviderUsageWindow>();
        AddWindow(unifiedWindows, "five_hour", 300, windows);
        AddWindow(unifiedWindows, "seven_day", 10_080, windows);
        if (windows.Count == 0) return null;

        return new ProviderUsageSnapshot(
            "claude-code",
            "Claude Code",
            null,
            DateTimeOffset.UtcNow,
            [new ProviderUsageBucket("claude", "Claude", windows)]);
    }

    private static void AddWindow(
        JsonElement unifiedWindows,
        string id,
        int durationMinutes,
        ICollection<ProviderUsageWindow> target)
    {
        if (!unifiedWindows.TryGetProperty(id, out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("utilization", out var utilizationValue)
            || utilizationValue.ValueKind != JsonValueKind.Number
            || !utilizationValue.TryGetDouble(out var utilization)
            || !double.IsFinite(utilization)
            || utilization < 0)
            return;

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resetsAt", out var resetValue)
            && resetValue.ValueKind == JsonValueKind.Number
            && resetValue.TryGetInt64(out var resetSeconds))
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds); }
            catch (ArgumentOutOfRangeException) { }
        }

        target.Add(new ProviderUsageWindow(
            id,
            utilization * 100d,
            durationMinutes,
            resetsAt));
    }
}
