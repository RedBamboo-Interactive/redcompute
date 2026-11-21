using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.NodeLink.Forward;

/// <summary>The two forward modes: one HTTP exchange per <c>port</c> channel, one WebSocket per <c>port.ws</c>.</summary>
public static class ForwardModes
{
    public const string Http = "http";
    public const string Ws = "ws";
}

/// <summary>
/// The forward's error codes (Phase 4 design 2.2): the node's refusals at OPEN, and the failures either side resets
/// an exchange with.
/// </summary>
public static class ForwardErrorCodes
{
    public const string RuntimeUnknown = "runtime_unknown";
    public const string RuntimeNotRunning = "runtime_not_running";
    public const string RouteNotAllowed = "route_not_allowed";
    public const string BodyTooLarge = "body_too_large";
    public const string Busy = "busy";

    /// <summary>A body's bytes or hash differ from its trailer, or the body ended before its head or trailer.</summary>
    public const string Truncated = "forward_truncated";

    /// <summary>A malformed OPEN, head, trailer or WS message: the peer broke the forward protocol.</summary>
    public const string BadForward = "bad_forward";

    /// <summary>The response head passes <see cref="ForwardLimits.MaxHeadBytes"/>.</summary>
    public const string HeadTooLarge = "head_too_large";

    /// <summary>The node could not reach the runtime, or the runtime failed the exchange.</summary>
    public const string UpstreamFailed = "upstream_failed";

    /// <summary>The link did not negotiate the forward feature (an older agent).</summary>
    public const string Unsupported = "forward_unsupported";

    /// <summary>No link session to the node right now.</summary>
    public const string NodeOffline = "node_offline";
}

/// <summary>The forward's caps (design 2.3). Both sides use the same defaults.</summary>
public sealed record ForwardLimits
{
    /// <summary>Open exchanges (HTTP or WS) per runtime. A full table queues on the server, never on the node.</summary>
    public int ExchangesPerRuntime { get; init; } = 32;

    /// <summary><c>port</c> and <c>port.ws</c> channels per link (the link itself allows 256).</summary>
    public int ChannelsPerLink { get; init; } = 128;

    public int MaxHeadBytes { get; init; } = 64 * 1024;

    /// <summary>A WS message, its type byte included: the <c>port.ws</c> class's reassembly cap.</summary>
    public int MaxWsMessageBytes { get; init; } = ChannelClasses.PortWs.ReassemblyCap;

    /// <summary>How often, and how long apart, the server retries an OPEN the node refused <c>busy</c>.</summary>
    public int BusyRetries { get; init; } = 5;
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public static ForwardLimits Default { get; } = new();
}

/// <summary>An exchange failed before its response (HTTP) or before the socket opened (WS), with a forward code.</summary>
public sealed class ForwardException(string code, string message, Exception? inner = null) : HttpRequestException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>A response body or WS stream failed after it started: truncated, reset, or the link lost.</summary>
public sealed class ForwardStreamException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The <c>meta</c> of a forward's OPEN (design 2.2). Parsing is strict: exactly the mode's members, each within its
/// bounds, so nothing the node does not read can ride along. The node rebuilds the request it sends to the runtime
/// from these parsed parts; it never copies a raw request line.
/// </summary>
public sealed record ForwardOpen(string Runtime, string Mode, string? Method, string Path, string Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers, long BodyBytes)
{
    public const int MaxRuntimeLength = 64;
    public const int MaxPathLength = 2048;
    public const int MaxQueryLength = 8192;
    public const int MaxHeaders = 64;
    public const int MaxHeaderNameLength = 256;
    public const int MaxHeaderValueLength = 8192;
    public const long MaxBodyBytes = 1L << 40;

    private static readonly string[] HttpMembers = ["runtime", "mode", "method", "path", "query", "headers", "bodyBytes"];
    private static readonly string[] WsMembers = ["runtime", "mode", "path", "query"];

    public static ForwardOpen Http(string runtime, string method, string path, string query,
        IReadOnlyList<KeyValuePair<string, string>> headers, long bodyBytes)
        => Checked(new ForwardOpen(runtime, ForwardModes.Http, method, path, query, headers, bodyBytes));

    public static ForwardOpen Ws(string runtime, string path, string query)
        => Checked(new ForwardOpen(runtime, ForwardModes.Ws, null, path, query, [], 0));

    private static ForwardOpen Checked(ForwardOpen open)
        => open.Validate() is { } error ? throw new ArgumentException(error) : open;

    public JsonObject ToMeta()
    {
        var meta = new JsonObject { ["runtime"] = Runtime, ["mode"] = Mode };
        if (Mode == ForwardModes.Http) meta["method"] = Method;
        meta["path"] = Path;
        meta["query"] = Query;
        if (Mode == ForwardModes.Http)
        {
            var headers = new JsonArray();
            foreach (var (name, value) in Headers) headers.Add(new JsonArray(name, value));
            meta["headers"] = headers;
            meta["bodyBytes"] = BodyBytes;
        }
        return meta;
    }

    /// <summary>Reads an OPEN's meta for <paramref name="mode"/>; null with <paramref name="error"/> when it is not one.</summary>
    public static ForwardOpen? Parse(JsonObject? meta, string mode, out string? error)
    {
        error = null;
        if (meta is null)
        {
            error = "The OPEN has no meta.";
            return null;
        }
        var members = mode == ForwardModes.Http ? HttpMembers : WsMembers;
        if (meta.Count != members.Length || members.Any(m => !meta.ContainsKey(m)))
        {
            error = $"A {mode} forward's meta has exactly: {string.Join(", ", members)}.";
            return null;
        }
        var runtime = Str(meta["runtime"]);
        var parsedMode = Str(meta["mode"]);
        var path = Str(meta["path"]);
        var query = Str(meta["query"]);
        if (runtime is null || parsedMode is null || path is null || query is null)
        {
            error = "runtime, mode, path and query are strings.";
            return null;
        }
        if (parsedMode != mode)
        {
            error = $"This channel carries mode '{mode}'.";
            return null;
        }
        string? method = null;
        var headers = new List<KeyValuePair<string, string>>();
        long bodyBytes = 0;
        if (mode == ForwardModes.Http)
        {
            method = Str(meta["method"]);
            if (meta["headers"] is not JsonArray list)
            {
                error = "headers is an array of [name, value] pairs.";
                return null;
            }
            foreach (var item in list)
            {
                if (item is not JsonArray { Count: 2 } pair || Str(pair[0]) is not { } name || Str(pair[1]) is not { } value)
                {
                    error = "headers is an array of [name, value] pairs.";
                    return null;
                }
                headers.Add(new(name, value));
                if (headers.Count > MaxHeaders) break;
            }
            if (meta["bodyBytes"] is not JsonValue body || !body.TryGetValue<long>(out bodyBytes)
                || body.GetValueKind() != JsonValueKind.Number)
            {
                error = "bodyBytes is an integer.";
                return null;
            }
        }
        var open = new ForwardOpen(runtime, mode, method, path, query, headers, bodyBytes);
        error = open.Validate();
        return error is null ? open : null;
    }

    private static string? Str(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Null when every part is within bounds, else why not.</summary>
    public string? Validate()
    {
        if (!IsRuntimeId(Runtime)) return $"runtime is 1-{MaxRuntimeLength} of [a-z0-9._@-], starting with a letter or digit.";
        if (Mode is not (ForwardModes.Http or ForwardModes.Ws)) return "mode is http or ws.";
        if (Mode == ForwardModes.Http && !IsMethod(Method)) return "method is 1-16 upper-case letters.";
        if (Mode == ForwardModes.Ws && (Method is not null || Headers.Count != 0 || BodyBytes != 0)) return "A ws forward has no method, headers or body.";
        if (!IsPath(Path)) return $"path starts with '/', is at most {MaxPathLength} visible ASCII characters, without '?' or '#'.";
        if (!IsQuery(Query)) return $"query is at most {MaxQueryLength} visible ASCII characters, without '#'.";
        if (Headers.Count > MaxHeaders) return $"At most {MaxHeaders} headers.";
        foreach (var (name, value) in Headers)
        {
            if (!IsToken(name, MaxHeaderNameLength)) return "A header name is an HTTP token.";
            if (!IsHeaderValue(value)) return $"A header value is at most {MaxHeaderValueLength} characters, without CR, LF, NUL or other controls.";
        }
        if (BodyBytes is < -1 or > MaxBodyBytes) return "bodyBytes is -1 (chunked) or 0..2^40.";
        return null;
    }

    public static bool IsRuntimeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxRuntimeLength || !char.IsAsciiLetterOrDigit(id[0])) return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '@' or '-')) return false;
        return true;
    }

    private static bool IsMethod(string? method)
        => method is { Length: > 0 and <= 16 } && method.All(char.IsAsciiLetterUpper);

    private static bool IsPath(string path)
        => path.Length is > 0 and <= MaxPathLength && path[0] == '/' && path.All(c => c is > ' ' and < '\x7f' and not '?' and not '#');

    private static bool IsQuery(string query)
        => query.Length <= MaxQueryLength && query.All(c => c is > ' ' and < '\x7f' and not '#');

    private static bool IsToken(string name, int max)
        => name.Length > 0 && name.Length <= max && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    private static bool IsHeaderValue(string value)
        => value.Length <= MaxHeaderValueLength && value.All(c => c == '\t' || (c >= ' ' && c != '\x7f'));
}

/// <summary>
/// The response head: the node's first bytes on a <c>port</c> channel, <c>u32 headLength</c> (little-endian) then the
/// JSON <c>{status, headers:[[k,v]…]}</c>, at most <see cref="ForwardLimits.MaxHeadBytes"/>. It may span frames (they
/// are cut at 16 KiB), so <see cref="Reader"/> takes it a frame at a time.
/// </summary>
public sealed record ForwardHead(int Status, IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public byte[] Encode(int maxBytes = 64 * 1024)
    {
        if (Status is < 100 or > 599) throw new ArgumentException("A status is 100..599.");
        var headers = new JsonArray();
        foreach (var (name, value) in Headers) headers.Add(new JsonArray(name, value));
        var json = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = Status, ["headers"] = headers });
        if (json.Length > maxBytes) throw new ForwardException(ForwardErrorCodes.HeadTooLarge, $"The response head is {json.Length} bytes, over {maxBytes}.");
        var bytes = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)json.Length);
        json.CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Parses a head's JSON; throws <see cref="ForwardStreamException"/> (<c>bad_forward</c>) when it is not one.</summary>
    public static ForwardHead Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject body || body.Count != 2
                || body["status"] is not JsonValue statusValue || statusValue.GetValueKind() != JsonValueKind.Number
                || !statusValue.TryGetValue<int>(out var status) || status is < 100 or > 599
                || body["headers"] is not JsonArray list)
                throw Bad("A head is exactly {status: 100..599, headers: [[name, value]…]}.");
            var headers = new List<KeyValuePair<string, string>>(list.Count);
            foreach (var item in list)
            {
                if (item is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue n || pair[1] is not JsonValue v
                    || n.GetValueKind() != JsonValueKind.String || v.GetValueKind() != JsonValueKind.String)
                    throw Bad("A head's headers are [name, value] string pairs.");
                var name = n.GetValue<string>();
                var value = v.GetValue<string>();
                if (name.Length == 0 || name.Any(c => c <= ' ' || c >= '\x7f' || c == ':') || value.Any(c => c is '\r' or '\n' or '\0'))
                    throw Bad("A head's header has a bad name or a CR, LF or NUL in its value.");
                headers.Add(new(name, value));
            }
            return new ForwardHead(status, headers);
        }
        catch (JsonException)
        {
            throw Bad("The head is not JSON.");
        }
    }

    private static ForwardStreamException Bad(string message) => new(ForwardErrorCodes.BadForward, message);

    /// <summary>Accumulates the head from the channel's first frames.</summary>
    public sealed class Reader(int maxBytes = 64 * 1024)
    {
        private readonly byte[] _length = new byte[4];
        private int _lengthHave;
        private byte[]? _json;
        private int _jsonHave;

        public ForwardHead? Head { get; private set; }

        /// <summary>Takes bytes until the head is complete; returns how many it took (the rest is body).</summary>
        public int Feed(ReadOnlySpan<byte> data)
        {
            if (Head is not null) return 0;
            var taken = 0;
            if (_lengthHave < 4)
            {
                var take = Math.Min(4 - _lengthHave, data.Length);
                data[..take].CopyTo(_length.AsSpan(_lengthHave));
                _lengthHave += take;
                taken += take;
                if (_lengthHave < 4) return taken;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(_length);
                if (length == 0 || length > maxBytes)
                    throw new ForwardStreamException(ForwardErrorCodes.HeadTooLarge, $"A head of {length} bytes (at most {maxBytes}).");
                _json = new byte[length];
            }
            var rest = data[taken..];
            var copy = Math.Min(_json!.Length - _jsonHave, rest.Length);
            rest[..copy].CopyTo(_json.AsSpan(_jsonHave));
            _jsonHave += copy;
            taken += copy;
            if (_jsonHave == _json.Length)
            {
                Head = Parse(_json);
                _json = null;
            }
            return taken;
        }
    }
}

/// <summary>
/// A body's trailer, the payload of the CLOSE that ends it: <c>{bytes, sha256}</c> (lower-case hex), what its sender
/// read from its source. The receiver fails the exchange <c>forward_truncated</c> when what it got differs.
/// </summary>
public sealed record ForwardTrailer(long Bytes, string Sha256)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["bytes"] = Bytes, ["sha256"] = Sha256 });

    public static ForwardTrailer? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject body || body.Count != 2
                || body["bytes"] is not JsonValue b || b.GetValueKind() != JsonValueKind.Number || !b.TryGetValue<long>(out var bytes) || bytes < 0
                || body["sha256"] is not JsonValue s || s.GetValueKind() != JsonValueKind.String)
                return null;
            var sha = s.GetValue<string>();
            return sha.Length == 64 && sha.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') ? new ForwardTrailer(bytes, sha) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when <paramref name="payload"/> is the trailer of exactly these bytes, else the failure.</summary>
    public static ForwardStreamException? Check(ReadOnlySpan<byte> payload, long bytes, string sha256)
    {
        if (TryParse(payload) is not { } trailer)
            return new ForwardStreamException(ForwardErrorCodes.Truncated, "The body ended without a valid trailer.");
        if (trailer.Bytes != bytes || !string.Equals(trailer.Sha256, sha256, StringComparison.Ordinal))
            return new ForwardStreamException(ForwardErrorCodes.Truncated,
                $"The body has {bytes} bytes, its trailer says {trailer.Bytes}{(trailer.Bytes == bytes ? " with another hash" : "")}.");
        return null;
    }
}

/// <summary>
/// A <c>port.ws</c> link message: one byte of type, then the payload. Type 0 is the node's "connected" (its
/// handshake with the runtime succeeded), sent once before anything else; 1 is a text message, 2 a binary one. A
/// WS close travels as the channel's CLOSE, its payload <c>{code, reason}</c> (empty: no status).
/// </summary>
public static class ForwardWsMessage
{
    public const byte Ready = 0;
    public const byte Text = 1;
    public const byte Binary = 2;

    public static byte[] Encode(byte type, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[1 + payload.Length];
        bytes[0] = type;
        payload.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    public static byte[] EncodeClose(int? code, string? reason)
    {
        if (code is null) return [];
        var text = reason ?? "";
        if (Encoding.UTF8.GetByteCount(text) > 123) text = text[..Math.Min(text.Length, 60)];
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["code"] = code.Value, ["reason"] = text });
    }

    /// <summary>The close's status and reason; (null, null) for an empty payload; throws for a malformed one.</summary>
    public static (int? Code, string? Reason) ParseClose(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return (null, null);
        try
        {
            if (JsonNode.Parse(payload) is JsonObject body && body.Count == 2
                && body["code"] is JsonValue c && c.GetValueKind() == JsonValueKind.Number && c.TryGetValue<int>(out var code) && code is >= 1000 and <= 4999
                && body["reason"] is JsonValue r && r.GetValueKind() == JsonValueKind.String)
            {
                var reason = r.GetValue<string>();
                if (Encoding.UTF8.GetByteCount(reason) <= 123) return (code, reason);
            }
        }
        catch (JsonException)
        {
        }
        throw new ForwardStreamException(ForwardErrorCodes.BadForward, "A WS close is {code: 1000..4999, reason (at most 123 bytes)}.");
    }
}
