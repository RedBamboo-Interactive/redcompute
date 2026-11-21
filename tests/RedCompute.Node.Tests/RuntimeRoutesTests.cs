using System.Text;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink.Forward;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The ComfyUI route allowlist and its parameter rules (Phase 4 design 2.4), and the body guards: what the node lets
/// through to a runtime, rebuilt from parsed parts, and everything it refuses.
/// </summary>
public sealed class RuntimeRoutesTests
{
    private const string Kind = RuntimeKinds.ComfyUi;

    [Theory]
    [InlineData("GET", "/system_stats", "", "/system_stats", "")]
    [InlineData("GET", "/queue", "", "/queue", "")]
    [InlineData("GET", "/object_info", "", "/object_info", "")]
    [InlineData("GET", "/object_info/KSampler", "", "/object_info/KSampler", "")]
    [InlineData("GET", "/history/3f2a-91", "", "/history/3f2a-91", "")]
    [InlineData("GET", "/history/3f2a-91", "max_items=5", "/history/3f2a-91", "max_items=5")]
    [InlineData("GET", "/view", "filename=ComfyUI_00001_.png&subfolder=&type=output", "/view", "filename=ComfyUI_00001_.png&subfolder=&type=output")]
    [InlineData("GET", "/view", "filename=a%20b.png&type=temp&subfolder=x/y", "/view", "filename=a%20b.png&type=temp&subfolder=x%2Fy")]
    [InlineData("POST", "/prompt", "", "/prompt", "")]
    [InlineData("POST", "/upload/image", "", "/upload/image", "")]
    [InlineData("POST", "/queue", "", "/queue", "")]
    [InlineData("POST", "/interrupt", "", "/interrupt", "")]
    [InlineData("POST", "/free", "", "/free", "")]
    [InlineData("POST", "/api/jobs/3f2a-91/cancel", "", "/api/jobs/3f2a-91/cancel", "")]
    public void Each_allowed_route_is_rebuilt_from_its_parts(string method, string path, string query, string rebuiltPath, string rebuiltQuery)
    {
        var route = RuntimeRoutes.Http(Kind, method, path, query, out var refusal);
        Assert.True(route is not null, refusal);
        Assert.Equal(rebuiltPath, route.Path);
        Assert.Equal(rebuiltQuery, route.Query);
    }

    [Theory]
    [InlineData("GET", "/manager/queue/start", "")]
    [InlineData("POST", "/manager/queue/install", "")]
    [InlineData("GET", "/customnode/getlist", "")]
    [InlineData("POST", "/api/manager/reboot", "")]
    [InlineData("GET", "/api/userdata/comfy.settings.json", "")]
    [InlineData("POST", "/api/userdata/x", "")]
    [InlineData("GET", "/userdata", "")]
    [InlineData("GET", "/internal/logs", "")]
    [InlineData("GET", "/extensions", "")]
    [InlineData("GET", "/", "")]
    [InlineData("DELETE", "/queue", "")]
    [InlineData("GET", "/prompt", "")]
    [InlineData("POST", "/system_stats", "")]
    [InlineData("PUT", "/upload/image", "")]
    [InlineData("POST", "/upload/mask", "")]
    [InlineData("GET", "/history", "")]
    [InlineData("GET", "/history/../manager", "")]
    [InlineData("GET", "/history/a%2F..%2Fmanager", "")]
    [InlineData("GET", "/history/x/y", "")]
    [InlineData("GET", "/system_stats/", "")]
    [InlineData("GET", "//system_stats", "")]
    [InlineData("GET", "/./system_stats", "")]
    [InlineData("GET", "/system_stats", "x=1")]
    [InlineData("GET", "/history/1", "max_items=1;drop")]
    [InlineData("GET", "/history/1", "max_items")]
    [InlineData("GET", "/view", "filename=../secret.png&type=output")]
    [InlineData("GET", "/view", "filename=..%5Csecret.png&type=output")]
    [InlineData("GET", "/view", "filename=a/b.png&type=output")]
    [InlineData("GET", "/view", "filename=C:x.png&type=output")]
    [InlineData("GET", "/view", "filename=.hidden&type=output")]
    [InlineData("GET", "/view", "filename=x.png&type=../../")]
    [InlineData("GET", "/view", "filename=x.png&type=models")]
    [InlineData("GET", "/view", "filename=x.png&subfolder=../../etc")]
    [InlineData("GET", "/view", "filename=x.png&subfolder=/etc")]
    [InlineData("GET", "/view", "filename=x.png&filename=y.png")]
    [InlineData("GET", "/view", "filename=x.png&path=/etc/passwd")]
    [InlineData("GET", "/view", "type=output")]
    [InlineData("GET", "/view", "filename=x%00.png")]
    [InlineData("GET", "/view", "filename=%E0%A4%A")]
    public void Everything_else_is_refused(string method, string path, string query)
    {
        Assert.Null(RuntimeRoutes.Http(Kind, method, path, query, out var refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
    }

    [Fact]
    public void Each_route_has_its_body_rule_and_cap()
    {
        Assert.Equal((0L, BodyRule.None), Rule("GET", "/system_stats"));
        Assert.Equal((16 * RuntimeRoutes.MiB, BodyRule.Any), Rule("POST", "/prompt"));
        Assert.Equal((256 * RuntimeRoutes.MiB, BodyRule.ImageUpload), Rule("POST", "/upload/image"));
        Assert.Equal((64 * RuntimeRoutes.KiB, BodyRule.QueueDeleteOnly), Rule("POST", "/queue"));
        Assert.Equal((64 * RuntimeRoutes.KiB, BodyRule.Any), Rule("POST", "/interrupt"));

        static (long, BodyRule) Rule(string method, string path)
        {
            var route = RuntimeRoutes.Http(Kind, method, path, "", out _)!;
            return (route.MaxBody, route.Body);
        }
    }

    [Fact]
    public void The_websocket_is_ws_with_one_client_id()
    {
        Assert.Equal("clientId=a1-b_2.c", RuntimeRoutes.Ws(Kind, "/ws", "clientId=a1-b_2.c", out _)!.Query);
        Assert.Equal("", RuntimeRoutes.Ws(Kind, "/ws", "", out _)!.Query);
        foreach (var (path, query) in new[] { ("/ws", "clientId=a/b"), ("/ws", "other=1"), ("/ws/x", ""), ("/manager/ws", ""), ("/api/ws", "") })
            Assert.Null(RuntimeRoutes.Ws(Kind, path, query, out _));
    }

    [Fact]
    public void An_unknown_kind_has_no_routes()
    {
        Assert.Null(RuntimeRoutes.Http("tts", "GET", "/health", "", out var refusal));
        Assert.Contains("tts", refusal);
        Assert.Null(RuntimeRoutes.Ws("tts", "/ws", "", out _));
    }

    [Fact]
    public void Credentials_forwarding_and_hop_headers_never_reach_the_runtime()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"), new("x-trace", "1"), new("Authorization", "Bearer secret"),
            new("cookie", "a=b"), new("Host", "evil.example"), new("connection", "keep-alive"), new("transfer-encoding", "chunked"),
            new("X-Forwarded-For", "10.0.0.1"), new("x-forwarded-host", "evil"), new("CF-Connecting-IP", "1.2.3.4"),
            new("forwarded", "for=1"), new("x-real-ip", "1"), new("proxy-authorization", "x"), new("sec-websocket-key", "k"),
            new("content-length", "5"), new("expect", "100-continue"), new("upgrade", "websocket"), new("te", "trailers"),
        };
        Assert.Equal(["content-type", "x-trace"], RuntimeRoutes.ForwardedHeaders(headers).Select(h => h.Key));
    }

    [Fact]
    public void An_upload_boundary_is_read_from_a_multipart_content_type_only()
    {
        Assert.Equal("abc", RuntimeRoutes.Boundary([new("Content-Type", "multipart/form-data; boundary=abc")]));
        Assert.Equal("a b", RuntimeRoutes.Boundary([new("content-type", "multipart/form-data; boundary=\"a b\"")]));
        Assert.Null(RuntimeRoutes.Boundary([new("content-type", "application/json")]));
        Assert.Null(RuntimeRoutes.Boundary([new("content-type", "multipart/form-data")]));
        Assert.Null(RuntimeRoutes.Boundary([new("content-type", "multipart/form-data; boundary=" + new string('a', 71))]));
        Assert.Null(RuntimeRoutes.Boundary([]));
    }

    // ---------------------------------------------------------------- body guards

    private const string B = "----leafboundary7";

    private static byte[] Multipart(params (string Headers, string Body)[] parts)
    {
        var text = new StringBuilder();
        foreach (var (headers, body) in parts) text.Append("--").Append(B).Append("\r\n").Append(headers).Append("\r\n\r\n").Append(body).Append("\r\n");
        text.Append("--").Append(B).Append("--\r\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static readonly (string, string) Image = ("Content-Disposition: form-data; name=\"image\"; filename=\"in put.png\"\r\nContent-Type: image/png",
        new string('ÿ', 3) + "PNG-bytes\r\n--not-the-boundary\r\n" + new string('x', 70_000));

    private static async Task<byte[]> ReadAll(Stream guard, int chunk)
    {
        var output = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = await guard.ReadAsync(buffer)) > 0) output.Write(buffer, 0, read);
        return output.ToArray();
    }

    /// <summary>Feeds the body in pieces of <paramref name="piece"/> bytes, as the link would.</summary>
    private sealed class Trickle(byte[] bytes, int piece) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, piece)], ct);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    [InlineData(1 << 20)]
    public async Task A_good_upload_passes_byte_for_byte_whatever_the_pieces(int piece)
    {
        var body = Multipart(Image, ("Content-Disposition: form-data; name=\"overwrite\"", "true"),
            ("Content-Disposition: form-data; name=\"type\"", "input"), ("Content-Disposition: form-data; name=\"subfolder\"", "leaf/in"));
        Assert.Equal(body, await ReadAll(new ImageUploadGuard(new Trickle(body, piece), B), 3000));
    }

    [Fact]
    public async Task An_upload_as_dotnet_writes_it_passes()
    {
        // MultipartFormDataContent: unquoted names and an RFC 6266 filename* beside filename.
        var body = Multipart(("Content-Disposition: form-data; name=image; filename=in%20put.png; filename*=utf-8''in%20put.png\r\nContent-Type: image/png", "PNG"),
            ("Content-Disposition: form-data; name=overwrite\r\nContent-Type: text/plain; charset=utf-8", "true"));
        Assert.Equal(body, await ReadAll(new ImageUploadGuard(new Trickle(body, 100), B), 4096));
    }

    public static TheoryData<string, byte[]> BadUploads => new()
    {
        { "a file name with a separator", Multipart(("Content-Disposition: form-data; name=\"image\"; filename=\"../x.png\"", "x")) },
        { "a file name with a backslash", Multipart(("Content-Disposition: form-data; name=\"image\"; filename=\"a\\\\x.png\"", "x")) },
        { "a hidden file name", Multipart(("Content-Disposition: form-data; name=\"image\"; filename=\".x\"", "x")) },
        { "two images", Multipart(Image, Image) },
        { "no image", Multipart(("Content-Disposition: form-data; name=\"type\"", "input")) },
        { "a file under another name", Multipart(("Content-Disposition: form-data; name=\"mask\"; filename=\"x.png\"", "x")) },
        { "an unknown field", Multipart(Image, ("Content-Disposition: form-data; name=\"path\"", "/etc")) },
        { "type models", Multipart(Image, ("Content-Disposition: form-data; name=\"type\"", "models")) },
        { "a subfolder going up", Multipart(Image, ("Content-Disposition: form-data; name=\"subfolder\"", "../../custom_nodes")) },
        { "overwrite as text", Multipart(Image, ("Content-Disposition: form-data; name=\"overwrite\"", "yes please")) },
        { "a field over 1 KiB", Multipart(Image, ("Content-Disposition: form-data; name=\"subfolder\"", new string('a', 2000))) },
        { "a part that is not form-data", Multipart(("Content-Disposition: attachment; name=\"image\"; filename=\"x.png\"", "x")) },
        { "an unknown disposition parameter", Multipart(("Content-Disposition: form-data; name=\"image\"; filename=\"x.png\"; size=3", "x")) },
        { "two dispositions", Multipart(("Content-Disposition: form-data; name=\"image\"; filename=\"x.png\"\r\nContent-Disposition: form-data; name=\"type\"", "x")) },
        { "no closing boundary", Encoding.UTF8.GetBytes($"--{B}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"x.png\"\r\n\r\nxyz") },
        { "no boundary at all", Encoding.UTF8.GetBytes(new string('z', 4000)) },
        { "an extended name going up", Multipart(("Content-Disposition: form-data; name=image; filename=x.png; filename*=utf-8''..%2Fx.png", "x")) },
        { "an extended name in another charset", Multipart(("Content-Disposition: form-data; name=image; filename*=iso-8859-1''x.png", "x")) },
        { "an extended name that does not decode", Multipart(("Content-Disposition: form-data; name=image; filename*=utf-8''x%2.png", "x")) },
        { "an extended name on another part", Multipart(Image, ("Content-Disposition: form-data; name=type; filename*=utf-8''x.png", "input")) },
        { "headers over 16 KiB", Encoding.UTF8.GetBytes($"--{B}\r\nX-Pad: {new string('p', 17_000)}\r\n\r\n") },
    };

    [Theory]
    [MemberData(nameof(BadUploads))]
    public async Task A_bad_upload_fails_route_not_allowed(string why, byte[] body)
    {
        var error = await Assert.ThrowsAsync<ForwardTargetException>(() => ReadAll(new ImageUploadGuard(new Trickle(body, 512), B), 4096));
        Assert.True(error.Code == ForwardErrorCodes.RouteNotAllowed, why);
    }

    [Fact]
    public async Task A_refused_part_never_reaches_the_runtime()
    {
        // The bytes before the refused part's headers go on; the part itself does not.
        var body = Multipart(Image, ("Content-Disposition: form-data; name=\"path\"", "SECRET-FIELD-VALUE"));
        var released = new MemoryStream();
        var guard = new ImageUploadGuard(new Trickle(body, 64), B);
        var buffer = new byte[64];
        await Assert.ThrowsAsync<ForwardTargetException>(async () =>
        {
            int read;
            while ((read = await guard.ReadAsync(buffer)) > 0) released.Write(buffer, 0, read);
        });
        var text = Encoding.UTF8.GetString(released.ToArray());
        Assert.Contains("PNG-bytes", text);
        Assert.DoesNotContain("name=\"path\"", text);
        Assert.DoesNotContain("SECRET-FIELD-VALUE", text);
    }

    [Theory]
    [InlineData("{\"delete\":[\"3f2a\",\"91b\"]}", true)]
    [InlineData("{\"delete\":[]}", true)]
    [InlineData("{\"clear\":true}", false)]
    [InlineData("{\"delete\":[\"a\"],\"clear\":true}", false)]
    [InlineData("{\"delete\":[1]}", false)]
    [InlineData("{\"delete\":\"a\"}", false)]
    [InlineData("[\"a\"]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public async Task Queue_posts_only_delete(string json, bool allowed)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var guard = new QueueDeleteGuard(new Trickle(bytes, 3), 64 * 1024);
        if (allowed)
            Assert.Equal(bytes, await ReadAll(guard, 16));
        else
            Assert.Equal(ForwardErrorCodes.RouteNotAllowed, (await Assert.ThrowsAsync<ForwardTargetException>(() => ReadAll(guard, 16))).Code);
    }

    [Fact]
    public async Task A_queue_body_over_its_cap_is_refused_before_it_ends()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"delete\":[\"" + new string('a', 200) + "\"]}");
        await Assert.ThrowsAsync<ForwardTargetException>(() => ReadAll(new QueueDeleteGuard(new Trickle(bytes, 50), 100), 16));
    }
}
