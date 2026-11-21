using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.SttLocal;
using RedCompute.Plugin.TtsLocal;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The network seam (Phase 4 P2, design 2.2): <see cref="IHostNetwork.CreateServiceHandler"/> and
/// <see cref="IHostNetwork.ConnectWebSocketAsync"/>. On node zero they return exactly today's objects,
/// pinned here by type, settings and the bytes they put on the wire. On a host whose resolved service
/// address never resolves, the speech plugins and the generic proxy still reach their backend, because
/// they go through the host.
/// </summary>
public sealed class HostNetworkSeamTests
{
    private static IHostNetwork Local => LocalExecutionHost.Shared.Network;

    [Fact]
    public void Node_zeros_handler_is_a_default_HttpClientHandler_like_the_one_HttpClient_builds_itself()
    {
        using var seam = Local.CreateServiceHandler(new Uri("http://127.0.0.1:8188"));
        using var today = new HttpClientHandler();
        Assert.Equal(typeof(HttpClientHandler), seam.GetType());
        var compared = 0;
        foreach (var property in typeof(HttpClientHandler).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            var (expected, actual) = (Read(property, today), Read(property, seam));
            Assert.True(expected == actual, $"{property.Name}: '{actual}' where new HttpClientHandler() has '{expected}'");
            compared++;
        }
        Assert.True(compared >= 20, $"only {compared} properties compared");

        // HttpClient's own handler is a new HttpClientHandler too (the field is private, so this reads it).
        using var client = new HttpClient();
        var inner = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client);
        Assert.Equal(typeof(HttpClientHandler), inner!.GetType());
    }

    private static string Read(PropertyInfo property, object target)
    {
        try
        {
            var value = property.GetValue(target);
            return value switch
            {
                null => "null",
                string or bool or int or long or TimeSpan or Enum => value.ToString()!,
                System.Collections.ICollection collection => $"{value.GetType().Name}[{collection.Count}]",
                _ => value.GetType().FullName!,
            };
        }
        catch (TargetInvocationException ex) when (ex.InnerException is PlatformNotSupportedException or NotSupportedException)
        {
            return "unsupported";
        }
    }

    [Fact]
    public async Task A_client_on_node_zeros_handler_sends_the_bytes_new_HttpClient_sends()
    {
        var url = "/system_stats?probe=1";
        var today = await CaptureHttpAsync(port => new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, url);
        var seam = await CaptureHttpAsync(port => Local.CreateServiceClient($"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5)), url);
        Assert.Equal(Normalize(today), Normalize(seam));
        Assert.StartsWith("GET /system_stats?probe=1 HTTP/1.1\r\n", today);

        var postToday = await CaptureHttpAsync(port => new HttpClient(), "/prompt", """{"prompt":{}}""");
        var postSeam = await CaptureHttpAsync(port => Local.CreateServiceClient($"http://127.0.0.1:{port}", TimeSpan.FromSeconds(100)), "/prompt", """{"prompt":{}}""");
        Assert.Equal(Normalize(postToday), Normalize(postSeam));
        Assert.EndsWith("""{"prompt":{}}""", postToday);
    }

    [Fact]
    public async Task Node_zeros_WebSocket_is_a_ClientWebSocket_that_sends_the_upgrade_a_new_one_sends()
    {
        string today;
        using (var socket = new ClientWebSocket())
            today = await CaptureUpgradeAsync(uri => socket.ConnectAsync(uri, CancellationToken.None), () => socket.State);

        WebSocket? seam = null;
        var request = await CaptureUpgradeAsync(async uri => seam = await Local.ConnectWebSocketAsync(uri), () => seam!.State);
        using (seam)
        {
            Assert.Equal(typeof(ClientWebSocket), seam!.GetType());
            using var fresh = new ClientWebSocket();
            var options = ((ClientWebSocket)seam).Options;
            Assert.Equal(fresh.Options.KeepAliveInterval, options.KeepAliveInterval);
            Assert.Equal(fresh.Options.HttpVersion, options.HttpVersion);
        }
        Assert.Equal(today, request);
        Assert.StartsWith("GET /ws?clientId=abc HTTP/1.1\r\n", today);
    }

    [Fact]
    public async Task A_failed_connect_disposes_the_socket_and_throws_as_ConnectAsync_does()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<WebSocketException>(() => Local.ConnectWebSocketAsync(new Uri($"ws://127.0.0.1:{port}/ws")));
    }

    [Fact]
    public async Task A_base_url_that_is_not_a_uri_gets_a_client_that_reaches_nothing()
    {
        using var client = Local.CreateServiceClient("http://not a host:8188", TimeSpan.FromSeconds(5));
        // As with new HttpClient(): the URL itself is refused before any handler.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://not a host:8188/x"));
        // And a request that does reach the handler goes nowhere.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:1/x"));
    }

    // ---- through a host whose resolved address never resolves ------------------------------------

    [Fact]
    public async Task Stt_reaches_its_backend_only_through_the_host_for_health_custom_routes_and_the_proxy()
    {
        await using var backend = await Backend.StartAsync();
        var host = new SentinelNetworkHost();
        var stt = new SttLocalProvider(new ProviderConfig { Type = "SttLocal", BackendPort = backend.Port }, "stt", _ => { }, host);
        Assert.True(await stt.StartAsync());
        Assert.Equal($"http://{SentinelNetworkHost.SentinelHost}:{backend.Port}", stt.GetProxyTargetUrl());

        var registry = new CapabilityRegistry();
        registry.Register("stt", new CapabilityDefinition { Slug = "stt", DisplayName = "STT", Description = "STT test" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [stt.Name] = stt }, stt.Name);
        var directory = Path.Combine(Path.GetTempPath(), "redcompute-network-seam-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        GenericCapabilityEndpoints.Map(app, new EndpointRegistry(app), registry,
            new JobTrackingService(() => new RedComputeDbContext(database)), (_, _) => { });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        Assert.Equal("""{"models":["base"]}""", await client.GetStringAsync("/stt/models"));
        Assert.Equal("echo:/echo/one?x=1", await client.GetStringAsync("/stt/echo/one?x=1"));

        var sentinel = SentinelNetworkHost.SentinelHost;
        Assert.Equal([$"GET {sentinel} /health", $"GET {sentinel} /models", $"GET {sentinel} /echo/one"], host.Recorder.Sent);
        Assert.Equal(["GET /health", "GET /models", "GET /echo/one"], backend.Requests);
        await app.StopAsync();
        await stt.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task Tts_voice_preparation_goes_through_the_host()
    {
        await using var backend = await Backend.StartAsync();
        var host = new SentinelNetworkHost();
        var tts = new TtsLocalProvider(new ProviderConfig { Type = "TtsLocal", BackendPort = backend.Port }, "tts", _ => { }, host);
        Assert.True(await tts.StartAsync());

        // A voice already loaded needs no reload: one /model/info through the host's handler.
        Assert.Null(await tts.PrepareAsync(new Dictionary<string, object?> { ["speaker"] = "Serena" }, tts.GetProxyTargetUrl()!, CancellationToken.None));
        Assert.Contains($"GET {SentinelNetworkHost.SentinelHost} /model/info", host.Recorder.Sent);
        Assert.Contains("GET /model/info", backend.Requests);
        Assert.Same(tts.ProxyClient, ((IServiceProxyClient)tts).ProxyClient);
        await tts.DisposeAsync();
    }

    /// <summary>A speech backend: health, the STT and TTS info routes, and an echo for the catch-all.</summary>
    private sealed class Backend : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public List<string> Requests { get; } = [];
        public int Port { get; private set; }

        private Backend(WebApplication app) => _app = app;

        public static async Task<Backend> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var backend = new Backend(app);
            app.Use(async (context, next) =>
            {
                lock (backend.Requests) backend.Requests.Add($"{context.Request.Method} {context.Request.Path}");
                await next();
            });
            app.MapGet("/health", () => Results.Ok());
            app.MapGet("/models", () => Results.Text("""{"models":["base"]}""", "application/json"));
            app.MapGet("/model/info", () => Results.Text("""{"speakers":["Serena"]}""", "application/json"));
            app.MapGet("/echo/{**rest}", (HttpContext context) =>
                Results.Text($"echo:{context.Request.Path}{context.Request.QueryString}"));
            await app.StartAsync();
            backend.Port = new Uri(app.Urls.Single()).Port;
            return backend;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ---- raw capture ------------------------------------------------------------------------------

    private static string Normalize(string request) => Regex.Replace(request, @"127\.0\.0\.1:\d+", "127.0.0.1:<port>");

    /// <summary>The bytes a client sends for one request, read by a bare TCP listener.</summary>
    private static async Task<string> CaptureHttpAsync(Func<int, HttpClient> create, string path, string? json = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = create(port);
        var url = $"http://127.0.0.1:{port}{path}";
        var send = json is null ? client.GetAsync(url)
            : client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        using var connection = await listener.AcceptTcpClientAsync();
        var stream = connection.GetStream();
        var request = await ReadRequestAsync(stream, json is null ? 0 : Encoding.UTF8.GetByteCount(json));
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        using (await send) { }
        return request;
    }

    /// <summary>The upgrade request a socket sends, its key masked, answered with a real 101.</summary>
    private static async Task<string> CaptureUpgradeAsync(Func<Uri, Task> connect, Func<WebSocketState> state)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connecting = connect(new Uri($"ws://127.0.0.1:{port}/ws?clientId=abc"));
        using var connection = await listener.AcceptTcpClientAsync();
        var stream = connection.GetStream();
        var request = await ReadRequestAsync(stream, 0);
        var key = Regex.Match(request, "Sec-WebSocket-Key: (\\S+)\r\n").Groups[1].Value;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        await connecting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketState.Open, state());
        return Normalize(request.Replace(key, "<key>"));
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, int bodyBytes)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var text = Encoding.ASCII.GetString(buffer.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0 && buffer.Count >= end + 4 + bodyBytes) return Encoding.UTF8.GetString(buffer.ToArray());
            var read = await stream.ReadAsync(chunk, timeout.Token);
            if (read == 0) return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }
    }
}
