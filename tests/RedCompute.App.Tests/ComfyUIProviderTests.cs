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
    [Theory]
    [InlineData("/api/assets/mask.png", "http://127.0.0.1:18804/api/assets/mask.png")]
    [InlineData("mask.png", "http://127.0.0.1:18804/api/assets/mask.png")]
    [InlineData("https://example.test/mask.png", "https://example.test/mask.png")]
    public void Asset_references_accept_server_relative_bare_and_absolute_urls(string value, string expected)
    {
        Assert.Equal(expected, ComfyUIProvider.NormalizeRedLeafAssetReference(value));
    }

    [Fact]
    public void Default_config_does_not_assume_a_machine_specific_ComfyUI_checkout()
    {
        var config = ConfigManager.CreateDefault();

        Assert.Null(config.Capabilities["image-gen"].Providers["comfyui"].ServerPath);
        Assert.Null(config.Capabilities["music-gen"].Providers["comfyui"].ServerPath);
        Assert.Null(config.Capabilities["image-gen"].ActiveProvider);
    }

    [Fact]
    public void Portable_install_uses_its_embedded_python_when_no_venv_is_configured()
    {
        var root = Path.Combine(Path.GetTempPath(), "redcompute-comfy-portable-" + Guid.NewGuid());
        var server = Path.Combine(root, "ComfyUI");
        var python = Path.Combine(root, "python_embeded", "python.exe");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "test");
        try
        {
            var resolved = ComfyUIProvider.ResolveNativePythonExecutable(new ProviderConfig
            {
                Type = "ComfyUI",
                ServerPath = server,
            });

            Assert.Equal(python, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [WindowsFact("launching ComfyUI builds Windows python.exe paths; launching is unsupported elsewhere")]
    public void Explicit_venv_wins_over_portable_embedded_python()
    {
        var config = new ProviderConfig
        {
            Type = "ComfyUI",
            ServerPath = @"C:\ComfyUI_windows_portable\ComfyUI",
            VenvPath = @"D:\Environments\comfy",
        };

        Assert.Equal(@"D:\Environments\comfy\Scripts\python.exe",
            ComfyUIProvider.ResolveNativePythonExecutable(config));
    }

    [WindowsFact("launching ComfyUI builds Windows python.exe paths; launching is unsupported elsewhere")]
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

    [Fact]
    public async Task Refused_connection_with_no_listener_has_nothing_to_release()
    {
        var logs = new List<string>();
        var provider = NewProvider(ClosedPort(), logs.Add);

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);

            Assert.True(result.Success, result.Detail);
            Assert.Contains("not listening", result.Detail);
            Assert.Contains(logs, line => line.Contains("Nothing to release"));
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Local_model_is_admitted_while_comfyui_is_not_up_yet()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var provider = NewProvider(ClosedPort(), acceleratorAdmission: coordinator);

        try
        {
            await using var lease = await coordinator.AcquireAsync(
                new AcceleratorWorkload("cuda:0", "ollama", "OpenCode ollama/gemma"));
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [WindowsFact("relies on Windows refusing connections once a listener's accept backlog is full; Linux leaves them pending")]
    public async Task Refused_connection_from_a_listening_backend_still_refuses_release()
    {
        // A full accept backlog is how a wedged server refuses connections on Windows.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var backlog = new List<TcpClient>();
        var provider = NewProvider(port);

        try
        {
            await FillAcceptBacklogAsync(port, backlog);

            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("cache release failed", result.Detail);
        }
        finally
        {
            backlog.ForEach(client => client.Dispose());
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Refused_connection_on_a_wsl_backend_still_refuses_release()
    {
        var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = ClosedPort(),
            WslDistro = "Ubuntu",
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "image-gen", _ => { });

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("cache release failed", result.Detail);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unresponsive_backend_times_out_and_refuses_release()
    {
        // The listener never accepts: TCP connects, then HTTP gets no answer.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var provider = NewProvider(((IPEndPoint)listener.LocalEndpoint).Port, timeout: TimeSpan.FromMilliseconds(200));

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("timed out", result.Detail);
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_reporting_a_timeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var provider = NewProvider(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.ReleaseCachedModelsAsync(cts.Token));
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Queue_http_error_refuses_release_without_calling_free()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var requests = new List<string>();
        var server = ServeStatusAsync(listener, requests, (500, "{}"));
        var provider = NewProvider(((IPEndPoint)listener.LocalEndpoint).Port);

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
            await server;

            Assert.False(result.Success);
            Assert.Contains("queue check failed with HTTP 500", result.Detail);
            Assert.Single(requests);
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Free_http_error_refuses_release()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var requests = new List<string>();
        var server = ServeStatusAsync(listener, requests,
            (200, "{\"queue_running\":[],\"queue_pending\":[]}"), (503, "{}"));
        var provider = NewProvider(((IPEndPoint)listener.LocalEndpoint).Port);

        try
        {
            var result = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
            await server;

            Assert.False(result.Success);
            Assert.Contains("cache release failed with HTTP 503", result.Detail);
            Assert.StartsWith("POST /free ", requests[1]);
        }
        finally
        {
            await provider.DisposeAsync();
            listener.Stop();
        }
    }

    private static ComfyUIProvider NewProvider(
        int port,
        Action<string>? log = null,
        IAcceleratorAdmissionCoordinator? acceleratorAdmission = null,
        TimeSpan? timeout = null)
    {
        return new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = port,
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "image-gen", log ?? (_ => { }), acceleratorAdmission)
        {
            CacheReleaseTimeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task FillAcceptBacklogAsync(int port, List<TcpClient> backlog)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                backlog.Add(client);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                client.Dispose();
                return;
            }
        }

        Assert.Fail("The accept backlog never filled");
    }

    private static Task ServeAsync(TcpListener listener, List<string> requests, params string[] responses)
        => ServeStatusAsync(listener, requests, responses.Select(body => (200, body)).ToArray());

    private static async Task ServeStatusAsync(TcpListener listener, List<string> requests, params (int Status, string Body)[] responses)
    {
        foreach (var (status, responseBody) in responses)
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
                $"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(body);
        }
    }
}
