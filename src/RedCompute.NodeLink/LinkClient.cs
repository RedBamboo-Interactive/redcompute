namespace RedCompute.NodeLink;

public sealed class LinkClientOptions
{
    public required string NodeId { get; init; }
    public required AgentInfo Agent { get; init; }
    public required ILinkHandler Handler { get; init; }

    /// <summary>Opens one connection to the ingress (a WebSocket with a fresh node assertion in production).</summary>
    public required Func<CancellationToken, Task<ILinkTransport>> Dial { get; init; }

    /// <summary>
    /// Runs on each dialled connection before the hello, within the handshake timeout, and returns the
    /// transport the link runs over: the Noise initiator for a node with a pinned server key
    /// (<see cref="Noise.NoiseLink.InitiateAsync"/>). Null runs the link in plaintext.
    /// </summary>
    public Func<ILinkTransport, CancellationToken, Task<ILinkTransport>>? Secure { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];
    public ProtocolRange Protocol { get; init; } = LinkProtocol.Supported;
    public LinkOptions Link { get; init; } = LinkOptions.Default;
    public ReconnectBackoff? Backoff { get; init; }

    /// <summary>
    /// The longest wait after a verdict redialling cannot fix: needs update, needs re-pin, a revocation a pinned node
    /// could not authenticate (design 4.7: hourly). The waits grow to it from <see cref="VerdictRetryFirst"/>.
    /// </summary>
    public TimeSpan NeedsUpdateRetry { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The first wait after such a verdict: one event costs about this, not an hour.</summary>
    public TimeSpan VerdictRetryFirst { get; init; } = TimeSpan.FromMinutes(1);
}

public enum LinkClientStatus
{
    Connecting,
    Online,
    Offline,
    /// <summary>
    /// The server refused the node (revoked or unknown) with a verdict the client may act on: the client stopped.
    /// For a node with <see cref="LinkClientOptions.Secure"/> that is only the server's sealed, in-band verdict
    /// (<see cref="LinkVerdicts"/>, or the welcome's refusal); for a node without it, also 403/404 or close 4403.
    /// </summary>
    Refused,
    /// <summary>
    /// A node with <see cref="LinkClientOptions.Secure"/> was told "revoked" or "unknown" by something it cannot
    /// authenticate: an HTTP 403/404 at the dial or a bare close 4403. Anything on the path could send that, so it
    /// deletes nothing and keeps retrying with backoff; its owner can still unpair it locally.
    /// </summary>
    RevokedUnverified,
    NeedsUpdate,
    /// <summary>
    /// The encrypted link cannot be set up with the pinned server key (4495), the server requires
    /// encryption this node has no pin for (4496), or refused the node's link key binding (4497). The client
    /// retries hourly with the same pin; only its owner's re-pin changes it.
    /// </summary>
    NeedsRepin,
    Stopped,
}

/// <summary>
/// The node's end of the link (design 4.7): dials, sends the hello (resuming its link session when it
/// has one), pumps the connection, and redials with backoff. A welcome with <c>resumed: false</c>
/// replaces the session: the old one is reset with <c>link_reset</c> and <see cref="SessionStarted"/>
/// runs for the new one, where the owner re-attaches its durable streams.
/// </summary>
public sealed class LinkClient : IAsyncDisposable
{
    private readonly LinkClientOptions _options;
    private readonly ReconnectBackoff _backoff;
    private readonly object _gate = new();
    private LinkSession? _session;
    private CancellationTokenSource? _run;

    public LinkClient(LinkClientOptions options)
    {
        _options = options;
        _backoff = options.Backoff ?? new ReconnectBackoff(Random.Shared);
        _verdictBackoff = new VerdictBackoff(Random.Shared, options.VerdictRetryFirst, options.NeedsUpdateRetry);
    }

    private readonly VerdictBackoff _verdictBackoff;

    public LinkSession? Session { get { lock (_gate) return _session; } }
    public LinkWelcome? Welcome { get; private set; }
    public LinkClientStatus Status { get; private set; } = LinkClientStatus.Offline;
    public string? StatusReason { get; private set; }

    /// <summary>Connections that completed the handshake, and how many of them resumed the session.</summary>
    public int Connections { get; private set; }
    public int ResumedConnections { get; private set; }

    /// <summary>A new (not resumed) link session started and is pumping. Runs off the pump.</summary>
    public event Func<LinkSession, Task>? SessionStarted;

    public event Action<LinkClientStatus, string?>? StatusChanged;

    private void SetStatus(LinkClientStatus status, string? reason)
    {
        Status = status;
        StatusReason = reason;
        try
        {
            StatusChanged?.Invoke(status, reason);
        }
        catch
        {
            // an observer must not stop the link
        }
    }

    /// <summary>Runs until <paramref name="ct"/> or until the server refuses the node for good.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _run = run;
        var token = run.Token;
        var retriedForClock = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (action, closeCode, retryAfter) = await ConnectOnceAsync(token);
                if (token.IsCancellationRequested) break;
                TimeSpan delay;
                switch (action)
                {
                    case DialAction.Stop:
                        SetStatus(LinkClientStatus.Refused, StatusReason);
                        await ResetSessionAsync(LinkErrorCodes.LinkReset);
                        return;
                    case DialAction.NeedsUpdate:
                        SetStatus(LinkClientStatus.NeedsUpdate, StatusReason);
                        delay = _verdictBackoff.Next();
                        break;
                    case DialAction.NeedsRepin:
                        SetStatus(LinkClientStatus.NeedsRepin, StatusReason);
                        delay = _verdictBackoff.Next();
                        break;
                    case DialAction.StopUnverified:
                        SetStatus(LinkClientStatus.RevokedUnverified, StatusReason);
                        delay = _verdictBackoff.Next();
                        break;
                    case DialAction.RetryAfter:
                        delay = retryAfter ?? _backoff.Next(closeCode);
                        break;
                    case DialAction.RetryNowWithServerTime when !retriedForClock:
                        retriedForClock = true;
                        delay = TimeSpan.Zero;
                        break;
                    default:
                        delay = _backoff.Next(closeCode);
                        break;
                }
                if (action != DialAction.RetryNowWithServerTime) retriedForClock = false;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _options.Link.Time, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (Status is not (LinkClientStatus.Refused or LinkClientStatus.NeedsUpdate or LinkClientStatus.NeedsRepin
                or LinkClientStatus.RevokedUnverified))
                SetStatus(LinkClientStatus.Stopped, null);
        }
    }

    private async Task<(DialAction Action, int? CloseCode, TimeSpan? RetryAfter)> ConnectOnceAsync(CancellationToken ct)
    {
        SetStatus(LinkClientStatus.Connecting, null);
        ILinkTransport? transport = null;
        try
        {
            transport = await _options.Dial(ct);
            if (_options.Secure is { } secure)
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(_options.Link.HandshakeTimeout);
                try
                {
                    transport = await secure(transport, handshake.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("No encrypted handshake answer in time.");
                }
            }
            LinkSession? current;
            lock (_gate) current = _session is { IsReset: false } live ? live : null;
            var hello = new LinkHello(_options.NodeId, _options.Protocol, _options.Agent, _options.Features, current?.ResumeInfo());
            await transport.SendAsync(LinkHandshake.HelloFrame(hello), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Link.HandshakeTimeout);
            byte[] first;
            try
            {
                first = await transport.ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No welcome in time.");
            }
            var (welcome, refusal) = LinkHandshake.ReadWelcome(FrameCodec.Decode(first));
            if (refusal is not null)
            {
                StatusReason = refusal.Code;
                await transport.CloseAsync(LinkCloseCodes.Normal, refusal.Code, CancellationToken.None);
                var code = refusal.Code switch
                {
                    "upgrade_required" => LinkCloseCodes.UpgradeRequired,
                    "node_revoked" => LinkCloseCodes.Revoked,
                    _ => LinkCloseCodes.ProtocolError,
                };
                // The welcome came through the transport: sealed when the link is encrypted.
                return (Authenticate(DialOutcomes.Classify(null, refusal.Code, code), authenticated: true), code, null);
            }

            var resumed = welcome!.Resumed && current is not null && current.LinkSessionId == welcome.LinkSessionId;
            LinkSession session;
            if (resumed)
            {
                session = current!;
            }
            else
            {
                session = new LinkSession(LinkRole.Node, welcome.LinkSessionId, _options.Handler, _options.Link, welcome.Features, welcome.ServerEpoch);
                LinkSession? old;
                lock (_gate)
                {
                    old = _session;
                    _session = session;
                }
                if (old is not null) await old.ResetAsync(LinkErrorCodes.LinkReset);
            }
            Welcome = welcome;
            Connections++;
            if (resumed) ResumedConnections++;
            var pump = session.RunTransportAsync(transport, resumed ? welcome.Channels : null, ct);
            _verdictBackoff.Reset();
            SetStatus(LinkClientStatus.Online, resumed ? "resumed" : "new session");
            if (!resumed && SessionStarted is { } started)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await started(session);
                    }
                    catch
                    {
                        // the owner logs its own failures; a durable stream retries at the next session
                    }
                }, CancellationToken.None);

            var ended = await pump;
            _backoff.OnLinkEnded(ended.UpFor);
            SetStatus(LinkClientStatus.Offline, $"closed {ended.Code}{(ended.Reason is null ? "" : ": " + ended.Reason)}");
            // A 4403 counts as the server's own word only with the in-band verdict that came before it.
            var action = DialOutcomes.Classify(null, null, ended.Code);
            if (action == DialAction.Stop && session.PeerVerdict is { } verdict)
            {
                StatusReason = verdict;
                return (action, ended.Code, null);
            }
            return (Authenticate(action, authenticated: false), ended.Code, null);
        }
        catch (LinkDialException error)
        {
            StatusReason = error.ErrorCode ?? error.Message;
            return (Authenticate(DialOutcomes.Classify(error.HttpStatus, error.ErrorCode, null), authenticated: false), null, error.RetryAfter);
        }
        catch (LinkClosedException error)
        {
            StatusReason = error.Message;
            return (Authenticate(DialOutcomes.Classify(null, null, error.Code), authenticated: false), error.Code, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (transport is not null) await transport.CloseAsync(LinkCloseCodes.GoingAway, "stopping", CancellationToken.None);
            return (DialAction.Retry, null, null);
        }
        catch (Exception error)
        {
            StatusReason = error.Message;
            var code = error is LinkProtocolException protocol ? protocol.CloseCode
                : error is TimeoutException ? LinkCloseCodes.Timeout : LinkCloseCodes.Abnormal;
            if (transport is not null) await transport.CloseAsync(code, error.Message, CancellationToken.None);
            // The encrypted handshake's own verdicts: a server without E2E, or a server key that is not the pin.
            var action = error is LinkProtocolException { CloseCode: LinkCloseCodes.UpgradeRequired or LinkCloseCodes.LinkKeyUnknown }
                ? DialOutcomes.Classify(null, null, code) : DialAction.Retry;
            return (action, code, null);
        }
    }

    /// <summary>
    /// A stop the node may act on (delete its keys): any for a node without E2E, as before; for a node with
    /// <see cref="LinkClientOptions.Secure"/> only one that came in band, sealed. Anything else becomes
    /// <see cref="DialAction.StopUnverified"/>.
    /// </summary>
    private DialAction Authenticate(DialAction action, bool authenticated)
        => action == DialAction.Stop && _options.Secure is not null && !authenticated ? DialAction.StopUnverified : action;

    /// <summary>Drops the current connection (the session stays, so the next one resumes it).</summary>
    public Task DropConnectionAsync(int code = LinkCloseCodes.GoingAway, string? reason = "dropped")
        => Session?.DetachAsync(code, reason) ?? Task.CompletedTask;

    private async Task ResetSessionAsync(string code)
    {
        LinkSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null) await session.ResetAsync(code);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        await ResetSessionAsync(LinkErrorCodes.LinkReset);
    }
}
