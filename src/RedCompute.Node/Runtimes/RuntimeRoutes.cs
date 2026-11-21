using System.Text;
using RedCompute.NodeLink.Forward;

namespace RedCompute.Node.Runtimes;

/// <summary>What a body must look like on a route, beyond its size.</summary>
public enum BodyRule
{
    /// <summary>No body (a GET).</summary>
    None,

    /// <summary>Streamed as it comes, only capped.</summary>
    Any,

    /// <summary><c>/queue</c>: a JSON object whose only member is <c>delete</c>, a list of prompt ids.</summary>
    QueueDeleteOnly,

    /// <summary><c>/upload/image</c>: multipart with one <c>image</c> file of a safe name, and only safe fields.</summary>
    ImageUpload,
}

/// <summary>An allowed exchange: the request the node sends the runtime, rebuilt from parsed parts.</summary>
public sealed record AllowedRoute(string Path, string Query, long MaxBody, BodyRule Body);

/// <summary>
/// The route allowlists, one table per runtime kind, in the agent's code and versioned with it (Phase 4 design 2.4).
/// The server's path and query are parsed into decoded segments and pairs, checked against the table and the
/// parameter rules, then rebuilt: what reaches the runtime is never the server's raw string. Everything else is
/// refused (<c>route_not_allowed</c>), including <c>/manager/*</c>, <c>/customnode/*</c>, <c>/api/manager/*</c>,
/// <c>/api/userdata*</c>, <c>/internal/*</c> and <c>/extensions</c>.
/// </summary>
public static class RuntimeRoutes
{
    public const long KiB = 1024;
    public const long MiB = 1024 * 1024;

    /// <summary>The route for an HTTP exchange, or why not (a route_not_allowed message).</summary>
    public static AllowedRoute? Http(string kind, string method, string path, string query, out string? refusal)
    {
        refusal = null;
        if (kind != RuntimeKinds.ComfyUi)
        {
            refusal = $"No routes for kind '{kind}'.";
            return null;
        }
        if (Segments(path, out refusal) is not { } segments) return null;
        if (Pairs(query, out refusal) is not { } pairs) return null;
        var rebuiltPath = "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
        var route = (method, segments) switch
        {
            ("GET", ["system_stats"]) => Plain(rebuiltPath, pairs, [], 0, BodyRule.None, ref refusal),
            ("GET", ["queue"]) => Plain(rebuiltPath, pairs, [], 0, BodyRule.None, ref refusal),
            ("GET", ["object_info"]) => Plain(rebuiltPath, pairs, [], 0, BodyRule.None, ref refusal),
            ("GET", ["object_info", var node]) when IsToken(node, 256) => Plain(rebuiltPath, pairs, [], 0, BodyRule.None, ref refusal),
            ("GET", ["history", var id]) when IsId(id) => Plain(rebuiltPath, pairs, ["max_items"], 0, BodyRule.None, ref refusal),
            ("GET", ["view"]) => View(rebuiltPath, pairs, ref refusal),
            ("POST", ["prompt"]) => Plain(rebuiltPath, pairs, [], 16 * MiB, BodyRule.Any, ref refusal),
            ("POST", ["upload", "image"]) => Plain(rebuiltPath, pairs, [], 256 * MiB, BodyRule.ImageUpload, ref refusal),
            ("POST", ["queue"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.QueueDeleteOnly, ref refusal),
            ("POST", ["interrupt"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.Any, ref refusal),
            ("POST", ["free"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.Any, ref refusal),
            ("POST", ["api", "jobs", var id, "cancel"]) when IsId(id) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.Any, ref refusal),
            _ => null,
        };
        if (route is null) refusal ??= $"{method} {path} is not on the {kind} allowlist.";
        return route;
    }

    /// <summary>The route for a WebSocket, or why not.</summary>
    public static AllowedRoute? Ws(string kind, string path, string query, out string? refusal)
    {
        refusal = null;
        if (kind != RuntimeKinds.ComfyUi)
        {
            refusal = $"No routes for kind '{kind}'.";
            return null;
        }
        if (Segments(path, out refusal) is not { } segments) return null;
        if (Pairs(query, out refusal) is not { } pairs) return null;
        if (segments is not ["ws"])
        {
            refusal = $"WS {path} is not on the {kind} allowlist.";
            return null;
        }
        foreach (var (key, value) in pairs)
            if (key != "clientId" || !IsToken(value, 128))
            {
                refusal = "The ComfyUI WebSocket takes one clientId (letters, digits, '-', '_', '.').";
                return null;
            }
        return new AllowedRoute("/ws", Query(pairs), 0, BodyRule.None);
    }

    private static AllowedRoute? Plain(string path, List<KeyValuePair<string, string>> pairs, string[] keys, long maxBody, BodyRule body,
        ref string? refusal)
    {
        foreach (var (key, value) in pairs)
        {
            if (!keys.Contains(key))
            {
                refusal = $"{path} takes no query parameter '{key}'.";
                return null;
            }
            if (!IsToken(value, 32))
            {
                refusal = $"The value of '{key}' is not allowed.";
                return null;
            }
        }
        return new AllowedRoute(path, Query(pairs), maxBody, body);
    }

    /// <summary><c>/view</c>: type output, temp or input; a file name without separators or <c>..</c>; a relative subfolder.</summary>
    private static AllowedRoute? View(string path, List<KeyValuePair<string, string>> pairs, ref string? refusal)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (!seen.Add(key))
            {
                refusal = $"/view takes '{key}' once.";
                return null;
            }
            var ok = key switch
            {
                "filename" => IsFileName(value),
                "subfolder" => IsSubfolder(value),
                "type" => value is "output" or "temp" or "input",
                "preview" => value.Length <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ';' or ','),
                "channel" => value is "rgb" or "rgba" or "a",
                _ => false,
            };
            if (!ok)
            {
                refusal = key is "filename" or "subfolder" or "type" or "preview" or "channel"
                    ? $"/view: '{key}' is not allowed ('{Shorten(value)}')."
                    : $"/view takes no query parameter '{key}'.";
                return null;
            }
        }
        if (!seen.Contains("filename"))
        {
            refusal = "/view needs a filename.";
            return null;
        }
        return new AllowedRoute(path, Query(pairs), 0, BodyRule.None);
    }

    /// <summary>A file name: no separator, no <c>..</c>, no drive, no control character, not hidden.</summary>
    public static bool IsFileName(string name)
        => name.Length is > 0 and <= 255 && name != "." && !name.Contains("..", StringComparison.Ordinal) && name[0] != '.'
           && !name.Any(c => c is '/' or '\\' or ':' or '\0' || char.IsControl(c));

    /// <summary>A relative subfolder: empty, or names joined by '/', each a <see cref="IsFileName"/>.</summary>
    public static bool IsSubfolder(string subfolder)
        => subfolder.Length == 0 || (subfolder.Length <= 255 && subfolder.Split('/').All(IsFileName));

    private static bool IsId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsToken(string value, int max)
        => value.Length is > 0 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string Shorten(string value) => value.Length > 40 ? value[..40] + "…" : value;

    /// <summary>The decoded path segments, or null when one is empty, a dot segment, or holds a separator or control.</summary>
    internal static List<string>? Segments(string path, out string? refusal)
    {
        refusal = null;
        if (path.Length == 0 || path[0] != '/')
        {
            refusal = "A path starts with '/'.";
            return null;
        }
        var segments = new List<string>();
        foreach (var raw in path[1..].Split('/'))
        {
            if (Decode(raw, plusIsSpace: false) is not { } segment)
            {
                refusal = $"The path segment '{Shorten(raw)}' does not decode.";
                return null;
            }
            if (segment.Length == 0 || segment is "." or ".." || segment.Any(c => c is '/' or '\\' or '\0' || char.IsControl(c)))
            {
                refusal = $"The path segment '{Shorten(raw)}' is not allowed.";
                return null;
            }
            segments.Add(segment);
        }
        return segments;
    }

    /// <summary>The decoded query pairs ('+' is a space, as the runtime reads it), or null when one is malformed.</summary>
    internal static List<KeyValuePair<string, string>>? Pairs(string query, out string? refusal)
    {
        refusal = null;
        var pairs = new List<KeyValuePair<string, string>>();
        if (query.Length == 0) return pairs;
        foreach (var part in query.Split('&'))
        {
            var equals = part.IndexOf('=');
            if (part.Length == 0 || equals <= 0)
            {
                refusal = "A query is name=value pairs.";
                return null;
            }
            if (Decode(part[..equals], plusIsSpace: true) is not { } key || Decode(part[(equals + 1)..], plusIsSpace: true) is not { } value)
            {
                refusal = "A query pair does not decode.";
                return null;
            }
            if ((key + value).Any(c => c == '\0' || char.IsControl(c)))
            {
                refusal = "A query value holds a control character.";
                return null;
            }
            pairs.Add(new(key, value));
        }
        return pairs;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Percent-decoding without guesses: every '%' is followed by two hex digits and the bytes are valid UTF-8, else null
    /// (<see cref="Uri.UnescapeDataString"/> would keep a broken escape as text and pass it on).
    /// </summary>
    internal static string? Decode(string raw, bool plusIsSpace)
    {
        var bytes = new byte[raw.Length];
        var count = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '%')
            {
                if (i + 2 >= raw.Length || !Uri.IsHexDigit(raw[i + 1]) || !Uri.IsHexDigit(raw[i + 2])) return null;
                bytes[count++] = (byte)Convert.ToInt32(raw.Substring(i + 1, 2), 16);
                i += 2;
            }
            else if (c > '\x7e' || c < ' ')
            {
                return null;
            }
            else
            {
                bytes[count++] = c == '+' && plusIsSpace ? (byte)' ' : (byte)c;
            }
        }
        try
        {
            return StrictUtf8.GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Query(IEnumerable<KeyValuePair<string, string>> pairs)
        => string.Join('&', pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    // ---------------------------------------------------------------- headers

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "proxy-authenticate", "proxy-authorization", "te", "trailer",
        "transfer-encoding", "upgrade", "host", "content-length", "authorization", "cookie", "forwarded", "x-real-ip",
        "expect",
    };

    /// <summary>
    /// The server's headers the runtime may see: hop-by-hop headers, <c>Host</c>, <c>Authorization</c>, <c>Cookie</c>,
    /// and every <c>X-Forwarded-*</c> and <c>Cf-*</c> header are dropped. CR, LF and NUL never get this far: the OPEN's
    /// parse refused them.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> ForwardedHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        => headers.Where(h => !Dropped.Contains(h.Key)
                              && !h.Key.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase)
                              && !h.Key.StartsWith("cf-", StringComparison.OrdinalIgnoreCase)
                              && !h.Key.StartsWith("sec-websocket-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>boundary</c> of a multipart content type, or null.</summary>
    public static string? Boundary(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var type = headers.LastOrDefault(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value;
        if (type is null || !type.TrimStart().StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var parameter in type.Split(';').Skip(1))
        {
            var trimmed = parameter.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            var boundary = trimmed["boundary=".Length..].Trim('"');
            return boundary.Length is > 0 and <= 70 && boundary.All(c => c is > ' ' and < '\x7f' or ' ') ? boundary : null;
        }
        return null;
    }

    internal static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    internal static ForwardTargetException NotAllowed(string message) => new(ForwardErrorCodes.RouteNotAllowed, message);
}
