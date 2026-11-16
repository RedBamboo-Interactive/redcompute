namespace RedCompute.NodeLink;

public sealed class LinkClientOptions
{
    public required string NodeId { get; init; }
    public required AgentInfo Agent { get; init; }
    public required ILinkHandler Handler { get; init; }

    /// <summary>Opens one connection to the ingress (a WebSocket with a fresh node assertion in production).</summary>
    public required Func<CancellationToken, Task<ILinkTransport>> Dial { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];
    public ProtocolRange Protocol { get; init; } = LinkProtocol.Supported;
    public LinkOptions Link { get; init; } = LinkOptions.Default;
    public ReconnectBackoff? Backoff { get; init; }

    /// <summary>How often a node that needs an update tries again (design 4.7: hourly).</summary>
    public TimeSpan NeedsUpdateRetry { get; init; } = TimeSpan.FromHours(1);
}

public enum LinkClientStatus
{
    Connecting,
    Online,
    Offline,
    /// <summary>The server refused the node (revoked or unknown): the client stopped.</summary>
    Refused,
    NeedsUpdate,
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
    }

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
                        delay = _options.NeedsUpdateRetry;
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
            if (Status is not (LinkClientStatus.Refused or LinkClientStatus.NeedsUpdate)) SetStatus(LinkClientStatus.Stopped, null);
        }
    }

    private async Task<(DialAction Action, int? CloseCode, TimeSpan? RetryAfter)> ConnectOnceAsync(CancellationToken ct)
    {
        SetStatus(LinkClientStatus.Connecting, null);
        ILinkTransport? transport = null;
        try
        {
            transport = await _options.Dial(ct);
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
                return (DialOutcomes.Classify(null, refusal.Code, code), code, null);
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
            return (DialOutcomes.Classify(null, null, ended.Code), ended.Code, null);
        }
        catch (LinkDialException error)
        {
            StatusReason = error.ErrorCode ?? error.Message;
            return (DialOutcomes.Classify(error.HttpStatus, error.ErrorCode, null), null, error.RetryAfter);
        }
        catch (LinkClosedException error)
        {
            StatusReason = error.Message;
            return (DialOutcomes.Classify(null, null, error.Code), error.Code, null);
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
            return (DialAction.Retry, code, null);
        }
    }

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
