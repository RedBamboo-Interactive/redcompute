using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// The forward end to end over an in-memory link (Phase 4 design 2.2, 2.3): the server's <see cref="ForwardClient"/>
/// against the node's <see cref="ForwardHost"/> and a fake runtime.
/// </summary>
public sealed class ForwardTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required LinkPair Pair { get; init; }
        public required FakeRuntime Runtime { get; init; }
        public required ForwardHost Host { get; init; }
        public required ForwardClient Client { get; init; }
        public required System.Collections.Concurrent.ConcurrentQueue<ForwardAudit> AuditSource { get; init; }

        public HttpClient Http(string runtime = FakeRuntime.Runtime)
            => new(Client.CreateHandler(runtime)) { BaseAddress = new Uri("http://comfyui-1.node-x.leaf-node.invalid:8188/") };

        public ValueTask DisposeAsync() => Pair.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(ForwardLimits? nodeLimits = null, ForwardLimits? serverLimits = null,
        IReadOnlyList<string>? nodeFeatures = null, FakeRuntime? runtime = null, IChannelHandler? rawNodeHandler = null)
    {
        runtime ??= new FakeRuntime();
        var audit = new System.Collections.Concurrent.ConcurrentQueue<ForwardAudit>();
        var host = new ForwardHost(runtime, nodeLimits, audit.Enqueue);
        ILinkHandler nodeHandler = rawNodeHandler is null
            ? new ForwardNodeHandler(host)
            : new TestLinkHandler { Accept = _ => new ChannelAcceptance(rawNodeHandler) };
        var pair = new LinkPair(nodeHandler: nodeHandler, nodeFeatures: nodeFeatures ?? ForwardFeatures.Node,
            serverFeatures: ForwardFeatures.Server).Start();
        await pair.OnlineAsync();
        var client = new ForwardClient(() => pair.Server.FindSession(LinkPair.NodeId), serverLimits);
        return new Rig { Pair = pair, Runtime = runtime, Host = host, Client = client, AuditSource = audit };
    }

    [Fact]
    public async Task A_get_streams_the_runtime_body_and_its_head()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        using var response = await http.GetAsync("/blob/5/1048577?x=1", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("5", response.Headers.GetValues("x-seed").Single());
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(SeededStream.Expected(5, 1048577), body);

        var open = rig.Runtime.Opens.Single();
        Assert.Equal("GET", open.Method);
        Assert.Equal("/blob/5/1048577", open.Path);
        Assert.Equal("x=1", open.Query);
        Assert.Equal(0, open.BodyBytes);
        Assert.DoesNotContain(open.Headers, h => h.Key is "host" or "connection" or "transfer-encoding" or "content-length");
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 0 && rig.Client.OpenExchanges == 0, what: "both tables to empty");
    }

    [Fact]
    public async Task A_post_streams_its_body_with_a_length_or_chunked()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        var body = SeededStream.Expected(9, 3 * 1024 * 1024 + 7);

        using var fixedLength = await http.PostAsync("/echo-hash", new ByteArrayContent(body));
        var answer = JsonDocument.Parse(await fixedLength.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(body.Length, answer.GetProperty("bytes").GetInt64());
        Assert.Equal(ForwardFeatures.Sha(body), answer.GetProperty("sha256").GetString());

        using var chunked = await http.PostAsync("/echo-hash", new StreamContent(new SeededStream(9, body.Length)));
        answer = JsonDocument.Parse(await chunked.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ForwardFeatures.Sha(body), answer.GetProperty("sha256").GetString());

        Assert.Equal([body.Length, -1], rig.Runtime.Opens.Select(o => o.BodyBytes));
    }

    [Theory]
    [InlineData("rt-x", "GET", "/system_stats", ForwardErrorCodes.RuntimeUnknown)]
    [InlineData("rt-off", "GET", "/system_stats", ForwardErrorCodes.RuntimeNotRunning)]
    [InlineData(FakeRuntime.Runtime, "POST", "/manager/queue/install", ForwardErrorCodes.RouteNotAllowed)]
    public async Task The_node_refuses_at_open_with_its_code(string runtime, string method, string path, string code)
    {
        await using var rig = await StartAsync();
        using var http = rig.Http(runtime);
        var refused = await Assert.ThrowsAsync<ForwardException>(() => http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)));
        Assert.Equal(code, refused.Code);
        Assert.Equal(0, rig.Runtime.ExchangesStarted);
        await Wait.UntilAsync(() => rig.Client.OpenExchanges == 0, what: "the slot back");
        Assert.Contains(rig.AuditSource.ToArray(), a => a.Outcome == code && a.Path == path);
    }

    [Fact]
    public async Task A_body_over_the_route_cap_is_refused_declared_and_streamed()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        var declared = await Assert.ThrowsAsync<ForwardException>(() => http.PostAsync("/small", new ByteArrayContent(new byte[2048])));
        Assert.Equal(ForwardErrorCodes.BodyTooLarge, declared.Code);
        Assert.Equal(0, rig.Runtime.ExchangesStarted);

        var streamed = await Assert.ThrowsAsync<ForwardException>(() => http.PostAsync("/small", new StreamContent(new SeededStream(1, 64 * 1024))));
        Assert.Equal(ForwardErrorCodes.BodyTooLarge, streamed.Code);
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 0 && rig.Client.OpenExchanges == 0, what: "both tables to empty");

        using var fits = await http.PostAsync("/small", new StreamContent(new SeededStream(1, 1024)));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
    }

    [Fact]
    public async Task A_runtime_failure_or_an_oversized_head_is_upstream_failed()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        Assert.Equal(ForwardErrorCodes.UpstreamFailed, (await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/fail"))).Code);
        Assert.Equal(ForwardErrorCodes.UpstreamFailed, (await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/head/70000"))).Code);
        using var small = await http.GetAsync("/head/100");
        Assert.Equal(100, small.Headers.GetValues("x-big").Single().Length);
        using var missing = await http.GetAsync("/nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not found", await missing.Content.ReadAsStringAsync());
    }

    public static TheoryData<string, string> Lies => new()
    {
        { "short", "{\"bytes\":4,\"sha256\":\"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\"}" },
        { "other hash", "{\"bytes\":3,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}" },
        { "no trailer", "" },
        { "not json", "nope" },
    };

    [Theory]
    [MemberData(nameof(Lies))]
    public async Task A_body_that_does_not_match_its_trailer_is_never_a_success(string why, string trailer)
    {
        await using var rig = await StartAsync(rawNodeHandler: new LyingNode(Encoding.UTF8.GetBytes(trailer)));
        using var http = rig.Http();
        using var response = await http.GetAsync("/x", HttpCompletionOption.ResponseHeadersRead);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsByteArrayAsync());
        var stream = failure as ForwardStreamException ?? failure.InnerException as ForwardStreamException;
        Assert.True(stream is not null, why + ": " + failure);
        Assert.Equal(ForwardErrorCodes.Truncated, stream.Code);
    }

    [Fact]
    public async Task A_node_that_closes_before_its_head_fails_the_exchange_truncated()
    {
        await using var rig = await StartAsync(rawNodeHandler: new LyingNode([], sendHead: false));
        using var http = rig.Http();
        Assert.Equal(ForwardErrorCodes.Truncated, (await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/x"))).Code);
    }

    [Fact]
    public async Task Without_the_feature_nothing_is_forwarded()
    {
        await using var rig = await StartAsync(nodeFeatures: [LinkFeatures.DescribeV1]);
        using var http = rig.Http();
        Assert.Equal(ForwardErrorCodes.Unsupported, (await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/blob/1/1"))).Code);
        Assert.Equal(ForwardErrorCodes.Unsupported,
            (await Assert.ThrowsAsync<ForwardException>(() => rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/ws", "seed=1&count=1"))).Code);

        // A server that opens one anyway is refused by the node.
        var session = rig.Pair.Server.FindSession(LinkPair.NodeId)!;
        var raw = await Assert.ThrowsAsync<LinkChannelResetException>(() => session.OpenChannelAsync(
            new ChannelOpenRequest(ChannelClasses.Port.Name, Meta: ForwardOpen.Http(FakeRuntime.Runtime, "GET", "/", "", [], 0).ToMeta()),
            new RecordingHandler()));
        Assert.Equal(LinkErrorCodes.ClassNotSupported, raw.Code);

        var offline = new ForwardClient(() => null);
        using var none = new HttpClient(offline.CreateHandler("rt-1"));
        Assert.Equal(ForwardErrorCodes.NodeOffline, (await Assert.ThrowsAsync<ForwardException>(() => none.GetAsync("http://x.invalid/"))).Code);
    }

    [Fact]
    public async Task A_malformed_open_is_refused_bad_forward()
    {
        await using var rig = await StartAsync();
        var session = rig.Pair.Server.FindSession(LinkPair.NodeId)!;
        var meta = ForwardOpen.Http(FakeRuntime.Runtime, "GET", "/", "", [], 0).ToMeta();
        meta["path"] = "/a\r\nHost: evil";
        var refused = await Assert.ThrowsAsync<LinkChannelResetException>(() =>
            session.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.Port.Name, Meta: meta), new RecordingHandler()));
        Assert.Equal(ForwardErrorCodes.BadForward, refused.Code);
        Assert.Empty(rig.Runtime.Opens);
    }

    [Fact]
    public async Task Past_32_exchanges_per_runtime_callers_queue_on_the_server()
    {
        await using var rig = await StartAsync(runtime: new FakeRuntime { BlobChunkDelay = TimeSpan.FromMilliseconds(10) });
        using var http = rig.Http();
        var downloads = Enumerable.Range(0, 40).Select(async i =>
        {
            var body = await http.GetByteArrayAsync($"/blob/{i}/{128 * 1024}");
            return body.AsSpan().SequenceEqual(SeededStream.Expected(i, 128 * 1024));
        }).ToList();
        Assert.All(await Task.WhenAll(downloads), Assert.True);
        Assert.InRange(rig.Runtime.MaxConcurrent, 2, 32);
        Assert.DoesNotContain(rig.AuditSource.ToArray(), a => a.Outcome == ForwardErrorCodes.Busy);
    }

    [Fact]
    public async Task The_node_answers_busy_past_its_own_table()
    {
        await using var rig = await StartAsync(nodeLimits: new ForwardLimits { ExchangesPerRuntime = 1 },
            serverLimits: new ForwardLimits { BusyRetries = 0 });
        using var http = rig.Http();
        using var cancel = new CancellationTokenSource();
        var hang = http.GetAsync("/hang", cancel.Token);
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 1, what: "the first exchange");
        Assert.Equal(ForwardErrorCodes.Busy, (await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/blob/1/10"))).Code);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hang);
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 0 && rig.Client.OpenExchanges == 0, what: "the slot back");
        Assert.Equal(10, (await http.GetByteArrayAsync("/blob/1/10")).Length);
    }

    [Fact]
    public async Task A_reader_that_stops_early_frees_the_exchange_on_both_sides()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        var response = await http.GetAsync("/blob/3/67108864", HttpCompletionOption.ResponseHeadersRead);
        await using (var body = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[100 * 1024];
            await body.ReadExactlyAsync(buffer);
            Assert.Equal(SeededStream.Expected(3, buffer.Length), buffer);
        }
        response.Dispose();
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 0 && rig.Client.OpenExchanges == 0, what: "both tables to empty");
        Assert.True(rig.Pair.ServerRuns.Count == 1, "one connection");
    }

    [Fact]
    public async Task A_websocket_carries_text_and_binary_both_ways_and_closes_cleanly()
    {
        await using var rig = await StartAsync();
        using var socket = await rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/ws", "seed=3&count=20");
        Assert.Equal(WebSocketState.Open, socket.State);
        var sent = new List<byte[]>();
        for (var i = 0; i < 10; i++)
        {
            var message = i == 4 ? SeededStream.Expected(i, 1024 * 1024) : Encoding.UTF8.GetBytes($"hello {i}");
            sent.Add(message);
            await socket.SendAsync(message, i == 4 ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, CancellationToken.None);
        }
        // a fragmented message is one link message
        await socket.SendAsync("frag-"u8.ToArray(), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync("ment"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        sent.Add("frag-ment"u8.ToArray());

        var buffer = new byte[1000]; // smaller than most messages: they come in parts
        for (var i = 0; i < 20; i++)
        {
            var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            Assert.Equal(FakeRuntime.WsMessage(3, 7, i), message.ToArray());
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        Assert.Equal("bye", socket.CloseStatusDescription);
        var received = rig.Runtime.WsReceived["3"];
        lock (received) Assert.Equal(sent, received);
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 0 && rig.Client.OpenExchanges == 0, what: "both tables to empty");
    }

    [Fact]
    public async Task A_websocket_message_is_at_most_16_MiB_and_a_refused_handshake_is_typed()
    {
        await using var rig = await StartAsync();
        using var socket = await rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/ws", "seed=1&count=0");
        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.SendAsync(new byte[16 * 1024 * 1024], WebSocketMessageType.Binary, true, CancellationToken.None));
        Assert.Equal(ForwardErrorCodes.UpstreamFailed,
            (await Assert.ThrowsAsync<ForwardException>(() => rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/nows", ""))).Code);
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed,
            (await Assert.ThrowsAsync<ForwardException>(() => rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/manager/ws", ""))).Code);
    }

    [Fact]
    public async Task A_server_restart_fails_in_flight_exchanges_link_reset_promptly()
    {
        await using var rig = await StartAsync(runtime: new FakeRuntime { BlobChunkDelay = TimeSpan.FromMilliseconds(5) });
        using var http = rig.Http();
        using var response = await http.GetAsync("/blob/1/67108864", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[64 * 1024]);
        using var socket = await rig.Client.ConnectWebSocketAsync(FakeRuntime.Runtime, "/ws", "seed=1&count=0");

        var before = DateTime.UtcNow;
        await rig.Pair.RestartServerAsync();
        var read = Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (await body.ReadAsync(buffer) > 0)
            {
            }
        });
        var failure = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinkErrorCodes.LinkReset, Assert.IsType<ForwardStreamException>(failure).Code);
        var ws = await Assert.ThrowsAsync<WebSocketException>(() => socket.ReceiveAsync(new byte[10], CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.True(DateTime.UtcNow - before < TimeSpan.FromSeconds(3));
        await Wait.UntilAsync(() => rig.Client.OpenExchanges == 0, what: "the server's table to empty");
    }

    /// <summary>
    /// Invariant 6 of design 6.2, in process: four large downloads on <c>port</c> channels (priority 2, 16 KiB frames)
    /// do not hold up control round trips. The exact bound (one frame time + 5 ms) is the scheduler's pure test; here
    /// the real link must stay well inside a generous one while the downloads run.
    /// </summary>
    [Fact]
    public async Task Control_round_trips_stay_quick_while_four_large_downloads_run()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        var downloads = Enumerable.Range(0, 4).Select(async i =>
        {
            using var response = await http.GetAsync($"/blob/{i}/{64 * 1024 * 1024}", HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0) total += read;
            return total;
        }).ToList();
        var session = rig.Pair.Server.FindSession(LinkPair.NodeId)!;
        var latencies = new List<double>();
        await Wait.UntilAsync(() => rig.Host.OpenExchanges == 4, what: "the downloads to start");
        while (!downloads.All(d => d.IsCompleted) && latencies.Count < 200)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<LinkRpcException>(() => session.CallAsync("test.none"));
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            await Task.Delay(2);
        }
        Assert.All(await Task.WhenAll(downloads), total => Assert.Equal(64 * 1024 * 1024, total));
        latencies.Sort();
        Assert.True(latencies.Count >= 10, $"only {latencies.Count} round trips during the downloads");
        var p99 = latencies[(int)(latencies.Count * 0.99)];
        Assert.True(p99 < 50, $"control p99 {p99:F1} ms over {latencies.Count} round trips");
    }

    [Fact]
    public async Task Every_open_has_an_audit_line_without_bodies()
    {
        await using var rig = await StartAsync();
        using var http = rig.Http();
        using (var ok = await http.PostAsync("/echo-hash", new StringContent("secret prompt")))
            await ok.Content.ReadAsStringAsync();
        await Assert.ThrowsAsync<ForwardException>(() => http.GetAsync("/manager/x"));
        await Wait.UntilAsync(() => rig.AuditSource.Count == 2, what: "two audit lines");
        var lines = rig.AuditSource.ToArray();
        var done = Assert.Single(lines, a => a.Outcome == "ok");
        Assert.Equal(("POST", "/echo-hash", 200, 13L), (done.Method, done.Path, done.Status, done.RequestBytes));
        Assert.Single(lines, a => a.Outcome == ForwardErrorCodes.RouteNotAllowed);
        Assert.DoesNotContain(lines, a => JsonSerializer.Serialize(a).Contains("secret", StringComparison.Ordinal));
    }

    /// <summary>A node that answers "abc" with the trailer it is given (or no head at all).</summary>
    private sealed class LyingNode(byte[] trailer, bool sendHead = true) : IChannelHandler, IChannelOpenedHandler
    {
        public void OnOpened(LinkChannelHandle channel) => _ = Task.Run(async () =>
        {
            if (sendHead)
            {
                await channel.SendAsync(new ForwardHead(200, []).Encode());
                await channel.SendAsync("abc"u8.ToArray());
            }
            channel.Close(trailer);
        });

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }
}
