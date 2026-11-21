using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedCompute.Node.Tests;

/// <summary>
/// A stand-in ComfyUI on 127.0.0.1 (Kestrel): records every request it gets (method, path and query as received,
/// headers, body size and SHA-256), answers <c>/system_stats</c>, streams <c>/view</c>, and echoes WebSocket messages.
/// </summary>
internal sealed class FakeComfy : IAsyncDisposable
{
    public sealed record Seen(string Method, string Target, IReadOnlyDictionary<string, string> Headers, long BodyBytes, string BodySha256);

    private readonly WebApplication _app;
    public ConcurrentQueue<Seen> Requests { get; } = new();
    public int Port { get; private set; }

    /// <summary>How long <c>/view?filename=slow.bin</c> waits between its 64 KiB chunks.</summary>
    public TimeSpan SlowChunkDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    private FakeComfy(WebApplication app) => _app = app;

    public static async Task<FakeComfy> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new FakeComfy(app);
        app.UseWebSockets();
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Port = new Uri(address).Port;
        return fake;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        var target = request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        if (ctx.WebSockets.IsWebSocketRequest)
        {
            Requests.Enqueue(new Seen("WS", target, headers, 0, ""));
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                await socket.SendAsync(message.ToArray(), result.MessageType, true, CancellationToken.None);
            }
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            sha.AppendData(chunk, 0, read);
            bytes += read;
        }
        Requests.Enqueue(new Seen(request.Method, target, headers, bytes, Convert.ToHexStringLower(sha.GetHashAndReset())));

        switch (request.Path.Value)
        {
            case "/system_stats":
                await ctx.Response.WriteAsJsonAsync(new { system = new { os = "nt", comfyui_version = "0.3.40" }, devices = Array.Empty<object>() });
                return;
            case "/view" when request.Query["filename"] == "slow.bin":
                ctx.Response.ContentType = "application/octet-stream";
                for (var i = 0; i < 1024 && !ctx.RequestAborted.IsCancellationRequested; i++)
                {
                    await ctx.Response.Body.WriteAsync(new byte[64 * 1024], ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(SlowChunkDelay, ctx.RequestAborted);
                }
                return;
            case "/view":
                ctx.Response.ContentType = "image/png";
                ctx.Response.Headers["Set-Cookie"] = "session=runtime";
                await ctx.Response.Body.WriteAsync(System.Text.Encoding.ASCII.GetBytes("PNG:" + request.Query["filename"]));
                return;
            case "/prompt":
                // As ComfyUI answers: the id it assigned (the node records it as one the server submitted).
                await ctx.Response.WriteAsJsonAsync(new { prompt_id = $"p-{Guid.NewGuid():N}", number = 1, node_errors = new { }, bytes });
                return;
            default:
                await ctx.Response.WriteAsJsonAsync(new { bytes });
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
