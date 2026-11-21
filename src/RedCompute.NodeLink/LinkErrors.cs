namespace RedCompute.NodeLink;

/// <summary>WebSocket close codes the link uses (design 4.7, 5.1).</summary>
public static class LinkCloseCodes
{
    public const int Normal = 1000;
    public const int GoingAway = 1001;
    public const int Abnormal = 1006;
    public const int MessageTooBig = 1009;
    /// <summary>The server failed on its own side (a fault, never a verdict on the node): the node retries.</summary>
    public const int InternalError = 1011;
    public const int ServiceRestart = 1012;
    /// <summary>A malformed frame, a text message, a hello for another node, a flow-control violation.</summary>
    public const int ProtocolError = 4400;
    public const int Revoked = 4403;
    /// <summary>No hello/welcome in time, or three missed heartbeats.</summary>
    public const int Timeout = 4408;
    /// <summary>A newer connection of the same node won.</summary>
    public const int Superseded = 4409;
    public const int UpgradeRequired = 4426;
    public const int RateLimited = 4429;
    /// <summary>E2E: the handshake is not for a link key the server holds, or the server did not prove the pinned one.</summary>
    public const int LinkKeyUnknown = 4495;
    /// <summary>E2E: this node's link is encrypted; the server refuses plaintext from it.</summary>
    public const int E2eRequired = 4496;
    /// <summary>E2E: the node's link key is not bound to its paired identity.</summary>
    public const int LinkIdentityInvalid = 4497;
}

/// <summary>
/// The server's in-band verdict on a node: <c>link.verdict {code}</c>, a control call it makes before it ends the
/// node's session for good. On an encrypted link it is sealed like every frame, so a pinned node can tell it from a
/// close code or an HTTP status, which anything on the path (the kernel's bridge, Cloudflare) could send. Only this
/// verdict, or the welcome's own refusal, lets a pinned node delete its keys.
/// </summary>
public static class LinkVerdicts
{
    public const string Method = "link.verdict";
    public const string Revoked = "node_revoked";
    public const string Unknown = "node_unknown";
}

/// <summary>The peer broke the protocol; the link closes with <see cref="CloseCode"/>.</summary>
public sealed class LinkProtocolException(int closeCode, string message) : Exception(message)
{
    public int CloseCode { get; } = closeCode;
}

/// <summary>The transport closed (by the peer, by us, or by the network).</summary>
public sealed class LinkClosedException(int? code, string? reason) : Exception(
    code is null ? "The link closed." : $"The link closed with {code}{(string.IsNullOrEmpty(reason) ? "" : ": " + reason)}.")
{
    public int? Code { get; } = code;
    public string? Reason { get; } = reason;
}

/// <summary>A control call failed: the peer answered an error, or the link session was reset.</summary>
public sealed class LinkRpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A channel was reset (by either side, or because the link session was lost).</summary>
public sealed class LinkChannelResetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Stable error codes carried in RPC errors and channel resets.</summary>
public static class LinkErrorCodes
{
    public const string LinkReset = "link_reset";
    public const string MethodNotFound = "method_not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string InvalidParams = "invalid_params";
    public const string Internal = "internal_error";
    public const string ClassNotSupported = "class_not_supported";
    public const string TooManyChannels = "too_many_channels";
    public const string ChannelUnknown = "channel_unknown";
    public const string StreamGap = "stream_gap";
    public const string Closed = "closed";
    public const string MessageTooLarge = "message_too_large";
}
