namespace RedCompute.PluginSdk;

/// <summary>
/// Optional provider-wide subscription or quota information. This data is account-scoped rather
/// than session-scoped; session products consume it only as a convenient presentation surface.
/// </summary>
public interface IProviderUsageSource
{
    string ProviderId { get; }
    Task<ProviderUsageSnapshot?> GetProviderUsageAsync(
        bool forceRefresh = false,
        CancellationToken ct = default);
}

public sealed record ProviderUsageSnapshot(
    string Provider,
    string DisplayName,
    string? PlanType,
    DateTimeOffset FetchedAt,
    IReadOnlyList<ProviderUsageBucket> Buckets,
    int? ResetCreditsAvailable = null);

public sealed record ProviderUsageBucket(
    string Id,
    string Name,
    IReadOnlyList<ProviderUsageWindow> Windows,
    ProviderUsageCredits? Credits = null,
    string? LimitReachedType = null);

public sealed record ProviderUsageWindow(
    string Id,
    double UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset? ResetsAt);

public sealed record ProviderUsageCredits(
    bool? HasCredits,
    bool? Unlimited,
    string? Balance);
