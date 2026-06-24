using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.TtsElevenLabs;

public class TtsElevenLabsProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

    public TtsElevenLabsProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;
        _apiKey = config.ApiKey ?? "";
        _model = config.Model ?? ProviderHelpers.GetExtra(config, "model", "eleven_v3");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("xi-api-key", _apiKey);
    }

    // IBackendProvider
    public string Name => "TTS ElevenLabs";
    public string CapabilitySlug => _capabilitySlug;
    public TimeSpan HealthCheckInterval => TimeSpan.FromMinutes(5);
    public int? ProcessId => null;

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_apiKey) ? BackendStatus.Stopped : BackendStatus.Running);

    public string? GetProxyTargetUrl() => null;

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        var p = request.Parameters;

        var text = ProviderHelpers.GetParam<string>(p, "text");
        if (string.IsNullOrWhiteSpace(text))
            return new JobResult { Success = false, ErrorMessage = "text is required" };

        var voiceId = ProviderHelpers.GetParam<string>(p, "voice");
        if (string.IsNullOrWhiteSpace(voiceId))
            return new JobResult { Success = false, ErrorMessage = "voice is required" };

        var stability = ProviderHelpers.GetParam<double?>(p, "stability") ?? 0.5;
        var similarityBoost = ProviderHelpers.GetParam<double?>(p, "similarity_boost") ?? 0.75;
        var style = ProviderHelpers.GetParam<double?>(p, "style") ?? 0.5;
        var speed = ProviderHelpers.GetParam<double?>(p, "speed") ?? 1.0;
        var language = ProviderHelpers.GetParam<string>(p, "language");

        var bodyObj = new Dictionary<string, object>
        {
            ["text"] = text,
            ["model_id"] = _model,
            ["voice_settings"] = new Dictionary<string, object>
            {
                ["stability"] = stability,
                ["similarity_boost"] = similarityBoost,
                ["style"] = style,
                ["use_speaker_boost"] = true,
                ["speed"] = speed
            },
            ["apply_text_normalization"] = "auto"
        };
        if (!string.IsNullOrEmpty(language))
            bodyObj["language_code"] = language;

        var json = JsonSerializer.Serialize(bodyObj);
        var requestMsg = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}");
        requestMsg.Headers.Add("Accept", "audio/mpeg");
        requestMsg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        _log($"[tts-elevenlabs] Synthesizing {text.Length} chars with voice {voiceId}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(requestMsg, ct);
        }
        catch (Exception ex)
        {
            return new JobResult { Success = false, ErrorMessage = $"HTTP error: {ex.Message}" };
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            return new JobResult { Success = false, ErrorMessage = $"ElevenLabs API error {(int)response.StatusCode}: {error}" };
        }

        var audioStream = await response.Content.ReadAsStreamAsync(ct);
        return new JobResult
        {
            Success = true,
            OutputStream = audioStream,
            ContentType = "audio/mpeg"
        };
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // IPluginProvider
    public static string ProviderTypeName => "TtsElevenLabs";
    public string DisplayName => "TTS ElevenLabs";
    public string ProviderType => "TtsElevenLabs";
    public bool IsProxy => false;
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
            Required = true,
            Description = "ElevenLabs voice ID"
        },
        ["stability"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 0.5,
            Description = "Voice stability (0-1)",
            Min = 0,
            Max = 1
        },
        ["similarity_boost"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 0.75,
            Description = "Voice similarity boost (0-1)",
            Min = 0,
            Max = 1
        },
        ["style"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 0.5,
            Description = "Style exaggeration (0-1)",
            Min = 0,
            Max = 1
        },
        ["speed"] = new ParameterSchema
        {
            Type = "number",
            Required = false,
            Default = 1.0,
            Description = "Speaking speed multiplier (0.25-4.0)",
            Min = 0.25,
            Max = 4.0
        },
        ["language"] = new ParameterSchema
        {
            Type = "string",
            Required = false,
            Description = "Language code for multilingual models (e.g. en, fr, de)"
        }
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "audio/mpeg",
        Streaming = false,
        MediaCategory = "audio",
        OutputEndpoint = "/tts/jobs/{id}/output"
    };

    // ICustomEndpointProvider
    public void MapCustomEndpoints(WebApplication app)
    {
        app.MapGet("/tts/elevenlabs/voices", async (HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = "ElevenLabs API key not configured" });
                return;
            }

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Failed to fetch voices from ElevenLabs" });
                    return;
                }

                var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var voices = data.TryGetProperty("voices", out var voicesEl)
                    ? voicesEl.EnumerateArray()
                        .Select(v => new
                        {
                            id = v.TryGetProperty("voice_id", out var id) ? id.GetString() : null,
                            name = v.TryGetProperty("name", out var name) ? name.GetString() : null,
                            category = v.TryGetProperty("category", out var cat) ? cat.GetString() : null
                        })
                        .ToList()
                    : [];

                await ctx.Response.WriteAsJsonAsync(new { voices });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
    }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() =>
    [
        new EndpointManifest
        {
            Method = "GET",
            Path = "/tts/elevenlabs/voices",
            Description = "List available ElevenLabs voices"
        }
    ];
}
