using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using RedCompute.Plugin.Kev;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class LocalBackendPlatformTests
{
    private const string Reason = "local GPU backends launch through wsl.exe/cmd.exe on Windows";

    [NonWindowsFact(Reason)]
    public async Task A_wsl_backend_fails_fast_with_a_typed_unsupported_status()
    {
        var logs = new List<string>();
        await using var provider = new LocalWslProvider(new ProviderConfig
        {
            Type = "LocalWsl",
            WslDistro = "Ubuntu",
            BackendPort = UnusedPort(),
            StartupTimeoutSeconds = 600,
        }, "tts", logs.Add);

        var started = await provider.StartAsync();

        Assert.False(started);
        Assert.Equal(BackendStatus.Unsupported, await provider.GetStatusAsync());
        Assert.Contains($"not supported on {PlatformSupport.PlatformName} yet", provider.StatusDetail);
        Assert.Null(provider.ProcessId);
        Assert.Null(provider.GetProxyTargetUrl());
        Assert.Contains(logs, line => line.Contains("not supported", StringComparison.Ordinal));
    }

    [NonWindowsFact(Reason)]
    public async Task Comfyui_refuses_to_launch_but_reports_why()
    {
        await using var provider = new ComfyUIProvider(new ProviderConfig
        {
            Type = "ComfyUI",
            ServerPath = "/opt/ComfyUI",
            BackendPort = UnusedPort(),
        }, "image-gen", _ => { });

        Assert.False(await provider.StartAsync());

        Assert.Equal(BackendStatus.Unsupported, await provider.GetStatusAsync());
        Assert.Contains("Launching a local ComfyUI is not supported", provider.StatusDetail);
    }

    [NonWindowsFact(Reason)]
    public async Task Kev_refuses_to_launch_with_the_platform_error_code()
    {
        await using var provider = new KevLocalQualityProvider(new ProviderConfig
        {
            Type = "KevLocalQuality",
            Endpoint = $"http://127.0.0.1:{UnusedPort()}",
            Model = "kev-latest",
            ModelRevision = "2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c",
            CalibrationRevision = "calibration-test",
            LaunchCommand = "exec python -m kev.serve",
            WslDistro = "Ubuntu",
            Extra = new Dictionary<string, object?> { ["BaseModelRevision"] = "68c46c4b3498877f3ef123c856ecfde50c39f404" },
        }, "decision", _ => { });

        Assert.False(await provider.StartAsync());

        Assert.Equal(BackendStatus.Unsupported, await provider.GetStatusAsync());
        Assert.Contains("Launching Kev is not supported", provider.StatusDetail);
    }

    [Fact]
    public async Task An_unsupported_provider_answers_503_with_the_platform_code_and_reason()
    {
        var provider = new StubProvider("Not here, for a reason.");

        var (status, body) = await Execute(GenericCapabilityEndpoints.NotRunning(provider, BackendStatus.Unsupported, "unused"));

        Assert.Equal(503, status);
        Assert.Equal(PlatformSupport.UnsupportedCode, body.GetProperty("error").GetString());
        Assert.Equal("Not here, for a reason.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Any_other_stopped_provider_keeps_the_not_running_answer()
    {
        var (status, body) = await Execute(GenericCapabilityEndpoints.NotRunning(
            new StubProvider(null), BackendStatus.Stopped, "Backend for 'tts' is Stopped."));

        Assert.Equal(503, status);
        Assert.Equal("provider_not_running", body.GetProperty("error").GetString());
        Assert.Equal("Backend for 'tts' is Stopped.", body.GetProperty("message").GetString());
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<(int Status, JsonElement Body)> Execute(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, JsonDocument.Parse(context.Response.Body).RootElement.Clone());
    }

    private sealed class StubProvider(string? detail) : IBackendProvider
    {
        public string Name => "stub";
        public string CapabilitySlug => "tts";
        public string? StatusDetail => detail;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(5);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Unsupported);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
