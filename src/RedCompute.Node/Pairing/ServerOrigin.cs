namespace RedCompute.Node.Pairing;

/// <summary>
/// The public origin of the Leaf server a node pairs with and pins (design 3.1): <c>https://host[:port]</c>,
/// lower-case, no path. Plain <c>http</c> only for a loopback host (development and tests): a node
/// never sends its assertion in clear text over a network.
/// </summary>
public static class ServerOrigin
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
            throw new NodeCommandException("bad_server", $"'{text}' is not a server address. Use the https:// address you open Leaf with.",
                NodeExitCodes.Usage);
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new NodeCommandException("insecure_server", $"'{text}' is not https. A node pairs only over https.", NodeExitCodes.Usage);
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new NodeCommandException("bad_server", $"'{text}' must be the server's address only, like https://leaf.example.com.",
                NodeExitCodes.Usage);
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>The link's WebSocket address: <c>wss://</c> (or <c>ws://</c> for loopback) + the path the server named at pairing.</summary>
    public static Uri LinkUri(string origin, string linkPath)
    {
        var scheme = origin.StartsWith("https://", StringComparison.Ordinal) ? "wss://" : "ws://";
        return new Uri(scheme + origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..] + linkPath);
    }

    /// <summary>A path the server named (<c>/api/nodes/link</c>): absolute, no scheme, host, query or traversal.</summary>
    public static bool IsSafePath(string? path)
        => path is { Length: > 1 and <= 256 }
           && path[0] == '/' && path[1] != '/'
           && !path.Contains("..", StringComparison.Ordinal)
           && path.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '-' or '_' or '.');
}
