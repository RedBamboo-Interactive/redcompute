using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RedCompute.NodeLink;

/// <summary>
/// Protocol versions and features (design 4.8). The protocol number covers the frame semantics and
/// the core of channel 0; each side supports [current - 2, current]. Additive change goes through
/// features, never through a protocol bump.
/// </summary>
public static class LinkProtocol
{
    public const int Current = 1;
    public const int Minimum = Current - 2 < 1 ? 1 : Current - 2;

    /// <summary>The WebSocket sub-protocol the ingress accepts.</summary>
    public const string SubProtocol = "leaf-node.v1";

    public static ProtocolRange Supported { get; } = new(Minimum, Current);

    /// <summary>The highest version both ranges contain, or null when they do not overlap.</summary>
    public static int? Negotiate(ProtocolRange node, ProtocolRange server)
    {
        if (node.Min > node.Max || server.Min > server.Max) return null;
        var high = Math.Min(node.Max, server.Max);
        var low = Math.Max(node.Min, server.Min);
        return high >= low ? high : null;
    }

    /// <summary>The features both sides list, in the server's order.</summary>
    public static string[] IntersectFeatures(IEnumerable<string>? node, IEnumerable<string>? server)
    {
        var offered = new HashSet<string>(node ?? [], StringComparer.Ordinal);
        return (server ?? []).Where(offered.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ProtocolRange(int Min, int Max);

/// <summary>Well-known feature names.</summary>
public static class LinkFeatures
{
    public const string DescribeV1 = "describe.v1";
    public const string HardwareV1 = "hardware.v1";
    public const string CliV1 = "cli.v1";
    public const string DiagEcho = "diag.echo";
    public const string SpoolDisk = "spool.disk";
}

public sealed record AgentInfo(string Version, string Os, string Arch);

public sealed record ChannelResume(uint Id, ulong Rx);

public sealed record LinkResume(string LinkSessionId, IReadOnlyList<ChannelResume> Channels);

/// <summary>The node's first frame: <c>{method: "link.hello", params: LinkHello}</c>.</summary>
public sealed record LinkHello(string NodeId, ProtocolRange Protocol, AgentInfo Agent, IReadOnlyList<string> Features,
    LinkResume? Resume = null);

public sealed record LinkLimits(int MaxFrame, int MaxChannels);

public sealed record AgentPolicy(string Minimum, string Recommended, string? DownloadUrl = null);

/// <summary>The server's answer: <c>{result: LinkWelcome}</c>.</summary>
public sealed record LinkWelcome(int Protocol, string LinkSessionId, bool Resumed, string ServerEpoch,
    IReadOnlyList<string> Features, int HeartbeatSeconds, LinkLimits Limits, AgentPolicy? AgentPolicy = null,
    IReadOnlyList<ChannelResume>? Channels = null);

/// <summary>A refused handshake: <c>{error: LinkHandshakeError}</c>, then the link closes with the code.</summary>
public sealed record LinkHandshakeError(string Code, string Message, int? ServerMin = null, int? ServerMax = null);

/// <summary>Reading and writing the handshake frames (the seq-0 DATA frame on channel 0).</summary>
public static class LinkHandshake
{
    public const string HelloMethod = "link.hello";

    public static byte[] HelloFrame(LinkHello hello)
        => Frame(new JsonObject { ["method"] = HelloMethod, ["params"] = JsonSerializer.SerializeToNode(hello, LinkProtocol.Json) });

    public static byte[] WelcomeFrame(LinkWelcome welcome)
        => Frame(new JsonObject { ["result"] = JsonSerializer.SerializeToNode(welcome, LinkProtocol.Json) });

    public static byte[] ErrorFrame(LinkHandshakeError error)
        => Frame(new JsonObject { ["error"] = JsonSerializer.SerializeToNode(error, LinkProtocol.Json) });

    private static byte[] Frame(JsonObject body)
        => FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 0, 0, 0, JsonSerializer.SerializeToUtf8Bytes(body)));

    /// <summary>The hello in a handshake frame; anything else is a protocol error.</summary>
    public static LinkHello ReadHello(LinkFrame frame)
    {
        var body = Body(frame);
        if (body["method"]?.GetValue<string>() != HelloMethod || body["params"] is not JsonObject parameters)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The first frame must be link.hello.");
        var hello = Deserialize<LinkHello>(parameters);
        if (string.IsNullOrWhiteSpace(hello.NodeId) || hello.Protocol is null || hello.Agent is null)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "link.hello needs nodeId, protocol and agent.");
        return hello with { Features = hello.Features ?? [] };
    }

    /// <summary>The welcome, or the refusal, in a handshake frame.</summary>
    public static (LinkWelcome? Welcome, LinkHandshakeError? Error) ReadWelcome(LinkFrame frame)
    {
        var body = Body(frame);
        if (body["error"] is JsonObject error) return (null, Deserialize<LinkHandshakeError>(error));
        if (body["result"] is not JsonObject result)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The server's first frame must be the welcome.");
        var welcome = Deserialize<LinkWelcome>(result);
        if (string.IsNullOrWhiteSpace(welcome.LinkSessionId) || welcome.Limits is null)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The welcome needs linkSessionId and limits.");
        return (welcome with { Features = welcome.Features ?? [] }, null);
    }

    private static JsonObject Body(LinkFrame frame)
    {
        if (!frame.IsHandshake)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "Expected the handshake frame first.");
        try
        {
            return JsonNode.Parse(frame.Payload.Span) as JsonObject
                ?? throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The handshake is a JSON object.");
        }
        catch (JsonException)
        {
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The handshake is not valid JSON.");
        }
    }

    private static T Deserialize<T>(JsonObject node)
    {
        try
        {
            return node.Deserialize<T>(LinkProtocol.Json)
                ?? throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Empty {typeof(T).Name}.");
        }
        catch (JsonException error)
        {
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"Malformed {typeof(T).Name}: {error.Message}");
        }
    }
}

/// <summary>Agent versions: major.minor.patch with an optional pre-release suffix, which sorts lower.</summary>
public static class AgentVersions
{
    public static int Compare(string a, string b)
    {
        var (va, pa) = Parse(a);
        var (vb, pb) = Parse(b);
        var byVersion = va.CompareTo(vb);
        if (byVersion != 0) return byVersion;
        if (pa is null) return pb is null ? 0 : 1;
        return pb is null ? -1 : string.CompareOrdinal(pa, pb);
    }

    public static bool IsValid(string? text) => text is not null && TryParse(text, out _, out _);

    private static (Version Version, string? Pre) Parse(string text)
        => TryParse(text, out var version, out var pre) ? (version, pre) : (new Version(0, 0, 0), text);

    private static bool TryParse(string text, out Version version, out string? pre)
    {
        var dash = text.IndexOf('-');
        pre = dash < 0 ? null : text[(dash + 1)..];
        var core = (dash < 0 ? text : text[..dash]).Split('+')[0];
        if (Version.TryParse(core, out var parsed) && parsed.Build >= 0)
        {
            version = new Version(parsed.Major, parsed.Minor, parsed.Build);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }
}
