using System.Net;
using System.Net.Sockets;
using System.Text;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ComfyUIProviderTests
{
    [Fact]
    public void Default_config_does_not_assume_a_machine_specific_ComfyUI_checkout()
    {
        var config = ConfigManager.CreateDefault();

        Assert.Null(config.Capabilities["image-gen"].Providers["comfyui"].ServerPath);
        Assert.Null(config.Capabilities["music-gen"].Providers["comfyui"].ServerPath);
    }

    [Fact]
    public void Native_start_arguments_support_external_storage_and_paths_with_spaces()
    {
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            ServerPath = @"C:\Program Files\Comfy UI",
            VenvPath = @"C:\Python Environments\Comfy UI",
            BackendPort = 8188,
            Extra = new Dictionary<string, object?>
            {
                ["ModelsDirectory"] = @"F:\AI Models\ComfyUI",
                ["OutputDirectory"] = @"T:\AI Data\Output",
                ["InputDirectory"] = @"T:\AI Data\Input",
                ["TempDirectory"] = @"T:\AI Data\Temp",
                ["UserDirectory"] = @"T:\AI Data\User",
                ["ServerArgs"] = "--preview-method auto",
            },
        }, "image-gen", _ => { });

        var startInfo = provider.BuildStartInfo();

        Assert.Equal(@"C:\Python Environments\Comfy UI\Scripts\python.exe", startInfo.FileName);
        Assert.Equal(@"C:\Program Files\Comfy UI", startInfo.WorkingDirectory);
        Assert.Contains("--models-directory \"F:\\AI Models\\ComfyUI\"", startInfo.Arguments);
        Assert.Contains("--output-directory \"T:\\AI Data\\Output\"", startInfo.Arguments);
        Assert.Contains("--input-directory \"T:\\AI Data\\Input\"", startInfo.Arguments);
        Assert.Contains("--temp-directory \"T:\\AI Data\\Temp\"", startInfo.Arguments);
        Assert.Contains("--user-directory \"T:\\AI Data\\User\"", startInfo.Arguments);
        Assert.Contains("--database-url \"sqlite:///T:/AI Data/User/comfyui.db\"", startInfo.Arguments);
        Assert.Contains("--preview-method auto", startInfo.Arguments);
    }

    [Fact]
    public void Explicit_database_url_overrides_the_user_directory_default()
    {
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            ServerPath = @"C:\ComfyUI",
            Extra = new Dictionary<string, object?>
            {
                ["UserDirectory"] = @"T:\AI\User",
                ["DatabaseUrl"] = "sqlite:///F:/Databases/comfy.db",
            },
        }, "image-gen", _ => { });

        var startInfo = provider.BuildStartInfo();

        Assert.Contains("--database-url sqlite:///F:/Databases/comfy.db", startInfo.Arguments);
        Assert.DoesNotContain("T:/AI/User/comfyui.db", startInfo.Arguments);
    }

    [Fact]
    public void Wsl_start_arguments_translate_windows_storage_paths()
    {
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            ServerPath = @"C:\Program Files\Comfy UI",
            VenvPath = "~/comfy env",
            WslDistro = "Ubuntu-24.04",
            Extra = new Dictionary<string, object?>
            {
                ["ModelsDirectory"] = @"F:\AI Models\ComfyUI",
                ["UserDirectory"] = @"T:\AI Data\User",
            },
        }, "image-gen", _ => { });

        var startInfo = provider.BuildStartInfo();
        var command = startInfo.ArgumentList[4];

        Assert.Equal("wsl.exe", startInfo.FileName);
        Assert.Equal(new[] { "-d", "Ubuntu-24.04", "bash", "-lc" }, startInfo.ArgumentList.Take(4));
        Assert.Contains("cd '/mnt/c/Program Files/Comfy UI'", command);
        Assert.Contains("source \"$HOME\"/'comfy env/bin/activate'", command);
        Assert.Contains("--models-directory '/mnt/f/AI Models/ComfyUI'", command);
        Assert.Contains("--user-directory '/mnt/t/AI Data/User'", command);
        Assert.Contains("--database-url 'sqlite:////mnt/t/AI Data/User/comfyui.db'", command);
    }

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

    [Fact]
    public async Task Idle_backend_releases_models_and_memory_for_accelerator_transition()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<string>();
        var server = ServeAsync(listener, requests,
            "{\"queue_running\":[],\"queue_pending\":[]}", "{}");
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = port,
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "image-gen", _ => { });

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
            await server;

            Assert.True(result.Success, result.Detail);
            Assert.StartsWith("GET /queue ", requests[0]);
            Assert.StartsWith("POST /free ", requests[1]);
            Assert.Contains("\"unload_models\":true", requests[1]);
            Assert.Contains("\"free_memory\":true", requests[1]);
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Busy_backend_refuses_release_without_calling_free()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<string>();
        var server = ServeAsync(listener, requests,
            "{\"queue_running\":[[\"prompt\",0]],\"queue_pending\":[]}");
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = port,
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "image-gen", _ => { });

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
            await server;

            Assert.False(result.Success);
            Assert.Contains("busy", result.Detail);
            Assert.Single(requests);
            Assert.StartsWith("GET /queue ", requests[0]);
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    private static async Task ServeAsync(TcpListener listener, List<string> requests, params string[] responses)
    {
        foreach (var responseBody in responses)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var received = 0;
            var headerEnd = -1;
            var contentLength = 0;
            do
            {
                var count = await stream.ReadAsync(buffer.AsMemory(received));
                if (count == 0) break;
                received += count;
                var text = Encoding.ASCII.GetString(buffer, 0, received);
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    foreach (var line in text[..headerEnd].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                }
            } while (headerEnd < 0 || received < headerEnd + 4 + contentLength);

            requests.Add(Encoding.ASCII.GetString(buffer, 0, received));
            var body = Encoding.UTF8.GetBytes(responseBody);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(body);
        }
    }
}
