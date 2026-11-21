using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace RedCompute.NodeLink.Forward;

/// <summary>What the node's forward target decided about one OPEN, under the link's lock (so: quickly).</summary>
public sealed record ForwardDecision
{
    private ForwardDecision() { }

    public string? RefusalCode { get; private init; }
    public string? RefusalMessage { get; private init; }

    /// <summary>The route's request body cap, enforced on <c>bodyBytes</c> and again while streaming.</summary>
    public long MaxRequestBody { get; private init; }

    /// <summary>The target's own state for the exchange (the runtime it resolved, say).</summary>
    public object? State { get; private init; }

    public bool Accepted => RefusalCode is null;

    public static ForwardDecision Accept(long maxRequestBody, object? state = null) => new() { MaxRequestBody = maxRequestBody, State = state };

    public static ForwardDecision Refuse(string code, string message) => new() { RefusalCode = code, RefusalMessage = message };
}

/// <summary>The runtime's response, as the node's target got it.</summary>
public sealed class ForwardUpstreamResponse(int status, IReadOnlyList<KeyValuePair<string, string>> headers, Stream body, IDisposable? owner = null)
    : IAsyncDisposable
{
    public int Status { get; } = status;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;
    public Stream Body { get; } = body;

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        owner?.Dispose();
    }
}

/// <summary>A target refused or failed an exchange with a forward code (the channel is reset with it).</summary>
public sealed class ForwardTargetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The node's side of a forward: the registered runtimes and how to reach them. The agent implements it (P5: the
/// runtime registry, per-kind route allowlists, the request rebuilt for the runtime); the harness fakes it.
/// </summary>
public interface IForwardTarget
{
    /// <summary>Decides an OPEN: which runtime, whether the route is allowed, its body cap. Runs under the link's lock.</summary>
    ForwardDecision Decide(ForwardOpen open);

    /// <summary>One HTTP exchange with the runtime. <paramref name="requestBody"/> streams the server's body as it arrives.</summary>
    Task<ForwardUpstreamResponse> ExchangeAsync(ForwardOpen open, ForwardDecision decision, Stream requestBody, CancellationToken ct);

    /// <summary>The WebSocket handshake with the runtime.</summary>
    Task<WebSocket> ConnectAsync(ForwardOpen open, ForwardDecision decision, CancellationToken ct);
}

/// <summary>One forward's audit line: what was asked and how it ended, never a body.</summary>
public sealed record ForwardAudit(string Runtime, string Mode, string? Method, string Path, long RequestBytes, int? Status,
    long ResponseBytes, string Outcome, string? Message);

/// <summary>
/// The node's forward dispatcher (Phase 4 design 2.2): the link handler hands it the server's <c>port</c> and
/// <c>port.ws</c> OPENs. It parses the meta strictly, holds the caps (<c>busy</c> past them), asks the target, and
/// runs the accepted exchange: the request body into the runtime as it arrives, the response head, the body, and the
/// trailer. Every OPEN gets an audit line.
/// </summary>
public sealed class ForwardHost
{
    private readonly IForwardTarget _target;
    private readonly Action<ForwardAudit>? _audit;
    private readonly ConcurrentDictionary<string, int> _perRuntime = new(StringComparer.Ordinal);
    private int _open;

    public ForwardHost(IForwardTarget target, ForwardLimits? limits = null, Action<ForwardAudit>? audit = null)
    {
        _target = target;
        _audit = audit;
        Limits = limits ?? ForwardLimits.Default;
    }

    public ForwardLimits Limits { get; }

    /// <summary>Exchanges open now.</summary>
    public int OpenExchanges => Volatile.Read(ref _open);

    public static bool IsForwardClass(string @class) => @class == ChannelClasses.Port.Name || @class == ChannelClasses.PortWs.Name;

    /// <summary>
    /// Accepts a forward OPEN, or throws <see cref="LinkChannelResetException"/> with the refusal's code. For
    /// <see cref="ILinkHandler.AcceptChannel"/>: runs under the session's lock.
    /// </summary>
    public ChannelAcceptance Accept(LinkSession session, ChannelOpenInfo info)
    {
        var http = info.Class == ChannelClasses.Port.Name;
        if (!http && info.Class != ChannelClasses.PortWs.Name)
            throw new LinkChannelResetException(LinkErrorCodes.ClassNotSupported, $"Channel class '{info.Class}' is not a forward.");
        var feature = http ? LinkFeatures.ForwardHttpV1 : LinkFeatures.ForwardWsV1;
        if (!session.HasFeature(feature))
            throw new LinkChannelResetException(LinkErrorCodes.ClassNotSupported, $"This link did not negotiate {feature}.");
        var mode = http ? ForwardModes.Http : ForwardModes.Ws;
        var open = ForwardOpen.Parse(info.Meta, mode, out var error);
        if (open is null)
        {
            Audit(new ForwardAudit("?", mode, null, "?", 0, null, 0, ForwardErrorCodes.BadForward, error));
            throw new LinkChannelResetException(ForwardErrorCodes.BadForward, error!);
        }
        ForwardDecision decision;
        try
        {
            decision = _target.Decide(open);
        }
        catch (Exception failure)
        {
            decision = ForwardDecision.Refuse(LinkErrorCodes.Internal, failure.Message);
        }
        if (decision.Accepted && http && open.BodyBytes > decision.MaxRequestBody)
            decision = ForwardDecision.Refuse(ForwardErrorCodes.BodyTooLarge, $"This route takes at most {decision.MaxRequestBody} bytes.");
        if (decision.Accepted && !TryReserve(open.Runtime))
            decision = ForwardDecision.Refuse(ForwardErrorCodes.Busy,
                $"At most {Limits.ExchangesPerRuntime} exchanges per runtime and {Limits.ChannelsPerLink} per link.");
        if (!decision.Accepted)
        {
            Audit(new ForwardAudit(open.Runtime, mode, open.Method, open.Path, Math.Max(0, open.BodyBytes), null, 0, decision.RefusalCode!,
                decision.RefusalMessage));
            throw new LinkChannelResetException(decision.RefusalCode!, decision.RefusalMessage!);
        }
        IChannelHandler handler = http ? new HttpExchange(this, open, decision) : new WsBridge(this, open, decision);
        return new ChannelAcceptance(handler);
    }

    private bool TryReserve(string runtime)
    {
        if (Interlocked.Increment(ref _open) > Limits.ChannelsPerLink)
        {
            Interlocked.Decrement(ref _open);
            return false;
        }
        var count = _perRuntime.AddOrUpdate(runtime, 1, (_, n) => n + 1);
        if (count > Limits.ExchangesPerRuntime)
        {
            _perRuntime.AddOrUpdate(runtime, 0, (_, n) => n - 1);
            Interlocked.Decrement(ref _open);
            return false;
        }
        return true;
    }

    private void Release(string runtime)
    {
        _perRuntime.AddOrUpdate(runtime, 0, (_, n) => n - 1);
        Interlocked.Decrement(ref _open);
    }

    private void Audit(ForwardAudit entry)
    {
        try
        {
            _audit?.Invoke(entry);
        }
        catch
        {
            // an audit sink's failure never fails the exchange
        }
    }

    /// <summary>
    /// The exchange's slot is freed once our CLOSE is queued and the server's CLOSE is read (or on a reset): both
    /// happen before the server can retire the channel and free its own slot, so our table never trails the server's
    /// in a fault-free run.
    /// </summary>
    private abstract class Exchange(ForwardHost host, ForwardOpen open)
    {
        private int _state; // bit 1: our close queued, bit 2: the peer's close read, bit 4: released
        protected readonly CancellationTokenSource Cancel = new();
        protected ForwardHost Host { get; } = host;
        protected ForwardOpen Open { get; } = open;
        protected long RequestBytes;
        protected long ResponseBytes;
        protected int? Status;

        protected void Mark(int bit, string outcome, string? message = null)
        {
            int before, after;
            do
            {
                before = Volatile.Read(ref _state);
                if ((before & 4) != 0) return;
                after = before | bit;
                if ((after & 3) == 3 || bit == 4) after |= 4;
            } while (Interlocked.CompareExchange(ref _state, after, before) != before);
            if ((after & 4) == 0) return;
            Host.Release(Open.Runtime);
            try
            {
                OnReleased();
            }
            catch
            {
            }
            Host.Audit(new ForwardAudit(Open.Runtime, Open.Mode, Open.Method, Open.Path, Interlocked.Read(ref RequestBytes), Status,
                Interlocked.Read(ref ResponseBytes), outcome, message));
        }

        protected void LocalClosed() => Mark(1, "ok");
        protected void RemoteClosed() => Mark(2, "ok");

        protected void Failed(LinkChannelHandle channel, string code, string message)
        {
            channel.Reset(code, message);
            Mark(4, code, message);
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
            try
            {
                Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            OnResetCore();
            Mark(4, code, message);
        }

        protected abstract void OnResetCore();

        /// <summary>The exchange is over, either way.</summary>
        protected virtual void OnReleased()
        {
        }
    }

    private sealed class HttpExchange(ForwardHost host, ForwardOpen open, ForwardDecision decision)
        : Exchange(host, open), IChannelHandler, IChannelOpenedHandler
    {
        private readonly ChunkPipe _request = new();
        private readonly IncrementalHash _requestSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void OnOpened(LinkChannelHandle channel) => _ = Task.Run(() => RunAsync(channel));

        private async Task RunAsync(LinkChannelHandle channel)
        {
            var ct = Cancel.Token;
            ForwardUpstreamResponse? response = null;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                response = await Host._target.ExchangeAsync(Open, decision, _request.Stream, ct);
                Status = response.Status;
                byte[] head;
                try
                {
                    head = new ForwardHead(response.Status, response.Headers).Encode(Host.Limits.MaxHeadBytes);
                }
                catch (ForwardException tooLarge)
                {
                    Failed(channel, ForwardErrorCodes.UpstreamFailed, tooLarge.Message);
                    return;
                }
                catch (ArgumentException bad)
                {
                    Failed(channel, ForwardErrorCodes.UpstreamFailed, bad.Message);
                    return;
                }
                await channel.SendAsync(head, ct);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    var read = await response.Body.ReadAsync(buffer.AsMemory(0, 64 * 1024), ct);
                    if (read == 0) break;
                    sha.AppendData(buffer, 0, read);
                    Interlocked.Add(ref ResponseBytes, read);
                    await channel.SendAsync(buffer.AsMemory(0, read), ct);
                }
                // The runtime answered: whatever of the request body is still coming is not wanted.
                _request.Discard();
                channel.Close(new ForwardTrailer(Interlocked.Read(ref ResponseBytes), Convert.ToHexStringLower(sha.GetHashAndReset())).Encode());
                LocalClosed();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // reset: OnReset already accounted for it
            }
            catch (LinkChannelResetException)
            {
            }
            catch (ForwardTargetException refused)
            {
                Failed(channel, refused.Code, refused.Message);
            }
            catch (ForwardStreamException body)
            {
                Failed(channel, body.Code, body.Message);
            }
            catch (Exception error)
            {
                Failed(channel, ForwardErrorCodes.UpstreamFailed, error.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _request.Discard();
                if (response is not null)
                {
                    try
                    {
                        await response.DisposeAsync();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            var bytes = Interlocked.Add(ref RequestBytes, data.Payload.Length);
            if (bytes > decision.MaxRequestBody)
            {
                _request.Complete(new ForwardStreamException(ForwardErrorCodes.BodyTooLarge, "The request body passed the route's cap."));
                Failed(channel, ForwardErrorCodes.BodyTooLarge, $"This route takes at most {decision.MaxRequestBody} bytes.");
                return;
            }
            if (Open.BodyBytes >= 0 && bytes > Open.BodyBytes)
            {
                _request.Complete(new ForwardStreamException(ForwardErrorCodes.BadForward, "The request body passed its declared size."));
                Failed(channel, ForwardErrorCodes.BadForward, $"The request body passed its declared {Open.BodyBytes} bytes.");
                return;
            }
            _requestSha.AppendData(data.Payload.Span);
            await _request.WriteAsync(data.Payload, Cancel.Token);
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            var bytes = Interlocked.Read(ref RequestBytes);
            var mismatch = ForwardTrailer.Check(channel.RemoteClosePayload.Span, bytes, Convert.ToHexStringLower(_requestSha.GetHashAndReset()));
            if (mismatch is null && Open.BodyBytes >= 0 && bytes != Open.BodyBytes)
                mismatch = new ForwardStreamException(ForwardErrorCodes.Truncated, $"The request body has {bytes} bytes, not its declared {Open.BodyBytes}.");
            if (mismatch is not null)
            {
                _request.Complete(mismatch);
                Failed(channel, mismatch.Code, mismatch.Message);
                return ValueTask.CompletedTask;
            }
            _request.Complete();
            RemoteClosed();
            return ValueTask.CompletedTask;
        }

        protected override void OnResetCore()
            => _request.Complete(new ForwardStreamException(LinkErrorCodes.Closed, "The exchange was reset."));
    }

    private sealed class WsBridge(ForwardHost host, ForwardOpen open, ForwardDecision decision)
        : Exchange(host, open), IChannelHandler, IChannelOpenedHandler
    {
        private readonly TaskCompletionSource<WebSocket> _upstream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile WebSocket? _socket;

        public void OnOpened(LinkChannelHandle channel) => _ = Task.Run(() => RunAsync(channel));

        private async Task RunAsync(LinkChannelHandle channel)
        {
            var ct = Cancel.Token;
            WebSocket socket;
            try
            {
                socket = await Host._target.ConnectAsync(Open, decision, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _upstream.TrySetCanceled(ct);
                return;
            }
            catch (Exception error)
            {
                _upstream.TrySetException(error);
                Failed(channel, error is ForwardTargetException refused ? refused.Code : ForwardErrorCodes.UpstreamFailed, error.Message);
                return;
            }
            _socket = socket;
            _upstream.TrySetResult(socket);
            if (ct.IsCancellationRequested)
            {
                socket.Abort();
                socket.Dispose();
                return;
            }
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                await channel.SendAsync(new[] { ForwardWsMessage.Ready }, ct);
                var message = new MemoryStream();
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, 64 * 1024), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        int? code = result.CloseStatus is { } status && status != WebSocketCloseStatus.Empty ? (int)status : null;
                        channel.Close(ForwardWsMessage.EncodeClose(code, result.CloseStatusDescription));
                        LocalClosed();
                        return;
                    }
                    if (message.Length == 0) message.WriteByte(result.MessageType == WebSocketMessageType.Text ? ForwardWsMessage.Text : ForwardWsMessage.Binary);
                    if (message.Length + result.Count > Host.Limits.MaxWsMessageBytes)
                    {
                        socket.Abort();
                        Failed(channel, LinkErrorCodes.MessageTooLarge, $"A runtime's WS message passed {Host.Limits.MaxWsMessageBytes - 1} bytes.");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    Interlocked.Add(ref ResponseBytes, message.Length - 1);
                    await channel.SendAsync(message.ToArray(), ct);
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (LinkChannelResetException)
            {
            }
            catch (Exception error)
            {
                socket.Abort();
                Failed(channel, ForwardErrorCodes.UpstreamFailed, error.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            var message = data.Payload;
            if (message.IsEmpty || message.Span[0] is not (ForwardWsMessage.Text or ForwardWsMessage.Binary))
            {
                Failed(channel, ForwardErrorCodes.BadForward, "A WS message from the server is text (1) or binary (2).");
                return;
            }
            WebSocket socket;
            try
            {
                socket = await _upstream.Task.WaitAsync(Cancel.Token);
            }
            catch
            {
                return; // the handshake failed or the exchange was reset: already accounted for
            }
            Interlocked.Add(ref RequestBytes, message.Length - 1);
            try
            {
                await socket.SendAsync(message[1..], message.Span[0] == ForwardWsMessage.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                    true, Cancel.Token);
            }
            catch (OperationCanceledException) when (Cancel.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                socket.Abort();
                Failed(channel, ForwardErrorCodes.UpstreamFailed, error.Message);
            }
        }

        public async ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            (int? Code, string? Reason) close;
            try
            {
                close = ForwardWsMessage.ParseClose(channel.RemoteClosePayload.Span);
            }
            catch (ForwardStreamException error)
            {
                Failed(channel, error.Code, error.Message);
                return;
            }
            WebSocket socket;
            try
            {
                socket = await _upstream.Task.WaitAsync(Cancel.Token);
            }
            catch
            {
                return;
            }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(close.Code is { } code ? (WebSocketCloseStatus)code : WebSocketCloseStatus.NormalClosure,
                        close.Reason, Cancel.Token);
                RemoteClosed();
            }
            catch (OperationCanceledException) when (Cancel.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                socket.Abort();
                Failed(channel, ForwardErrorCodes.UpstreamFailed, error.Message);
            }
        }

        protected override void OnResetCore()
        {
            var socket = _socket;
            if (socket is null) return;
            socket.Abort();
            socket.Dispose();
        }

        protected override void OnReleased() => _socket?.Dispose();
    }
}
