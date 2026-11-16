using System.Text.Json.Nodes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Spool;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>Client, server and sessions over in-memory pipes, with short clocks.</summary>
public sealed class LinkSessionTests
{
    [Fact]
    public async Task Calls_go_both_ways_and_errors_come_back_typed()
    {
        await using var pair = new LinkPair().Start();
        var node = await pair.OnlineAsync();
        var server = pair.Server.FindSession(LinkPair.NodeId)!;

        Assert.Equal(5, (await server.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
        Assert.Equal(9, (await node.CallAsync("test.add", new JsonObject { ["a"] = 4, ["b"] = 5 })).GetInt32());
        Assert.Equal(LinkErrorCodes.MethodNotFound, (await Assert.ThrowsAsync<LinkRpcException>(() => server.CallAsync("node.shell"))).Code);
        Assert.Equal("test_failed", (await Assert.ThrowsAsync<LinkRpcException>(() => server.CallAsync("test.fail"))).Code);
        Assert.Equal(LinkErrorCodes.Internal, (await Assert.ThrowsAsync<LinkRpcException>(() => server.CallAsync("test.boom"))).Code);

        // A slow request does not hold up others.
        var slow = server.CallAsync("test.slow");
        Assert.Equal(3, (await server.CallAsync("test.add", new JsonObject { ["a"] = 1, ["b"] = 2 })).GetInt32());
        Assert.False(slow.IsCompleted);
        Assert.Equal("slow", (await slow).GetString());

        await node.NotifyAsync("node.report", new JsonObject { ["x"] = 1 });
        await Wait.UntilAsync(() => ((TestLinkHandler)pair.ServerHandler).Notifications.Contains("node.report"));
    }

    [Fact]
    public async Task The_welcome_carries_the_negotiated_features_and_policy()
    {
        await using var pair = new LinkPair(policy: new AgentPolicy("0.1.0", "0.2.0", "https://example.test/node")).Start();
        var node = await pair.OnlineAsync();
        Assert.Equal(["describe.v1", "diag.echo"], node.Features);
        Assert.Equal("0.2.0", pair.Client.Welcome!.AgentPolicy!.Recommended);
        Assert.Equal("epoch-1", node.Epoch);
    }

    [Fact]
    public async Task An_echo_channel_round_trips_large_messages_in_order_and_retires_after_both_closes()
    {
        await using var pair = new LinkPair().Start();
        await pair.OnlineAsync();
        var server = pair.Server.FindSession(LinkPair.NodeId)!;
        var received = new RecordingHandler { CloseWhenRemoteCloses = false };
        var channel = await server.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), received);
        Assert.Equal(0u, channel.Id % 2); // server-opened ids are even

        var sent = new List<byte[]>();
        var random = new Random(5);
        long total = 0;
        while (total < 4 << 20)
        {
            var chunk = new byte[random.Next(1, FrameCodec.MaxPayload + 1)];
            random.NextBytes(chunk);
            sent.Add(chunk);
            total += chunk.Length;
            await channel.SendAsync(chunk);
        }
        channel.Close();
        await received.RemoteClosed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // diag.echo is a stream class: frames arrive cut at 16 KiB (priority 3), so compare the bytes.
        Assert.Equal(sent.SelectMany(b => b).ToArray(), received.Messages.SelectMany(b => b).ToArray());
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.OpenChannelCount);
    }

    [Fact]
    public async Task An_unsupported_class_is_reset_and_the_link_stays_up()
    {
        await using var pair = new LinkPair().Start();
        var node = await pair.OnlineAsync();
        var error = await Assert.ThrowsAsync<LinkChannelResetException>(() =>
            node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.Port.Name), new RecordingHandler()));
        Assert.Equal(LinkErrorCodes.ClassNotSupported, error.Code);
        Assert.Equal(5, (await node.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
    }

    [Fact]
    public async Task A_dropped_connection_resumes_the_session_and_loses_nothing()
    {
        var serverSink = new RecordingHandler();
        var serverHandler = new TestLinkHandler { Accept = _ => new ChannelAcceptance(serverSink) };
        await using var pair = new LinkPair(serverHandler: serverHandler).Start();
        var node = await pair.OnlineAsync();
        var channel = await node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.TestStream.Name, Priority: 1), new RecordingHandler());
        for (var i = 0; i < 200; i++)
        {
            await channel.SendAsync(BitConverter.GetBytes(i));
            if (i % 50 == 25)
            {
                var resumed = pair.Client.ResumedConnections;
                await pair.Client.DropConnectionAsync();
                await Wait.UntilAsync(() => pair.Client.ResumedConnections > resumed, what: "the resume");
            }
        }
        channel.Close();
        await serverSink.RemoteClosed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(Enumerable.Range(0, 200), serverSink.Messages.Select(m => BitConverter.ToInt32(m)));
        Assert.Same(node, pair.Client.Session);
        Assert.True(pair.Client.ResumedConnections >= 3, $"resumed {pair.Client.ResumedConnections}");
    }

    [Fact]
    public async Task A_lost_session_resets_ephemeral_channels_and_calls_with_link_reset()
    {
        await using var pair = new LinkPair().Start();
        var first = await pair.OnlineAsync();
        var sink = new RecordingHandler();
        var channel = await first.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), sink);
        var hanging = pair.Server.FindSession(LinkPair.NodeId)!.CallAsync("test.slow");

        // The server forgets the node's session (a RedCompute restart): the node's next hello cannot resume.
        await pair.RestartServerAsync();
        Assert.Equal(LinkErrorCodes.LinkReset, (await Assert.ThrowsAsync<LinkRpcException>(() => hanging)).Code);
        Assert.Equal(LinkErrorCodes.LinkReset, await sink.Reset.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_new_connection_supersedes_the_old_one_with_4409()
    {
        await using var pair = new LinkPair().Start();
        var session = await pair.OnlineAsync();
        var (node, server) = LinkPipe.Create();
        var second = Task.Run(() => pair.Server.RunAsync(server, LinkPair.NodeId, CancellationToken.None));
        await node.SendAsync(LinkHandshake.HelloFrame(new LinkHello(LinkPair.NodeId, LinkProtocol.Supported,
            new AgentInfo("0.1.0", "linux", "x64"), [], session.ResumeInfo())), CancellationToken.None);
        var (welcome, _) = LinkHandshake.ReadWelcome(FrameCodec.Decode(await node.ReceiveAsync(CancellationToken.None)));
        Assert.True(welcome!.Resumed);
        var first = pair.ServerRuns[0];
        Assert.Equal(LinkCloseCodes.Superseded, (await first.WaitAsync(TimeSpan.FromSeconds(5)))!.Code);
        await node.CloseAsync(LinkCloseCodes.Normal, null, CancellationToken.None);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Opens_that_arrive_out_of_order_are_both_accepted_and_a_stale_duplicate_is_not_reopened()
    {
        await using var pair = new LinkPair();
        var (node, server) = LinkPipe.Create();
        var run = pair.Server.RunAsync(server, LinkPair.NodeId, CancellationToken.None);
        await node.SendAsync(LinkHandshake.HelloFrame(new LinkHello(LinkPair.NodeId, LinkProtocol.Supported,
            new AgentInfo("0.1.0", "linux", "x64"), [])), CancellationToken.None);
        Assert.NotNull(LinkHandshake.ReadWelcome(FrameCodec.Decode(await node.ReceiveAsync(CancellationToken.None))).Welcome);

        byte[] Open(uint id) => FrameCodec.Encode(new LinkFrame(FrameKind.Open, FrameFlags.None, id, 0, 0,
            System.Text.Encoding.UTF8.GetBytes("""{"class":"diag.echo"}""")));
        await node.SendAsync(Open(3), CancellationToken.None); // channel 1's OPEN was lost or overtaken
        await node.SendAsync(Open(1), CancellationToken.None);
        await node.SendAsync(Open(1), CancellationToken.None); // a duplicate: answered with the accept again, never a second channel
        var accepts = new HashSet<uint>();
        while (accepts.Count < 2)
        {
            var frame = FrameCodec.Decode(await node.ReceiveAsync(CancellationToken.None));
            if (frame.Kind == FrameKind.Open) accepts.Add(frame.Channel);
            Assert.NotEqual(FrameKind.Reset, frame.Kind);
        }
        Assert.Equal(new uint[] { 1, 3 }, accepts.Order());
        Assert.Equal(2, pair.Server.FindSession(LinkPair.NodeId)!.OpenChannelCount);
        await node.CloseAsync(LinkCloseCodes.Normal, null, CancellationToken.None);
        await run;
    }

    [Fact]
    public async Task A_hello_for_another_node_is_closed_4400()
    {
        await using var pair = new LinkPair();
        var (node, server) = LinkPipe.Create();
        var run = pair.Server.RunAsync(server, "node_SOMEONE_ELSE", CancellationToken.None);
        await node.SendAsync(LinkHandshake.HelloFrame(new LinkHello(LinkPair.NodeId, LinkProtocol.Supported,
            new AgentInfo("0.1.0", "linux", "x64"), [])), CancellationToken.None);
        var (_, error) = LinkHandshake.ReadWelcome(FrameCodec.Decode(await node.ReceiveAsync(CancellationToken.None)));
        Assert.Equal("node_mismatch", error!.Code);
        var closed = await Assert.ThrowsAsync<LinkClosedException>(async () => await node.ReceiveAsync(CancellationToken.None));
        Assert.Equal(LinkCloseCodes.ProtocolError, closed.Code);
        Assert.Null(await run);
    }

    [Fact]
    public async Task An_agent_below_the_minimum_needs_an_update()
    {
        await using var pair = new LinkPair(policy: new AgentPolicy("0.2.0", "0.2.0"), agentVersion: "0.1.9").Start();
        await Wait.UntilAsync(() => pair.Client.Status == LinkClientStatus.NeedsUpdate);
        Assert.Equal("upgrade_required", pair.Client.StatusReason);
    }

    [Fact]
    public async Task No_protocol_overlap_is_a_4426_with_the_server_range()
    {
        await using var pair = new LinkPair();
        var (node, server) = LinkPipe.Create();
        var run = pair.Server.RunAsync(server, LinkPair.NodeId, CancellationToken.None);
        await node.SendAsync(LinkHandshake.HelloFrame(new LinkHello(LinkPair.NodeId, new ProtocolRange(7, 9),
            new AgentInfo("9.0.0", "linux", "x64"), [])), CancellationToken.None);
        var (_, error) = LinkHandshake.ReadWelcome(FrameCodec.Decode(await node.ReceiveAsync(CancellationToken.None)));
        Assert.Equal("upgrade_required", error!.Code);
        Assert.Equal(LinkProtocol.Current, error.ServerMax);
        Assert.Equal(LinkCloseCodes.UpgradeRequired, (await Assert.ThrowsAsync<LinkClosedException>(async () => await node.ReceiveAsync(CancellationToken.None))).Code);
        await run;
    }

    [Fact]
    public async Task Revoking_closes_the_link_with_4403_and_the_client_stops()
    {
        await using var pair = new LinkPair().Start();
        await pair.OnlineAsync();
        pair.BoundNodeId = "node_REFUSED"; // the next dial would not authenticate as this node
        var revoked = DateTime.UtcNow;
        await pair.Server.RevokeAsync(LinkPair.NodeId);
        await pair.ClientRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - revoked < TimeSpan.FromSeconds(1));
        Assert.Equal(LinkClientStatus.Refused, pair.Client.Status);
        Assert.Null(pair.Client.Session);
    }

    [Fact]
    public async Task A_corrupt_frame_closes_the_link_with_4400_and_the_node_resumes()
    {
        await using var pair = new LinkPair().Start();
        var session = await pair.OnlineAsync();
        // Simulate the server receiving garbage: the server pump fails with 4400.
        var serverSession = pair.Server.FindSession(LinkPair.NodeId)!;
        var runs = pair.ServerRuns.Count;
        await serverSession.DetachAsync(LinkCloseCodes.ProtocolError, "corrupt");
        await Wait.UntilAsync(() => pair.ServerRuns.Count > runs && pair.Client.Status == LinkClientStatus.Online);
        Assert.Same(session, pair.Client.Session);
        Assert.Equal(5, (await session.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
    }

    [Fact]
    public async Task Missed_heartbeats_take_the_link_down_and_it_comes_back()
    {
        LinkPair? pair = null;
        var dials = 0;
        async Task<ILinkTransport> Dial(CancellationToken ct)
        {
            var transport = await pair!.DialAsync(ct);
            return Interlocked.Increment(ref dials) == 1 ? new MuteAfter(transport, 4) : transport;
        }
        await using (pair = new LinkPair(dial: Dial).Start())
        {
            await Wait.UntilAsync(() => dials >= 2, TimeSpan.FromSeconds(5), "a redial after the heartbeat timeout");
            var session = await pair.OnlineAsync();
            Assert.Equal(5, (await session.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
        }
    }

    [Fact]
    public async Task A_durable_stream_reattaches_after_a_server_restart_and_continues_after_what_was_committed()
    {
        var sink = new DurableSink();
        var serverHandler = new TestLinkHandler { Accept = info => info.StreamKey is null ? null : new ChannelAcceptance(sink, sink.Committed) };
        await using var pair = new LinkPair(serverHandler: serverHandler);
        using var writer = new DurableStreamWriter("proc/7/stdout", new MemoryStreamSpool(), priority: 1);
        var attaches = 0;
        pair.Client.SessionStarted += async session =>
        {
            Interlocked.Increment(ref attaches);
            await session.AttachDurableAsync(writer);
        };
        pair.Start();
        await pair.OnlineAsync();
        for (var i = 0; i < 300; i++)
        {
            writer.Write(BitConverter.GetBytes(i));
            if (i is 100 or 200)
            {
                // The commit store survives a restart, the link session does not.
                var before = Volatile.Read(ref attaches);
                await pair.RestartServerAsync();
                await Wait.UntilAsync(() => Volatile.Read(ref attaches) > before, what: "the re-attach");
            }
        }
        writer.Complete();
        await writer.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(sink.Violations);
        Assert.Equal(Enumerable.Range(0, 300), sink.Records.Select(r => BitConverter.ToInt32(r)));
        Assert.True(attaches >= 3, $"attached {attaches} times");
        Assert.Equal(0, writer.SpooledBytes);
    }

    /// <summary>Commits durable frames in a list with their seq, like a transcript store would.</summary>
    private sealed class DurableSink : IChannelHandler
    {
        private readonly object _lock = new();
        public List<byte[]> Records { get; } = [];
        public List<string> Violations { get; } = [];
        public ulong Committed { get { lock (_lock) return _committed; } }
        private ulong _committed;

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            lock (_lock)
            {
                if (data.Seq != _committed + 1) Violations.Add($"seq {data.Seq} after {_committed}");
                Records.Add(data.Payload.ToArray());
                _committed = data.Seq;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            lock (_lock) _committed++; // the CLOSE is committed too
            channel.Close();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }

    /// <summary>A transport that silently stops delivering after n frames (a half-open path).</summary>
    private sealed class MuteAfter(ILinkTransport inner, int frames) : ILinkTransport
    {
        private int _received;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => inner.SendAsync(message, ct);

        public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                var message = await inner.ReceiveAsync(ct);
                if (++_received <= frames) return message;
            }
        }

        public ValueTask CloseAsync(int code, string? reason, CancellationToken ct) => inner.CloseAsync(code, reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
