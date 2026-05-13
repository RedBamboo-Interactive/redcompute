using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Suno;

public class SunoProvider : IPluginProvider
{
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private BackendStatus _status = BackendStatus.Stopped;

    private const double PollIntervalSeconds = 5.0;
    private const double PollTimeoutSeconds = 300.0;
    private const string CallbackPlaceholder = "https://example.com";

    public string Name => "Suno";
    public string CapabilitySlug => _capabilitySlug;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
    public Action<double>? ProgressCallback { get; set; }
    public List<SunoClipResult>? LastClipResults { get; private set; }

    // IPluginProvider properties
    public string DisplayName => "Suno AI";
    public string ProviderType => "Suno";
    public bool IsProxy => false;
    public bool SupportsProgress => true;
    public bool SupportsRerun => true;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new ParameterSchema { Type = "string", Required = true, Description = "Musical description" },
        ["style"] = new ParameterSchema { Type = "string", Required = false, Description = "Genre/style tags" },
        ["title"] = new ParameterSchema { Type = "string", Required = false, Description = "Song title" },
        ["instrumental"] = new ParameterSchema { Type = "boolean", Required = false, Description = "Instrumental only", Default = true }
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "audio/mpeg",
        Streaming = false,
        MediaCategory = "audio",
        OutputEndpoint = "/music-gen/jobs/{id}/output"
    };

    public SunoProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;

        _baseUrl = ProviderHelpers.GetExtra(config, "BaseUrl", "https://api.sunoapi.org").TrimEnd('/');
        _model = ProviderHelpers.GetExtra(config, "Model", "V4_5");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public void SetProgressCallback(Action<double>? callback)
    {
        ProgressCallback = callback;
    }

    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
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

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(_status);

    public string? GetProxyTargetUrl() => null;

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        var progressCallback = ProgressCallback;
        var p = request.Parameters;
        var prompt = ProviderHelpers.GetParam<string>(p, "prompt");
        var style = ProviderHelpers.GetParam<string>(p, "style") ?? "";
        var title = ProviderHelpers.GetParam<string>(p, "title") ?? "";
        var instrumental = ProviderHelpers.GetParam<bool?>(p, "instrumental") ?? true;

        if (string.IsNullOrWhiteSpace(prompt))
            return new JobResult { Success = false, ErrorMessage = "prompt is required" };

        if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return new JobResult { Success = false, ErrorMessage = "Another generation is in progress. Try again shortly." };

        try
        {
            var taskId = await SubmitAsync(prompt, style, title, instrumental, ct);
            if (taskId == null)
                return new JobResult { Success = false, ErrorMessage = "Failed to submit to Suno API" };

            _log($"[Suno] Submitted task {taskId}");
            progressCallback?.Invoke(0.1);

            var clips = await PollUntilDoneAsync(taskId, progressCallback, ct);
            if (clips == null)
                return new JobResult { Success = false, ErrorMessage = "Suno generation failed or timed out" };

            var results = new List<SunoClipResult>();
            MemoryStream? firstClipStream = null;

            foreach (var clip in clips)
            {
                var audioUrl = clip.TryGetProperty("audioUrl", out var au) ? au.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(audioUrl)) continue;

                var clipTitle = clip.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
                var clipTags = clip.TryGetProperty("tags", out var tg) ? tg.GetString() ?? style : style;
                var clipId = clip.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                var imageUrl = clip.TryGetProperty("imageUrl", out var iu) ? iu.GetString() ?? "" : "";

                var audioData = await DownloadAudioAsync(audioUrl, ct);
                if (audioData == null) continue;

                var slug = Slugify(clipTitle);
                var filename = $"{slug}-{(clipId.Length >= 8 ? clipId[..8] : clipId)}.mp3";

                firstClipStream ??= audioData;

                results.Add(new SunoClipResult
                {
                    ClipId = clipId,
                    Title = clipTitle,
                    Tags = clipTags,
                    AudioUrl = audioUrl,
                    ImageUrl = imageUrl,
                    Filename = filename,
                    AudioData = audioData
                });
            }

            if (results.Count == 0 || firstClipStream == null)
                return new JobResult { Success = false, ErrorMessage = "No audio clips produced" };

            var resultJson = JsonSerializer.Serialize(new
            {
                clips = results.Select(r => new
                {
                    r.ClipId, r.Title, r.Tags, r.AudioUrl, r.ImageUrl, r.Filename
                })
            });

            _log($"[Suno] Generated {results.Count} clip(s): {string.Join(", ", results.Select(r => r.Title))}");
            LastClipResults = results;

            return new JobResult
            {
                Success = true,
                OutputStream = firstClipStream,
                ContentType = "audio/mpeg",
                ResultJson = resultJson
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _semaphore.Dispose();
    }

    // --- Private methods ---

    private async Task<string?> SubmitAsync(string prompt, string style, string title, bool instrumental, CancellationToken ct)
    {
        var payload = new
        {
            customMode = true,
            model = _model,
            prompt,
            style,
            title,
            instrumental,
            callBackUrl = CallbackPlaceholder
        };

        try
        {
            using var submitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            submitCts.CancelAfter(TimeSpan.FromSeconds(30));

            var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/v1/generate", payload, submitCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log($"[Suno] Submit error {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
                return null;
            }

            var data = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (data.TryGetProperty("code", out var code) && code.GetInt32() != 200)
            {
                var msg = data.TryGetProperty("msg", out var m) ? m.GetString() : "unknown";
                _log($"[Suno] API error: {msg}");
                return null;
            }

            if (data.TryGetProperty("data", out var d) && d.TryGetProperty("taskId", out var tid))
                return tid.GetString();

            _log("[Suno] Response missing taskId");
            return null;
        }
        catch (Exception ex)
        {
            _log($"[Suno] Submit exception: {ex.Message}");
            return null;
        }
    }

    private async Task<List<JsonElement>?> PollUntilDoneAsync(string taskId, Action<double>? progressCallback, CancellationToken ct)
    {
        var elapsed = 0.0;
        var seenTextSuccess = false;
        var seenComposing = false;

        while (elapsed < PollTimeoutSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);
            elapsed += PollIntervalSeconds;

            try
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollCts.CancelAfter(TimeSpan.FromSeconds(15));

                var resp = await _http.GetAsync($"{_baseUrl}/api/v1/generate/record-info?taskId={taskId}", pollCts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[Suno] Poll HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (body.TryGetProperty("code", out var code) && code.GetInt32() != 200)
                    continue;

                if (!body.TryGetProperty("data", out var taskData)) continue;
                var status = taskData.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

                if (status == "FAILED")
                {
                    var errMsg = taskData.TryGetProperty("errorMessage", out var em) ? em.GetString() : "Unknown error";
                    _log($"[Suno] Task failed: {errMsg}");
                    return null;
                }

                if (status == "SUCCESS")
                {
                    progressCallback?.Invoke(1.0);
                    if (taskData.TryGetProperty("response", out var response) &&
                        response.TryGetProperty("sunoData", out var sunoData))
                    {
                        var clips = new List<JsonElement>();
                        foreach (var clip in sunoData.EnumerateArray())
                            clips.Add(clip.Clone());
                        if (clips.Count > 0) return clips;
                    }
                    _log("[Suno] SUCCESS but sunoData empty");
                    return null;
                }

                if (status == "TEXT_SUCCESS" && !seenTextSuccess)
                {
                    seenTextSuccess = true;
                    progressCallback?.Invoke(0.33);
                    _log("[Suno] Lyrics written");
                }
                else if (seenTextSuccess && status != "TEXT_SUCCESS" && !seenComposing)
                {
                    seenComposing = true;
                    progressCallback?.Invoke(0.66);
                    _log("[Suno] Composing audio");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[Suno] Poll exception: {ex.Message}");
            }
        }

        _log($"[Suno] Timed out after {PollTimeoutSeconds}s");
        return null;
    }

    private async Task<MemoryStream?> DownloadAudioAsync(string url, CancellationToken ct)
    {
        try
        {
            using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dlCts.CancelAfter(TimeSpan.FromSeconds(60));

            var resp = await _http.GetAsync(url, dlCts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
        catch (Exception ex)
        {
            _log($"[Suno] Audio download error: {ex.Message}");
            return null;
        }
    }

    private static string Slugify(string text)
    {
        text = text.ToLowerInvariant();
        text = Regex.Replace(text, @"[^\w\s-]", "");
        text = Regex.Replace(text, @"[\s_-]+", "-");
        text = text.Trim('-');
        return text.Length > 50 ? text[..50] : (text.Length > 0 ? text : "music");
    }
}

public class SunoClipResult
{
    public required string ClipId { get; init; }
    public required string Title { get; init; }
    public required string Tags { get; init; }
    public required string AudioUrl { get; init; }
    public required string ImageUrl { get; init; }
    public required string Filename { get; init; }
    public MemoryStream? AudioData { get; init; }
}
