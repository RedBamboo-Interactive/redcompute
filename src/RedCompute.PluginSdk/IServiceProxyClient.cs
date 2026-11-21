using System.Net.Http;

namespace RedCompute.PluginSdk;

/// <summary>
/// A proxy-mode provider whose backend its execution host serves (Phase 4, design 3.3). The generic
/// proxy (<c>StreamingProxy</c>: proxy-mode generate and the <c>/{slug}/{**path}</c> catch-all)
/// sends through <see cref="ProxyClient"/>, built on the host's handler, instead of a client of its
/// own, so a remote node's service is reached only over its link.
/// </summary>
public interface IServiceProxyClient
{
    /// <summary>The generic proxy's timeout, the one its own client always had.</summary>
    static readonly TimeSpan ProxyTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A client over the host's handler with <see cref="ProxyTimeout"/>; the provider owns it.</summary>
    HttpClient ProxyClient { get; }
}
