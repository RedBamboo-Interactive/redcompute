using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RedCompute.App.Services.Nodes;
using RedCompute.Plugin.ComfyUI;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// A remote job's input assets are fetched on the server, never through the node (review P4-P7, item 2; design P8).
/// The real ComfyUIProvider runs over NodeHub, the link and the node's real RuntimeForwardTarget against a fake ComfyUI,
/// with a fake kernel on the server side serving the asset. The node's ComfyUI gets the bytes through
/// <c>/upload/image</c> under a fresh name; the asset's id and URL appear in no request that reached it, and not in the
/// node's audit log or its log.
/// </summary>
public sealed class ComfyRemoteAssetTests : IAsyncLifetime
{
    private const string AssetId = "asset-7f3c9e2b41d8";
    private const string Secret = "tok-5ac81e09";
    private static readonly byte[] AssetBytes = Encoding.ASCII.GetBytes("\x89PNG the input image for img2img");

    private ComfyNodeKit _kit = null!;
    private FakeKernelAssets _kernel = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync()
    {
        _kit = await ComfyNodeKit.StartAsync();
        _kernel = await FakeKernelAssets.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _kit.DisposeAsync();
        await _kernel.DisposeAsync();
    }

    private ComfyUIProvider Provider(bool serverNetwork = true, long maxAssetBytes = 256L << 20) => new(
        RemoteProviderConfig.For("ComfyUI", null, _kit.Runtime), "image-gen", line => { lock (_log) _log.Add(line); },
        null, _kit.Host, serverNetwork ? new ServerNetwork(LocalExecutionHost.Shared.Network) : null)
    {
        RedLeafBaseUrl = _kernel.BaseUrl,
        MaxAssetBytes = maxAssetBytes,
    };

    public static TheoryData<string> References => new() { "id", "url", "string", "path" };

    private object Reference(string form) => form switch
    {
        "id" => new { assetId = AssetId },
        "url" => new { assetUrl = $"{_kernel.BaseUrl}/api/assets/{AssetId}/content?token={Secret}" },
        "path" => new { assetUrl = $"/api/assets/{AssetId}" },
        _ => AssetId,
    };

    [Theory]
    [MemberData(nameof(References))]
    public async Task Img2img_fetches_the_asset_on_the_server_and_the_node_sees_only_its_bytes(string form)
    {
        await using var provider = Provider();
        var result = await provider.ExecuteAsync(ComfyNodeKit.Request(Reference(form))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result!.Success, result.ErrorMessage);
        // Fetched here, on the server, once.
        var fetch = Assert.Single(_kernel.Requests);
        Assert.StartsWith($"/api/assets/{AssetId}", fetch);

        // The node's ComfyUI got the bytes through /upload/image, and the prompt loads that upload.
        var upload = Assert.Single(_kit.Comfy.Requests, r => r.Target == "/upload/image");
        Assert.Equal("POST", upload.Method);
        Assert.Contains(Encoding.Latin1.GetString(AssetBytes), upload.Text);
        var prompt = Assert.Single(_kit.Comfy.Prompts);
        var loaded = (string?)prompt["prompt"]!["10"]!["inputs"]!["image"];
        Assert.Matches("^rl_[0-9a-f]{32}\\.png$", loaded);
        Assert.Matches($"filename=\"?{System.Text.RegularExpressions.Regex.Escape(loaded!)}", upload.Text);

        AssertNodeNeverSawTheReference();
    }

    [Fact]
    public async Task An_asset_the_server_cannot_fetch_fails_the_job_and_nothing_reaches_the_node()
    {
        _kernel.Missing = true;
        await using var provider = Provider();

        var result = await provider.ExecuteAsync(ComfyNodeKit.Request(Reference("id"))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result!.Success);
        Assert.Contains("input asset for 'image' could not be fetched on the server", result.ErrorMessage);
        Assert.Empty(_kit.Comfy.Prompts);
        Assert.DoesNotContain(_kit.Comfy.Requests, r => r.Target == "/upload/image");
        AssertNodeNeverSawTheReference();
    }

    [Fact]
    public async Task A_remote_provider_without_a_server_network_refuses_the_asset_rather_than_send_it_to_the_node()
    {
        await using var provider = Provider(serverNetwork: false);

        var result = await provider.ExecuteAsync(ComfyNodeKit.Request(Reference("url"))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result!.Success);
        Assert.Empty(_kernel.Requests);
        Assert.Empty(_kit.Comfy.Prompts);
        AssertNodeNeverSawTheReference();
    }

    [Fact]
    public async Task An_asset_over_the_server_cap_fails_the_job()
    {
        _kernel.Body = new byte[64 * 1024];
        await using var provider = Provider(maxAssetBytes: 16 * 1024);

        var result = await provider.ExecuteAsync(ComfyNodeKit.Request(Reference("id"))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result!.Success);
        Assert.Empty(_kit.Comfy.Prompts);
        Assert.DoesNotContain(_kit.Comfy.Requests, r => r.Target == "/upload/image");
        lock (_log) Assert.Contains(_log, line => line.Contains("16 KiB cap"));
    }

    [Theory]
    [InlineData("image/png", "http://k/a/x.jpg", ".png")]
    [InlineData("application/octet-stream", "http://k/a/x.JPG", ".jpg")]
    [InlineData("application/octet-stream", "http://k/a/x.tar%2F..%2Fevil", ".bin")]
    [InlineData("application/octet-stream", "http://k/a/x.verylongext", ".bin")]
    [InlineData("application/octet-stream", "http://k/a/x", ".bin")]
    public void The_upload_extension_is_from_the_media_type_or_a_plain_url_one(string contentType, string url, string expected)
        => Assert.Equal(expected, ComfyUIProvider.AssetExtension(contentType, url));

    private void AssertNodeNeverSawTheReference()
    {
        var kernelPort = new Uri(_kernel.BaseUrl).Port.ToString();
        foreach (var seen in _kit.Comfy.Requests)
        {
            Assert.DoesNotContain(AssetId, seen.Text);
            Assert.DoesNotContain(Secret, seen.Text);
            Assert.DoesNotContain("/api/assets", seen.Text);
            Assert.DoesNotContain("127.0.0.1:" + kernelPort, seen.Text);
        }
        foreach (var text in new[] { _kit.Node.AuditText(), _kit.Node.LogText() })
        {
            Assert.DoesNotContain(AssetId, text);
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("/api/assets", text);
        }
    }

    /// <summary>The kernel's asset store, on the server's loopback: <c>/api/assets/{id}[/content]</c>.</summary>
    private sealed class FakeKernelAssets : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public ConcurrentQueue<string> Requests { get; } = new();
        public string BaseUrl { get; private set; } = "";
        public bool Missing { get; set; }
        public byte[] Body { get; set; } = AssetBytes;

        private FakeKernelAssets(WebApplication app) => _app = app;

        public static async Task<FakeKernelAssets> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var fake = new FakeKernelAssets(app);
            app.Run(async ctx =>
            {
                fake.Requests.Enqueue(ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString.ToUriComponent());
                if (fake.Missing || !ctx.Request.Path.StartsWithSegments($"/api/assets/{AssetId}"))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                ctx.Response.ContentType = "image/png";
                await ctx.Response.Body.WriteAsync(fake.Body);
            });
            await app.StartAsync();
            fake.BaseUrl = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
