using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Suno;

internal sealed record SunoProviderTiming(
    TimeSpan PollInterval,
    TimeSpan PollTimeout,
    TimeSpan? StemPollTimeout = null)
{
    public static SunoProviderTiming Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(600),
        TimeSpan.FromSeconds(1200));
    public TimeSpan MediaRetryInterval { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan MediaRetryTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

public sealed class SunoProvider : IPluginProvider
{
    private const string CallbackPlaceholder = "https://example.com";
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly HttpClient _apiHttp;
    private readonly HttpClient _downloadHttp;
    private readonly bool _ownsApiHttp;
    private readonly bool _ownsDownloadHttp;
    private readonly SunoProviderTiming _timing;
    private readonly long _maxDownloadBytes;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private BackendStatus _status = BackendStatus.Stopped;

    public string Name => "Suno";
    public string CapabilitySlug => _capabilitySlug;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
    public Action<double>? ProgressCallback { get; set; }
    public List<SunoClipResult>? LastClipResults { get; private set; }
    public string DisplayName => "Suno API";
    public string ProviderType => "Suno";
    public bool IsProxy => false;
    public bool SupportsProgress => true;
    public bool SupportsRerun => true;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["operation"] = new() { Type = "string", Required = false, Default = "generate", Description = "generate, sounds, extend, split_stem, split_stem_advanced, separate_vocal, wav, or recover" },
        ["sourceOperation"] = new() { Type = "string", Required = false, Description = "Original operation for a no-charge recovery of an existing provider task" },
        ["prompt"] = new() { Type = "string", Required = false, Description = "Musical or structural description" },
        ["style"] = new() { Type = "string", Required = false, Description = "Genre and production tags" },
        ["title"] = new() { Type = "string", Required = false, Description = "Track title" },
        ["negativeTags"] = new() { Type = "string", Required = false, Description = "Styles and traits to exclude" },
        ["instrumental"] = new() { Type = "boolean", Required = false, Default = true, Description = "Instrumental only" },
        ["model"] = new() { Type = "string", Required = false, Description = "Suno model override" },
        ["duration"] = new() { Type = "number", Required = false, Description = "Requested duration in seconds where supported" },
        ["audioId"] = new() { Type = "string", Required = false, Description = "Provider audio id from an earlier result" },
        ["taskId"] = new() { Type = "string", Required = false, Description = "Provider task id from an earlier result" },
        ["continueAt"] = new() { Type = "number", Required = false, Description = "Extension point in seconds" },
        ["separationType"] = new() { Type = "string", Required = false, Default = "split_stem", Description = "separate_vocal, split_stem, or split_stem_advanced" },
        ["stemName"] = new() { Type = "string", Required = false, Description = "Instrument name for split_stem_advanced" },
        ["soundLoop"] = new() { Type = "boolean", Required = false, Default = false, Description = "Ask the V5 sounds endpoint for a loop" },
        ["soundTempo"] = new() { Type = "integer", Required = false, Description = "Sounds endpoint tempo from 1 to 300 BPM" },
        ["soundKey"] = new() { Type = "string", Required = false, Default = "Any", Description = "Sounds endpoint musical key" },
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "audio/mpeg",
        Streaming = false,
        MediaCategory = "audio",
        OutputEndpoint = "/music-gen/jobs/{id}/output?artifact={name}",
    };

    public SunoProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
        : this(config, capabilitySlug, log, null, null, null)
    {
    }

    internal SunoProvider(
        ProviderConfig config,
        string capabilitySlug,
        Action<string> log,
        HttpClient? apiHttp,
        HttpClient? downloadHttp,
        SunoProviderTiming? timing)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;
        _baseUrl = ProviderHelpers.GetExtra(config, "BaseUrl", "https://api.sunoapi.org").TrimEnd('/');
        _model = ProviderHelpers.GetExtra(config, "Model", "V5");
        _timing = timing ?? SunoProviderTiming.Default;
        _maxDownloadBytes = ParseLong(ProviderHelpers.GetExtra(config, "MaxDownloadBytes", "268435456"), 268_435_456);

        _ownsApiHttp = apiHttp is null;
        _apiHttp = apiHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            _apiHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        _ownsDownloadHttp = downloadHttp is null;
        _downloadHttp = downloadHttp ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // Deliberately never put the API credential on the download client. Audio and
        // cover URLs are often served by a different CDN origin.
    }

    public void SetProgressCallback(Action<double>? callback) => ProgressCallback = callback;

    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _status = BackendStatus.Error;
            _log("[Suno] No API key configured");
            return Task.FromResult(false);
        }
        _status = BackendStatus.Running;
        _log($"[Suno] Ready (model: {_model}, endpoint: {_baseUrl})");
        return Task.FromResult(true);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _status = BackendStatus.Stopped;
        _log("[Suno] Stopped");
        return Task.CompletedTask;
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_status);
    public string? GetProxyTargetUrl() => null;

    public Dictionary<string, string> ValidateParameters(Dictionary<string, object?> parameters)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var operation = Operation(parameters);
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "generate", "sounds", "extend", "split_stem", "split_stem_advanced", "separate_vocal", "wav", "recover",
        };
        if (!supported.Contains(operation)) errors["operation"] = "unsupported";
        var requestedModel = ProviderHelpers.GetParam<string>(parameters, "model") ?? _model;
        if (operation is "generate" or "extend" && requestedModel is not ("V4" or "V4_5" or "V4_5PLUS" or "V4_5ALL" or "V5" or "V5_5"))
            errors["model"] = "must be V4, V4_5, V4_5PLUS, V4_5ALL, V5, or V5_5";

        switch (operation)
        {
            case "generate":
                Require(parameters, errors, "prompt", "style", "title");
                var duration = ProviderHelpers.GetParam<double?>(parameters, "duration");
                if (duration.HasValue && !requestedModel.Equals("V5_5", StringComparison.OrdinalIgnoreCase))
                    errors["duration"] = "is supported only by the V5_5 model";
                if (duration.HasValue && (duration.Value is < 10 or > 360 || duration.Value != Math.Truncate(duration.Value)))
                    errors["duration"] = "must be a whole number from 10 through 360";
                break;
            case "sounds":
                Require(parameters, errors, "prompt");
                var tempo = ProviderHelpers.GetParam<int?>(parameters, "soundTempo");
                if (tempo.HasValue && tempo is < 1 or > 300) errors["soundTempo"] = "must be between 1 and 300";
                break;
            case "extend":
                Require(parameters, errors, "audioId", "prompt", "style", "title");
                if (ProviderHelpers.GetParam<double?>(parameters, "continueAt") is not > 0)
                    errors["continueAt"] = "must be greater than zero";
                break;
            case "split_stem":
            case "separate_vocal":
                Require(parameters, errors, "taskId", "audioId");
                break;
            case "split_stem_advanced":
                Require(parameters, errors, "taskId", "audioId", "stemName");
                break;
            case "wav":
                Require(parameters, errors, "taskId", "audioId");
                break;
            case "recover":
                Require(parameters, errors, "taskId", "sourceOperation");
                var sourceOperation = SourceOperation(parameters);
                if (sourceOperation is not ("generate" or "sounds" or "extend" or "split_stem" or "split_stem_advanced" or "separate_vocal" or "wav"))
                    errors["sourceOperation"] = "must identify a recoverable Suno operation";
                break;
        }
        return errors;
    }

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        var validation = ValidateParameters(request.Parameters);
        if (validation.Count > 0)
            return Failure(string.Join("; ", validation.Select(pair => $"{pair.Key}: {pair.Value}")));
        if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return Failure("Another Suno operation is in progress. Try again shortly.");

        var operation = Operation(request.Parameters);
        var sourceOperation = operation == "recover" ? SourceOperation(request.Parameters) : operation;
        var recovering = operation == "recover";
        int? creditsBefore = null;
        int? creditsAfter = null;
        int? reportedConsumed = null;
        int? reportedRemaining = null;
        string? taskId = null;
        JsonElement? taskData = null;
        try
        {
            if (recovering)
            {
                taskId = ProviderHelpers.GetParam<string>(request.Parameters, "taskId")!;
                _log($"[Suno] Recovering existing {sourceOperation} task {taskId} without a new submission");
            }
            else
            {
                creditsBefore = await TryGetCreditsAsync(ct);
                taskId = await SubmitAsync(operation, request.Parameters, ct);
                _log($"[Suno] Submitted {operation} task {taskId}");
            }
            ProgressCallback?.Invoke(0.08);
            taskData = await PollUntilDoneAsync(sourceOperation, taskId, ct);
            reportedConsumed = FindNamedInt(taskData.Value,
                "creditsConsumed", "creditConsumed", "consumedCredits", "credits_used");
            reportedRemaining = FindNamedInt(taskData.Value,
                "creditsRemaining", "remainingCredits", "remaining_credits", "balance");
            creditsAfter = reportedRemaining ?? await TryGetCreditsAsync(ct);
            ProgressCallback?.Invoke(0.75);
            var prepared = await PrepareOutputsAsync(sourceOperation, taskId, taskData.Value, ct);
            var consumed = reportedConsumed ?? (!recovering && creditsBefore.HasValue && creditsAfter.HasValue
                ? Math.Max(0, creditsBefore.Value - creditsAfter.Value)
                : (int?)null);
            var measurement = reportedConsumed.HasValue
                ? "provider_task"
                : consumed.HasValue ? "balance_delta" : "unavailable";

            ProgressCallback?.Invoke(1);
            return BuildJobResult(sourceOperation, taskId, prepared, creditsBefore, creditsAfter, consumed, measurement, recovering);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[Suno] Operation failed: {ex.Message}");
            creditsAfter ??= reportedRemaining ?? await TryGetCreditsAsync(ct);
            if (taskData.HasValue)
            {
                reportedConsumed ??= FindNamedInt(taskData.Value,
                    "creditsConsumed", "creditConsumed", "consumedCredits", "credits_used");
                reportedRemaining ??= FindNamedInt(taskData.Value,
                    "creditsRemaining", "remainingCredits", "remaining_credits", "balance");
                creditsAfter ??= reportedRemaining;
            }
            var consumed = reportedConsumed ?? (!recovering && creditsBefore.HasValue && creditsAfter.HasValue
                ? Math.Max(0, creditsBefore.Value - creditsAfter.Value)
                : (int?)null);
            return Failure(ex.Message, BuildFailureAudit(
                sourceOperation, taskId, ex.Message, creditsBefore, creditsAfter, consumed,
                reportedConsumed.HasValue ? "provider_task" : consumed.HasValue ? "balance_delta" : "unavailable",
                recovering,
                taskData.HasValue));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_ownsApiHttp) _apiHttp.Dispose();
        if (_ownsDownloadHttp) _downloadHttp.Dispose();
        _semaphore.Dispose();
    }

    private async Task<string> SubmitAsync(string operation, Dictionary<string, object?> p, CancellationToken ct)
    {
        var callbackUrl = ProviderHelpers.GetExtra(_config, "CallbackUrl", CallbackPlaceholder);
        var model = ProviderHelpers.GetParam<string>(p, "model") ?? _model;
        var endpoint = operation switch
        {
            "generate" => "/api/v1/generate",
            "sounds" => "/api/v1/generate/sounds",
            "extend" => "/api/v1/generate/extend",
            "split_stem" or "split_stem_advanced" or "separate_vocal" => "/api/v1/vocal-removal/generate",
            "wav" => "/api/v1/wav/generate",
            _ => throw new InvalidOperationException($"Unsupported Suno operation '{operation}'"),
        };
        var payload = operation switch
        {
            "generate" => GeneratePayload(p, model, callbackUrl),
            "sounds" => SoundsPayload(p, callbackUrl),
            "extend" => ExtendPayload(p, model, callbackUrl),
            "split_stem" or "split_stem_advanced" or "separate_vocal" => StemPayload(p, operation, callbackUrl),
            "wav" => WavPayload(p, callbackUrl),
            _ => throw new InvalidOperationException($"Unsupported Suno operation '{operation}'"),
        };
        foreach (var key in payload.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
            payload.Remove(key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _apiHttp.PostAsJsonAsync(_baseUrl + endpoint, payload, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Suno submit HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (GetInt(root, "code") is { } code && code != 200)
            throw new InvalidOperationException($"Suno API rejected {operation}: {GetString(root, "msg") ?? "unknown error"}");
        var taskId = root.TryGetProperty("data", out var data) ? GetString(data, "taskId") : null;
        return !string.IsNullOrWhiteSpace(taskId)
            ? taskId
            : throw new InvalidDataException("Suno submit response is missing taskId");
    }

    private async Task<JsonElement> PollUntilDoneAsync(string operation, string taskId, CancellationToken ct)
    {
        var path = operation switch
        {
            "wav" => "/api/v1/wav/record-info",
            "split_stem" or "split_stem_advanced" or "separate_vocal" => "/api/v1/vocal-removal/record-info",
            _ => "/api/v1/generate/record-info",
        };
        var timeout = operation is "split_stem" or "split_stem_advanced" or "separate_vocal"
            ? _timing.StemPollTimeout ?? _timing.PollTimeout
            : _timing.PollTimeout;
        var deadline = DateTimeOffset.UtcNow + timeout;
        var seenIntermediate = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _apiHttp.GetAsync(
                    $"{_baseUrl}{path}?taskId={Uri.EscapeDataString(taskId)}", pollTimeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var root = document.RootElement;
                    if ((GetInt(root, "code") ?? 200) == 200 && root.TryGetProperty("data", out var data))
                    {
                        var status = GetString(data, "status") ?? GetString(data, "successFlag") ?? "";
                        if (status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)) return data.Clone();
                        if (IsFailure(status) || HasError(data))
                            throw new InvalidOperationException(GetString(data, "errorMessage") ?? $"Suno {operation} failed with status {status}");
                        if (!seenIntermediate && status is "TEXT_SUCCESS" or "FIRST_SUCCESS")
                        {
                            seenIntermediate = true;
                            ProgressCallback?.Invoke(status == "TEXT_SUCCESS" ? 0.30 : 0.55);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[Suno] Poll retry after {ex.Message}");
            }
            await Task.Delay(_timing.PollInterval, ct);
        }
        throw new TimeoutException($"Suno {operation} timed out after {timeout.TotalSeconds:0} seconds");
    }

    private async Task<PreparedOutput> PrepareOutputsAsync(
        string operation, string taskId, JsonElement taskData, CancellationToken ct)
    {
        return operation switch
        {
            "generate" or "sounds" or "extend" => await PrepareTracksAsync(taskId, taskData, ct),
            "split_stem" or "split_stem_advanced" or "separate_vocal" => await PrepareStemsAsync(taskData, ct),
            "wav" => await PrepareWavAsync(taskData, ct),
            _ => throw new InvalidOperationException($"Unsupported output operation '{operation}'"),
        };
    }

    private async Task<PreparedOutput> PrepareTracksAsync(string taskId, JsonElement taskData, CancellationToken ct)
    {
        if (!TryPath(taskData, out var sunoData, "response", "sunoData") || sunoData.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Suno generation completed without sunoData");
        var artifacts = new List<PreparedArtifact>();
        var tracks = new JsonArray();
        var legacy = new List<SunoClipResult>();
        var index = 0;
        try
        {
            foreach (var clip in sunoData.EnumerateArray())
            {
                var audioUrl = GetString(clip, "audioUrl", "audio_url");
                var streamAudioUrl = GetString(clip, "streamAudioUrl", "stream_audio_url");
                var audioUrls = new[] { audioUrl, streamAudioUrl }
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => url!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (audioUrls.Length == 0) continue;
                var clipId = GetString(clip, "id") ?? $"clip-{index}";
                var title = GetString(clip, "title") ?? $"Track {index + 1}";
                var tags = GetString(clip, "tags");
                var duration = GetDouble(clip, "duration");
                var audioName = $"clip-{index}";
                var audio = await DownloadAsync(
                    audioUrls, audioName, $"{Slug(title)}-{Short(clipId)}", "audio", null, clipId, duration,
                    retryTransient: true, ct);
                artifacts.Add(audio);
                string? coverName = null;
                var imageUrl = GetString(clip, "imageUrl", "image_url");
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    coverName = $"cover-{index}";
                    try
                    {
                        artifacts.Add(await DownloadAsync(
                            [imageUrl], coverName, $"{Slug(title)}-{Short(clipId)}-cover", "cover", null, clipId, null,
                            retryTransient: false, ct));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log($"[Suno] Optional cover '{coverName}' was skipped: {ex.Message}");
                        coverName = null;
                    }
                }
                tracks.Add(new JsonObject
                {
                    ["providerTaskId"] = taskId,
                    ["providerAudioId"] = clipId,
                    ["title"] = title,
                    ["tags"] = tags,
                    ["durationSeconds"] = duration,
                    ["audioArtifact"] = audioName,
                    ["coverArtifact"] = coverName,
                });
                legacy.Add(new SunoClipResult
                {
                    ClipId = clipId,
                    Title = title,
                    Tags = tags ?? "",
                    AudioUrl = audioUrls[0],
                    ImageUrl = imageUrl ?? "",
                    Filename = audio.FileName,
                });
                index++;
            }
            if (artifacts.Count == 0 || tracks.Count == 0) throw new InvalidDataException("Suno produced no downloadable tracks");
            LastClipResults = legacy;
            return new PreparedOutput(artifacts, tracks);
        }
        catch
        {
            foreach (var artifact in artifacts) artifact.Stream.Dispose();
            throw;
        }
    }

    private async Task<PreparedOutput> PrepareStemsAsync(JsonElement taskData, CancellationToken ct)
    {
        if (!TryPath(taskData, out var originData, "response", "originData") || originData.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Suno separation completed without originData");
        var artifacts = new List<PreparedArtifact>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var stem in originData.EnumerateArray())
            {
                var url = GetString(stem, "audio_url", "audioUrl");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var role = GetString(stem, "stem_type_group_name", "stemTypeGroupName") ?? "stem";
                var baseName = $"stem-{Slug(role)}";
                var name = baseName;
                var suffix = 2;
                while (!usedNames.Add(name)) name = $"{baseName}-{suffix++}";
                artifacts.Add(await DownloadAsync(
                    url, name, name, "stem", role,
                    GetString(stem, "id"), GetDouble(stem, "duration"), ct));
            }
            if (artifacts.Count == 0) throw new InvalidDataException("Suno produced no downloadable stems");
            return new PreparedOutput(artifacts, []);
        }
        catch
        {
            foreach (var artifact in artifacts) artifact.Stream.Dispose();
            throw;
        }
    }

    private async Task<PreparedOutput> PrepareWavAsync(JsonElement taskData, CancellationToken ct)
    {
        if (!TryPath(taskData, out var response, "response"))
            throw new InvalidDataException("Suno WAV conversion completed without a response");
        var url = GetString(response, "audioWavUrl", "audio_wav_url");
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Suno WAV conversion response is missing audioWavUrl");
        var artifact = await DownloadAsync(url, "wav", "music-master", "audio", "master", GetString(taskData, "musicId"), null, ct);
        return new PreparedOutput([artifact], []);
    }

    private Task<PreparedArtifact> DownloadAsync(
        string url,
        string name,
        string fileStem,
        string kind,
        string? role,
        string? providerId,
        double? duration,
        CancellationToken ct)
        => DownloadAsync([url], name, fileStem, kind, role, providerId, duration, retryTransient: true, ct);

    private async Task<PreparedArtifact> DownloadAsync(
        IEnumerable<string> urls,
        string name,
        string fileStem,
        string kind,
        string? role,
        string? providerId,
        double? duration,
        bool retryTransient,
        CancellationToken ct)
    {
        var candidates = new List<Uri>();
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _log($"[Suno] Ignored an unsafe URL for artifact '{name}'");
                continue;
            }
            try
            {
                await EnsurePublicDownloadTargetAsync(uri, name, ct);
                candidates.Add(uri);
            }
            catch (InvalidDataException ex)
            {
                _log($"[Suno] Ignored an unsafe URL for artifact '{name}': {ex.Message}");
            }
        }
        if (candidates.Count == 0)
            throw new InvalidDataException($"Suno returned no safe download URL for artifact '{name}'");

        var retryTimeout = retryTransient ? _timing.MediaRetryTimeout : TimeSpan.Zero;
        var deadline = DateTimeOffset.UtcNow + retryTimeout;
        var attempts = 0;
        DownloadAttemptFailure? lastFailure = null;
        do
        {
            var cycleHasRetryableFailure = false;
            DownloadAttemptFailure? cycleTerminalFailure = null;
            foreach (var uri in candidates)
            {
                attempts++;
                DownloadAttempt attempt;
                try
                {
                    attempt = await TryDownloadOnceAsync(
                        uri, name, fileStem, kind, role, providerId, duration, ct);
                }
                catch (HttpRequestException)
                {
                    attempt = new DownloadAttempt(null,
                        new DownloadAttemptFailure(uri.Host, "network failure", true));
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    attempt = new DownloadAttempt(null,
                        new DownloadAttemptFailure(uri.Host, "request timeout", true));
                }
                if (attempt.Artifact is not null) return attempt.Artifact;
                lastFailure = attempt.Failure;
                if (lastFailure is { Retryable: true }) cycleHasRetryableFailure = true;
                else cycleTerminalFailure ??= lastFailure;
            }

            if (!cycleHasRetryableFailure && cycleTerminalFailure is not null)
                throw new InvalidDataException(
                    $"Suno artifact '{name}' from '{cycleTerminalFailure.Host}' failed: {cycleTerminalFailure.Detail}");
            if (DateTimeOffset.UtcNow >= deadline) break;
            var remaining = deadline - DateTimeOffset.UtcNow;
            var delay = remaining < _timing.MediaRetryInterval ? remaining : _timing.MediaRetryInterval;
            if (delay > TimeSpan.Zero)
            {
                _log($"[Suno] Artifact '{name}' is not ready ({lastFailure?.Detail ?? "unknown response"} from '{lastFailure?.Host ?? "unknown host"}'); retrying");
                await Task.Delay(delay, ct);
            }
        } while (DateTimeOffset.UtcNow <= deadline);

        throw new HttpRequestException(
            $"Suno artifact '{name}' from '{lastFailure?.Host ?? "unknown host"}' remained unavailable ({lastFailure?.Detail ?? "unknown response"}) after {attempts} attempt(s)");
    }

    private async Task<DownloadAttempt> TryDownloadOnceAsync(
        Uri uri,
        string name,
        string fileStem,
        string kind,
        string? role,
        string? providerId,
        double? duration,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("RedCompute/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(kind == "cover" ? "image/*" : "audio/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
            return new DownloadAttempt(null,
                new DownloadAttemptFailure(uri.Host, detail, IsTransientMediaStatus(response.StatusCode)));
        }
        if (response.Content.Headers.ContentLength is { } length && length > _maxDownloadBytes)
            throw new InvalidDataException($"Suno artifact '{name}' exceeds the {_maxDownloadBytes}-byte download limit");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var memory = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > _maxDownloadBytes)
            {
                memory.Dispose();
                throw new InvalidDataException($"Suno artifact '{name}' exceeds the {_maxDownloadBytes}-byte download limit");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        memory.Position = 0;
        if (memory.Length == 0)
        {
            memory.Dispose();
            return new DownloadAttempt(null,
                new DownloadAttemptFailure(uri.Host, "empty response", true));
        }
        var declaredType = response.Content.Headers.ContentType?.MediaType;
        var contentType = string.IsNullOrWhiteSpace(declaredType) ||
                          declaredType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                          declaredType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? ContentTypeFromUri(uri, kind)
            : declaredType;
        var expectedPrefix = kind == "cover" ? "image/" : "audio/";
        if (!contentType.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            memory.Dispose();
            return new DownloadAttempt(null,
                new DownloadAttemptFailure(uri.Host, $"content type '{contentType}'", true));
        }
        var extension = ExtensionFor(contentType, kind);
        return new DownloadAttempt(
            new PreparedArtifact(name, $"{fileStem}{extension}", contentType, kind, role, providerId, duration, memory),
            null);
    }

    private JobResult BuildJobResult(
        string operation,
        string taskId,
        PreparedOutput prepared,
        int? creditsBefore,
        int? creditsAfter,
        int? creditsConsumed,
        string measurement,
        bool recovered)
    {
        var primary = prepared.Artifacts[0];
        var artifactRows = new JsonArray(prepared.Artifacts.Select(artifact => (JsonNode?)new JsonObject
        {
            ["name"] = artifact.Name,
            ["kind"] = artifact.Kind,
            ["role"] = artifact.Role,
            ["fileName"] = artifact.FileName,
            ["contentType"] = artifact.ContentType,
            ["providerId"] = artifact.ProviderId,
            ["durationSeconds"] = artifact.DurationSeconds,
        }).ToArray());
        var result = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["operation"] = operation,
            ["providerTaskId"] = taskId,
            ["recovered"] = recovered,
            ["tracks"] = prepared.Tracks,
            ["artifacts"] = artifactRows,
            ["credits"] = new JsonObject
            {
                ["before"] = creditsBefore,
                ["after"] = creditsAfter,
                ["consumed"] = creditsConsumed,
                ["measurement"] = measurement,
            },
        };
        var extras = prepared.Artifacts.Skip(1).Select((artifact, index) => new JobOutputPart
        {
            Suffix = $"_artifact{index + 1}",
            Name = artifact.Name,
            FileName = artifact.FileName,
            ContentType = artifact.ContentType,
            Data = artifact.Stream,
        }).ToArray();
        return new JobResult
        {
            Success = true,
            OutputStream = primary.Stream,
            ContentType = primary.ContentType,
            PrimaryOutputName = primary.Name,
            PrimaryFileName = primary.FileName,
            ResultJson = result.ToJsonString(),
            ExtraOutputs = extras,
        };
    }

    private async Task<int?> TryGetCreditsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _apiHttp.GetAsync(_baseUrl + "/api/v1/generate/credit", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _log($"[Suno] Credit balance unavailable: HTTP {(int)response.StatusCode} ({response.StatusCode})");
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            if ((GetInt(root, "code") ?? 200) != 200 || !root.TryGetProperty("data", out var data))
            {
                _log($"[Suno] Credit balance unavailable: {DescribeCreditEnvelope(root)}");
                return null;
            }
            var credits = ReadCreditValue(data);
            if (!credits.HasValue)
                _log($"[Suno] Credit balance unreadable: {DescribeCreditEnvelope(root)}");
            return credits;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[Suno] Credit balance probe failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static int? ReadCreditValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var numeric)) return numeric;
            if (value.TryGetDecimal(out var decimalValue) && decimalValue is >= 0 and <= int.MaxValue && decimalValue == Math.Truncate(decimalValue))
                return (int)decimalValue;
        }
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var textValue) &&
            textValue is >= 0 and <= int.MaxValue && textValue == Math.Truncate(textValue))
            return (int)textValue;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return null;
        return FindNamedInt(value,
            "credits", "credit", "totalCredits", "availableCredits", "creditBalance",
            "creditsRemaining", "remainingCredits", "remaining_credits", "balance");
    }

    private static int? FindNamedInt(JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var direct = ReadCreditValue(property.Value);
                    if (direct.HasValue) return direct;
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                var nested = FindNamedInt(property.Value, names);
                if (nested.HasValue) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindNamedInt(item, names);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    private static string DescribeCreditEnvelope(JsonElement root)
    {
        var code = GetInt(root, "code")?.ToString(CultureInfo.InvariantCulture) ?? "missing";
        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var value)
            ? DescribeJsonShape(value, 0)
            : "missing";
        return $"code={code}; data={data}";
    }

    private static string DescribeJsonShape(JsonElement value, int depth)
    {
        if (depth >= 3) return value.ValueKind.ToString().ToLowerInvariant();
        return value.ValueKind switch
        {
            JsonValueKind.Object => "object{" + string.Join(',', value.EnumerateObject().Take(12)
                .Select(property => $"{property.Name}:{DescribeJsonShape(property.Value, depth + 1)}")) + "}",
            JsonValueKind.Array => value.GetArrayLength() == 0
                ? "array[0]"
                : $"array[{value.GetArrayLength()}]<{DescribeJsonShape(value[0], depth + 1)}>",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => value.ValueKind.ToString().ToLowerInvariant(),
        };
    }

    private static Dictionary<string, object?> GeneratePayload(Dictionary<string, object?> p, string model, string callback) => new()
    {
        ["customMode"] = true,
        ["instrumental"] = ProviderHelpers.GetParam<bool?>(p, "instrumental") ?? true,
        ["model"] = model,
        ["callBackUrl"] = callback,
        ["prompt"] = ProviderHelpers.GetParam<string>(p, "prompt"),
        ["style"] = ProviderHelpers.GetParam<string>(p, "style"),
        ["title"] = ProviderHelpers.GetParam<string>(p, "title"),
        ["duration"] = ProviderHelpers.GetParam<double?>(p, "duration"),
        ["negativeTags"] = ProviderHelpers.GetParam<string>(p, "negativeTags"),
    };

    private static Dictionary<string, object?> SoundsPayload(Dictionary<string, object?> p, string callback) => new()
    {
        ["prompt"] = ProviderHelpers.GetParam<string>(p, "prompt"),
        ["model"] = "V5",
        ["soundLoop"] = ProviderHelpers.GetParam<bool?>(p, "soundLoop") ?? false,
        ["soundTempo"] = ProviderHelpers.GetParam<int?>(p, "soundTempo"),
        ["soundKey"] = ProviderHelpers.GetParam<string>(p, "soundKey") ?? "Any",
        ["grabLyrics"] = false,
        ["callBackUrl"] = callback,
    };

    private static Dictionary<string, object?> ExtendPayload(Dictionary<string, object?> p, string model, string callback) => new()
    {
        ["defaultParamFlag"] = false,
        ["instrumental"] = ProviderHelpers.GetParam<bool?>(p, "instrumental") ?? true,
        ["audioId"] = ProviderHelpers.GetParam<string>(p, "audioId"),
        ["prompt"] = ProviderHelpers.GetParam<string>(p, "prompt"),
        ["style"] = ProviderHelpers.GetParam<string>(p, "style"),
        ["title"] = ProviderHelpers.GetParam<string>(p, "title"),
        ["continueAt"] = ProviderHelpers.GetParam<double?>(p, "continueAt"),
        ["model"] = model,
        ["negativeTags"] = ProviderHelpers.GetParam<string>(p, "negativeTags"),
        ["callBackUrl"] = callback,
    };

    private static Dictionary<string, object?> StemPayload(Dictionary<string, object?> p, string operation, string callback) => new()
    {
        ["taskId"] = ProviderHelpers.GetParam<string>(p, "taskId"),
        ["audioId"] = ProviderHelpers.GetParam<string>(p, "audioId"),
        ["type"] = ProviderHelpers.GetParam<string>(p, "separationType") ?? operation,
        ["stemName"] = ProviderHelpers.GetParam<string>(p, "stemName"),
        ["callBackUrl"] = callback,
    };

    private static Dictionary<string, object?> WavPayload(Dictionary<string, object?> p, string callback) => new()
    {
        ["taskId"] = ProviderHelpers.GetParam<string>(p, "taskId"),
        ["audioId"] = ProviderHelpers.GetParam<string>(p, "audioId"),
        ["callBackUrl"] = callback,
    };

    private static string Operation(Dictionary<string, object?> p)
        => (ProviderHelpers.GetParam<string>(p, "operation") ?? "generate").Trim().ToLowerInvariant();

    private static string SourceOperation(Dictionary<string, object?> p)
        => (ProviderHelpers.GetParam<string>(p, "sourceOperation") ?? "").Trim().ToLowerInvariant();

    private static void Require(Dictionary<string, object?> p, Dictionary<string, string> errors, params string[] fields)
    {
        foreach (var field in fields)
            if (string.IsNullOrWhiteSpace(ProviderHelpers.GetParam<string>(p, field))) errors[field] = "required";
    }

    private static string BuildFailureAudit(
        string operation,
        string? taskId,
        string error,
        int? creditsBefore,
        int? creditsAfter,
        int? creditsConsumed,
        string measurement,
        bool recovered,
        bool recoverableProviderTask) => new JsonObject
    {
        ["schemaVersion"] = 3,
        ["operation"] = operation,
        ["providerTaskId"] = taskId,
        ["recovered"] = recovered,
        ["recoverableProviderTask"] = recoverableProviderTask,
        ["status"] = "failed",
        ["error"] = error,
        ["credits"] = new JsonObject
        {
            ["before"] = creditsBefore,
            ["after"] = creditsAfter,
            ["consumed"] = creditsConsumed,
            ["measurement"] = measurement,
        },
    }.ToJsonString();

    private static JobResult Failure(string message, string? resultJson = null)
        => new() { Success = false, ErrorMessage = message, ResultJson = resultJson };

    private static bool IsFailure(string status)
        => status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("CALLBACK_EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("SENSITIVE_WORD_ERROR", StringComparison.OrdinalIgnoreCase);

    private static bool HasError(JsonElement data)
        => data.TryGetProperty("errorCode", out var code) && code.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
           !(code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 0);

    private static bool IsTransientMediaStatus(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict or
           HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or
           HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
           HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)status == 425;

    private static bool TryPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var part in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        return true;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static async Task EnsurePublicDownloadTargetAsync(Uri uri, string artifactName, CancellationToken ct)
    {
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Suno returned a local download URL for artifact '{artifactName}'");
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"Suno artifact host '{uri.Host}' could not be resolved safely", ex);
        }
        if (addresses.Length == 0 || addresses.Any(IsNonPublicAddress))
            throw new InvalidDataException($"Suno returned a private or reserved download target for artifact '{artifactName}'");
    }

    internal static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                   (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               (bytes[0] & 0xfe) == 0xfc ||
               (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    private static string ContentTypeFromUri(Uri uri, string kind) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => kind == "cover" ? "image/jpeg" : "audio/mpeg",
    };

    private static string ExtensionFor(string contentType, string kind) => contentType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/jpeg" => ".jpg",
        _ => kind == "cover" ? ".jpg" : ".mp3",
    };

    private static string Slug(string text)
    {
        text = Regex.Replace(text.ToLowerInvariant(), @"[^\w\s-]", "");
        text = Regex.Replace(text, @"[\s_-]+", "-").Trim('-');
        return text.Length > 50 ? text[..50] : text.Length > 0 ? text : "music";
    }

    private static string Short(string value) => value.Length > 8 ? value[..8] : value;
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    private static long ParseLong(string value, long fallback) => long.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private sealed record PreparedOutput(List<PreparedArtifact> Artifacts, JsonArray Tracks);
    private sealed record DownloadAttempt(PreparedArtifact? Artifact, DownloadAttemptFailure? Failure);
    private sealed record DownloadAttemptFailure(string Host, string Detail, bool Retryable);
    private sealed record PreparedArtifact(
        string Name,
        string FileName,
        string ContentType,
        string Kind,
        string? Role,
        string? ProviderId,
        double? DurationSeconds,
        MemoryStream Stream);
}

public sealed class SunoClipResult
{
    public required string ClipId { get; init; }
    public required string Title { get; init; }
    public required string Tags { get; init; }
    public required string AudioUrl { get; init; }
    public required string ImageUrl { get; init; }
    public required string Filename { get; init; }
    public MemoryStream? AudioData { get; init; }
}
