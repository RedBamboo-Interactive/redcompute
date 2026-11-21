using System.Net.Http;

namespace RedCompute.PluginSdk.Hosting;

public static class HostNetworkExtensions
{
    /// <summary>
    /// An <see cref="HttpClient"/> over the host's handler for the service it serves at
    /// <paramref name="hostBaseUrl"/> (<see cref="IHostNetwork.CreateServiceHandler"/>). Locally the same
    /// as <c>new HttpClient { Timeout = timeout }</c>. A base URL that is not an absolute Uri gets a
    /// handler that refuses every request: locally no request reaches it (an HttpClient refuses such a
    /// URL first), and elsewhere nothing goes around the host.
    /// </summary>
    public static HttpClient CreateServiceClient(this IHostNetwork network, string hostBaseUrl, TimeSpan timeout)
        => new(Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)
            ? network.CreateServiceHandler(uri)
            : new UnresolvableServiceHandler(hostBaseUrl)) { Timeout = timeout };

    private sealed class UnresolvableServiceHandler(string hostBaseUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException($"'{hostBaseUrl}' is not a service address");
    }

    /// <summary>
    /// The base URL the server uses for a service the host serves at <paramref name="hostBaseUrl"/>
    /// (scheme, host and port, no trailing slash). Locally the host resolves every URI to itself, so
    /// the configured string comes back exactly as given, casing and all.
    /// </summary>
    public static string ResolveServiceBaseUrl(this IHostNetwork network, string hostBaseUrl)
    {
        if (!Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)) return hostBaseUrl;
        var resolved = network.ResolveServiceUri(uri);
        return ReferenceEquals(resolved, uri) || resolved.Equals(uri)
            ? hostBaseUrl
            : resolved.GetLeftPart(UriPartial.Authority);
    }
}
