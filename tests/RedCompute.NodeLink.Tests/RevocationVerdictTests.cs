using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// The P0.5a crypto review's blocker: a revocation a node acts on. A node that pinned the server's link key deletes
/// its keys only on the server's sealed, in-band verdict (<c>link.verdict</c>, or the welcome's refusal). A close 4403
/// or an HTTP 403/404 at the dial could come from anything on the path (the kernel's bridge, Cloudflare): for that
/// node it is "revoked, unverified", which keeps everything and retries with backoff. A node that never pinned still
/// stops on them, as before.
/// </summary>
public sealed class RevocationVerdictTests
{
    private const string NodeId = LinkPair.NodeId;

    /// <summary>The path between node and server, honest until a test makes it forge a verdict.</summary>
    private sealed class ForgingPath
    {
        private ILinkTransport? _towardNode;
        private ILinkTransport? _towardServer;
        public int Dials;
        public int? ForgedHttpStatus { get; set; }

        public Task<ILinkTransport> DialAsync(Func<ILinkTransport, Task> accept)
        {
            Interlocked.Increment(ref Dials);
            if (ForgedHttpStatus is { } status)
                throw new LinkDialException(status, status == 404 ? LinkVerdicts.Unknown : LinkVerdicts.Revoked, "forged on the path");
            var (node, towardNode) = LinkPipe.Create();
            var (towardServer, server) = LinkPipe.Create();
            _towardNode = towardNode;
            _towardServer = towardServer;
            _ = Task.Run(() => accept(server));
            _ = Task.Run(() => PumpAsync(towardNode, towardServer));
            _ = Task.Run(() => PumpAsync(towardServer, towardNode));
            return Task.FromResult(node);
        }

        /// <summary>One close frame with 4403, sent to the node by the path; the server never said it.</summary>
        public async Task ForgeRevokedCloseAsync()
        {
            await _towardNode!.CloseAsync(LinkCloseCodes.Revoked, LinkVerdicts.Revoked, CancellationToken.None);
            await _towardServer!.CloseAsync(LinkCloseCodes.Abnormal, "cut", CancellationToken.None);
        }

        private static async Task PumpAsync(ILinkTransport from, ILinkTransport to)
        {
            try
            {
                while (true) await to.SendAsync(await from.ReceiveAsync(CancellationToken.None), CancellationToken.None);
            }
            catch (LinkClosedException)
            {
            }
        }
    }

    private static LinkPair Pair(ForgingPath path, E2eKeys? keys)
    {
        LinkPair? pair = null;
        pair = new LinkPair(
            clientSecure: keys?.ClientSecure(NodeId),
            serverSecure: keys?.ServerSecure,
            dial: _ => path.DialAsync(t => pair!.Server.RunAsync(t, NodeId, CancellationToken.None)),
            needsUpdateRetry: TimeSpan.FromMilliseconds(50));
        return pair;
    }

    private static List<LinkClientStatus> Record(LinkClient client)
    {
        var statuses = new List<LinkClientStatus>();
        client.StatusChanged += (status, _) => { lock (statuses) statuses.Add(status); };
        return statuses;
    }

    [Fact]
    public async Task A_forged_4403_on_an_encrypted_link_is_unverified_and_the_node_comes_back()
    {
        using var keys = new E2eKeys();
        var path = new ForgingPath();
        await using var pair = Pair(path, keys);
        var statuses = Record(pair.Client);
        pair.Start();
        await pair.OnlineAsync();

        await path.ForgeRevokedCloseAsync();

        await Wait.UntilAsync(() => { lock (statuses) return statuses.Contains(LinkClientStatus.RevokedUnverified); }, what: "revoked-unverified");
        await Wait.UntilAsync(() => pair.Client.Connections == 2 && pair.Client.Status == LinkClientStatus.Online, what: "the link back up");
        lock (statuses) Assert.DoesNotContain(LinkClientStatus.Refused, statuses);
        Assert.False(pair.ClientRun.IsCompleted);
        Assert.Equal(2, keys.Encrypted);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task A_forged_http_verdict_at_the_dial_is_unverified_for_a_pinned_node_and_it_keeps_dialling(int status)
    {
        using var keys = new E2eKeys();
        var path = new ForgingPath { ForgedHttpStatus = status };
        await using var pair = Pair(path, keys).Start();

        await Wait.UntilAsync(() => path.Dials >= 3, what: "several dials");
        Assert.Equal(LinkClientStatus.RevokedUnverified, pair.Client.Status);
        Assert.False(pair.ClientRun.IsCompleted);

        path.ForgedHttpStatus = null; // the path stops lying: the node links again, nothing lost
        await pair.OnlineAsync();
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task A_node_that_never_pinned_still_stops_on_an_http_verdict(int status)
    {
        var path = new ForgingPath { ForgedHttpStatus = status };
        await using var pair = Pair(path, keys: null).Start();
        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Equal(1, path.Dials);
    }

    [Fact]
    public async Task A_node_that_never_pinned_still_stops_on_a_bare_4403()
    {
        var path = new ForgingPath();
        await using var pair = Pair(path, keys: null).Start();
        await pair.OnlineAsync();
        await path.ForgeRevokedCloseAsync();
        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
    }

    [Fact]
    public async Task The_server_s_own_revocation_reaches_a_pinned_node_sealed_and_stops_it()
    {
        using var keys = new E2eKeys();
        var path = new ForgingPath();
        await using var pair = Pair(path, keys).Start();
        var session = await pair.OnlineAsync();

        await pair.Server.RevokeAsync(NodeId);

        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Equal(LinkVerdicts.Revoked, pair.Client.StatusReason);
        Assert.Equal(LinkVerdicts.Revoked, session.PeerVerdict);
    }

    [Fact]
    public async Task A_pinned_node_the_server_already_revoked_is_refused_in_the_sealed_welcome_and_stops()
    {
        using var keys = new E2eKeys();
        var path = new ForgingPath();
        await using var pair = Pair(path, keys);
        await pair.Server.RevokeAsync(NodeId);
        pair.Start();

        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Equal(1, keys.Encrypted); // the refusal came on the encrypted link
    }

    [Fact]
    public async Task A_verdict_the_node_sends_the_server_is_ignored_and_only_the_node_records_one()
    {
        await using var pair = new LinkPair().Start();
        var node = await pair.OnlineAsync();
        var server = pair.Server.FindSession(NodeId)!;
        // The verdict is the server's to send: one from a node is an unknown method there, and records nothing.
        await Assert.ThrowsAsync<LinkRpcException>(() => node.CallAsync(LinkVerdicts.Method, new System.Text.Json.Nodes.JsonObject { ["code"] = LinkVerdicts.Revoked }));
        Assert.Null(server.PeerVerdict);
        Assert.Null(node.PeerVerdict);
    }

    [Fact]
    public void The_verdict_backoff_starts_at_a_minute_doubles_and_settles_at_the_hour()
    {
        var backoff = new VerdictBackoff(new Random(3), TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        var waits = Enumerable.Range(0, 10).Select(_ => backoff.Next()).ToList();
        for (var i = 0; i < waits.Count; i++)
        {
            var step = TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(1).Ticks, TimeSpan.FromMinutes(1).Ticks << i));
            Assert.InRange(waits[i], step / 2, step);
        }
        Assert.True(waits[0] <= TimeSpan.FromMinutes(1));
        Assert.True(waits[^1] >= TimeSpan.FromMinutes(30));
        backoff.Reset();
        Assert.True(backoff.Next() <= TimeSpan.FromMinutes(1));
    }
}
