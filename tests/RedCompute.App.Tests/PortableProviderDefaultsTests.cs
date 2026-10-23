using RedCompute.App.Services;
using RedCompute.Core.Configuration;
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
        Assert.DoesNotContain("flash-qla-sm120", json, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Upgrade_preserves_explicit_source_developer_configuration()
    {
        var config = new RedComputeConfig
        {
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["image-gen"] = new()
                {
                    ActiveProvider = "comfyui",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["comfyui"] = new()
                        {
                            Type = "ComfyUI",
                            ServerPath = @"C:\src\ComfyUI",
                            VenvPath = @"C:\venvs\comfy",
                        },
                    },
                },
                ["decision"] = new()
                {
                    ActiveProvider = "kev-local-quality",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["kev-local-quality"] = new()
                        {
                            Type = "KevLocalQuality",
                            WslDistro = "Ubuntu-Khrom",
                            Extra = new Dictionary<string, object?>
                            {
                                ["KernelBackend"] = "flash-qla-sm120",
                            },
                        },
                    },
                },
            },
        };

        Assert.False(ConfigManager.RemoveLegacyMachineSpecificSettings(config));
        Assert.Equal("comfyui", config.Capabilities["image-gen"].ActiveProvider);
        Assert.Equal(@"C:\src\ComfyUI",
            config.Capabilities["image-gen"].Providers["comfyui"].ServerPath);
        Assert.Equal("kev-local-quality", config.Capabilities["decision"].ActiveProvider);
        Assert.Equal("Ubuntu-Khrom",
            config.Capabilities["decision"].Providers["kev-local-quality"].WslDistro);
        Assert.Equal("flash-qla-sm120",
            config.Capabilities["decision"].Providers["kev-local-quality"].Extra!["KernelBackend"]);
    }

}
