using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using RedCompute.Core.Providers;
using RedCompute.NodeLink.Forward;

namespace RedCompute.Node.Runtimes;

/// <summary>
/// The agent's forward target (Phase 4 design 2.2, 2.4): a server OPEN reaches a registered, running runtime on its
/// kind's allowlist, and nothing else. The node builds the request it sends the runtime itself:
/// <list type="bullet">
/// <item>HTTP/1.1 with <c>Connection: close</c>, one exchange per connection;</item>
/// <item><c>Host: 127.0.0.1:&lt;port&gt;</c>, the runtime's own address from the owner's registration (the server
/// never names a host or a port);</item>
/// <item>hop-by-hop headers, <c>Authorization</c>, <c>Cookie</c> and every <c>X-Forwarded-*</c>/<c>Cf-*</c> header
/// dropped (CR/LF were refused at OPEN);</item>
/// <item>the path and query rebuilt from parsed parts (<see cref="RuntimeRoutes"/>), with the route's body rule.</item>
/// </list>
/// </summary>
public sealed class RuntimeForwardTarget(RuntimeManager runtimes) : IForwardTarget
{
    private sealed record Admitted(RuntimeRegistration Runtime, AllowedRoute Route, string? Boundary);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxConnectionsPerServer = 64,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public ForwardDecision Decide(ForwardOpen open)
    {
        var runtime = runtimes.Find(open.Runtime);
        if (runtime is null) return ForwardDecision.Refuse(ForwardErrorCodes.RuntimeUnknown, $"No runtime '{open.Runtime}' is registered on this node.");
        if (!runtime.Enabled) return ForwardDecision.Refuse(ForwardErrorCodes.RuntimeNotRunning, $"The node's owner disabled '{open.Runtime}'.");
        if (runtimes.Status(open.Runtime) is not { Status: BackendStatus.Running })
            return ForwardDecision.Refuse(ForwardErrorCodes.RuntimeNotRunning, $"'{open.Runtime}' is not running.");
        string? refusal;
        var route = open.Mode == ForwardModes.Ws
            ? RuntimeRoutes.Ws(runtime.Kind, open.Path, open.Query, out refusal)
            : RuntimeRoutes.Http(runtime.Kind, open.Method!, open.Path, open.Query, out refusal);
        if (route is null) return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, refusal ?? "Not on the allowlist.");
        string? boundary = null;
        if (route.Body == BodyRule.ImageUpload && (boundary = RuntimeRoutes.Boundary(open.Headers)) is null)
            return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, "An upload is multipart/form-data with a boundary.");
        return ForwardDecision.Accept(route.MaxBody, new Admitted(runtime, route, boundary));
    }

    public async Task<ForwardUpstreamResponse> ExchangeAsync(ForwardOpen open, ForwardDecision decision, Stream requestBody, CancellationToken ct)
    {
        var (runtime, route, boundary) = (Admitted)decision.State!;
        var request = new HttpRequestMessage(new HttpMethod(open.Method!), Uri(runtime, route))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Host = $"127.0.0.1:{runtime.Port}";
        request.Headers.ConnectionClose = true;
        if (open.BodyBytes != 0 && route.Body != BodyRule.None)
        {
            Stream body = route.Body switch
            {
                BodyRule.ImageUpload => new ImageUploadGuard(requestBody, boundary!),
                BodyRule.QueueDeleteOnly => new QueueDeleteGuard(requestBody, route.MaxBody),
                _ => requestBody,
            };
            request.Content = new StreamContent(body, 64 * 1024);
            if (open.BodyBytes > 0) request.Content.Headers.ContentLength = open.BodyBytes;
        }
        foreach (var (name, value) in RuntimeRoutes.ForwardedHeaders(open.Headers))
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException error) when (Find<ForwardTargetException>(error) is { } refused)
        {
            throw refused;
        }
        catch (HttpRequestException error)
        {
            throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, $"{runtime.Id} did not answer: {error.Message}");
        }
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (IsHopByHop(name)) continue;
            foreach (var value in values)
                if (!value.Any(c => c is '\r' or '\n' or '\0')) headers.Add(new(name.ToLowerInvariant(), value));
        }
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new ForwardUpstreamResponse((int)response.StatusCode, headers, stream, response);
    }

    public async Task<WebSocket> ConnectAsync(ForwardOpen open, ForwardDecision decision, CancellationToken ct)
    {
        var (runtime, route, _) = (Admitted)decision.State!;
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var uri = new UriBuilder("ws", "127.0.0.1", runtime.Port, route.Path) { Query = route.Query }.Uri;
        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException)
        {
            socket.Dispose();
            throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, $"{runtime.Id}'s WebSocket did not open: {error.Message}");
        }
    }

    private static Uri Uri(RuntimeRegistration runtime, AllowedRoute route)
        => new($"http://127.0.0.1:{runtime.Port}{route.Path}{(route.Query.Length > 0 ? "?" + route.Query : "")}");

    private static bool IsHopByHop(string name) => name.ToLowerInvariant() is "connection" or "keep-alive" or "proxy-connection"
        or "proxy-authenticate" or "te" or "trailer" or "transfer-encoding" or "upgrade" or "set-cookie";

    private static T? Find<T>(Exception error) where T : Exception
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is T found) return found;
        return null;
    }
}
