namespace RedCompute.PluginSdk;

/// <summary>
/// Concrete provider-owned settings resolved from an entity-backed quality tier.
/// Provider plugins use this boundary when they need a small internal inference
/// without reintroducing a provider-local model string.
/// </summary>
public sealed record ProviderQualityMode(
    string Provider,
    string Model,
    string? Effort,
    int? TimeoutSeconds,
    string QualityTier);

public interface IProviderQualityModeResolver
{
    /// <summary>
    /// Resolves one explicitly requested tier for the preferred provider. Unknown tiers,
    /// unavailable catalogs, and providers without a model fail closed.
    /// </summary>
    bool TryResolveRequested(
        string qualityTier,
        string? preferredProvider,
        out ProviderQualityMode? resolved);
}
