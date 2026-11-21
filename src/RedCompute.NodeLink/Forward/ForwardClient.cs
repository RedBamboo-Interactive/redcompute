using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace RedCompute.NodeLink.Forward;

/// <summary>
/// The server's side of the forward (Phase 4 design 2.2): an <see cref="HttpRequestMessage"/> becomes one exchange
/// on a <c>port</c> channel, a WebSocket one <c>port.ws</c> channel, to a runtime the node registered. No listener
/// anywhere: only code holding this object reaches the node's runtime. One per node; <c>session</c> gives the node's
/// current link session (null while it is offline).
/// <para>Caps: <see cref="ForwardLimits.ExchangesPerRuntime"/> and <see cref="ForwardLimits.ChannelsPerLink"/> open
/// exchanges; a caller past them waits here (the queue is on the server, never on the node).</para>
/// </summary>
public sealed class ForwardClient
{
    private readonly Func<LinkSession?> _session;
    private readonly SemaphoreSlim _link;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runtimes = new(StringComparer.Ordinal);

    public ForwardClient(Func<LinkSession?> session, ForwardLimits? limits = null)
    {
        _session = session;
        Limits = limits ?? ForwardLimits.Default;
        _link = new SemaphoreSlim(Limits.ChannelsPerLink, Limits.ChannelsPerLink);
    }

    public ForwardLimits Limits { get; }

    /// <summary>Exchanges open now (tests).</summary>
    public int OpenExchanges => Limits.ChannelsPerLink - _link.CurrentCount;

    /// <summary>A handler that sends each request to <paramref name="runtime"/> (the request URI's host and port are ignored).</summary>
    public HttpMessageHandler CreateHandler(string runtime)
    {
        if (!ForwardOpen.IsRuntimeId(runtime)) throw new ArgumentException($"'{runtime}' is not a runtime id.", nameof(runtime));
        return new Handler(this, runtime);
    }

    /// <summary>
    /// One HTTP exchange. Returns once the node sent the response head; the body streams as it arrives, and a body
    /// whose bytes or hash differ from the node's trailer fails its read with <see cref="ForwardStreamException"/>
    /// (<c>forward_truncated</c>). A refusal or failure before the head throws <see cref="ForwardException"/>.
    /// Dispose the response (or read its body to the end) to free the exchange.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(string runtime, HttpRequestMessage request, CancellationToken ct = default)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no URI.");
        if (!uri.IsAbsoluteUri) uri = new Uri(new Uri("http://runtime.invalid/"), uri);
        var content = request.Content;
        long bodyBytes = content is null ? 0 : content.Headers.ContentLength ?? -1;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in request.Headers)
            if (!IsHopByHop(name)) foreach (var value in values) headers.Add(new(name.ToLowerInvariant(), value));
        if (content is not null)
            foreach (var (name, values) in content.Headers)
                if (!IsHopByHop(name) && !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    foreach (var value in values) headers.Add(new(name.ToLowerInvariant(), value));
        ForwardOpen open;
        try
        {
            open = ForwardOpen.Http(runtime, request.Method.Method.ToUpperInvariant(), uri.AbsolutePath,
                uri.Query.Length > 0 ? uri.Query[1..] : "", headers, bodyBytes);
        }
        catch (ArgumentException error)
        {
            throw new ForwardException(ForwardErrorCodes.BadForward, error.Message, error);
        }

        var lease = await AcquireAsync(runtime, ct);
        var reader = new ExchangeReader(Limits);
        LinkChannelHandle handle;
        try
        {
            handle = await OpenAsync(new ChannelOpenRequest(ChannelClasses.Port.Name, Meta: open.ToMeta()), reader,
                LinkFeatures.ForwardHttpV1, ct);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        _ = handle.Completion.ContinueWith(_ => lease.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        reader.Attach(handle);
        _ = UploadAsync(handle, content, bodyBytes);

        ForwardHead head;
        try
        {
            using (ct.Register(() => handle.Reset(LinkErrorCodes.Closed, "The caller gave up.")))
                head = await reader.Head.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            handle.Reset(LinkErrorCodes.Closed, "The caller gave up.");
            throw;
        }

        var response = new HttpResponseMessage((HttpStatusCode)head.Status)
        {
            RequestMessage = request,
            Version = HttpVersion.Version11,
            Content = new StreamContent(reader.Body),
        };
        foreach (var (name, value) in head.Headers)
            if (!IsHopByHop(name) && !response.Headers.TryAddWithoutValidation(name, value))
                response.Content.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    /// <summary>
    /// One WebSocket to <paramref name="runtime"/>'s <paramref name="path"/>. Returns once the node's handshake with
    /// the runtime succeeded; a refusal or a failed handshake throws <see cref="ForwardException"/>.
    /// </summary>
    public async Task<WebSocket> ConnectWebSocketAsync(string runtime, string path, string query, CancellationToken ct = default)
    {
        ForwardOpen open;
        try
        {
            open = ForwardOpen.Ws(runtime, path, query.StartsWith('?') ? query[1..] : query);
        }
        catch (ArgumentException error)
        {
            throw new ForwardException(ForwardErrorCodes.BadForward, error.Message, error);
        }
        var lease = await AcquireAsync(runtime, ct);
        var socket = new ForwardWebSocket(Limits.MaxWsMessageBytes);
        LinkChannelHandle handle;
        try
        {
            handle = await OpenAsync(new ChannelOpenRequest(ChannelClasses.PortWs.Name, Meta: open.ToMeta()), socket,
                LinkFeatures.ForwardWsV1, ct);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        _ = handle.Completion.ContinueWith(_ => lease.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        socket.Attach(handle);
        try
        {
            await socket.Ready.WaitAsync(Limits.WsHandshakeTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            handle.Reset(LinkErrorCodes.Closed, "The caller gave up.");
            throw;
        }
        catch (TimeoutException)
        {
            var message = $"The node did not open the WebSocket within {Limits.WsHandshakeTimeout.TotalSeconds:0.#} s.";
            handle.Reset(LinkErrorCodes.Closed, message);
            throw new ForwardException(ForwardErrorCodes.UpstreamFailed, message);
        }
        return socket;
    }

    private async Task<LinkChannelHandle> OpenAsync(ChannelOpenRequest request, IChannelHandler handler, string feature, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = _session();
            if (session is null || session.IsReset)
                throw new ForwardException(ForwardErrorCodes.NodeOffline, "The node is not connected.");
            if (!session.HasFeature(feature))
                throw new ForwardException(ForwardErrorCodes.Unsupported, $"The node's link has no {feature}.");
            try
            {
                return await session.OpenChannelAsync(request, handler, ct);
            }
            catch (LinkChannelResetException refused) when (refused.Code == ForwardErrorCodes.Busy && attempt < Limits.BusyRetries)
            {
                // The node's table can trail ours by one round trip: try again shortly.
                await Task.Delay(Limits.BusyRetryDelay * (attempt + 1), ct);
            }
            catch (LinkChannelResetException refused)
            {
                throw new ForwardException(refused.Code, refused.Message, refused);
            }
        }
    }

    private async Task<Lease> AcquireAsync(string runtime, CancellationToken ct)
    {
        var perRuntime = _runtimes.GetOrAdd(runtime, _ => new SemaphoreSlim(Limits.ExchangesPerRuntime, Limits.ExchangesPerRuntime));
        await perRuntime.WaitAsync(ct);
        try
        {
            await _link.WaitAsync(ct);
        }
        catch
        {
            perRuntime.Release();
            throw;
        }
        return new Lease(perRuntime, _link);
    }

    /// <summary>The request body onto the channel, then CLOSE with its trailer. A failure resets the exchange.</summary>
    private static async Task UploadAsync(LinkChannelHandle handle, HttpContent? content, long declared)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long sent = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            if (content is not null)
            {
                await using var body = await content.ReadAsStreamAsync();
                while (true)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, 64 * 1024));
                    if (read == 0) break;
                    sent += read;
                    if (declared >= 0 && sent > declared)
                    {
                        handle.Reset(ForwardErrorCodes.BadForward, $"The request body passed its declared {declared} bytes.");
                        return;
                    }
                    sha.AppendData(buffer, 0, read);
                    await handle.SendAsync(buffer.AsMemory(0, read));
                }
            }
            if (declared >= 0 && sent != declared)
            {
                handle.Reset(ForwardErrorCodes.BadForward, $"The request body has {sent} bytes, not its declared {declared}.");
                return;
            }
            handle.Close(new ForwardTrailer(sent, Convert.ToHexStringLower(sha.GetHashAndReset())).Encode());
        }
        catch (Exception error) when (error is LinkChannelResetException or InvalidOperationException)
        {
            // the exchange is already over (reset by the node, the caller, or the link)
        }
        catch (Exception error)
        {
            handle.Reset(ForwardErrorCodes.BadForward, "Reading the request body failed: " + error.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host",
    };

    internal static bool IsHopByHop(string name) => HopByHop.Contains(name);

    private sealed class Lease(SemaphoreSlim runtime, SemaphoreSlim link) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            link.Release();
            runtime.Release();
        }
    }

    private sealed class Handler(ForwardClient client, string runtime) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => client.SendAsync(runtime, request, ct);
    }

    /// <summary>The response side of one exchange: the head, then the body into a bounded pipe, then the trailer check.</summary>
    private sealed class ExchangeReader : IChannelHandler
    {
        private readonly ForwardHead.Reader _head;
        private readonly TaskCompletionSource<ForwardHead> _headDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly ChunkPipe _body;
        private LinkChannelHandle? _handle;
        private long _bytes;

        public ExchangeReader(ForwardLimits limits)
        {
            _head = new ForwardHead.Reader(limits.MaxHeadBytes);
            _body = new ChunkPipe(onReaderGone: () => _handle?.Reset(LinkErrorCodes.Closed, "The reader stopped."));
        }

        public Task<ForwardHead> Head => _headDone.Task;
        public Stream Body => _body.Stream;

        public void Attach(LinkChannelHandle handle) => _handle = handle;

        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            var payload = data.Payload;
            if (_head.Head is null)
            {
                int taken;
                try
                {
                    taken = _head.Feed(payload.Span);
                }
                catch (ForwardStreamException error)
                {
                    _headDone.TrySetException(new ForwardException(error.Code, error.Message));
                    channel.Reset(error.Code, error.Message);
                    return;
                }
                payload = payload[taken..];
                if (_head.Head is { } head) _headDone.TrySetResult(head);
            }
            if (payload.IsEmpty) return;
            _bytes += payload.Length;
            _sha.AppendData(payload.Span);
            await _body.WriteAsync(payload, ct);
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            if (_head.Head is null)
            {
                var error = new ForwardException(ForwardErrorCodes.Truncated, "The node ended the exchange before its response head.");
                _headDone.TrySetException(error);
                _body.Complete(new ForwardStreamException(error.Code, error.Message));
                return ValueTask.CompletedTask;
            }
            var mismatch = ForwardTrailer.Check(channel.RemoteClosePayload.Span, _bytes, Convert.ToHexStringLower(_sha.GetHashAndReset()));
            _body.Complete(mismatch);
            if (mismatch is not null) channel.Reset(mismatch.Code, mismatch.Message);
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
            _headDone.TrySetException(new ForwardException(code, message));
            _body.Complete(new ForwardStreamException(code, message));
        }
    }
}
