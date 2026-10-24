using RedCompute.Core.Configuration;

namespace RedCompute.PluginSdk;

/// <summary>
/// Send-time guard for vaulted provider API keys. RedLeaf binds every stored key to the
/// origin (scheme, host, port) it was entered for and releases it only together with
/// that destination. Every outbound use checks the URL it is actually about to call,
/// so a cached key can never follow a later endpoint, base URL or Extra change to
/// another host. A key that RedLeaf did not release with a binding is never sent.
/// </summary>
public static class ProviderKeyBindings
{
    public const string MismatchMessage = "endpoint changed since the key was stored; re-enter the API key";
    public const string UnboundMessage = "the API key is not bound to a destination; re-enter the API key";

    /// <summary>The provider's key when <paramref name="destination"/> is its bound origin, otherwise null.</summary>
    public static string? Release(ProviderConfig config, Uri destination)
        => config.KeyBinding is { } binding
            ? Release(binding.ApiKey, binding.Destination, destination.AbsoluteUri)
            : null;

    /// <summary>The provider's key for <paramref name="destination"/>, or an exception with the user-facing reason.</summary>
    public static string Require(ProviderConfig config, Uri destination)
        => config.KeyBinding is null
            ? throw new InvalidOperationException(UnboundMessage)
            : Release(config, destination) ?? throw new InvalidOperationException(MismatchMessage);

    /// <summary>The one release rule: <paramref name="apiKey"/> only to the origin it is bound to.</summary>
    public static string? Release(string? apiKey, string? boundDestination, string? destination)
        => !string.IsNullOrEmpty(apiKey)
            && boundDestination is not null
            && string.Equals(boundDestination, Normalize(destination), StringComparison.Ordinal)
                ? apiKey
                : null;

    /// <summary>scheme://host:port, lower-cased, with ws/wss folded into http/https (same rule as RedLeaf).</summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
            return null;
        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => "https",
            "http" or "ws" => "http",
            _ => null,
        };
        if (scheme is null) return null;
        var port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port;
        return $"{scheme}://{uri.IdnHost.ToLowerInvariant()}:{port}";
    }
}
