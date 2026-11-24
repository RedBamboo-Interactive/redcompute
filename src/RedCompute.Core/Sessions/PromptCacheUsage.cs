namespace RedCompute.Core.Sessions;

/// <summary>Input traffic and the subset served from cache, in comparable provider-reported tokens.</summary>
public sealed record PromptCacheCounts(long InputTokens, long CachedInputTokens)
{
    public static PromptCacheCounts? From(long? input, long? cached) =>
        input is > 0 && cached is >= 0 && cached <= input
            ? new(input.Value, cached.Value)
            : null;
}

/// <summary>Measured session totals and, when available, a single latest provider request.</summary>
/// <param name="Session">Cumulative counts for requests with comparable input and cache-read counts.</param>
/// <param name="Partial">The measured counts do not cover the whole session.</param>
/// <param name="LatestRequest">One provider request, which may be part of a user turn.</param>
public sealed record PromptCacheUsage(PromptCacheCounts? Session, bool Partial = false, PromptCacheCounts? LatestRequest = null);
