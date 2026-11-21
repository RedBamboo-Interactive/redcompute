using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ComfyUI;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero's <see cref="IHostRuntimes"/> (Phase 4 P3, design 2.4): a registry node zero's providers
/// fill, whose start and stop are the providers' own.
/// </summary>
public sealed class LocalHostRuntimesTests
{
    [Fact]
    public async Task A_registered_backend_is_listed_started_and_stopped_through_its_provider()
    {
        var runtimes = new LocalHostRuntimes();
        var status = BackendStatus.Stopped;
        var calls = new List<string>();
        var changes = new List<HostRuntime>();
        runtimes.Changed += changes.Add;
        using var registration = runtimes.Register(new LocalRuntimeRegistration(
            "comfyui@8188", "comfyui", "ComfyUI", "image-gen", 8188, "cuda:0",
            () => status, () => status == BackendStatus.Error ? "boom" : null,
            _ => { calls.Add("start"); status = BackendStatus.Running; return Task.FromResult(true); },
            _ => { calls.Add("stop"); status = BackendStatus.Stopped; return Task.CompletedTask; }));

        var listed = Assert.Single(runtimes.List());
        Assert.Equal(new HostRuntime("comfyui@8188", "comfyui", "ComfyUI", HostRuntimeManagement.Spawn, true,
            BackendStatus.Stopped, null, "cuda:0", listed.Capabilities, listed.Ports, null), listed);
        Assert.Equal(["image-gen"], listed.Capabilities);
        Assert.Equal([8188], listed.Ports);

        Assert.Equal(BackendStatus.Running, (await runtimes.StartAsync("comfyui@8188")).Status);
        Assert.Equal(BackendStatus.Running, runtimes.Status("comfyui@8188")!.Status);
        Assert.Equal(BackendStatus.Stopped, (await runtimes.StopAsync("comfyui@8188", TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(["start", "stop"], calls);
        Assert.Equal([BackendStatus.Stopped, BackendStatus.Running, BackendStatus.Stopped], changes.Select(change => change.Status));
    }

    [Fact]
    public async Task Providers_sharing_a_runtime_are_one_entry_running_when_any_is()
    {
        var runtimes = new LocalHostRuntimes();
        var image = BackendStatus.Stopped;
        var music = BackendStatus.Running;
        var started = new List<string>();
        using var first = runtimes.Register(Comfy("image-gen", () => image, started));
        using var second = runtimes.Register(Comfy("music-gen", () => music, started));

        var entry = Assert.Single(runtimes.List());
        Assert.Equal(["image-gen", "music-gen"], entry.Capabilities);
        Assert.Equal(BackendStatus.Running, entry.Status);
        await runtimes.StartAsync("comfyui@8188");
        Assert.Equal(["image-gen"], started); // the first registration starts it

        second.Dispose();
        Assert.Equal(BackendStatus.Stopped, runtimes.Status("comfyui@8188")!.Status);
        first.Dispose();
        Assert.Empty(runtimes.List());
        Assert.Null(runtimes.Status("comfyui@8188"));
    }

    [Fact]
    public async Task An_unknown_id_is_runtime_unknown()
    {
        var runtimes = new LocalHostRuntimes();
        var start = await Assert.ThrowsAsync<HostRuntimeException>(() => runtimes.StartAsync("nope"));
        Assert.Equal(HostRuntimeException.Unknown, start.Code);
        var stop = await Assert.ThrowsAsync<HostRuntimeException>(() => runtimes.StopAsync("nope"));
        Assert.Equal(HostRuntimeException.Unknown, stop.Code);
    }

    [Fact]
    public async Task ComfyUI_and_LocalWsl_register_on_a_local_host_and_leave_when_disposed()
    {
        var host = new LocalExecutionHost("node_runtimes");
        var comfy = new ComfyUIProvider(new ProviderConfig { Type = "ComfyUI", BackendPort = 18190 }, "image-gen", _ => { }, host: host);
        var music = new ComfyUIProvider(new ProviderConfig { Type = "ComfyUI", BackendPort = 18190 }, "music-gen", _ => { }, host: host);
        var tts = new LocalWslProvider(new ProviderConfig { Type = "TtsLocal", BackendPort = 18191 }, "tts", _ => { }, host);

        var listed = host.Runtimes.List();
        Assert.Equal(["comfyui@18190", "tts@18191"], listed.Select(runtime => runtime.Id));
        Assert.Equal(["image-gen", "music-gen"], listed[0].Capabilities);
        Assert.Equal("cuda:0", listed[0].Accelerator);
        Assert.Equal(("TtsLocal", "tts (TtsLocal)", "tts"), (listed[1].Kind, listed[1].Label, listed[1].Capabilities.Single()));
        Assert.All(listed, runtime => Assert.Equal(BackendStatus.Stopped, runtime.Status));

        await comfy.DisposeAsync();
        await music.DisposeAsync();
        await tts.DisposeAsync();
        Assert.Empty(host.Runtimes.List());
    }

    private static LocalRuntimeRegistration Comfy(string capability, Func<BackendStatus> status, List<string> started) => new(
        "comfyui@8188", "comfyui", "ComfyUI", capability, 8188, "cuda:0", status, () => null,
        _ => { started.Add(capability); return Task.FromResult(true); }, _ => Task.CompletedTask);
}
