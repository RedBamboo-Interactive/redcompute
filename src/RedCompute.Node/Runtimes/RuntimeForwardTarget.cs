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
/// <item>at most one <c>Content-Type</c>; an upload's is the one the guard parsed, rebuilt.</item>
/// <item>the prompt routes acting only on the prompts the server submitted here (<see cref="Submitted"/>).</item>
/// <item>an answer the node rewrites (<see cref="ResponseRule"/>: <c>GET /queue</c> redacted) asked for without
/// <c>Accept-Encoding</c>, so the node reads it as it is.</item>
/// </list>
/// </summary>
public sealed class RuntimeForwardTarget(RuntimeManager runtimes) : IForwardTarget
{
    private sealed record Admitted(RuntimeRegistration Runtime, AllowedRoute Route, string? Boundary);

    /// <summary>The prompt ids each runtime answered to the server's <c>/prompt</c> through this forward.</summary>
    public SubmittedPrompts Submitted { get; } = new();

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
        if (RuntimeRoutes.ContentTypes(open.Headers) > 1)
            return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, "A request carries one Content-Type.");
        if (route.NeedsBody && open.BodyBytes == 0)
            return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, $"{open.Method} {route.Path} needs its body.");
        if (route.PromptId is { } promptId && !Submitted.Contains(runtime.Id, promptId))
            return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed,
                $"{route.Path} acts only on a prompt the server submitted through this node.");
        string? boundary = null;
        if (route.Body == BodyRule.ImageUpload && (boundary = RuntimeRoutes.Boundary(open.Headers)) is null)
            return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, "An upload is multipart/form-data with one boundary.");
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
            Func<string, bool> submitted = id => Submitted.Contains(runtime.Id, id);
            Stream body = route.Body switch
            {
                BodyRule.ImageUpload => new ImageUploadGuard(requestBody, boundary!),
                BodyRule.QueueDeleteOnly => new QueueDeleteGuard(requestBody, route.MaxBody, submitted),
                BodyRule.InterruptOwnPrompt => new InterruptGuard(requestBody, route.MaxBody, submitted),
                BodyRule.PromptSubmit => new PromptSubmitGuard(requestBody, route.MaxBody),
                BodyRule.FreeOnly => new FreeGuard(requestBody, route.MaxBody),
                _ => requestBody,
            };
            request.Content = new StreamContent(body, 64 * 1024);
            if (open.BodyBytes > 0) request.Content.Headers.ContentLength = open.BodyBytes;
            // The upload's one Content-Type is the one its guard parses, rebuilt; the server's string never goes on.
            if (route.Body == BodyRule.ImageUpload)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", RuntimeRoutes.UploadContentType(boundary!));
        }
        foreach (var (name, value) in RuntimeRoutes.ForwardedHeaders(open.Headers))
        {
            if (route.Body == BodyRule.ImageUpload && name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
            if (route.Response != ResponseRule.Pass && name.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        // /free unloads the runtime's models: never while the owner's own prompts run or wait (review item 5).
        if (route.Body == BodyRule.FreeOnly) await RefuseWhileOwnerBusyAsync(runtime, ct);

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
        if (route.Response == ResponseRule.QueueRedacted) return await RedactedQueueAsync(runtime, response, ct);
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (IsHopByHop(name)) continue;
            foreach (var value in values)
                if (!value.Any(c => c is '\r' or '\n' or '\0')) headers.Add(new(name.ToLowerInvariant(), value));
        }
        var stream = await response.Content.ReadAsStreamAsync(ct);
        if (route.Body == BodyRule.PromptSubmit && response.IsSuccessStatusCode)
            stream = new PromptAnswerRecorder(stream, id => Submitted.Add(runtime.Id, id));
        return new ForwardUpstreamResponse((int)response.StatusCode, headers, stream, response);
    }

    /// <summary>
    /// <c>GET /queue</c>, redacted on the node (<see cref="ResponseRule.QueueRedacted"/>): read whole (capped), rewritten,
    /// and sent as JSON with its own length. A failed answer goes back as its status alone, with no body.
    /// </summary>
    private async Task<ForwardUpstreamResponse> RedactedQueueAsync(RuntimeRegistration runtime, HttpResponseMessage response,
        CancellationToken ct)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode) return new ForwardUpstreamResponse((int)response.StatusCode, [], Stream.Null);
            if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, "The runtime's /queue answer is encoded.");
            byte[] answer;
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
                answer = await RuntimeAnswers.ReadCappedAsync(body, RuntimeAnswers.MaxQueueBytes, "/queue", ct);
            var redacted = RuntimeAnswers.RedactQueue(answer, id => Submitted.Contains(runtime.Id, id));
            return new ForwardUpstreamResponse((int)response.StatusCode,
                [new("content-type", "application/json"), new("content-length", redacted.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                new MemoryStream(redacted));
        }
    }

    /// <summary>
    /// <c>runtime_busy</c> unless the runtime's own queue shows no prompt the server did not submit: the node asks the
    /// runtime itself. A queue it cannot read is refused too, since it cannot show the owner idle.
    /// </summary>
    private async Task RefuseWhileOwnerBusyAsync(RuntimeRegistration runtime, CancellationToken ct)
    {
        int? owners;
        try
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{runtime.Port}/queue")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            check.Headers.ConnectionClose = true;
            using var answer = await Http.SendAsync(check, HttpCompletionOption.ResponseHeadersRead, ct);
            owners = answer.IsSuccessStatusCode && answer.Content.Headers.ContentEncoding.Count == 0
                ? RuntimeAnswers.OwnerPrompts(await RuntimeAnswers.ReadCappedAsync(await answer.Content.ReadAsStreamAsync(ct),
                    RuntimeAnswers.MaxQueueBytes, "/queue", ct), id => Submitted.Contains(runtime.Id, id))
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or ForwardTargetException)
        {
            owners = null;
        }
        if (owners is null)
            throw new ForwardTargetException(ForwardErrorCodes.RuntimeBusy, $"{runtime.Id}'s queue could not be read, so /free is not sent.");
        if (owners > 0)
            throw new ForwardTargetException(ForwardErrorCodes.RuntimeBusy,
                $"The node's owner has {owners} prompt(s) running or queued on {runtime.Id}; /free waits until they end.");
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
