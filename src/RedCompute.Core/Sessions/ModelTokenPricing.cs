namespace RedCompute.Core.Sessions;

/// <summary>
/// API-equivalent token rates for a model. These estimates are useful when a subscription-backed
/// provider reports tokens but no monetary charge; they do not claim to be the user's invoice.
/// </summary>
public sealed record ModelTokenPricing(
    double InputUsdPerMillion,
    double CachedInputUsdPerMillion,
    double OutputUsdPerMillion)
{
    public double EstimateUsd(int inputTokens, int cachedInputTokens, int outputTokens)
    {
        var input = Math.Max(0, inputTokens);
        var cached = Math.Clamp(cachedInputTokens, 0, input);
        var uncached = input - cached;
        var output = Math.Max(0, outputTokens);

        return ((uncached * InputUsdPerMillion)
                + (cached * CachedInputUsdPerMillion)
                + (output * OutputUsdPerMillion)) / 1_000_000d;
    }
}
