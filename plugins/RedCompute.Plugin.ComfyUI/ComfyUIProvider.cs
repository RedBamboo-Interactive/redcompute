using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
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
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Plugin.ComfyUI;

public class ComfyUIProvider : IPluginProvider, ICustomEndpointProvider
{
    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly string _host;
    private readonly int _port;
    // Both over the host's handler (design 2.2): built in the constructor, once the host is known.
    private readonly HttpClient _http;
    private readonly HttpClient _healthClient;
    private readonly IAcceleratorAdmissionCoordinator? _acceleratorAdmission;
    private readonly IDisposable? _acceleratorRegistration;
    private readonly IDisposable? _runtimeRegistration;
    private readonly string _acceleratorId;
    private readonly IExecutionHost _executionHost;
    private readonly string _baseUrl;
    private IHostProcess? _process;

    /// <summary>Launch seam for the ComfyUI server process; the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchServerProcess { get; set; }

    /// <summary>Launch seam for the WSL stop (<c>pkill</c> inside the distro); the default is the execution host.</summary>
    internal Func<HostSpawnSpec, IHostProcess?> LaunchStopProcess { get; set; }
    private readonly ConcurrentQueue<string> _startupErrors = new();
    private BackendStatus _status = BackendStatus.Stopped;
    private DateTime _lastHealthCheck;

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    private readonly double _pollTimeoutSeconds;

    internal TimeSpan CacheReleaseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan PromptSubmitTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan PromptCancelTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan PromptCancelPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public string Name => "ComfyUI";
    public string CapabilitySlug => _capabilitySlug;
    public string DisplayName => "ComfyUI";
    public string ProviderType => "ComfyUI";
    public bool IsProxy => false;
    public bool SupportsProgress => true;
    public bool SupportsRerun => true;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);

    private string BaseUrl => _baseUrl;

    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["workflow_entity"] = new ParameterSchema { Type = "object", Required = true, Description = "Frozen RedLeaf ComfyUI workflow entity identity" },
        ["workflow_inputs"] = new ParameterSchema { Type = "object", Required = true, Description = "Fully materialized workflow inputs" },
        ["workflow_input_sources"] = new ParameterSchema { Type = "object", Required = false, Description = "Whether each effective input was explicit, defaulted, or generated" },
        ["workflow_json"] = new ParameterSchema { Type = "string", Required = true, Description = "Frozen ComfyUI API-format node graph" },
        ["workflow_parameters"] = new ParameterSchema { Type = "array", Required = true, Description = "Parameter-to-node injection contract" },
        ["output_node"] = new ParameterSchema { Type = "string", Required = true, Description = "Node id that produces the output" },
        ["media_type"] = new ParameterSchema { Type = "string", Required = true, Description = "Output media type" }
    };

    public ReturnSchema OutputSchema => new()
    {
        ContentType = _capabilitySlug.Equals("music-gen", StringComparison.OrdinalIgnoreCase)
            ? "audio/flac"
            : "image/png",
        Streaming = false,
        MediaCategory = _capabilitySlug.Equals("music-gen", StringComparison.OrdinalIgnoreCase)
            ? "audio"
            : "image",
        OutputEndpoint = $"/{_capabilitySlug}/jobs/{{id}}/output"
    };

    public ComfyUIProvider(ProviderConfig config, string capabilitySlug, Action<string> log,
        IAcceleratorAdmissionCoordinator? acceleratorAdmission = null, IExecutionHost? host = null)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;
        _executionHost = host ?? LocalExecutionHost.Shared;
        LaunchServerProcess = spec => _executionHost.Processes.Start(spec);
        LaunchStopProcess = spec => _executionHost.Processes.Start(spec);

        _host = ProviderHelpers.GetExtra(config, "Host", "127.0.0.1");
        _port = config.BackendPort ?? 8188;
        _baseUrl = _executionHost.Network.ResolveServiceBaseUrl($"http://{_host}:{_port}");
        _http = _executionHost.Network.CreateServiceClient($"http://{_host}:{_port}", TimeSpan.FromMinutes(30));
        _healthClient = _executionHost.Network.CreateServiceClient($"http://{_host}:{_port}", TimeSpan.FromSeconds(5));
        _pollTimeoutSeconds = double.TryParse(ProviderHelpers.GetExtra(config, "_pollTimeoutSeconds", "1800"), out var t) ? t : 1800;
        _acceleratorId = ProviderHelpers.GetExtra(config, "AcceleratorId", "cuda:0");
        _acceleratorAdmission = acceleratorAdmission;
        // Node zero's runtimes list (design 2.4): this provider's ComfyUI, started and stopped as always.
        if (_executionHost.Runtimes is LocalHostRuntimes runtimes)
            _runtimeRegistration = runtimes.Register(new LocalRuntimeRegistration(
                $"comfyui@{_port}", "comfyui", "ComfyUI", capabilitySlug, _port, _acceleratorId,
                () => _status, () => StatusDetail, StartAsync, StopAsync));
        _acceleratorRegistration = acceleratorAdmission?.RegisterPreemptibleResident(
            new AcceleratorResidentRegistration(
                _acceleratorId,
                "comfyui",
                $"comfyui:{BaseUrl}",
                ReleaseCachedModelsAsync));
    }

    public void MapCustomEndpoints(WebApplication app) { }

    public IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];

    private static readonly string UnsupportedDetail =
        PlatformSupport.LocalBackendUnsupported("Launching a local ComfyUI");

    public string? StatusDetail => _status == BackendStatus.Unsupported ? UnsupportedDetail : null;

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_status == BackendStatus.Running) return true;
        _status = BackendStatus.Starting;

        // Check if already running externally
        if (await CheckHealthAsync())
        {
            _status = BackendStatus.Running;
            _log($"[ComfyUI] Already running at {BaseUrl}");
            return true;
        }

        // Launching goes through wsl.exe or a Windows Python; elsewhere only an already-running
        // ComfyUI (local or remote, reached over HTTP above) can serve.
        if (!_executionHost.Platform.CanLaunchLocalBackends)
        {
            _status = BackendStatus.Unsupported;
            _log($"[ComfyUI] {UnsupportedDetail} (no ComfyUI answered at {BaseUrl})");
            return false;
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
            while (_startupErrors.TryDequeue(out _)) { }
            EnsureConfiguredDirectories();
            _process = LaunchServerProcess(BuildSpawnSpec());
            if (_process == null)
            {
                _status = BackendStatus.Error;
                _log("[ComfyUI] Failed to start process");
                return false;
            }

            _process.OutputLine += line => { if (line != null) _log($"[ComfyUI] {line}"); };
            _process.ErrorLine += line =>
            {
                if (line == null) return;
                _startupErrors.Enqueue(line);
                while (_startupErrors.Count > 12) _startupErrors.TryDequeue(out var _discarded);
                _log($"[ComfyUI:err] {line}");
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (_process.HasExited)
                {
                    _status = BackendStatus.Error;
                    var detail = string.Join(" | ", _startupErrors);
                    _log(detail.Length == 0
                        ? $"[ComfyUI] Process exited with code {_process.ExitCode} before becoming healthy"
                        : $"[ComfyUI] Process exited with code {_process.ExitCode}: {detail}");
                    return false;
                }
                if (await CheckHealthAsync())
                {
                    _status = BackendStatus.Running;
                    _log($"[ComfyUI] Started at {BaseUrl}");
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
                    var kill = LaunchStopProcess(new HostSpawnSpec
                    {
                        FileName = "wsl.exe",
                        RawArguments = $"-d {_config.WslDistro} pkill -f \"python.*main.py\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        Containment = ContainmentMode.None,
                        OwnerLabel = "comfyui-stop",
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

        // A single ComfyUI process can back several RedCompute capabilities. A
        // non-default provider instance is not started during boot, so discover
        // the already-running shared backend instead of reporting Stopped and
        // rejecting an explicitly routed job.
        var healthy = await CheckHealthAsync();
        if (healthy)
            _status = BackendStatus.Running;
        else if (_status == BackendStatus.Running)
            _status = BackendStatus.Error;
        return _status;
    }

    public string? GetProxyTargetUrl() => null;

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        var progressCallback = request.ProgressCallback;
        var p = request.Parameters;
        var inlineJson = ProviderHelpers.GetParam<string>(p, "workflow_json");
        var prompt = ProviderHelpers.GetParam<string>(p, "prompt");
        var negative = ProviderHelpers.GetParam<string>(p, "negative") ?? "";
        var seedParam = ProviderHelpers.GetParam<long?>(p, "seed");
        if (string.IsNullOrWhiteSpace(inlineJson))
            return new JobResult { Success = false, ErrorMessage = "workflow_json is required; resolve a comfyui-workflow entity through RedLeaf" };

        var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inlineJson);
        if (workflow == null)
            return new JobResult { Success = false, ErrorMessage = "Failed to parse workflow_json" };
        workflow.Remove("_axl");

        var outputNode = ProviderHelpers.GetParam<string>(p, "output_node");
        if (string.IsNullOrWhiteSpace(outputNode))
            return new JobResult { Success = false, ErrorMessage = "output_node is required" };

        var workflowName = WorkflowSnapshotName(p.TryGetValue("workflow_entity", out var snapshot) ? snapshot : null);
        var wfDef = new WorkflowDefinition
        {
            Name = workflowName,
            OutputNode = outputNode,
            MediaType = ProviderHelpers.GetParam<string>(p, "media_type") ?? "image",
            Parameters = ParseWorkflowParameters(p.TryGetValue("workflow_parameters", out var wp) ? wp : null),
        };

        var actualSeed = seedParam ?? Random.Shared.NextInt64(0, uint.MaxValue);

        var extraParams = new Dictionary<string, object?>(p);
        extraParams.Remove("workflow_json");
        extraParams.Remove("workflow_entity");
        extraParams.Remove("workflow_inputs");
        extraParams.Remove("workflow_input_sources");
        extraParams.Remove("workflow_parameters");
        extraParams.Remove("output_node");
        extraParams.Remove("media_type");
        extraParams.Remove("prompt");
        extraParams.Remove("negative");
        extraParams.Remove("seed");

        await ResolveAssetParamsAsync(extraParams, wfDef.Parameters, ct);
        InjectParameters(workflow, wfDef.Parameters, prompt, negative, actualSeed, extraParams);

        await using var acceleratorLease = _acceleratorAdmission is null
            ? null
            : await _acceleratorAdmission.AcquireAsync(
                new AcceleratorWorkload(_acceleratorId, "comfyui", $"{_capabilitySlug} generation"), ct);

        var clientId = Guid.NewGuid().ToString();
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
                "audio" => "audio/flac",
                _ => "image/png"
            },
            ResultJson = resultMeta
        };
    }

    private static string WorkflowSnapshotName(object? value)
    {
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            return "ComfyUI workflow";
        if (element.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
            return name.GetString()!;
        if (element.TryGetProperty("slug", out var slug) && !string.IsNullOrWhiteSpace(slug.GetString()))
            return slug.GetString()!;
        return "ComfyUI workflow";
    }

    private static List<WorkflowParameter> ParseWorkflowParameters(object? value)
    {
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Array)
            return [];

        var parameters = new List<WorkflowParameter>();
        foreach (var parameter in element.EnumerateArray())
        {
            parameters.Add(new WorkflowParameter
            {
                Name = parameter.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                NodeId = parameter.TryGetProperty("node_id", out var nodeId) ? nodeId.ToString() : "",
                Field = parameter.TryGetProperty("field", out var field) ? field.GetString() ?? "" : "",
                Type = parameter.TryGetProperty("type", out var type) ? type.GetString() ?? "string" : "string",
            });
        }
        return parameters;
    }

    public async ValueTask DisposeAsync()
    {
        _acceleratorRegistration?.Dispose();
        _runtimeRegistration?.Dispose();
        await StopAsync();
        _http.Dispose();
        _healthClient.Dispose();
    }

    // --- Private methods ---

    /// <summary>The server launch: the shared recipe (design 2.4) over this provider's config.</summary>
    internal HostSpawnSpec BuildSpawnSpec() => RuntimeRecipes.ComfyUi(Recipe(), _executionHost);

    /// <summary>What the recipe is built from: config.json's entry, read exactly as before the move.</summary>
    private ComfyUiRecipe Recipe() => new()
    {
        Host = _host,
        Port = _port,
        WslDistro = _config.WslDistro,
        ServerPath = _config.ServerPath,
        VenvPath = _config.VenvPath,
        ServerArgs = ProviderHelpers.GetExtra(_config, "ServerArgs", ""),
        ModelsDirectory = ProviderHelpers.GetExtra(_config, "ModelsDirectory", ""),
        OutputDirectory = ProviderHelpers.GetExtra(_config, "OutputDirectory", ""),
        InputDirectory = ProviderHelpers.GetExtra(_config, "InputDirectory", ""),
        TempDirectory = ProviderHelpers.GetExtra(_config, "TempDirectory", ""),
        UserDirectory = ProviderHelpers.GetExtra(_config, "UserDirectory", ""),
        DatabaseUrl = ProviderHelpers.GetExtra(_config, "DatabaseUrl", ""),
    };

    internal static string ResolveNativePythonExecutable(ProviderConfig config)
        => ResolveNativePythonExecutable(config, LocalExecutionHost.Shared);

    internal static string ResolveNativePythonExecutable(ProviderConfig config, IExecutionHost host)
        => RuntimeRecipes.ComfyUiNativePython(config.VenvPath, config.ServerPath, host);

    private void EnsureConfiguredDirectories()
    {
        if (_config.WslDistro != null) return;

        foreach (var key in new[]
                 {
                     "ModelsDirectory", "OutputDirectory", "InputDirectory",
                     "TempDirectory", "UserDirectory"
                 })
        {
            var path = ProviderHelpers.GetExtra(_config, key, "").Trim();
            if (path.Length > 0) _executionHost.Files.CreateDirectory(_executionHost.Path(path));
        }
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

    internal async Task<AcceleratorReleaseResult> ReleaseCachedModelsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CacheReleaseTimeout);
        try
        {
            var queueResponse = await _http.GetAsync($"{BaseUrl}/queue", timeout.Token);
            if (!queueResponse.IsSuccessStatusCode)
                return AcceleratorReleaseResult.Refused(
                    $"ComfyUI queue check failed with HTTP {(int)queueResponse.StatusCode}");

            var queue = await queueResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            if (!TryReadQueueCount(queue, "queue_running", out var running)
                || !TryReadQueueCount(queue, "queue_pending", out var pending))
                return AcceleratorReleaseResult.Refused("ComfyUI queue response did not contain auditable running and pending queues");
            if (running > 0 || pending > 0)
                return AcceleratorReleaseResult.Refused(
                    $"ComfyUI is busy ({running} running, {pending} pending)");

            var releaseResponse = await _http.PostAsJsonAsync(
                $"{BaseUrl}/free",
                new { unload_models = true, free_memory = true },
                timeout.Token);
            if (!releaseResponse.IsSuccessStatusCode)
                return AcceleratorReleaseResult.Refused(
                    $"ComfyUI cache release failed with HTTP {(int)releaseResponse.StatusCode}");

            _log("[ComfyUI] Released cached models for accelerator transition");
            return AcceleratorReleaseResult.Released("ComfyUI acknowledged model unload and memory release");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AcceleratorReleaseResult.Refused(
                $"ComfyUI cache release timed out after {CacheReleaseTimeout.TotalSeconds:0.###} seconds");
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex) && NothingListensOnBackendPort())
        {
            // No server means no cache: the prompt queue is in memory, and models only
            // load from prompts, which need this port and RedCompute's own go through the
            // accelerator gate the caller is holding. A ComfyUI still booting is therefore
            // in the state /free would leave it in, and waiting for it would gain nothing.
            _log($"[ComfyUI] Nothing to release: no server is listening at {BaseUrl}");
            return AcceleratorReleaseResult.Released($"ComfyUI is not listening at {BaseUrl}, so it holds no cached models");
        }
        catch (Exception ex)
        {
            return AcceleratorReleaseResult.Refused($"ComfyUI cache release failed: {ex.Message}");
        }
    }

    private static bool IsConnectionRefused(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return true;
        }

        return false;
    }

    /// <summary>
    /// A refused connection alone does not prove the server is gone: Windows also refuses
    /// connections when a listener's accept backlog is full, which is how a wedged ComfyUI
    /// that may still hold the GPU can look. Only the local listener table can tell
    /// them apart, so a remote or WSL backend is never reported as absent.
    /// </summary>
    private bool NothingListensOnBackendPort()
    {
        var isLocal = string.Equals(_host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(_host, out var address) && IPAddress.IsLoopback(address));
        if (!isLocal || _config.WslDistro != null)
            return false;

        return !_executionHost.Network.IsListening(_port);
    }

    private static bool TryReadQueueCount(JsonElement queue, string propertyName, out int count)
    {
        count = 0;
        if (queue.ValueKind != JsonValueKind.Object
            || !queue.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return false;
        count = value.GetArrayLength();
        return true;
    }

    private void InjectParameters(
        Dictionary<string, JsonElement> workflow,
        List<WorkflowParameter> paramDefs,
        string? prompt, string negative, long seed,
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
            else
                value = null;

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

    private async Task<string?> QueuePromptAsync(Dictionary<string, JsonElement> workflow, string clientId)
    {
        var payload = new { prompt = workflow, client_id = clientId };
        // Deliberately not the job's token: a cancellation that lands while /prompt is in flight
        // must still learn the prompt id, or the prompt would be queued with nobody to cancel it.
        using var submit = new CancellationTokenSource(PromptSubmitTimeout);
        var ct = submit.Token;
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

    /// <summary>
    /// Takes one of our prompts back out of ComfyUI: a running prompt is interrupted, a pending one
    /// is dequeued, and the call waits until ComfyUI no longer lists it so the accelerator lease is
    /// only released once the GPU is actually free.
    ///
    /// Never stops a prompt that is not <paramref name="promptId"/>. Builds with the per-id cancel
    /// (POST /api/jobs/{id}/cancel) check and interrupt atomically. Older builds fall back to
    /// /queue delete and an /interrupt that is only sent after GET /queue shows this exact prompt
    /// running, and it carries the prompt_id so builds that support targeted interrupts check again.
    /// </summary>
    internal async Task<PromptCancelOutcome> CancelPromptAsync(string promptId, string reason)
    {
        using var budget = new CancellationTokenSource(PromptCancelTimeout);
        var ct = budget.Token;
        var method = "none";
        try
        {
            if (await TryCancelByIdAsync(promptId, ct))
            {
                method = "jobs-cancel";
            }
            else
            {
                method = "queue-delete";
                using (var delete = await _http.PostAsJsonAsync($"{BaseUrl}/queue",
                           new { delete = new[] { promptId } }, ct))
                {
                    if (!delete.IsSuccessStatusCode)
                        _log($"[ComfyUI] /queue delete for {promptId} answered HTTP {(int)delete.StatusCode}");
                }

                var snapshot = await ReadQueueAsync(ct);
                if (snapshot is { } queue && queue.Running.Contains(promptId))
                {
                    method = "interrupt";
                    using var interrupt = await _http.PostAsJsonAsync($"{BaseUrl}/interrupt",
                        new { prompt_id = promptId }, ct);
                    if (!interrupt.IsSuccessStatusCode)
                        _log($"[ComfyUI] /interrupt for {promptId} answered HTTP {(int)interrupt.StatusCode}");
                }
                else if (snapshot is { } other && other.Running.Count > 0)
                {
                    _log($"[ComfyUI] Not interrupting: the running prompt is {string.Join(", ", other.Running)}, not {promptId}");
                }
            }

            while (true)
            {
                var queue = await ReadQueueAsync(ct);
                if (queue is null)
                {
                    _log($"[ComfyUI] Cancelled prompt {promptId} ({reason}, {method}); queue state unreadable");
                    return new PromptCancelOutcome(method, Settled: false);
                }
                if (!queue.Value.Running.Contains(promptId) && !queue.Value.Pending.Contains(promptId))
                {
                    _log($"[ComfyUI] Cancelled prompt {promptId} ({reason}, {method}); ComfyUI released it");
                    return new PromptCancelOutcome(method, Settled: true);
                }
                await Task.Delay(PromptCancelPollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _log($"[ComfyUI] Prompt {promptId} was still listed {PromptCancelTimeout.TotalSeconds:0.#}s after cancelling it ({reason}, {method})");
            return new PromptCancelOutcome(method, Settled: false);
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Cancelling prompt {promptId} failed ({reason}, {method}): {ex.Message}");
            return new PromptCancelOutcome(method, Settled: false);
        }
    }

    private async Task<bool> TryCancelByIdAsync(string promptId, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(
                $"{BaseUrl}/api/jobs/{Uri.EscapeDataString(promptId)}/cancel", content, ct);
            if (!response.IsSuccessStatusCode) return false;
            // Only a JSON answer with the cancelled flag is the real route; anything else (an
            // older build's catch-all, a proxy page) falls back to the guarded path.
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("cancelled", out var cancelled)
                && cancelled.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<QueueSnapshot?> ReadQueueAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{BaseUrl}/queue", ct);
        if (!response.IsSuccessStatusCode) return null;
        var queue = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!TryReadQueueIds(queue, "queue_running", out var running)
            || !TryReadQueueIds(queue, "queue_pending", out var pending))
            return null;
        return new QueueSnapshot(running, pending);
    }

    /// <summary>ComfyUI queue items are arrays: [number, prompt_id, prompt, extra_data, outputs].</summary>
    private static bool TryReadQueueIds(JsonElement queue, string propertyName, out HashSet<string> ids)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        if (queue.ValueKind != JsonValueKind.Object
            || !queue.TryGetProperty(propertyName, out var items)
            || items.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 1
                && item[1].ValueKind == JsonValueKind.String)
                ids.Add(item[1].GetString()!);
        }
        return true;
    }

    private readonly record struct QueueSnapshot(HashSet<string> Running, HashSet<string> Pending);

    internal readonly record struct PromptCancelOutcome(string Method, bool Settled);

    private async Task<(MemoryStream? stream, string? contentType, string? error)?> RunWithWebSocketAsync(
        Dictionary<string, JsonElement> workflow, string outputNode, string clientId, Action<double>? progressCallback, CancellationToken ct)
    {
        // The progress socket goes through the host like every other request (design 2.2): the Uri is
        // ComfyUI's as the host sees it, never a resolved one.
        WebSocket connected;
        try
        {
            var wsUri = new Uri($"ws://{_host}:{_port}/ws?clientId={clientId}");
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            connected = await _executionHost.Network.ConnectWebSocketAsync(wsUri, connectCts.Token);
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] WebSocket connect failed: {ex.Message}");
            return (null, null, $"WebSocket connection failed: {ex.Message}");
        }
        using var ws = connected;

        ct.ThrowIfCancellationRequested();
        var promptId = await QueuePromptAsync(workflow, clientId);
        if (promptId == null)
            return (null, null, "Failed to queue prompt");

        _log($"[ComfyUI] Queued prompt {promptId}");

        // From here the prompt exists in ComfyUI's queue. Any exit other than ComfyUI reporting
        // it finished (cancellation, timeout, a dropped socket, an exception) must take it back
        // out, or it keeps the GPU after RedCompute has released the accelerator lease.
        var promptSettled = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            return await FollowPromptAsync(ws, promptId, workflow.Count, outputNode,
                progressCallback, () => promptSettled = true, ct);
        }
        finally
        {
            if (!promptSettled)
            {
                var reason = ct.IsCancellationRequested ? "job cancelled" : "run ended before the prompt finished";
                await CancelPromptAsync(promptId, reason);
            }
        }
    }

    private async Task<(MemoryStream? stream, string? contentType, string? error)?> FollowPromptAsync(
        WebSocket ws, string promptId, int workflowNodeCount, string outputNode,
        Action<double>? progressCallback, Action markSettled, CancellationToken ct)
    {
        var totalNodes = Math.Max(workflowNodeCount, 1);
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
                        markSettled();
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
                    markSettled();
                    progressCallback?.Invoke(1.0);
                    goto fetchOutput;

                case "execution_error":
                    markSettled();
                    var errorMsg = data.TryGetProperty("exception_message", out var exMsg)
                        ? exMsg.GetString() ?? "Unknown error"
                        : "ComfyUI execution error";
                    _log($"[ComfyUI] Execution error: {errorMsg}");
                    return (null, null, errorMsg);

                case "execution_interrupted":
                    // Our own cancel ends with this message too, but then the token is already
                    // cancelled and the caller sees the cancellation, not this failure.
                    markSettled();
                    ct.ThrowIfCancellationRequested();
                    _log($"[ComfyUI] Prompt {promptId} was interrupted outside RedCompute");
                    return (null, null, "ComfyUI interrupted the prompt before it finished");
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
            else if (nodeOutput.TryGetProperty("audio", out mediaList))
                fallbackContentType = "audio/flac";
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
                ".flac" => "audio/flac",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".opus" => "audio/opus",
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

    private async Task ResolveAssetParamsAsync(
        Dictionary<string, object?> extras,
        List<WorkflowParameter> paramDefs,
        CancellationToken ct)
    {
        foreach (var param in paramDefs)
        {
            if (!extras.TryGetValue(param.Name, out var value) || value == null) continue;

            string? assetUrl = null;

            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty("assetUrl", out var urlProp))
                    assetUrl = NormalizeRedLeafAssetReference(urlProp.GetString());
                else if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty("assetId", out var idProp))
                    assetUrl = NormalizeRedLeafAssetReference(idProp.GetString());
                else if (je.ValueKind == JsonValueKind.String && param.Type is "image" or "video" or "audio")
                    assetUrl = NormalizeRedLeafAssetReference(je.GetString());
            }

            if (string.IsNullOrEmpty(assetUrl)) continue;

            if (assetUrl.StartsWith("/"))
                assetUrl = $"http://127.0.0.1:18804{assetUrl}";

            var uploadedName = await UploadFileToComfyAsync(assetUrl, ct);
            if (uploadedName != null)
                extras[param.Name] = uploadedName;
            else
                _log($"[ComfyUI] Warning: failed to resolve asset for param '{param.Name}'");
        }
    }

    internal static string? NormalizeRedLeafAssetReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
        if (value.StartsWith('/')) return $"http://127.0.0.1:18804{value}";
        return $"http://127.0.0.1:18804/api/assets/{value}";
    }

    private async Task<string?> UploadFileToComfyAsync(string fileUrl, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(fileUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[ComfyUI] Failed to download asset: HTTP {(int)resp.StatusCode} from {fileUrl}");
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            var ext = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "video/mp4" => ".mp4",
                "video/webm" => ".webm",
                "audio/wav" => ".wav",
                "audio/mpeg" => ".mp3",
                _ => Path.GetExtension(new Uri(fileUrl).AbsolutePath) is { Length: > 0 } uriExt ? uriExt : ".bin"
            };

            var filename = $"rl_{Guid.NewGuid():N}{ext}";
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "image", filename);
            form.Add(new StringContent("true"), "overwrite");

            var uploadResp = await _http.PostAsync($"{BaseUrl}/upload/image", form, ct);
            if (!uploadResp.IsSuccessStatusCode)
            {
                var body = await uploadResp.Content.ReadAsStringAsync(ct);
                _log($"[ComfyUI] Asset upload error {(int)uploadResp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
                return null;
            }

            var result = await uploadResp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var name = result.GetProperty("name").GetString();
            _log($"[ComfyUI] Uploaded asset '{name}' ({bytes.Length / 1024}KB, {contentType})");
            return name;
        }
        catch (Exception ex)
        {
            _log($"[ComfyUI] Asset upload error: {ex.Message}");
            return null;
        }
    }

}
