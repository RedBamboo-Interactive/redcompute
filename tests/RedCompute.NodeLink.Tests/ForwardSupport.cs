using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// A deterministic fake runtime behind the node's <see cref="ForwardHost"/> (Phase 4 design 6.2):
/// <list type="bullet">
/// <item><c>GET /blob/{seed}/{size}</c> streams seeded bytes;</item>
/// <item><c>POST /echo-hash</c> answers <c>{bytes, sha256}</c> of the body it read;</item>
/// <item><c>/ws?seed=S&amp;count=N</c> emits N numbered binary messages and records what it receives;</item>
/// <item><c>/head/{n}</c> answers a header of n bytes; <c>/fail</c> throws; <c>/hang</c> never answers.</item>
/// </list>
/// Runtime <c>rt-1</c> runs, <c>rt-off</c> is registered but stopped, anything else is unknown; <c>/manager/*</c> is
/// not on the allowlist; <c>/small</c> takes 1 KiB of body.
/// </summary>
internal sealed class FakeRuntime : IForwardTarget
{
    public const string Runtime = "rt-1";
    public int ExchangesStarted;
    public int MaxConcurrent;
    private int _concurrent;
    public ConcurrentDictionary<string, List<byte[]>> WsReceived { get; } = new();
    public ConcurrentQueue<ForwardOpen> Opens { get; } = new();
    public TimeSpan BlobChunkDelay { get; init; }

    public ForwardDecision Decide(ForwardOpen open)
    {
        Opens.Enqueue(open);
        if (open.Runtime == "rt-off") return ForwardDecision.Refuse(ForwardErrorCodes.RuntimeNotRunning, "rt-off is stopped.");
        if (open.Runtime != Runtime) return ForwardDecision.Refuse(ForwardErrorCodes.RuntimeUnknown, $"No runtime {open.Runtime}.");
        if (open.Path.StartsWith("/manager/", StringComparison.Ordinal)) return ForwardDecision.Refuse(ForwardErrorCodes.RouteNotAllowed, "Not on the allowlist.");
        return ForwardDecision.Accept(open.Path == "/small" ? 1024 : 256L * 1024 * 1024);
    }

    public async Task<ForwardUpstreamResponse> ExchangeAsync(ForwardOpen open, ForwardDecision decision, Stream requestBody, CancellationToken ct)
    {
        Interlocked.Increment(ref ExchangesStarted);
        var now = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref MaxConcurrent, now);
        try
        {
            var parts = open.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                case ["blob", var seed, var size] when open.Method == "GET":
                    return new ForwardUpstreamResponse(200, [new("content-type", "application/octet-stream"), new("x-seed", seed)],
                        new SeededStream(int.Parse(seed), long.Parse(size), BlobChunkDelay), new Releaser(() => Interlocked.Decrement(ref _concurrent)));
                case ["echo-hash"] or ["small"] when open.Method == "POST":
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[32 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await requestBody.ReadAsync(buffer, ct)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        total += read;
                    }
                    var answer = JsonSerializer.SerializeToUtf8Bytes(new { bytes = total, sha256 = Convert.ToHexStringLower(sha.GetHashAndReset()) });
                    Interlocked.Decrement(ref _concurrent);
                    return new ForwardUpstreamResponse(200, [new("content-type", "application/json")], new MemoryStream(answer));
                }
                case ["head", var n]:
                    Interlocked.Decrement(ref _concurrent);
                    return new ForwardUpstreamResponse(200, [new("x-big", new string('a', int.Parse(n)))], new MemoryStream());
                case ["fail"]:
                    Interlocked.Decrement(ref _concurrent);
                    throw new IOException("the runtime closed the connection");
                case ["hang"]:
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException();
                default:
                    Interlocked.Decrement(ref _concurrent);
                    return new ForwardUpstreamResponse(404, [], new MemoryStream("not found"u8.ToArray()));
            }
        }
        catch
        {
            Interlocked.Decrement(ref _concurrent);
            throw;
        }
    }

    public async Task<WebSocket> ConnectAsync(ForwardOpen open, ForwardDecision decision, CancellationToken ct)
    {
        if (open.Path != "/ws") throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, "No WebSocket there.");
        var query = open.Query.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        var seed = int.Parse(query["seed"]);
        var count = int.Parse(query["count"]);
        var (client, server) = await SocketPair.CreateAsync(ct);
        var received = WsReceived.GetOrAdd(query["seed"], _ => []);
        _ = Task.Run(() => RunWsAsync(server, seed, count, received));
        return client;
    }

    /// <summary>The runtime's end: emits its messages while it records what it gets, closes when the server closes.</summary>
    private static async Task RunWsAsync(WebSocket socket, int seed, int count, List<byte[]> received)
    {
        try
        {
            var emit = Task.Run(async () =>
            {
                for (var i = 0; i < count; i++)
                    await socket.SendAsync(WsMessage(seed, 7, i), WebSocketMessageType.Binary, true, CancellationToken.None);
            });
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await emit;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                lock (received) received.Add(message.ToArray());
                message.SetLength(0);
            }
        }
        catch
        {
            socket.Abort();
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>WS message i of a stream: [u32 index][seeded bytes], 1 B .. 64 KiB, a few up to 512 KiB.</summary>
    public static byte[] WsMessage(int seed, int stream, int index)
    {
        var random = new Random(HashCode.Combine(seed, stream, index, 99));
        var size = random.NextDouble() < 0.1 ? random.Next(64 * 1024, 512 * 1024) : random.Next(4, 4096);
        var bytes = new byte[size];
        random.NextBytes(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        return bytes;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}

/// <summary>Seeded pseudo-random bytes, produced as they are read (a 64 MiB body never sits in memory).</summary>
internal sealed class SeededStream(int seed, long length, TimeSpan chunkDelay = default) : Stream
{
    private readonly Random _random = new(seed);
    private readonly byte[] _block = new byte[16 * 1024];
    private int _blockAt = 16 * 1024;
    private long _position;

    public static byte[] Expected(int seed, long length)
    {
        using var stream = new SeededStream(seed, length);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static string ExpectedSha(int seed, long length)
    {
        using var stream = new SeededStream(seed, length);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Whole 16 KiB blocks drawn in order, so any read size gives the same bytes.
        if (_blockAt == _block.Length)
        {
            _random.NextBytes(_block);
            _blockAt = 0;
        }
        var take = (int)Math.Min(Math.Min(count, _block.Length - _blockAt), length - _position);
        if (take <= 0) return 0;
        _block.AsSpan(_blockAt, take).CopyTo(buffer.AsSpan(offset));
        _blockAt += take;
        _position += take;
        return take;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (chunkDelay > TimeSpan.Zero) await Task.Delay(chunkDelay, ct);
        var array = new byte[Math.Min(buffer.Length, 16 * 1024)];
        var read = Read(array, 0, array.Length);
        array.AsSpan(0, read).CopyTo(buffer.Span);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Two WebSockets joined over a loopback TCP connection (a real WS framing, no HTTP handshake).</summary>
internal static class SocketPair
{
    public static async Task<(WebSocket Client, WebSocket Server)> CreateAsync(CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(ct).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var server = await accept;
        return (WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, TimeSpan.FromSeconds(30)),
            WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, TimeSpan.FromSeconds(30)));
    }
}

/// <summary>The node's link handler with a forward host: forwards go to it, the rest is the test handler's.</summary>
internal sealed class ForwardNodeHandler(ForwardHost host, Func<ChannelOpenInfo, ChannelAcceptance?>? other = null) : LinkHandler
{
    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
        => ForwardHost.IsForwardClass(info.Class) ? host.Accept(session, info) : other?.Invoke(info);
}

internal static class ForwardFeatures
{
    public static IReadOnlyList<string> Node { get; } =
        [LinkFeatures.DescribeV1, LinkFeatures.DiagEcho, LinkFeatures.ForwardHttpV1, LinkFeatures.ForwardWsV1];

    public static IReadOnlyList<string> Server { get; } =
        [LinkFeatures.DescribeV1, LinkFeatures.DiagEcho, LinkFeatures.ForwardHttpV1, LinkFeatures.ForwardWsV1];

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
