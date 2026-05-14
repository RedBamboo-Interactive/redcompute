using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.TtsLocal;

public class TtsLocalProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly LocalWslProvider _inner;
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly string _voicesBasePath;

    public TtsLocalProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _inner = new LocalWslProvider(config, capabilitySlug, log);
        _voicesBasePath = config.VoicesBasePath ?? "";
    }

    // IBackendProvider delegation
    public string Name => "TTS Local";
    public string CapabilitySlug => _capabilitySlug;
    public Task<bool> StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _inner.StopAsync(ct);
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => _inner.GetStatusAsync(ct);
    public string? GetProxyTargetUrl() => _inner.GetProxyTargetUrl();
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
    public TimeSpan HealthCheckInterval => _inner.HealthCheckInterval;
    public int? ProcessId => _inner.ProcessId;
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    // IPluginProvider properties
    public static string ProviderTypeName => "TtsLocal";
    public string DisplayName => "TTS Local (Qwen3-TTS)";
    public string ProviderType => "TtsLocal";
    public bool IsProxy => true;
    public string ProxyGeneratePath => "/synthesize/wav";
    public bool SupportsProgress => false;
    public bool SupportsRerun => true;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["text"] = new ParameterSchema
        {
            Type = "string",
            Required = true,
            Description = "Text to synthesize"
        },
        ["voice"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "Serena",
            Description = "Voice name"
        },
        ["language"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "English",
            Description = "Language for synthesis",
            Enum = ["English", "Chinese", "French", "Japanese", "Korean"]
        },
        ["emotion"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "neutral",
            Description = "Emotional tone",
            Enum = ["neutral", "excited", "happy", "sad", "angry", "sarcastic", "curious", "confident"]
        },
        ["instruct"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Free-form instruction for voice style"
        },
        ["speed"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 1.0,
            Description = "Playback speed multiplier",
            Min = 0.5,
            Max = 2.0
        },
        ["stream"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Stream audio chunks as they are generated"
        }
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "audio/wav",
        Streaming = false,
        MediaCategory = "audio",
        OutputEndpoint = "/tts/jobs/{id}/output"
    };

    // ICustomEndpointProvider
    public void MapCustomEndpoints(WebApplication app)
    {
        app.MapGet("/tts/voices", () =>
        {
            var builtin = new[] { "Serena", "Aiden", "Ryan", "Vivian" };
            var custom = new List<string>();

            if (!string.IsNullOrEmpty(_voicesBasePath) && Directory.Exists(_voicesBasePath))
            {
                foreach (var dir in Directory.GetDirectories(_voicesBasePath))
                {
                    var safetensorsPath = Path.Combine(dir, "model", "checkpoint", "model.safetensors");
                    if (File.Exists(safetensorsPath))
                    {
                        custom.Add(Path.GetFileName(dir));
                    }
                }
            }

            return Results.Json(new { builtin, custom });
        });
    }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() =>
    [
        new EndpointManifest
        {
            Method = "GET",
            Path = "/tts/voices",
            Description = "List available TTS voices (built-in and custom)"
        }
    ];

    public Dictionary<string, object?> TransformParameters(Dictionary<string, object?> parameters)
    {
        // Backend expects 'speaker' not 'voice'
        if (parameters.TryGetValue("voice", out var voice))
        {
            parameters["speaker"] = voice;
            parameters.Remove("voice");
        }
        return parameters;
    }
}
