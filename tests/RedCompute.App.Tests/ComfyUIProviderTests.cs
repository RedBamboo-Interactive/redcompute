using System.Net;
using System.Net.Sockets;
using System.Text;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ComfyUIProviderTests
{
    [Fact]
    public void Music_capability_advertises_audio_output()
    {
        var provider = new ComfyUIProvider(new ProviderConfig { Type = "ComfyUI" }, "music-gen", _ => { });

        Assert.Equal("music-gen", provider.CapabilitySlug);
        Assert.Equal("audio/flac", provider.OutputSchema.ContentType);
        Assert.Equal("audio", provider.OutputSchema.MediaCategory);
        Assert.Equal("/music-gen/jobs/{id}/output", provider.OutputSchema.OutputEndpoint);
        Assert.Equal(".flac", GenericCapabilityEndpoints.ExtensionFor(provider.OutputSchema.ContentType));
        Assert.Equal(".flac", GenericCapabilityEndpoints.ExtensionFor("Audio/FLAC"));
    }

    [Fact]
    public void Image_capability_keeps_image_output_contract()
    {
        var provider = new ComfyUIProvider(new ProviderConfig { Type = "ComfyUI" }, "image-gen", _ => { });

        Assert.Equal("image/png", provider.OutputSchema.ContentType);
        Assert.Equal("image", provider.OutputSchema.MediaCategory);
        Assert.Equal("/image-gen/jobs/{id}/output", provider.OutputSchema.OutputEndpoint);
    }

    [Fact]
    public async Task Secondary_capability_discovers_an_already_running_shared_backend()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var response = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request);
            var payload = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(payload);
        });
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = port,
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "music-gen", _ => { });

        try
        {
            Assert.Equal(BackendStatus.Running, await provider.GetStatusAsync());
            await response;
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }
}
