using System.Text;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.NodeLink.Tests;

public sealed class NegotiationTests
{
    [Theory]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(1, 3, 1, 1, 1)]   // node newer: it lowers itself
    [InlineData(1, 1, 1, 3, 1)]   // node older: the server speaks its version
    [InlineData(2, 4, 3, 5, 4)]
    [InlineData(3, 5, 1, 3, 3)]
    [InlineData(4, 6, 1, 3, null)] // no overlap: 4426
    [InlineData(1, 2, 3, 5, null)]
    [InlineData(3, 1, 1, 3, null)] // an inverted range is no range
    public void The_highest_common_version_or_none(int nodeMin, int nodeMax, int serverMin, int serverMax, int? expected)
        => Assert.Equal(expected, LinkProtocol.Negotiate(new(nodeMin, nodeMax), new(serverMin, serverMax)));

    [Fact]
    public void Each_side_supports_current_minus_two_to_current()
    {
        Assert.Equal(LinkProtocol.Current, LinkProtocol.Supported.Max);
        Assert.Equal(Math.Max(1, LinkProtocol.Current - 2), LinkProtocol.Supported.Min);
    }

    [Fact]
    public void Features_are_the_intersection_in_the_servers_order()
    {
        Assert.Equal(["describe.v1", "diag.echo"],
            LinkProtocol.IntersectFeatures(["diag.echo", "spool.disk", "describe.v1", "x.future"], ["describe.v1", "hardware.v1", "diag.echo"]));
        Assert.Empty(LinkProtocol.IntersectFeatures(null, ["describe.v1"]));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("0.1.1", "0.1.0", 1)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    [InlineData("garbage", "0.0.1", -1)]
    public void Agent_versions_compare_as_semver(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(AgentVersions.Compare(a, b)));

    [Fact]
    public void Hello_and_welcome_round_trip_and_unknown_members_are_ignored()
    {
        var hello = new LinkHello("node_X", new ProtocolRange(1, 1), new AgentInfo("0.1.0", "linux", "x64"), ["describe.v1"],
            new LinkResume("s1", [new ChannelResume(0, 4), new ChannelResume(3, 9)]));
        var read = LinkHandshake.ReadHello(FrameCodec.Decode(LinkHandshake.HelloFrame(hello)));
        Assert.Equal("node_X", read.NodeId);
        Assert.Equal(9UL, read.Resume!.Channels[1].Rx);

        var json = """{"method":"link.hello","params":{"nodeId":"n","protocol":{"min":1,"max":2},"agent":{"version":"0.1.0","os":"linux","arch":"x64"},"future":{"a":1}}}""";
        var frame = FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 0, 0, 0, Encoding.UTF8.GetBytes(json)));
        var tolerant = LinkHandshake.ReadHello(FrameCodec.Decode(frame));
        Assert.Empty(tolerant.Features);

        var welcome = new LinkWelcome(1, "s1", true, "e", ["describe.v1"], 15, new LinkLimits(65536, 256),
            new AgentPolicy("0.1.0", "0.2.0", "https://example.test/node"));
        var (back, error) = LinkHandshake.ReadWelcome(FrameCodec.Decode(LinkHandshake.WelcomeFrame(welcome)));
        Assert.Null(error);
        Assert.Equal("0.2.0", back!.AgentPolicy!.Recommended);

        var (none, refusal) = LinkHandshake.ReadWelcome(FrameCodec.Decode(LinkHandshake.ErrorFrame(new LinkHandshakeError("upgrade_required", "old", 1, 1))));
        Assert.Null(none);
        Assert.Equal(1, refusal!.ServerMax);
    }

    [Theory]
    [InlineData("""{"method":"link.bye"}""")]
    [InlineData("""{"method":"link.hello","params":{"nodeId":""}}""")]
    [InlineData("""not json""")]
    [InlineData("""[1,2]""")]
    public void A_bad_hello_is_a_protocol_error(string json)
    {
        var frame = FrameCodec.Decode(FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 0, 0, 0, Encoding.UTF8.GetBytes(json))));
        Assert.Equal(LinkCloseCodes.ProtocolError, Assert.Throws<LinkProtocolException>(() => LinkHandshake.ReadHello(frame)).CloseCode);
    }
}

public sealed class BackoffAndHeartbeatTests
{
    [Fact]
    public void Full_jitter_doubles_to_the_cap_and_is_deterministic_for_a_seed()
    {
        var a = new ReconnectBackoff(new Random(42));
        var b = new ReconnectBackoff(new Random(42));
        var delays = Enumerable.Range(0, 12).Select(_ => a.Next()).ToList();
        Assert.Equal(delays, Enumerable.Range(0, 12).Select(_ => b.Next()));
        for (var i = 0; i < delays.Count; i++)
        {
            var ceiling = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, i)));
            Assert.InRange(delays[i], TimeSpan.Zero, ceiling);
        }
        Assert.Contains(delays.Skip(8), d => d > TimeSpan.FromSeconds(4)); // it does reach far once the ceiling is high
    }

    [Fact]
    public void A_link_up_for_a_minute_resets_the_schedule_and_a_1012_retries_fast()
    {
        var backoff = new ReconnectBackoff(new Random(1));
        for (var i = 0; i < 5; i++) backoff.Next();
        backoff.OnLinkEnded(TimeSpan.FromSeconds(59));
        Assert.Equal(5, backoff.Attempt);
        backoff.OnLinkEnded(TimeSpan.FromSeconds(60));
        Assert.Equal(0, backoff.Attempt);
        for (var i = 0; i < 200; i++)
        {
            backoff.Reset();
            Assert.InRange(backoff.Next(LinkCloseCodes.ServiceRestart), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
        }
    }

    [Theory]
    [InlineData(null, null, null, DialAction.Retry)]
    [InlineData(503, null, null, DialAction.Retry)]
    [InlineData(401, "assertion_invalid", null, DialAction.RetryNowWithServerTime)]
    [InlineData(401, "assertion_replayed", null, DialAction.Retry)]
    [InlineData(403, "node_revoked", null, DialAction.Stop)]
    [InlineData(404, "node_unknown", null, DialAction.Stop)]
    [InlineData(426, null, null, DialAction.NeedsUpdate)]
    [InlineData(429, null, null, DialAction.RetryAfter)]
    [InlineData(null, null, 1012, DialAction.Retry)]
    [InlineData(null, null, 1001, DialAction.Retry)]
    [InlineData(null, null, 4403, DialAction.Stop)]
    [InlineData(null, null, 4426, DialAction.NeedsUpdate)]
    [InlineData(null, null, 4429, DialAction.RetryAfter)]
    [InlineData(null, null, 4409, DialAction.Superseded)]
    [InlineData(null, null, 4408, DialAction.Retry)]
    public void Dial_outcomes_follow_the_table(int? status, string? error, int? close, DialAction expected)
        => Assert.Equal(expected, DialOutcomes.Classify(status, error, close));

    [Fact]
    public void The_link_is_dead_after_three_silent_intervals_and_a_pong_gives_rtt_and_offset()
    {
        var time = new ManualTime();
        var options = new LinkOptions { Time = time, HeartbeatInterval = TimeSpan.FromSeconds(15) };
        var heartbeat = new LinkHeartbeat(options);
        Assert.True(heartbeat.PingDue());
        var ping = heartbeat.NextPing("epoch-1");
        Assert.False(heartbeat.PingDue());
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(heartbeat.PingDue());

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(heartbeat.IsDead());
        heartbeat.OnFrameReceived();
        time.Advance(TimeSpan.FromSeconds(44));
        Assert.False(heartbeat.IsDead());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(heartbeat.IsDead());

        // The peer answers 2 s after our ping went out with a clock 10 s ahead; the reply takes the same 2 s back.
        var peer = new ManualTime();
        var peerHeartbeat = new LinkHeartbeat(new LinkOptions { Time = peer });
        var sent = new LinkHeartbeat(options);
        var ourPing = sent.NextPing(null);
        peer.Advance(time.GetUtcNow() - peer.GetUtcNow() + TimeSpan.FromSeconds(12));
        var pong = peerHeartbeat.PongFor(ourPing);
        time.Advance(TimeSpan.FromSeconds(4));
        sent.OnPong(pong);
        Assert.Equal(TimeSpan.FromSeconds(4), sent.Rtt);
        Assert.Equal(TimeSpan.FromSeconds(10), sent.ClockOffset);
        Assert.NotEmpty(ping);
    }
}
