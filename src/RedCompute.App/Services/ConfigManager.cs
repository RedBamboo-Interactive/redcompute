using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            json = MigrateClaudeConfig(json);
            Config = JsonSerializer.Deserialize<RedComputeConfig>(json, JsonOptions) ?? CreateDefault();
        }
        catch
        {
            Config = CreateDefault();
        }

        MergeNewCapabilities();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private string MigrateClaudeConfig(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root || !root.ContainsKey("Claude"))
                return json;

            var claude = root["Claude"]?.AsObject();
            if (claude == null) { root.Remove("Claude"); return root.ToJsonString(JsonOptions); }

            var caps = root["Capabilities"]?.AsObject();
            if (caps == null) { caps = new JsonObject(); root["Capabilities"] = caps; }

            var aiSession = caps["ai-session"]?.AsObject();
            if (aiSession == null) { aiSession = new JsonObject { ["ActiveProvider"] = "claude-code" }; caps["ai-session"] = aiSession; }

            var providers = aiSession["Providers"]?.AsObject();
            if (providers == null) { providers = new JsonObject(); aiSession["Providers"] = providers; }

            var provider = providers["claude-code"]?.AsObject();
            if (provider == null) { provider = new JsonObject { ["Type"] = "ClaudeCode" }; providers["claude-code"] = provider; }

            foreach (var prop in claude)
            {
                if (!provider.ContainsKey(prop.Key) && prop.Value != null)
                    provider[prop.Key] = prop.Value.DeepClone();
            }

            root.Remove("Claude");
            var migrated = root.ToJsonString(JsonOptions);
            File.WriteAllText(ConfigPath, migrated);
            return migrated;
        }
        catch
        {
            return json;
        }
    }

    private static readonly HashSet<string> RemovedCapabilities = ["ai-prompt"];

    private void MergeNewCapabilities()
    {
        var defaults = CreateDefault();
        var dirty = false;
        foreach (var (slug, defaultCap) in defaults.Capabilities)
        {
            if (!Config.Capabilities.TryGetValue(slug, out var existingCap))
            {
                Config.Capabilities[slug] = defaultCap;
                dirty = true;
                continue;
            }

            foreach (var (providerName, defaultProvider) in defaultCap.Providers)
            {
                if (!existingCap.Providers.TryGetValue(providerName, out var existingProvider))
                {
                    existingCap.Providers[providerName] = defaultProvider;
                    dirty = true;
                    continue;
                }

                if (existingProvider.WslDistro == null && defaultProvider.WslDistro != null)
                { existingProvider.WslDistro = defaultProvider.WslDistro; dirty = true; }
                if (existingProvider.VenvPath == null && defaultProvider.VenvPath != null)
                { existingProvider.VenvPath = defaultProvider.VenvPath; dirty = true; }
                if (existingProvider.ServerPath == null && defaultProvider.ServerPath != null)
                { existingProvider.ServerPath = defaultProvider.ServerPath; dirty = true; }
                if (existingProvider.BackendPort == null && defaultProvider.BackendPort != null)
                { existingProvider.BackendPort = defaultProvider.BackendPort; dirty = true; }
                if (existingProvider.HealthEndpoint == null && defaultProvider.HealthEndpoint != null)
                { existingProvider.HealthEndpoint = defaultProvider.HealthEndpoint; dirty = true; }
            }
        }
        // Migrate LocalWsl → specialized provider types for TTS/STT
        var typeMigrations = new Dictionary<string, Dictionary<string, string>>
        {
            ["tts"] = new() { ["LocalWsl"] = "TtsLocal", ["LocalNative"] = "TtsLocal" },
            ["stt"] = new() { ["LocalWsl"] = "SttLocal", ["LocalNative"] = "SttLocal" }
        };
        foreach (var (slug, migrations) in typeMigrations)
        {
            if (!Config.Capabilities.TryGetValue(slug, out var cap)) continue;
            foreach (var (_, provider) in cap.Providers)
            {
                if (migrations.TryGetValue(provider.Type, out var newType))
                {
                    provider.Type = newType;
                    dirty = true;
                }
            }
        }

        foreach (var slug in RemovedCapabilities)
        {
            if (Config.Capabilities.Remove(slug))
                dirty = true;
        }
        if (dirty) Save();
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
                    ActiveProvider = "local-wsl",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["local-wsl"] = new()
                        {
                            Type = "TtsLocal",
                            WslDistro = "Ubuntu-24.04",
                            VenvPath = "~/tts-env",
                            ServerPath = @"T:\Projects\Qwen3-TTS\qwen3-tts-server",
                            BackendPort = 8765,
                            HealthEndpoint = "/health",
                            Model = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
                            VoicesBasePath = @"T:\Projects\Qwen3-TTS",
                            StartupTimeoutSeconds = 180
                        },
                        ["elevenlabs"] = new()
                        {
                            Type = "TtsElevenLabs",
                            ApiKey = "",
                            Model = "eleven_multilingual_v2"
                        }
                    }
                },
                ["stt"] = new()
                {
                    ActiveProvider = "local-wsl",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["local-wsl"] = new()
                        {
                            Type = "SttLocal",
                            WslDistro = "Ubuntu-24.04",
                            VenvPath = "~/stt-env",
                            ServerPath = @"T:\Projects\faster-whisper-server",
                            BackendPort = 8766,
                            HealthEndpoint = "/health",
                            Model = "large-v3",
                            StartupTimeoutSeconds = 300
                        },
                        ["local-wsl-turbo"] = new()
                        {
                            Type = "SttLocal",
                            WslDistro = "Ubuntu-24.04",
                            VenvPath = "~/stt-env",
                            ServerPath = @"T:\Projects\faster-whisper-server",
                            BackendPort = 8767,
                            HealthEndpoint = "/health",
                            Model = "large-v3-turbo",
                            StartupTimeoutSeconds = 300
                        }
                    }
                },
                ["music-gen"] = new()
                {
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
                },
                ["ai-session"] = new()
                {
                    ActiveProvider = "claude-code",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["claude-code"] = new()
                        {
                            Type = "ClaudeCode",
                            Extra = new Dictionary<string, object?>
                            {
                                ["ProjectsRoot"] = @"T:\Projects",
                                ["MaxSessions"] = 99,
                                ["DefaultOneshotModel"] = "haiku"
                            }
                        },
                        ["codex"] = new()
                        {
                            Type = "Codex",
                            Extra = new Dictionary<string, object?>
                            {
                                ["ProjectsRoot"] = @"T:\Projects",
                                ["MaxSessions"] = 99,
                            }
                        },
                        ["opencode"] = new()
                        {
                            Type = "OpenCode",
                            Extra = new Dictionary<string, object?>
                            {
                                ["ProjectsRoot"] = @"T:\Projects",
                                ["MaxSessions"] = 99,
                            }
                        }
                    }
                }
            }
        };
    }
}
