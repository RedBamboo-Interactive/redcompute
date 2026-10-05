using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.SttLocal;

public class SttLocalProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly LocalWslProvider _inner;
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private static readonly HttpClient ProxyClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SttLocalProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _inner = new LocalWslProvider(config, capabilitySlug, log);
    }

    // IBackendProvider delegation
    public string Name => "STT Local";
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
    public static string ProviderTypeName => "SttLocal";
    public string DisplayName => "STT Local (faster-whisper)";
    public string ProviderType => "SttLocal";
    public bool IsProxy => true;
    public string ProxyGeneratePath => "/transcribe";
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["audio"] = new ParameterSchema
        {
            Type = "file",
            Required = true,
            Description = "Audio file to transcribe"
        },
        ["audio_base64"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Base64-encoded audio data (alternative to file upload)"
        },
        ["language"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "auto",
            Description = "Language",
            Enum = [
                "auto", "en", "fr", "de", "es", "it", "pt", "nl", "pl", "ru",
                "uk", "ja", "zh", "ko", "ar", "tr", "sv", "da", "no", "fi",
                "cs", "el", "ro", "hu", "hi", "th", "vi", "id", "ms", "he",
                "bg", "ca", "hr", "sk", "sl", "sr", "lt", "lv", "et"
            ]
        },
        ["task"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "transcribe",
            Description = "Task to perform",
            Enum = ["transcribe", "translate"]
        },
        ["word_timestamps"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Include word-level timestamps in output"
        },
        ["stream"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Stream partial transcription results"
        },
        ["initial_prompt"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Optional prompt to guide the transcription"
        },
        ["vad_filter"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = true,
            Description = "Enable voice activity detection filter"
        },
        ["diarize"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Enrich the transcription with anonymous speaker turns"
        },
        ["diarization_mode"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Default = "offline",
            Description = "Offline whole-file or stateful streaming diarization",
            Enum = ["offline", "streaming"]
        },
        ["diarization_session_id"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Stable diarization session identifier for streaming audio"
        },
        ["diarization_chunk_id"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Idempotency identifier for a streaming audio chunk"
        },
        ["diarization_is_first_chunk"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Begin or reset the streaming diarization session"
        },
        ["diarization_is_last_chunk"] = new ParameterSchema
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Flush and close the streaming diarization session"
        },
        ["diarization_max_speakers"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 8,
            Min = 1,
            Max = 8,
            Description = "Maximum anonymous speaker channels retained in the result"
        }
    };

    public Dictionary<string, string> ValidateParameters(Dictionary<string, object?> parameters)
    {
        var errors = new Dictionary<string, string>();
        if (!BoolValue(parameters.GetValueOrDefault("diarize"))) return errors;

        var mode = StringValue(parameters.GetValueOrDefault("diarization_mode")) ?? "offline";
        if (mode is not ("offline" or "streaming"))
            errors["diarization_mode"] = "must be offline or streaming";
        if (mode == "streaming")
        {
            if (string.IsNullOrWhiteSpace(StringValue(parameters.GetValueOrDefault("diarization_session_id"))))
                errors["diarization_session_id"] = "required when streaming diarization is enabled";
            if (string.IsNullOrWhiteSpace(StringValue(parameters.GetValueOrDefault("diarization_chunk_id"))))
                errors["diarization_chunk_id"] = "required when streaming diarization is enabled";
        }

        if (NumberValue(parameters.GetValueOrDefault("diarization_max_speakers")) is { } count &&
            (count < 1 || count > 8 || count != Math.Truncate(count)))
            errors["diarization_max_speakers"] = "must be an integer from 1 through 8";

        return errors;
    }

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "application/json",
        Streaming = false,
        OutputEndpoint = "/stt/jobs/{id}/output"
    };

    // ICustomEndpointProvider
    public void MapCustomEndpoints(WebApplication app)
    {
        app.MapGet("/stt/models", async () =>
        {
            var proxyUrl = _inner.GetProxyTargetUrl();
            if (proxyUrl == null)
                return Results.StatusCode(503);

            try
            {
                var response = await ProxyClient.GetAsync($"{proxyUrl}/models");
                var content = await response.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "backend_unavailable", message = $"Failed to reach STT backend: {ex.Message}" }, statusCode: 502);
            }
        });

        app.MapGet("/stt/languages", async () =>
        {
            var proxyUrl = _inner.GetProxyTargetUrl();
            if (proxyUrl == null)
                return Results.StatusCode(503);

            try
            {
                var response = await ProxyClient.GetAsync($"{proxyUrl}/languages");
                var content = await response.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "backend_unavailable", message = $"Failed to reach STT backend: {ex.Message}" }, statusCode: 502);
            }
        });
    }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() =>
    [
        new EndpointManifest
        {
            Method = "GET",
            Path = "/stt/models",
            Description = "List available STT models"
        },
        new EndpointManifest
        {
            Method = "GET",
            Path = "/stt/languages",
            Description = "List supported languages for transcription"
        }
    ];

    private static string? StringValue(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        _ => value.ToString(),
    };

    private static bool BoolValue(object? value) => value switch
    {
        bool boolean => boolean,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } json
            when bool.TryParse(json.GetString(), out var parsed) => parsed,
        _ when bool.TryParse(value?.ToString(), out var parsed) => parsed,
        _ => false,
    };

    private static double? NumberValue(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetDouble(out var number) => number,
        _ when double.TryParse(value.ToString(), out var number) => number,
        _ => null,
    };
}
