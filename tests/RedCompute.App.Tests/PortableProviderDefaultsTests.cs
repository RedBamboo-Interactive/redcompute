using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class PortableProviderDefaultsTests
{
    [Fact]
    public void Optional_and_local_capabilities_are_not_active_on_a_fresh_install()
    {
        var config = ConfigManager.CreateDefault();

        foreach (var slug in new[]
                 {
                     "voice-session", "tts", "stt", "diarization", "image-gen", "decision"
                 })
            Assert.Null(config.Capabilities[slug].ActiveProvider);
    }

    [Fact]
    public void Fresh_defaults_contain_no_developer_checkout_paths()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ConfigManager.CreateDefault());

        Assert.DoesNotContain(@"T:\Projects", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upgrade_removes_only_the_known_legacy_machine_paths()
    {
        var config = new RedComputeConfig
        {
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["tts"] = new()
                {
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["local-wsl"] = new()
                        {
                            Type = "TtsLocal",
                            ServerPath = @"T:\Projects\Qwen3-TTS\qwen3-tts-server",
                            VoicesBasePath = @"T:\Projects\Qwen3-TTS",
                        },
                    },
                },
                ["image-gen"] = new()
                {
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["comfyui"] = new()
                        {
                            Type = "ComfyUI",
                            ServerPath = "T:/Projects/ComfyUI/",
                            Extra = new Dictionary<string, object?>
                            {
                                ["WorkflowsDir"] = @"T:\Projects\Roaster\Roaster.GuildBot\axl-workspace\workflows",
                                ["OutputDirectory"] = @"D:\My ComfyUI\Output",
                            },
                        },
                    },
                },
            },
        };

        Assert.True(ConfigManager.RemoveLegacyMachineSpecificSettings(config));
        var tts = config.Capabilities["tts"].Providers["local-wsl"];
        var comfy = config.Capabilities["image-gen"].Providers["comfyui"];
        Assert.Null(tts.ServerPath);
        Assert.Null(tts.VoicesBasePath);
        Assert.Null(comfy.ServerPath);
        Assert.False(comfy.Extra!.ContainsKey("WorkflowsDir"));
        Assert.Equal(@"D:\My ComfyUI\Output", comfy.Extra["OutputDirectory"]);
        Assert.False(ConfigManager.RemoveLegacyMachineSpecificSettings(config));
    }

    [Theory]
    [InlineData("ai-session", true)]
    [InlineData("image-gen", false)]
    [InlineData("decision", false)]
    [InlineData("diarization", false)]
    [InlineData("voice-session", false)]
    public void Only_session_host_infrastructure_starts_at_boot(string slug, bool expected)
        => Assert.Equal(expected, App.ShouldStartAtBoot(slug));

    [Fact]
    public async Task Concurrent_first_jobs_start_one_backend_once()
    {
        var provider = new DeferredProvider();

        var statuses = await Task.WhenAll(
            ProviderStartup.EnsureRunningAsync(provider),
            ProviderStartup.EnsureRunningAsync(provider));

        Assert.All(statuses, status => Assert.Equal(BackendStatus.Running, status));
        Assert.Equal(1, provider.StartCount);
    }

    private sealed class DeferredProvider : IBackendProvider
    {
        private BackendStatus _status = BackendStatus.Stopped;
        public int StartCount { get; private set; }
        public string Name => "deferred";
        public string CapabilitySlug => "test";
        public TimeSpan HealthCheckInterval => TimeSpan.Zero;
        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            StartCount++;
            await Task.Delay(20, ct);
            _status = BackendStatus.Running;
            return true;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            _status = BackendStatus.Stopped;
            return Task.CompletedTask;
        }
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(_status);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
            => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
