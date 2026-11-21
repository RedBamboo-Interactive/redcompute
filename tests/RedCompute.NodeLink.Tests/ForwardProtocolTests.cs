using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>The forward's wire pieces (Phase 4 design 2.2, 6.1): the OPEN meta, the head, the trailer, the WS mapping.</summary>
public sealed class ForwardProtocolTests
{
    private static JsonObject HttpMeta() => new()
    {
        ["runtime"] = "comfyui-1",
        ["mode"] = "http",
        ["method"] = "POST",
        ["path"] = "/upload/image",
        ["query"] = "",
        ["headers"] = new JsonArray(new JsonArray("content-type", "multipart/form-data; boundary=x")),
        ["bodyBytes"] = 812341,
    };

    [Fact]
    public void An_http_open_round_trips_through_its_meta()
    {
        var open = ForwardOpen.Http("comfyui-1", "POST", "/upload/image", "overwrite=true", [new("content-type", "image/png")], 812341);
        var parsed = ForwardOpen.Parse(open.ToMeta(), ForwardModes.Http, out var error);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(open.Runtime, parsed.Runtime);
        Assert.Equal("POST", parsed.Method);
        Assert.Equal("/upload/image", parsed.Path);
        Assert.Equal("overwrite=true", parsed.Query);
        Assert.Equal(812341, parsed.BodyBytes);
        Assert.Equal([new("content-type", "image/png")], parsed.Headers);
    }

    [Fact]
    public void A_ws_open_round_trips_and_carries_no_method_headers_or_body()
    {
        var meta = ForwardOpen.Ws("comfyui-1", "/ws", "clientId=abc").ToMeta();
        Assert.Equal(["runtime", "mode", "path", "query"], meta.Select(p => p.Key));
        var parsed = ForwardOpen.Parse(meta, ForwardModes.Ws, out var error);
        Assert.Null(error);
        Assert.Equal("clientId=abc", parsed!.Query);
    }

    public static TheoryData<string, Action<JsonObject>> BadMetas => new()
    {
        { "extra member", m => m["argv"] = "rm -rf /" },
        { "missing member", m => m.Remove("query") },
        { "host smuggled", m => m["host"] = "10.0.0.1" },
        { "upper-case runtime", m => m["runtime"] = "ComfyUI" },
        { "runtime with a slash", m => m["runtime"] = "../x" },
        { "long runtime", m => m["runtime"] = new string('a', 65) },
        { "lower-case method", m => m["method"] = "post" },
        { "method with a space", m => m["method"] = "GET /x" },
        { "relative path", m => m["path"] = "upload/image" },
        { "path with a query", m => m["path"] = "/view?filename=x" },
        { "path with a fragment", m => m["path"] = "/view#x" },
        { "path with a space", m => m["path"] = "/a b" },
        { "path with CR LF", m => m["path"] = "/a\r\nHost: evil" },
        { "path with non-ASCII", m => m["path"] = "/café" },
        { "query with a fragment", m => m["query"] = "a=1#x" },
        { "query with LF", m => m["query"] = "a=1\nb" },
        { "header value with CR", m => m["headers"] = new JsonArray(new JsonArray("x-a", "1\r\nX-Injected: 1")) },
        { "header value with LF", m => m["headers"] = new JsonArray(new JsonArray("x-a", "1\nX-Injected: 1")) },
        { "header value with NUL", m => m["headers"] = new JsonArray(new JsonArray("x-a", "1\0")) },
        { "header name with a colon", m => m["headers"] = new JsonArray(new JsonArray("x:a", "1")) },
        { "header not a pair", m => m["headers"] = new JsonArray(new JsonArray("x-a")) },
        { "header not strings", m => m["headers"] = new JsonArray(new JsonArray("x-a", 1)) },
        { "65 headers", m => m["headers"] = new JsonArray(Enumerable.Range(0, 65).Select(i => (JsonNode)new JsonArray($"x-{i}", "v")).ToArray()) },
        { "body below -1", m => m["bodyBytes"] = -2 },
        { "body above 2^40", m => m["bodyBytes"] = (1L << 40) + 1 },
        { "body as a string", m => m["bodyBytes"] = "5" },
        { "body as a fraction", m => m["bodyBytes"] = 1.5 },
        { "body as an exponent", m => m["bodyBytes"] = JsonNode.Parse("5e3") },
        { "body missing", m => m.Remove("bodyBytes") },
        { "mode ws on a port channel", m => m["mode"] = "ws" },
    };

    [Fact]
    public void The_unspoiled_meta_parses()
    {
        // The control for the malformed cases below: each spoils exactly one thing of this meta. Built in memory (an
        // int bodyBytes) and read from the wire, it is the same OPEN.
        var open = ForwardOpen.Parse(HttpMeta(), ForwardModes.Http, out var error);
        Assert.True(open is not null, error);
        Assert.Equal(812341, open.BodyBytes);
        var wire = ForwardOpen.Parse(JsonNode.Parse(HttpMeta().ToJsonString())!.AsObject(), ForwardModes.Http, out error);
        Assert.True(wire is not null, error);
        Assert.Equal(open.ToMeta().ToJsonString(), wire.ToMeta().ToJsonString());
    }

    [Theory]
    [MemberData(nameof(BadMetas))]
    public void A_malformed_http_open_is_refused(string why, Action<JsonObject> spoil)
    {
        // The unspoiled meta parses (The_unspoiled_meta_parses), so a refusal here is this case's.
        Assert.NotNull(ForwardOpen.Parse(HttpMeta(), ForwardModes.Http, out _));
        var meta = HttpMeta();
        spoil(meta);
        Assert.True(ForwardOpen.Parse(meta, ForwardModes.Http, out var error) is null, why);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_ws_open_with_http_members_is_refused()
    {
        var meta = ForwardOpen.Ws("comfyui-1", "/ws", "").ToMeta();
        meta["method"] = "GET";
        Assert.Null(ForwardOpen.Parse(meta, ForwardModes.Ws, out _));
        Assert.Null(ForwardOpen.Parse(null, ForwardModes.Ws, out _));
    }

    [Fact]
    public void The_head_is_a_length_then_json_and_may_span_frames()
    {
        var head = new ForwardHead(201, [new("content-type", "image/png"), new("x-a", "1")]);
        var bytes = head.Encode();
        Assert.Equal((uint)(bytes.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var body = new byte[] { 9, 8, 7 };
        var stream = bytes.Concat(body).ToArray();

        var reader = new ForwardHead.Reader();
        var taken = 0;
        foreach (var size in new[] { 1, 2, 3, 5, 1000 })
        {
            if (taken >= stream.Length) break;
            var slice = stream.AsSpan(taken, Math.Min(size, stream.Length - taken));
            var used = reader.Feed(slice);
            taken += used;
            if (reader.Head is not null) break;
        }
        Assert.NotNull(reader.Head);
        Assert.Equal(201, reader.Head.Status);
        Assert.Equal(head.Headers, reader.Head.Headers);
        Assert.Equal(bytes.Length, taken);
        Assert.Equal(0, reader.Feed(body));
    }

    [Fact]
    public void A_head_over_64_KiB_is_refused_on_both_sides()
    {
        var big = new ForwardHead(200, [new("x-big", new string('a', 64 * 1024))]);
        Assert.Equal(ForwardErrorCodes.HeadTooLarge, Assert.Throws<ForwardException>(() => big.Encode()).Code);

        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, 64 * 1024 + 1);
        Assert.Equal(ForwardErrorCodes.HeadTooLarge, Assert.Throws<ForwardStreamException>(() => new ForwardHead.Reader().Feed(length)).Code);
        BinaryPrimitives.WriteUInt32LittleEndian(length, 0);
        Assert.Equal(ForwardErrorCodes.HeadTooLarge, Assert.Throws<ForwardStreamException>(() => new ForwardHead.Reader().Feed(length)).Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"status\":200}")]
    [InlineData("{\"status\":99,\"headers\":[]}")]
    [InlineData("{\"status\":600,\"headers\":[]}")]
    [InlineData("{\"status\":\"200\",\"headers\":[]}")]
    [InlineData("{\"status\":200,\"headers\":[],\"x\":1}")]
    [InlineData("{\"status\":200,\"headers\":[[\"a\"]]}")]
    [InlineData("{\"status\":200,\"headers\":[[\"a b\",\"1\"]]}")]
    [InlineData("{\"status\":200,\"headers\":[[\"a\",\"1\\r\\nb: 2\"]]}")]
    public void A_malformed_head_is_bad_forward(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var framed = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)bytes.Length);
        bytes.CopyTo(framed, 4);
        Assert.Equal(ForwardErrorCodes.BadForward, Assert.Throws<ForwardStreamException>(() => new ForwardHead.Reader().Feed(framed)).Code);
    }

    [Fact]
    public void The_trailer_names_the_bytes_and_their_hash()
    {
        var body = "hello"u8.ToArray();
        var sha = ForwardFeatures.Sha(body);
        var trailer = new ForwardTrailer(body.Length, sha).Encode();
        Assert.Null(ForwardTrailer.Check(trailer, body.Length, sha));
        Assert.Equal(ForwardErrorCodes.Truncated, ForwardTrailer.Check(trailer, body.Length - 1, sha)!.Code);
        Assert.Equal(ForwardErrorCodes.Truncated, ForwardTrailer.Check(trailer, body.Length, ForwardFeatures.Sha([1]))!.Code);
        Assert.Equal(ForwardErrorCodes.Truncated, ForwardTrailer.Check([], body.Length, sha)!.Code);
        Assert.Null(ForwardTrailer.TryParse(Encoding.UTF8.GetBytes($"{{\"bytes\":5,\"sha256\":\"{sha.ToUpperInvariant()}\"}}")));
        Assert.Null(ForwardTrailer.TryParse(Encoding.UTF8.GetBytes($"{{\"bytes\":-1,\"sha256\":\"{sha}\"}}")));
        Assert.Null(ForwardTrailer.TryParse(Encoding.UTF8.GetBytes($"{{\"bytes\":5,\"sha256\":\"{sha}\",\"more\":1}}")));
    }

    [Fact]
    public void A_ws_message_is_a_type_byte_then_its_payload_and_a_close_is_code_and_reason()
    {
        Assert.Equal(new byte[] { 1, (byte)'h', (byte)'i' }, ForwardWsMessage.Encode(ForwardWsMessage.Text, "hi"u8));
        Assert.Equal(new byte[] { 2 }, ForwardWsMessage.Encode(ForwardWsMessage.Binary, []));
        Assert.Equal((1000, "bye"), ForwardWsMessage.ParseClose(ForwardWsMessage.EncodeClose(1000, "bye")));
        Assert.Equal(((int?)null, (string?)null), ForwardWsMessage.ParseClose(ForwardWsMessage.EncodeClose(null, "ignored")));
        Assert.Throws<ForwardStreamException>(() => ForwardWsMessage.ParseClose("{\"code\":999,\"reason\":\"\"}"u8));
        Assert.Throws<ForwardStreamException>(() => ForwardWsMessage.ParseClose("{\"code\":1000}"u8));
        Assert.Throws<ForwardStreamException>(() => ForwardWsMessage.ParseClose("nope"u8));
        // A reason is at most 123 bytes, as a WebSocket close's is.
        Assert.True(ForwardWsMessage.EncodeClose(1000, new string('x', 400)).Length < 200);
    }

    [Fact]
    public void Port_ws_is_a_message_class_with_a_16_MiB_cap_and_both_forward_classes_autotune()
    {
        var ws = ChannelClasses.Find("port.ws");
        Assert.NotNull(ws);
        Assert.Equal(2, ws.Priority);
        Assert.Equal(256 * 1024, ws.Window);
        Assert.True(ws.Messages);
        Assert.False(ws.MayBeDurable);
        Assert.Equal(16 * 1024 * 1024, ws.ReassemblyCap);
        Assert.True(ws.AutoTune);
        Assert.True(ChannelClasses.Port.AutoTune);
        Assert.False(ChannelClasses.Port.Messages);
        Assert.False(ChannelClasses.Stdio.AutoTune);
        Assert.False(ChannelClasses.Blob.AutoTune);
    }

    [Fact]
    public async Task A_close_carries_its_payload_to_the_peer()
    {
        RecordingHandler? accepted = null;
        LinkChannelHandle? peer = null;
        var capture = new CloseCapture();
        await using var pair = new LinkPair(serverHandler: new TestLinkHandler
        {
            Accept = info => new ChannelAcceptance(capture),
        }).Start();
        var node = await pair.OnlineAsync();
        var channel = await node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), accepted = new RecordingHandler());
        await channel.SendAsync("body"u8.ToArray());
        channel.Close("{\"bytes\":4}"u8.ToArray());
        peer = await capture.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("{\"bytes\":4}", Encoding.UTF8.GetString(capture.Payload));
        await accepted.RemoteClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(accepted.RemoteClosed.Task.IsCompletedSuccessfully);
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ArgumentException>(() => peer.Close(new byte[FrameCodec.MaxSendPayload + 1]));
    }

    [Fact]
    public async Task An_acceptor_refuses_with_its_own_code()
    {
        await using var pair = new LinkPair(serverHandler: new TestLinkHandler
        {
            Accept = _ => throw new LinkChannelResetException("route_not_allowed", "Not on the allowlist."),
        }).Start();
        var node = await pair.OnlineAsync();
        var refused = await Assert.ThrowsAsync<LinkChannelResetException>(() =>
            node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), new RecordingHandler()));
        Assert.Equal("route_not_allowed", refused.Code);
        Assert.Equal("Not on the allowlist.", refused.Message);

        // Any other exception stays internal_error, with no detail of its own type.
        await using var other = new LinkPair(serverHandler: new TestLinkHandler { Accept = _ => throw new InvalidOperationException("x") }).Start();
        var session = await other.OnlineAsync();
        var failed = await Assert.ThrowsAsync<LinkChannelResetException>(() =>
            session.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), new RecordingHandler()));
        Assert.Equal(LinkErrorCodes.Internal, failed.Code);
    }

    [Fact]
    public async Task An_opened_handler_hears_of_its_channel_before_the_peer_sends_anything()
    {
        var opened = new OpenedProbe();
        await using var pair = new LinkPair(serverHandler: new TestLinkHandler { Accept = _ => new ChannelAcceptance(opened) }).Start();
        var node = await pair.OnlineAsync();
        var received = new RecordingHandler();
        await node.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), received);
        var handle = await opened.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await handle.SendAsync("first"u8.ToArray());
        await Wait.UntilAsync(() => received.Count == 1);
        Assert.Equal("first", Encoding.UTF8.GetString(received.Messages[0]));
    }

    private sealed class CloseCapture : IChannelHandler
    {
        public TaskCompletionSource<LinkChannelHandle> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Payload { get; private set; } = [];

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            Payload = channel.RemoteClosePayload.ToArray();
            channel.Close();
            Closed.TrySetResult(channel);
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message) => Closed.TrySetException(new Exception(code));
    }

    private sealed class OpenedProbe : IChannelHandler, IChannelOpenedHandler
    {
        public TaskCompletionSource<LinkChannelHandle> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnOpened(LinkChannelHandle channel) => Opened.TrySetResult(channel);
        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct) => ValueTask.CompletedTask;
        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }
}
