using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.DiarizationLocal;

public sealed class DiarizationLocalProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly LocalWslProvider _inner;
    private static readonly HttpClient ProxyClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public DiarizationLocalProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _inner = new LocalWslProvider(config, capabilitySlug, log);
        CapabilitySlug = capabilitySlug;
    }

    public static string ProviderTypeName => "DiarizationLocal";
    public string Name => "Diarization Local";
    public string DisplayName => "Diarization Local (Nemotron 3)";
    public string ProviderType => ProviderTypeName;
    public string CapabilitySlug { get; }
    public bool IsProxy => true;
    public string ProxyGeneratePath => "/diarize";
    public bool SupportsProgress => false;
    public bool SupportsRerun => true;
    public string ContractVersion => "diarization.v1";
    public TimeSpan HealthCheckInterval => _inner.HealthCheckInterval;
    public int? ProcessId => _inner.ProcessId;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["audio"] = new()
        {
            Type = "file",
            Required = true,
            Description = "Audio file containing one or more speakers",
        },
        ["audio_base64"] = new()
        {
            Type = "string",
            Required = false,
            Description = "Base64-encoded audio data (alternative to file upload)",
        },
        ["mode"] = new()
        {
            Type = "string",
            Required = false,
            Default = "offline",
            Description = "Offline whole-file analysis or stateful streaming analysis",
            Enum = ["offline", "streaming"],
        },
        ["session_id"] = new()
        {
            Type = "string",
            Required = false,
            Description = "Stable session identifier required for streaming mode",
        },
        ["chunk_id"] = new()
        {
            Type = "string",
            Required = false,
            Description = "Idempotency identifier required for each streaming audio chunk",
        },
        ["is_first_chunk"] = new()
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Reset and begin a streaming session with this chunk",
        },
        ["is_last_chunk"] = new()
        {
            Type = "boolean",
            Required = false,
            Default = false,
            Description = "Flush and close the streaming session after this chunk",
        },
        ["max_speakers"] = new()
        {
            Type = "number",
            Required = false,
            Default = 8,
            Min = 1,
            Max = 8,
            Description = "Maximum anonymous speaker channels retained in the result",
        },
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "application/json",
        Streaming = false,
        OutputEndpoint = "/diarization/jobs/{id}/output",
    };

    public Dictionary<string, string> ValidateParameters(Dictionary<string, object?> parameters)
    {
        var errors = new Dictionary<string, string>();
        var mode = StringValue(parameters.GetValueOrDefault("mode")) ?? "offline";
        if (mode is not ("offline" or "streaming"))
            errors["mode"] = "must be offline or streaming";

        if (mode == "streaming")
        {
            if (string.IsNullOrWhiteSpace(StringValue(parameters.GetValueOrDefault("session_id"))))
                errors["session_id"] = "required for streaming mode";
            if (string.IsNullOrWhiteSpace(StringValue(parameters.GetValueOrDefault("chunk_id"))))
                errors["chunk_id"] = "required for streaming mode";
        }
        else if (BoolValue(parameters.GetValueOrDefault("is_first_chunk")) ||
                 BoolValue(parameters.GetValueOrDefault("is_last_chunk")))
        {
            errors["mode"] = "chunk boundaries are only valid in streaming mode";
        }

        if (NumberValue(parameters.GetValueOrDefault("max_speakers")) is { } count &&
            (count < 1 || count > 8 || count != Math.Truncate(count)))
            errors["max_speakers"] = "must be an integer from 1 through 8";

        return errors;
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _inner.StopAsync(ct);
    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => _inner.GetStatusAsync(ct);
    public string? GetProxyTargetUrl() => _inner.GetProxyTargetUrl();
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        => Task.FromResult<JobResult?>(null);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    public void MapCustomEndpoints(WebApplication app)
    {
        app.MapGet("/diarization/model", async () =>
        {
            var proxyUrl = _inner.GetProxyTargetUrl();
            if (proxyUrl is null) return Results.StatusCode(503);
            try
            {
                var response = await ProxyClient.GetAsync($"{proxyUrl}/model/info");
                var content = await response.Content.ReadAsStringAsync();
                return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    error = "backend_unavailable",
                    message = $"Failed to reach diarization backend: {ex.Message}",
                }, statusCode: 502);
            }
        });
    }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() =>
    [
        new EndpointManifest
        {
            Method = "GET",
            Path = "/diarization/model",
            Description = "Describe the loaded diarization model and streaming contract",
        },
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
