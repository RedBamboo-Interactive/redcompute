namespace RedCompute.PluginSdk.Hosting;

public static class HostNetworkExtensions
{
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
