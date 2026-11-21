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

    /// <summary>
    /// <c>/queue</c>: a JSON object whose only member is <c>delete</c>, a list of prompt ids the server submitted through
    /// this node (<see cref="SubmittedPrompts"/>).
    /// </summary>
    QueueDeleteOnly,

    /// <summary><c>/prompt</c>: a JSON object that does not choose its own <c>prompt_id</c> (ComfyUI assigns it).</summary>
    PromptSubmit,

    /// <summary>
    /// <c>/interrupt</c>: exactly <c>{"prompt_id": id}</c> with an id the server submitted through this node. ComfyUI then
    /// interrupts only if that prompt is the one running; without a <c>prompt_id</c> it would interrupt whatever runs,
    /// the owner's own work included.
    /// </summary>
    InterruptOwnPrompt,

    /// <summary><c>/upload/image</c>: multipart with one <c>image</c> file of a safe name, and only safe fields.</summary>
    ImageUpload,
}

/// <summary>
/// An allowed exchange: the request the node sends the runtime, rebuilt from parsed parts. <paramref name="PromptId"/> is
/// the prompt a route acts on by its path (<c>/api/jobs/{id}/cancel</c>): one the server must have submitted.
/// <paramref name="Response"/> is what the node does with the runtime's answer (<see cref="ResponseRule"/>).
/// </summary>
public sealed record AllowedRoute(string Path, string Query, long MaxBody, BodyRule Body, string? PromptId = null,
    ResponseRule Response = ResponseRule.Pass)
{
    /// <summary>Whether the route needs a body (its rule reads one): a body-less request there is refused.</summary>
    public bool NeedsBody => Body is BodyRule.ImageUpload or BodyRule.QueueDeleteOnly or BodyRule.PromptSubmit or BodyRule.InterruptOwnPrompt;
}

/// <summary>
/// The route allowlists, one table per runtime kind, in the agent's code and versioned with it (Phase 4 design 2.4).
/// The server's path and query are parsed into decoded segments and pairs, checked against the table and the
/// parameter rules, then rebuilt: what reaches the runtime is never the server's raw string. Everything else is
/// refused (<c>route_not_allowed</c>), including <c>/manager/*</c>, <c>/customnode/*</c>, <c>/api/manager/*</c>,
/// <c>/api/userdata*</c>, <c>/internal/*</c> and <c>/extensions</c>.
/// <para>
/// What the owner keeps on the node stays there (review P4-P7, item 4): <c>/view</c> reads only <c>output</c> and
/// <c>temp</c> (never <c>input</c>, the owner's own images); <c>/object_info</c> is not on the list, since it lists the
/// owner's input files and models and the server never asks for it; and the routes that stop or dequeue a prompt act only
/// on prompts the server submitted through this node.
/// </para>
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
            ("GET", ["queue"]) => Plain(rebuiltPath, pairs, [], 0, BodyRule.None, ref refusal) is { } queue
                ? queue with { Response = ResponseRule.QueueRedacted }
                : null,
            ("GET", ["history", var id]) when IsId(id) => Plain(rebuiltPath, pairs, ["max_items"], 0, BodyRule.None, ref refusal),
            ("GET", ["view"]) => View(rebuiltPath, pairs, ref refusal),
            ("POST", ["prompt"]) => Plain(rebuiltPath, pairs, [], 16 * MiB, BodyRule.PromptSubmit, ref refusal),
            ("POST", ["upload", "image"]) => Plain(rebuiltPath, pairs, [], 256 * MiB, BodyRule.ImageUpload, ref refusal),
            ("POST", ["queue"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.QueueDeleteOnly, ref refusal),
            ("POST", ["interrupt"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.InterruptOwnPrompt, ref refusal),
            ("POST", ["free"]) => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.Any, ref refusal),
            ("POST", ["api", "jobs", var id, "cancel"]) when IsId(id)
                => Plain(rebuiltPath, pairs, [], 64 * KiB, BodyRule.Any, ref refusal) is { } cancel ? cancel with { PromptId = id } : null,
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

    /// <summary><c>/view</c>: type output or temp; a file name without separators or <c>..</c>; a relative subfolder.</summary>
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
                "type" => value is "output" or "temp",
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

    internal static bool IsId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

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

    /// <summary>How many <c>Content-Type</c> headers the server sent (more than one is refused: review P4-P7, item 5).</summary>
    public static int ContentTypes(IEnumerable<KeyValuePair<string, string>> headers)
        => headers.Count(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>boundary</c> of the one multipart content type, or null: not exactly one <c>Content-Type</c>, not
    /// <c>multipart/form-data</c>, no boundary or two, or a boundary outside RFC 2046's characters (1 to 70, digits,
    /// letters, <c>'()+_,-./:=?</c> and inner spaces). The node then sends the runtime exactly
    /// <c>multipart/form-data; boundary="…"</c> rebuilt from it (<see cref="UploadContentType"/>), never the server's string.
    /// </summary>
    public static string? Boundary(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var types = headers.Where(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        if (types is not [var type]) return null;
        var parameters = type.Split(';');
        if (!parameters[0].Trim().Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        string? boundary = null;
        foreach (var parameter in parameters.Skip(1))
        {
            var trimmed = parameter.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            if (boundary is not null) return null;
            var value = trimmed["boundary=".Length..];
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            boundary = value;
        }
        return boundary is { Length: > 0 and <= 70 } && boundary[^1] != ' '
               && boundary.All(c => char.IsAsciiLetterOrDigit(c) || "'()+_,-./:=? ".Contains(c))
            ? boundary
            : null;
    }

    /// <summary>The one <c>Content-Type</c> an upload reaches the runtime with.</summary>
    public static string UploadContentType(string boundary) => $"multipart/form-data; boundary=\"{boundary}\"";

    internal static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    internal static ForwardTargetException NotAllowed(string message) => new(ForwardErrorCodes.RouteNotAllowed, message);
}
