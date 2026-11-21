using System.Collections.Concurrent;

namespace RedCompute.NodeLink;

public sealed class LinkServerOptions
{
    /// <summary>This server's boot epoch: a node tells a server restart from a network drop by it.</summary>
    public required string ServerEpoch { get; init; }

    /// <summary>The application handler for one node's session.</summary>
    public required Func<string, ILinkHandler> HandlerFactory { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];
    public AgentPolicy? AgentPolicy { get; init; }
    public ProtocolRange Protocol { get; init; } = LinkProtocol.Supported;
    public LinkOptions Link { get; init; } = LinkOptions.Default;

    /// <summary>How long a revocation waits for the node to read its in-band verdict before the link closes 4403.</summary>
    public TimeSpan VerdictTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs on each connection before the hello, within the handshake timeout, with the node id the
    /// ingress bound, and returns the transport the link runs over: the Noise responder
    /// (<see cref="Noise.NoiseLink.AcceptAsync"/>). A <see cref="LinkProtocolException"/> closes the
    /// connection with its code. Null runs every link in plaintext.
    /// </summary>
    public Func<ILinkTransport, string, CancellationToken, Task<ILinkTransport>>? Secure { get; init; }
}

/// <summary>
/// The server's end of every node link, without the HTTP: NodeHub authenticates the upgrade and hands
/// the connection here with the node id the kernel bound to it. It reads the hello, refuses a node id
/// that differs from the bound one (4400) or a protocol/agent too old (4426), supersedes an older
/// connection of the same node (4409), resumes the node's link session when the hello names it and it
/// is younger than <see cref="LinkOptions.SessionRetention"/>, or starts a new one (the old one resets
/// with <c>link_reset</c>), answers the welcome and pumps the connection. A node once revoked here
/// (<see cref="RevokeAsync"/>) never holds a live session again, whatever stage its connection was at.
/// </summary>
public sealed class LinkServer : IAsyncDisposable
{
    private readonly LinkServerOptions _options;
    private readonly ConcurrentDictionary<string, NodeEntry> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();

    private sealed class NodeEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public LinkSession? Session { get; set; }
    }

    public LinkServer(LinkServerOptions options)
    {
        _options = options;
        _ = Task.Run(SweepLoopAsync);
    }

    public LinkServerOptions Options => _options;

    /// <summary>A connection completed its handshake (resumed or not).</summary>
    public event Action<string, LinkSession, bool>? Connected;

    /// <summary>A connection ended; the session may still be resumed.</summary>
    public event Action<string, LinkSession, LinkDisconnect>? Disconnected;

    /// <summary>A node's session ended for good (replaced, expired, revoked, shutdown).</summary>
    public event Action<string, LinkSession>? SessionEnded;

    public LinkSession? FindSession(string nodeId)
        => _nodes.TryGetValue(nodeId, out var entry) && entry.Session is { IsReset: false } session ? session : null;

    public bool IsOnline(string nodeId) => FindSession(nodeId)?.IsAttached == true;

    public IReadOnlyCollection<string> NodeIds => _nodes.Keys.ToList();

    /// <summary>Whether <see cref="RevokeAsync"/> was called for the node (terminal for this server).</summary>
    public bool IsRevoked(string nodeId) => _revoked.ContainsKey(nodeId);

    /// <summary>
    /// Runs one authenticated connection for <paramref name="boundNodeId"/> to its end. Returns how it
    /// ended, or null when the handshake failed (the connection is closed with the reason's code).
    /// </summary>
    public async Task<LinkDisconnect?> RunAsync(ILinkTransport transport, string boundNodeId, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var token = linked.Token;
        LinkHello hello;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.Link.HandshakeTimeout);
            if (_options.Secure is { } secure) transport = await secure(transport, boundNodeId, timeout.Token);
            hello = LinkHandshake.ReadHello(FrameCodec.Decode(await transport.ReceiveAsync(timeout.Token)));
        }
        catch (LinkProtocolException error)
        {
            await transport.CloseAsync(error.CloseCode, error.Message, CancellationToken.None);
            return null;
        }
        catch (LinkClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            await transport.CloseAsync(token.IsCancellationRequested ? LinkCloseCodes.GoingAway : LinkCloseCodes.Timeout, "no hello", CancellationToken.None);
            return null;
        }

        if (!string.Equals(hello.NodeId, boundNodeId, StringComparison.Ordinal))
            return await RefuseAsync(transport, LinkCloseCodes.ProtocolError, new LinkHandshakeError("node_mismatch",
                "The hello names another node than the one that authenticated."));
        var version = LinkProtocol.Negotiate(hello.Protocol, _options.Protocol);
        if (version is null)
            return await RefuseAsync(transport, LinkCloseCodes.UpgradeRequired, new LinkHandshakeError("upgrade_required",
                $"No common protocol: the node speaks {hello.Protocol.Min}..{hello.Protocol.Max}, the server {_options.Protocol.Min}..{_options.Protocol.Max}.",
                _options.Protocol.Min, _options.Protocol.Max));
        if (_options.AgentPolicy is { } policy && AgentVersions.Compare(hello.Agent.Version, policy.Minimum) < 0)
            return await RefuseAsync(transport, LinkCloseCodes.UpgradeRequired, new LinkHandshakeError("upgrade_required",
                $"Agent {hello.Agent.Version} is older than the minimum {policy.Minimum}.", _options.Protocol.Min, _options.Protocol.Max));

        if (IsRevoked(boundNodeId)) return await RefuseRevokedAsync(transport);

        var entry = _nodes.GetOrAdd(boundNodeId, _ => new NodeEntry());
        Task<LinkDisconnect> pump;
        LinkSession session;
        bool resumed;
        LinkSession? replaced = null;
        await entry.Gate.WaitAsync(token);
        try
        {
            // A revoke may have landed since the check above, and may have removed this very entry (no later
            // revoke could reach a session left on it): it is the revoked set, read under the gate, that decides.
            if (IsRevoked(boundNodeId)) return await RefuseRevokedAsync(transport);
            if (!_nodes.TryGetValue(boundNodeId, out var current) || !ReferenceEquals(current, entry))
            {
                // Removed by a shutdown: nothing may attach to an entry the server no longer tracks.
                await transport.CloseAsync(LinkCloseCodes.ServiceRestart, "server_stopping", CancellationToken.None);
                return null;
            }
            // The newest authenticated connection wins (design 4.7).
            if (entry.Session is { IsAttached: true } attached) await attached.DetachAsync(LinkCloseCodes.Superseded, "superseded");
            var existing = entry.Session is { IsReset: false } live ? live : null;
            resumed = hello.Resume is { } resume && existing is not null && existing.LinkSessionId == resume.LinkSessionId
                && (existing.DetachedAt == 0 || _options.Link.Time.GetElapsedTime(existing.DetachedAt) < _options.Link.SessionRetention);
            if (!resumed)
            {
                replaced = existing;
                var features = LinkProtocol.IntersectFeatures(hello.Features, _options.Features);
                entry.Session = new LinkSession(LinkRole.Server, Guid.NewGuid().ToString("N"), _options.HandlerFactory(boundNodeId),
                    _options.Link, features, _options.ServerEpoch);
            }
            session = entry.Session!;
            if (replaced is not null) await replaced.ResetAsync(LinkErrorCodes.LinkReset);

            var welcome = new LinkWelcome(version.Value, session.LinkSessionId, resumed, _options.ServerEpoch, session.Features,
                (int)Math.Ceiling(_options.Link.HeartbeatInterval.TotalSeconds), new LinkLimits(FrameCodec.MaxPayload, _options.Link.MaxChannels),
                _options.AgentPolicy, resumed ? session.ChannelReceipts() : null);
            try
            {
                await transport.SendAsync(LinkHandshake.WelcomeFrame(welcome), token);
            }
            catch (Exception error) when (error is LinkClosedException or OperationCanceledException)
            {
                await transport.CloseAsync(LinkCloseCodes.GoingAway, "handshake failed", CancellationToken.None);
                return null;
            }
            pump = session.RunTransportAsync(transport, resumed ? hello.Resume!.Channels : null, token);
        }
        finally
        {
            entry.Gate.Release();
        }
        if (replaced is not null) Raise(() => SessionEnded?.Invoke(boundNodeId, replaced));
        Raise(() => Connected?.Invoke(boundNodeId, session, resumed));
        var ended = await pump;
        Raise(() => Disconnected?.Invoke(boundNodeId, session, ended));
        return ended;
    }

    private static Task<LinkDisconnect?> RefuseRevokedAsync(ILinkTransport transport)
        => RefuseAsync(transport, LinkCloseCodes.Revoked, new LinkHandshakeError("node_revoked", "This node was revoked."));

    private static async Task<LinkDisconnect?> RefuseAsync(ILinkTransport transport, int code, LinkHandshakeError error)
    {
        try
        {
            await transport.SendAsync(LinkHandshake.ErrorFrame(error), CancellationToken.None);
        }
        catch (LinkClosedException)
        {
        }
        await transport.CloseAsync(code, error.Code, CancellationToken.None);
        return null;
    }

    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // observers must not break the link
        }
    }

    /// <summary>
    /// Ends a node's session at once: it is told <c>link.verdict {code: node_revoked}</c> in band (sealed on an
    /// encrypted link; at most <see cref="LinkServerOptions.VerdictTimeout"/>), then its connection closes with 4403,
    /// its channels and calls fail,
    /// and nothing more is accepted from it, ever: the node is remembered as revoked before this returns
    /// its task (synchronously), so a connection still in its handshake is refused 4403 too.
    /// </summary>
    public async Task RevokeAsync(string nodeId)
    {
        _revoked[nodeId] = 0;
        if (!_nodes.TryRemove(nodeId, out var entry)) return;
        await entry.Gate.WaitAsync();
        try
        {
            if (entry.Session is { } session)
            {
                // In band first: a pinned node deletes its keys only on a verdict that came sealed (a bare 4403
                // could come from anything on the path).
                await session.SendVerdictAsync(LinkVerdicts.Revoked, _options.VerdictTimeout, LinkCloseCodes.Revoked);
                await session.ResetAsync("node_revoked", LinkCloseCodes.Revoked);
                Raise(() => SessionEnded?.Invoke(nodeId, session));
            }
            entry.Session = null;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Resets sessions detached for longer than the retention. Returns how many ended.</summary>
    public async Task<int> SweepExpiredAsync()
    {
        var ended = 0;
        foreach (var (nodeId, entry) in _nodes)
        {
            if (!await entry.Gate.WaitAsync(0)) continue;
            try
            {
                if (entry.Session is { IsAttached: false } session && session.DetachedAt != 0
                    && _options.Link.Time.GetElapsedTime(session.DetachedAt) >= _options.Link.SessionRetention)
                {
                    await session.ResetAsync(LinkErrorCodes.LinkReset);
                    entry.Session = null;
                    ended++;
                    Raise(() => SessionEnded?.Invoke(nodeId, session));
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        return ended;
    }

    private async Task SweepLoopAsync()
    {
        var every = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(100).Ticks, _options.Link.SessionRetention.Ticks / 10));
        try
        {
            while (true)
            {
                await Task.Delay(every, _options.Link.Time, _life.Token);
                await SweepExpiredAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Closes every connection with <paramref name="closeCode"/> (1012 for a restart) and ends every session.</summary>
    public async Task ShutdownAsync(int closeCode = LinkCloseCodes.ServiceRestart)
    {
        _life.Cancel();
        foreach (var (nodeId, entry) in _nodes)
        {
            if (entry.Session is { } session)
            {
                await session.ResetAsync(LinkErrorCodes.LinkReset, closeCode);
                Raise(() => SessionEnded?.Invoke(nodeId, session));
            }
        }
        _nodes.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_life.IsCancellationRequested) await ShutdownAsync();
        _life.Dispose();
    }
}
