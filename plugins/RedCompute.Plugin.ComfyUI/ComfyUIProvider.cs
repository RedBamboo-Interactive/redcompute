using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ComfyUI;

public class ComfyUIProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly string _host;
    private readonly int _port;
    private readonly string _defaultWorkflow;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _process;
    private BackendStatus _status = BackendStatus.Stopped;
    private DateTime _lastHealthCheck;

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    private readonly double _pollTimeoutSeconds;

    public string Name => "ComfyUI";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "ComfyUI";
    public string ProviderType => "ComfyUI";
    public bool IsProxy => false;
    public bool SupportsProgress => true;
    public bool SupportsRerun => true;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);
    public WorkflowLoader WorkflowLoader { get; }
    public Action<double>? ProgressCallback { get; set; }

    private string BaseUrl => $"http://{_host}:{_port}";

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["prompt"] = new ParameterSchema { Type = "string", Required = true, Description = "Text prompt for image generation" },
        ["workflow"] = new ParameterSchema { Type = "string", Required = false, Default = "z_turbo", Description = "Workflow name to use" },
        ["negative"] = new ParameterSchema { Type = "string", Required = false, Description = "Negative prompt" },
        ["seed"] = new ParameterSchema { Type = "integer", Required = false, Description = "Random seed for reproducibility" },
        ["width"] = new ParameterSchema { Type = "integer", Required = false, Description = "Output image width" },
        ["height"] = new ParameterSchema { Type = "integer", Required = false, Description = "Output image height" },
        ["image_url"] = new ParameterSchema { Type = "string", Required = false, Description = "Source image URL for img2img workflows" }
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = "image/png",
        Streaming = false,
        MediaCategory = "image",
        OutputEndpoint = "/image-gen/jobs/{id}/output"
    };

    public ComfyUIProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;

        _host = ProviderHelpers.GetExtra(config, "Host", "127.0.0.1");
        _port = config.BackendPort ?? 8188;
        _defaultWorkflow = ProviderHelpers.GetExtra(config, "DefaultWorkflow", "z_turbo");

        var workflowsDir = ProviderHelpers.GetExtra(config, "WorkflowsDir", "workflows");
        _pollTimeoutSeconds = double.TryParse(ProviderHelpers.GetExtra(config, "_pollTimeoutSeconds", "1800"), out var t) ? t : 1800;
        WorkflowLoader = new WorkflowLoader(workflowsDir, log);
    }

    public void SetProgressCallback(Action<double>? callback)
    {
        ProgressCallback = callback;
    }

    public void MapCustomEndpoints(WebApplication app)
    {
        app.MapGet($"/{CapabilitySlug}/workflows", () =>
            Results.Json(WorkflowLoader.Workflows));

        app.MapGet($"/{CapabilitySlug}/workflows/{{name}}", (string name) =>
        {
            var wf = WorkflowLoader.Get(name);
            return wf != null ? Results.Json(wf) : Results.NotFound(new { error = "not_found", message = $"Workflow '{name}' not found" });
        });
    }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() =>
    [
        new EndpointManifest
        {
            Method = "GET",
            Path = $"/{CapabilitySlug}/workflows",
            Description = "List available ComfyUI workflows"
        },
        new EndpointManifest
        {
            Method = "GET",
            Path = $"/{CapabilitySlug}/workflows/{{name}}",
            Description = "Get details of a specific ComfyUI workflow",
            Parameters = new Dictionary<string, ParameterSchema>
            {
                ["name"] = new ParameterSchema { Type = "string", Required = true, Description = "Workflow name" }
            }
        }
    ];

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_status == BackendStatus.Running) return true;
        _status = BackendStatus.Starting;

        // Check if already running externally
        if (await CheckHealthAsync())
        {
            WorkflowLoader.Reload();
            _status = BackendStatus.Running;
            _log($"[ComfyUI] Already running at {BaseUrl} ({WorkflowLoader.Workflows.Count} workflows)");
            return true;
        }

        // Launch process
        if (_config.ServerPath == null)
        {
            _status = BackendStatus.Error;
            _log("[ComfyUI] ServerPath not configured — cannot launch ComfyUI");
            return false;
        }

        try
        {
            var startInfo = BuildStartInfo();
            _process = Process.Start(startInfo);
            if (_process == null)
            {
                _status = BackendStatus.Error;
                _log("[ComfyUI] Failed to start process");
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) _log($"[ComfyUI] {e.Data}"); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log($"[ComfyUI:err] {e.Data}"); };

            var timeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (await CheckHealthAsync())
                {
                    WorkflowLoader.Reload();
                    _status = BackendStatus.Running;
                    _log($"[ComfyUI] Started at {BaseUrl} ({WorkflowLoader.Workflows.Count} workflows)");
                    return true;
                }
                await Task.Delay(2000, ct);
            }

            _status = BackendStatus.Error;
            _log($"[ComfyUI] Failed to become healthy within {_config.StartupTimeoutSeconds}s");
            return false;
        }
        catch (Exception ex)
        {
            _status = BackendStatus.Error;
            _log($"[ComfyUI] Start failed: {ex.Message}");
            return false;
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _status = BackendStatus.Draining;

        if (_process != null && !_process.HasExited)
        {
            try
            {
                if (_config.WslDistro != null)
                {
                    var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = $"-d {_config.WslDistro} pkill -f \"python.*main.py\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    kill?.WaitForExit(5000);
                }
                else
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _process.Dispose();
            _process = null;
        }

        _status = BackendStatus.Stopped;
        _log("[ComfyUI] Stopped");
        return Task.CompletedTask;
    }

    public async Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastHealthCheck < TimeSpan.FromSeconds(5))
            return _status;

        _lastHealthCheck = DateTime.UtcNow;

        if (_status == BackendStatus.Running && !await CheckHealthAsync())
            _status = BackendStatus.Error;
        else if (_status == BackendStatus.Error && await CheckHealthAsync())
        {
            WorkflowLoader.Reload();
            _status = BackendStatus.Running;
        }
        return _status;
    }

    public string? GetProxyTargetUrl() => null;

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        var progressCallback = ProgressCallback;
        var p = request.Parameters;
        var workflowName = ProviderHelpers.GetParam<string>(p, "workflow") ?? _defaultWorkflow;
        var prompt = ProviderHelpers.GetParam<string>(p, "prompt");
        var negative = ProviderHelpers.GetParam<string>(p, "negative") ?? "";
        var seedParam = ProviderHelpers.GetParam<long?>(p, "seed");

        if (string.IsNullOrWhiteSpace(prompt))
            return new JobResult { Success = false, ErrorMessage = "prompt is required" };

        var wfDef = WorkflowLoader.Get(workflowName);
        if (wfDef == null)
        {
            var available = string.Join(", ", WorkflowLoader.Workflows.Keys);
            return new JobResult { Success = false, ErrorMessage = $"Unknown workflow '{workflowName}'. Available: {available}" };
        }

        var workflow = WorkflowLoader.LoadWorkflowJson(workflowName);
        if (workflow == null)
            return new JobResult { Success = false, ErrorMessage = $"Failed to load workflow JSON for '{workflowName}'" };

        var actualSeed = seedParam ?? Random.Shared.NextInt64(0, uint.MaxValue);

        var extraParams = new Dictionary<string, object?>(p);
        extraParams.Remove("workflow");
        extraParams.Remove("prompt");
        extraParams.Remove("negative");
        extraParams.Remove("seed");

        // Handle image upload for img2img/video workflows
        var imageUrl = ProviderHelpers.GetParam<string>(extraParams, "image_url");
        if (!string.IsNullOrEmpty(imageUrl))
        {
            var uploadedName = await UploadImageAsync(imageUrl, ct);
            if (uploadedName == null)
                return new JobResult { Success = false, ErrorMessage = "Failed to upload source image to ComfyUI" };
            extraParams.Remove("image_url");
            extraParams["image"] = uploadedName;
        }

        InjectParameters(workflow, wfDef.Parameters, prompt, negative, actualSeed, extraParams);

        var clientId = Guid.NewGuid().ToString();
        var outputNode = wfDef.OutputNode;

        var result = await RunWithWebSocketAsync(workflow, outputNode, clientId, progressCallback, ct);
        if (result == null)
            return new JobResult { Success = false, ErrorMessage = "Generation failed or timed out" };

        var (stream, contentType, error) = result.Value;
        if (stream == null)
            return new JobResult { Success = false, ErrorMessage = error ?? "No output produced" };

        var resultMeta = JsonSerializer.Serialize(new
        {
            seed = actualSeed,
            workflow = workflowName,
            mediaType = wfDef.MediaType
        });

        return new JobResult
        {
            Success = true,
            OutputStream = stream,
            ContentType = contentType ?? wfDef.MediaType switch
            {
                "video" => "video/mp4",
                "animation" => "image/webp",
                _ => "image/png"
            },
            ResultJson = resultMeta
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _healthClient.Dispose();
    }

    // --- Private methods ---

    private ProcessStartInfo BuildStartInfo()
    {
        var listenArgs = $"--listen {_host} --port {_port}";
        var serverArgs = ProviderHelpers.GetExtra(_config, "ServerArgs", "");
        if (!string.IsNullOrWhiteSpace(serverArgs))
            listenArgs += $" {serverArgs}";

        if (_config.WslDistro != null)
        {
            var venvActivate = _config.VenvPath != null ? $"source {_config.VenvPath}/bin/activate && " : "";
            var serverPath = ProviderHelpers.ConvertToWslPath(_config.ServerPath ?? ".");
            var command = $"{venvActivate}cd {serverPath} && python main.py {listenArgs}";

            return new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = $"-d {_config.WslDistro} bash -c \"{command}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        // Native Windows — use venv python if configured, otherwise system python
        var pythonExe = _config.VenvPath != null
            ? Path.Combine(_config.VenvPath, "Scripts", "python.exe")
            : "python";

        return new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"main.py {listenArgs}",
            WorkingDirectory = _config.ServerPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private async Task<bool> CheckHealthAsync()
    {
        try
        {
            var resp = await _healthClient.GetAsync($"{BaseUrl}/system_stats");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void InjectParameters(
        Dictionary<string, JsonElement> workflow,
        List<WorkflowParameter> paramDefs,
        string prompt, string negative, long seed,
        Dictionary<string, object?> extras)
    {
        var values = new Dictionary<string, object?>(extras, StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = prompt,
            ["negative"] = negative,
            ["seed"] = seed
        };

        foreach (var param in paramDefs)
        {
            object? value;
            if (values.TryGetValue(param.Name, out var v))
                value = v;
            else if (param.Default is string s && s == "random")
                value = Random.Shared.NextInt64(0, uint.MaxValue);
            else
                value = param.Default;

            if (value == null) continue;
            if (!workflow.TryGetValue(param.NodeId, out var node)) continue;

            var nodeDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.GetRawText());
            if (nodeDict == null || !nodeDict.TryGetValue("inputs", out var inputs)) continue;

            var inputsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputs.GetRawText());
            if (inputsDict == null) continue;

            inputsDict[param.Field] = JsonSerializer.SerializeToElement(ConvertValue(value));
            nodeDict["inputs"] = JsonSerializer.SerializeToElement(inputsDict);
            workflow[param.NodeId] = JsonSerializer.SerializeToElement(nodeDict);
        }
    }

    private static object ConvertValue(object value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString()!,
                JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => je.ToString()
            };
        }
        return value;
    }

    private async Task<string?> QueuePromptAsync(Dictionary<string, JsonElement> workflow, string clientId, CancellationToken ct)
    {
        var payload = new { prompt = workflow, client_id = clientId };
        try
        {
            var resp = await _http.PostAsJsonAsync($"{BaseUrl}/prompt", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log($"[ComfyUI] /prompt error {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
                return null;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.GetProperty("prompt_id").GetString();
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Queue error: {ex.Message}");
            return null;
        }
    }

    private async Task<(MemoryStream? stream, string? contentType, string? error)?> RunWithWebSocketAsync(
        Dictionary<string, JsonElement> workflow, string outputNode, string clientId, Action<double>? progressCallback, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        try
        {
            var wsUri = new Uri($"ws://{_host}:{_port}/ws?clientId={clientId}");
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(wsUri, connectCts.Token);
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] WebSocket connect failed: {ex.Message}");
            return (null, null, $"WebSocket connection failed: {ex.Message}");
        }

        var promptId = await QueuePromptAsync(workflow, clientId, ct);
        if (promptId == null)
            return (null, null, "Failed to queue prompt");

        _log($"[ComfyUI] Queued prompt {promptId}");

        var totalNodes = Math.Max(workflow.Count, 1);
        var nodesCached = 0;
        var nonCachedTotal = totalNodes;
        var executingSeen = 0;
        var currentNodeFrac = 0.0;

        double GlobalFrac()
        {
            var nodesFinished = Math.Max(0, executingSeen - 1);
            return Math.Min((nodesFinished + currentNodeFrac) / nonCachedTotal, 0.95);
        }

        var executionStarted = false;
        var deadline = DateTime.UtcNow.AddHours(24);
        var buffer = new byte[8192];

        while (ws.State == WebSocketState.Open && DateTime.UtcNow < deadline)
        {
            using var msgStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recvCts.CancelAfter(TimeSpan.FromHours(24));
                result = await ws.ReceiveAsync(buffer, recvCts.Token);
                msgStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var msgJson = Encoding.UTF8.GetString(msgStream.ToArray());
            using var msgDoc = JsonDocument.Parse(msgJson);
            var msg = msgDoc.RootElement;

            if (!msg.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();
            if (!msg.TryGetProperty("data", out var data)) continue;

            // Filter to our prompt
            if (data.TryGetProperty("prompt_id", out var pidProp) && pidProp.GetString() != promptId)
                continue;

            switch (type)
            {
                case "execution_cached":
                    if (data.TryGetProperty("nodes", out var cachedNodes))
                    {
                        nodesCached = cachedNodes.GetArrayLength();
                        nonCachedTotal = Math.Max(totalNodes - nodesCached, 1);
                    }
                    break;

                case "executing":
                    if (!executionStarted)
                    {
                        executionStarted = true;
                        deadline = DateTime.UtcNow.AddSeconds(_pollTimeoutSeconds);
                        _log($"[ComfyUI] Execution started for {promptId}, timeout in {_pollTimeoutSeconds}s");
                    }
                    if (data.TryGetProperty("node", out var nodeProp) && nodeProp.ValueKind == JsonValueKind.Null)
                    {
                        progressCallback?.Invoke(1.0);
                        goto fetchOutput;
                    }
                    executingSeen++;
                    currentNodeFrac = 0.0;
                    progressCallback?.Invoke(GlobalFrac());
                    break;

                case "progress":
                    if (data.TryGetProperty("value", out var val) && data.TryGetProperty("max", out var max))
                    {
                        currentNodeFrac = (double)val.GetInt32() / Math.Max(max.GetInt32(), 1);
                        progressCallback?.Invoke(GlobalFrac());
                    }
                    break;

                case "execution_success":
                    progressCallback?.Invoke(1.0);
                    goto fetchOutput;

                case "execution_error":
                    var errorMsg = data.TryGetProperty("exception_message", out var exMsg)
                        ? exMsg.GetString() ?? "Unknown error"
                        : "ComfyUI execution error";
                    _log($"[ComfyUI] Execution error: {errorMsg}");
                    return (null, null, errorMsg);
            }
        }

        if (DateTime.UtcNow >= deadline)
        {
            _log("[ComfyUI] Generation timed out");
            return (null, null, $"Generation timed out after {_pollTimeoutSeconds}s");
        }

        return (null, null, "WebSocket closed unexpectedly");

    fetchOutput:
        return await FetchOutputAsync(promptId, outputNode, ct);
    }

    private async Task<(MemoryStream? stream, string? contentType, string? error)> FetchOutputAsync(
        string promptId, string outputNode, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/history/{promptId}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[ComfyUI] /history error {(int)resp.StatusCode}");
                return (null, null, "Failed to fetch history");
            }

            var history = await resp.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct);
            if (history == null || !history.TryGetValue(promptId, out var entry))
                return (null, null, "Prompt not found in history");

            using var entryDoc = JsonDocument.Parse(entry.GetRawText());
            var outputs = entryDoc.RootElement.GetProperty("outputs");

            if (!outputs.TryGetProperty(outputNode, out var nodeOutput))
            {
                _log($"[ComfyUI] Output node {outputNode} not found in history");
                return (null, null, $"Output node {outputNode} not found");
            }

            // SaveImage → "images", SaveVideo → "gifs"
            JsonElement mediaList;
            string fallbackContentType;
            if (nodeOutput.TryGetProperty("images", out mediaList))
                fallbackContentType = "image/png";
            else if (nodeOutput.TryGetProperty("gifs", out mediaList))
                fallbackContentType = "video/mp4";
            else
                return (null, null, "No media in output node");

            if (mediaList.GetArrayLength() == 0)
                return (null, null, "Empty media list");

            var first = mediaList[0];
            var filename = first.GetProperty("filename").GetString()!;
            var subfolder = first.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
            var fileType = first.TryGetProperty("type", out var ft) ? ft.GetString() ?? "output" : "output";

            var contentType = Path.GetExtension(filename).ToLowerInvariant() switch
            {
                ".webp" => "image/webp",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                _ => fallbackContentType
            };

            var stream = await DownloadFileAsync(filename, subfolder, fileType, ct);
            if (stream == null)
                return (null, null, "Failed to download output file");

            return (stream, contentType, null);
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Fetch output error: {ex.Message}");
            return (null, null, ex.Message);
        }
    }

    private async Task<MemoryStream?> DownloadFileAsync(string filename, string subfolder, string type, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Download error: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> UploadImageAsync(string imageUrl, CancellationToken ct)
    {
        try
        {
            var imageResp = await _http.GetAsync(imageUrl, ct);
            if (!imageResp.IsSuccessStatusCode)
            {
                _log($"[ComfyUI] Failed to download source image: HTTP {(int)imageResp.StatusCode}");
                return null;
            }
            var imageBytes = await imageResp.Content.ReadAsByteArrayAsync(ct);
            var contentType = imageResp.Content.Headers.ContentType?.MediaType ?? "image/png";
            var ext = contentType.Contains("jpeg") || contentType.Contains("jpg") ? ".jpg" : ".png";

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(imageBytes), "image", $"upload{ext}");
            form.Add(new StringContent("true"), "overwrite");

            var uploadResp = await _http.PostAsync($"{BaseUrl}/upload/image", form, ct);
            if (!uploadResp.IsSuccessStatusCode)
            {
                var body = await uploadResp.Content.ReadAsStringAsync(ct);
                _log($"[ComfyUI] Upload error {(int)uploadResp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
                return null;
            }

            var result = await uploadResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            return result.GetProperty("name").GetString();
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Image upload error: {ex.Message}");
            return null;
        }
    }
}
