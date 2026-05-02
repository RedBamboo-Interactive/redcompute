using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RedComputeConfig Config { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            Config = CreateDefault();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            Config = JsonSerializer.Deserialize<RedComputeConfig>(json, JsonOptions) ?? CreateDefault();
        }
        catch
        {
            Config = CreateDefault();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static RedComputeConfig CreateDefault()
    {
        return new RedComputeConfig
        {
            ApiPort = 18800,
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["tts"] = new()
                {
                    Enabled = true,
                    ActiveProvider = "local-wsl",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["local-wsl"] = new()
                        {
                            Type = "LocalWsl",
                            WslDistro = "Ubuntu-24.04",
                            VenvPath = "~/tts-env",
                            ServerPath = @"T:\Projects\Qwen3-TTS\qwen3-tts-server",
                            BackendPort = 8765,
                            HealthEndpoint = "/health",
                            Model = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
                            StartupTimeoutSeconds = 180
                        }
                    }
                },
                ["music-gen"] = new()
                {
                    Enabled = false,
                    ActiveProvider = "suno",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["suno"] = new()
                        {
                            Type = "Suno",
                            ApiKey = "",
                            Extra = new Dictionary<string, object?>
                            {
                                ["BaseUrl"] = "https://api.sunoapi.org",
                                ["Model"] = "V4_5"
                            }
                        }
                    }
                },
                ["image-gen"] = new()
                {
                    Enabled = false,
                    ActiveProvider = "comfyui",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["comfyui"] = new()
                        {
                            Type = "ComfyUI",
                            ServerPath = @"T:\Projects\ComfyUI",
                            BackendPort = 8188,
                            HealthEndpoint = "/system_stats",
                            StartupTimeoutSeconds = 120,
                            Extra = new Dictionary<string, object?>
                            {
                                ["Host"] = "127.0.0.1",
                                ["WorkflowsDir"] = @"T:\Projects\Roaster\Roaster.GuildBot\axl-workspace\workflows",
                                ["DefaultWorkflow"] = "z_turbo"
                            }
                        }
                    }
                }
            }
        };
    }
}
