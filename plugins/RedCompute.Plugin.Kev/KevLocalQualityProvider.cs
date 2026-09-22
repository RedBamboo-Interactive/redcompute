using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Configuration;
using RedCompute.Core.Decisions;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Kev;

public sealed class KevLocalQualityProvider :
    IPluginProvider,
    IJobExtendedProvider,
    IProviderStatusDetails,
    IProviderSelfValidator
{
    private const int MaxSidecarResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProviderConfig _config;
    private readonly string _capabilitySlug;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IKevProcessController _processController;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _requests = new();
    private readonly Uri? _endpoint;
    private readonly string? _configurationError;
    private readonly string _model;
    private readonly string _modelRevision;
    private readonly string _calibrationRevision;
    private readonly string? _baseModelRevision;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _mergeLora;
    private readonly string _dtype;
    private readonly string _kernelBackend;

    private KevOwnedProcess? _ownedProcess;
    private volatile BackendStatus _status = BackendStatus.Stopped;
    private volatile bool _administrativelyStopped;
    private volatile bool _inferenceWarmed;
    private volatile bool _endpointReachable;
    private string? _loadedRun;
    private string? _loadedDtype;
    private string? _loadedDevice;
    private double? _loadedTemperature;
    private string? _lastErrorCode;
    private string? _lastError;

    public KevLocalQualityProvider(
        ProviderConfig config,
        string capabilitySlug,
        Action<string> log,
        HttpClient? httpClient = null,
        IKevProcessController? processController = null)
    {
        _config = config;
        _capabilitySlug = capabilitySlug;
        _log = log;
        _processController = processController ?? new KevProcessController();
        _http = httpClient ?? new HttpClient();
        _ownsHttp = httpClient is null;
        _http.Timeout = Timeout.InfiniteTimeSpan;

        _model = string.IsNullOrWhiteSpace(config.Model) ? "kev-latest" : config.Model.Trim();
        _modelRevision = string.IsNullOrWhiteSpace(config.ModelRevision)
            ? "unconfigured" : config.ModelRevision.Trim();
        _calibrationRevision = string.IsNullOrWhiteSpace(config.CalibrationRevision)
            ? "checkpoint-default" : config.CalibrationRevision.Trim();
        _baseModelRevision = ExtraString(config, "BaseModelRevision")?.Trim();
        _requestTimeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds ?? 120, 1, 600));
        _mergeLora = ExtraBool(config, "MergeLora", false);
        _dtype = ExtraString(config, "Dtype") ?? "bfloat16";
        _kernelBackend = ExtraString(config, "KernelBackend") ?? "flash-qla-sm120";

        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !IsPrivateLocal(endpoint))
        {
            _configurationError =
                "endpoint must be an absolute private-local HTTP(S) URI";
        }
        else if (!IsImmutableRevision(config.ModelRevision))
        {
            _configurationError =
                "modelRevision must be a lowercase 40-character hexadecimal commit SHA";
        }
        else if (!IsImmutableRevision(_baseModelRevision))
        {
            _configurationError =
                "baseModelRevision must be a lowercase 40-character hexadecimal commit SHA";
        }
        else if (config.TimeoutSeconds is <= 0 or > 600)
        {
            _configurationError = "timeoutSeconds must be in the range 1..600";
        }
        else
        {
            _endpoint = new Uri(endpoint.ToString().TrimEnd('/') + "/");
        }
    }

    public static string ProviderTypeName => "KevLocalQuality";
    public string ProviderId => "kev-local-quality";
    public string Name => "kev-local-quality";
    public string DisplayName => "Kev Local Quality";
    public string ProviderType => ProviderTypeName;
    public string CapabilitySlug => _capabilitySlug;
    public Dictionary<string, ParameterSchema> InputParameters => new()
    {
        ["state"] = new ParameterSchema
        {
            Type = "object",
            Required = true,
            Description = "String, object, or array state evaluated by every question",
        },
        ["questions"] = new ParameterSchema
        {
            Type = "object",
            Required = true,
            Description = "Map of discriminated choice, score, and noul questions",
        },
    };
    public ReturnSchema OutputSchema => new()
    {
        ContentType = "application/json",
        Streaming = false,
        OutputEndpoint = "/decision/jobs/{id}/output",
    };
    public object RequestSchema => DecisionContract.RequestSchema;
    public object ResponseSchema => DecisionContract.ResponseSchema;
    public string ContractVersion => DecisionContract.Version;
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => true;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(5);
    public int? ProcessId => _ownedProcess?.HostProcessId;

    public Dictionary<string, string> ValidateParameters(
        Dictionary<string, object?> parameters)
        => DecisionContract.ValidateOnly(parameters);

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        _administrativelyStopped = false;
        _lastError = null;
        _lastErrorCode = null;
        if (_configurationError is not null)
        {
            SetError("invalid_provider_configuration", _configurationError);
            return false;
        }

        _status = BackendStatus.Starting;
        if (await ProbeAsync(ct))
        {
            _status = BackendStatus.Running;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_config.LaunchCommand))
        {
            _status = BackendStatus.Stopped;
            _lastErrorCode = "sidecar_unavailable";
            _lastError = "Kev endpoint is not reachable and no launchCommand is configured";
            return false;
        }

        try
        {
            ApplyLaunchDefaults();
            _ownedProcess = await _processController.StartAsync(_config, _log, ct);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(
                Math.Clamp(_config.StartupTimeoutSeconds, 1, 900));
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(ct))
                {
                    _status = BackendStatus.Running;
                    _log($"[Kev] Process-ready at {_endpoint}; inference warm-up is still pending");
                    return true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            SetError("sidecar_start_timeout", "Kev did not become process-ready before the startup timeout");
            await StopOwnedProcessAsync(CancellationToken.None);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopOwnedProcessAsync(CancellationToken.None);
            _status = BackendStatus.Stopped;
            throw;
        }
        catch (Exception ex)
        {
            var resourceBusy = LooksLikeResourceBusy(ex.Message);
            SetError(resourceBusy ? "resource_busy" : "sidecar_start_failed",
                resourceBusy
                    ? "Kev could not start within available GPU memory; release a conflicting model and retry"
                    : ex.Message);
            await StopOwnedProcessAsync(CancellationToken.None);
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _administrativelyStopped = true;
        _status = BackendStatus.Draining;
        foreach (var request in _requests.Values)
            request.Cancel();
        await StopOwnedProcessAsync(ct);
        _endpointReachable = false;
        _inferenceWarmed = false;
        _status = BackendStatus.Stopped;
    }

    public async Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (_administrativelyStopped)
            return BackendStatus.Stopped;
        if (_configurationError is not null)
            return BackendStatus.Error;

        var reachable = await ProbeAsync(ct);
        _status = reachable ? BackendStatus.Running :
            _ownedProcess is not null ? BackendStatus.Error : BackendStatus.Stopped;
        return _status;
    }

    public string? GetProxyTargetUrl() => null;

    public Task<JobResult?> ExecuteAsync(
        JobRequest request,
        CancellationToken ct = default)
        => ExecuteDecisionAsync(request, ct, registerRequest: true);

    private async Task<JobResult?> ExecuteDecisionAsync(
        JobRequest request,
        CancellationToken ct,
        bool registerRequest)
    {
        var validation = DecisionContract.Validate(request.Parameters);
        if (!validation.IsValid)
            return Failure("validation_failed", "One or more decision fields are invalid", 422,
                new { fields = validation.Errors });

        if (_configurationError is not null || _endpoint is null)
            return Failure("invalid_provider_configuration",
                _configurationError ?? "Kev endpoint is not configured", 503);

        var requestId = request.JobId ?? Guid.NewGuid();
        CancellationTokenSource jobCancellation;
        var ownsRegistration = false;
        if (registerRequest)
        {
            jobCancellation = new CancellationTokenSource();
            if (!_requests.TryAdd(requestId, jobCancellation))
            {
                jobCancellation.Dispose();
                return Failure("duplicate_provider_request",
                    "A request with this job id is already running", 409);
            }
            ownsRegistration = true;
        }
        else if (request.JobId is null ||
                 !_requests.TryGetValue(requestId, out jobCancellation!))
            return Failure("validation_job_not_registered",
                "The validation job identity is not registered", 500);

        using var timeout = new CancellationTokenSource(_requestTimeout);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            ct, jobCancellation.Token, timeout.Token);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var upstreamBody = new
            {
                state = validation.Request!.State,
                model = _model,
                questions = validation.Request.Questions,
            };
            using var message = NewSidecarRequest(
                HttpMethod.Post, "v1/systemone",
                JsonSerializer.Serialize(upstreamBody, JsonOptions));
            using var response = await _http.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, execution.Token);
            var json = await ReadBoundedAsync(response.Content, execution.Token);

            if (!response.IsSuccessStatusCode)
            {
                var resourceBusy = response.StatusCode is HttpStatusCode.Conflict or
                    HttpStatusCode.Locked or HttpStatusCode.InsufficientStorage ||
                    LooksLikeResourceBusy(json);
                return Failure(
                    resourceBusy ? "resource_busy" : "kev_sidecar_error",
                    resourceBusy
                        ? "Kev does not have sufficient local GPU capacity; no other provider was evicted"
                        : $"Kev sidecar returned HTTP {(int)response.StatusCode}",
                    resourceBusy ? 409 : MapUpstreamStatus(response.StatusCode),
                    new { upstreamStatus = (int)response.StatusCode });
            }

            var normalized = NormalizeResponse(
                validation.Request, json, request, stopwatch.Elapsed.TotalMilliseconds);
            _inferenceWarmed = true;
            _lastError = null;
            _lastErrorCode = null;
            return new JobResult
            {
                Success = true,
                ResultJson = JsonSerializer.Serialize(normalized, JsonOptions),
                ContentType = "application/json",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested ||
                                                  execution.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested || jobCancellation.IsCancellationRequested)
                throw new OperationCanceledException(execution.Token);
            return Failure("provider_timeout",
                $"Kev exceeded the configured {_requestTimeout.TotalSeconds:0} second timeout", 504);
        }
        catch (JsonException ex)
        {
            return Failure("invalid_provider_response",
                $"Kev returned a response that does not match the decision contract: {ex.Message}", 502);
        }
        catch (HttpRequestException ex)
        {
            MarkEndpointUnreachable();
            return Failure("sidecar_unavailable", ex.Message, 502);
        }
        finally
        {
            if (ownsRegistration)
            {
                _requests.TryRemove(requestId, out _);
                jobCancellation.Dispose();
            }
        }
    }

    public void CancelJob(string jobKey)
    {
        if (Guid.TryParse(jobKey, out var id) && _requests.TryGetValue(id, out var request))
            request.Cancel();
    }

    public Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds)
        => jobIds.Where(_requests.ContainsKey)
            .ToDictionary(id => id, _ => "inference");

    public async Task<object> GetStatusDetailsAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        if (_administrativelyStopped)
            await ProbeAsync(ct);
        return new
        {
            provider = Name,
            status = status.ToString(),
            administrativelyStopped = _administrativelyStopped,
            endpoint = _endpoint?.ToString().TrimEnd('/'),
            endpointReachable = _endpointReachable,
            readiness = _endpointReachable
                ? _inferenceWarmed ? "inference-warmed" : "process-ready"
                : "not-ready",
            lifecycle = _ownedProcess is null ? "attached" : "owned",
            processId = ProcessId,
            model = _model,
            modelRevision = _modelRevision,
            calibrationRevision = _calibrationRevision,
            configured = new
            {
                timeoutSeconds = _requestTimeout.TotalSeconds,
                baseModelRevision = _baseModelRevision,
                mergeLora = _mergeLora,
                dtype = _dtype,
                kernelBackend = _kernelBackend,
            },
            observed = new
            {
                run = _loadedRun,
                dtype = _loadedDtype,
                device = _loadedDevice,
                temperature = _loadedTemperature,
            },
            activeRequests = _requests.Count,
            lastError = _lastErrorCode is null ? null : new
            {
                code = _lastErrorCode,
                message = _lastError,
            },
        };
    }

    public async Task<ProviderValidationResult> ValidateProviderAsync(
        ProviderValidationContext context,
        CancellationToken ct = default)
    {
        using var validationCancellation = new CancellationTokenSource();
        if (!_requests.TryAdd(context.JobId, validationCancellation))
            return new ProviderValidationResult(
                false, Failure("duplicate_provider_request",
                    "A request with this job id is already running", 409)
                    .ResultJson,
                "duplicate_provider_request",
                "A request with this job id is already running", 409);

        using var validationExecution = CancellationTokenSource.CreateLinkedTokenSource(
            ct, validationCancellation.Token);
        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>( """
            {
              "state": {"ticket":"The package is late and I was charged twice."},
              "questions": {
                "route": {
                  "type":"choice",
                  "instructions":"Which single issue is most prominent?",
                  "criteria":{"shipping":"Delivery problem","billing":"Payment problem"}
                },
                "urgent": {
                  "type":"noul",
                  "instructions":"Does this require urgent attention?"
                },
                "severity": {
                  "type":"score",
                  "instructions":"How severe is the issue?",
                  "criteria":["low","medium","high"]
                }
              }
            }
            """)!;

            var result = await ExecuteDecisionAsync(new JobRequest
            {
                JobId = context.JobId,
                QueuedAt = context.QueuedAt,
                InvocationStartedAt = context.InvocationStartedAt,
                CapabilitySlug = _capabilitySlug,
                Parameters = parameters,
                Provenance = context.Provenance,
            }, validationExecution.Token, registerRequest: false);
            if (result is not { Success: true, ResultJson: not null })
                return new ProviderValidationResult(
                    false, result?.ResultJson, result?.ErrorCode,
                    result?.ErrorMessage, result?.ErrorStatusCode ?? 500);

            object permutation;
            try
            {
                var permutationJson = JsonSerializer.Serialize(new
                {
                    request = new
                    {
                        state = new
                        {
                            ticket = "The package is late and I was charged twice.",
                        },
                        model = _model,
                        questions = new
                        {
                            route = new
                            {
                                type = "choice",
                                instructions = "Which single issue is most prominent?",
                                criteria = new
                                {
                                    shipping = "Delivery problem",
                                    billing = "Payment problem",
                                },
                            },
                        },
                    },
                    question = "route",
                    n_perm = 6,
                    seed = 0,
                }, JsonOptions);
                using var request = NewSidecarRequest(
                    HttpMethod.Post, "v1/systemone/permute", permutationJson);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    validationExecution.Token);
                timeout.CancelAfter(_requestTimeout);
                using var response = await _http.SendAsync(request, timeout.Token);
                var body = await ReadBoundedAsync(response.Content, timeout.Token);
                permutation = response.IsSuccessStatusCode
                    ? JsonSerializer.Deserialize<JsonElement>(body)
                    : new
                    {
                        supported = false,
                        message = $"Permutation diagnostic returned HTTP {(int)response.StatusCode}",
                    };
            }
            catch (OperationCanceledException) when (!validationExecution.IsCancellationRequested)
            {
                permutation = new
                {
                    supported = false,
                    message = "Permutation diagnostic exceeded the configured timeout",
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                permutation = new { supported = false, message = ex.Message };
            }

            validationExecution.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(result.ResultJson);
            var output = JsonSerializer.Serialize(new
            {
                ok = true,
                readiness = "inference-warmed",
                decision = document.RootElement,
                permutation,
                permutationInstabilityIsProviderFailure = false,
                note = "Permutation instability is surfaced for caller judgment; it does not fail provider validation.",
            }, JsonOptions);
            return new ProviderValidationResult(true, output);
        }
        finally
        {
            _requests.TryRemove(context.JobId, out _);
        }
    }


    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_ownsHttp) _http.Dispose();
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (_endpoint is null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = NewSidecarRequest(HttpMethod.Get, "v1/models");
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                MarkEndpointUnreachable();
                return false;
            }

            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("models", out var models) &&
                models.ValueKind == JsonValueKind.Array &&
                models.GetArrayLength() > 0)
            {
                var model = models[0];
                _loadedRun = StringProperty(model, "run") ?? StringProperty(model, "id");
                _loadedDtype = StringProperty(model, "dtype");
                _loadedDevice = StringProperty(model, "device");
                _loadedTemperature = NumberProperty(model, "temperature");
            }
            _endpointReachable = true;
            return true;
        }
        catch
        {
            MarkEndpointUnreachable();
            return false;
        }
    }

    private DecisionResponse NormalizeResponse(
        DecisionRequest request,
        string json,
        JobRequest job,
        double totalMs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("answers", out var answersNode) ||
            answersNode.ValueKind != JsonValueKind.Object)
            throw new JsonException("$.answers must be an object");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        var responseIds = answersNode.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!request.Questions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(responseIds))
            throw new JsonException("$.answers keys must exactly match the request questions");
        foreach (var (id, question) in request.Questions)
        {
            if (!answersNode.TryGetProperty(id, out var answer) ||
                answer.ValueKind != JsonValueKind.Object)
                throw new JsonException($"$.answers.{id} is required");

            var type = StringProperty(answer, "type");
            answers[id] = question switch
            {
                ChoiceQuestion choice when type == "choice" =>
                    NormalizeChoice(id, choice, answer),
                ScoreQuestion score when type == "score" =>
                    NormalizeScore(id, score, answer),
                NoulQuestion when type == "noul" =>
                    NormalizeNoul(id, answer),
                _ => throw new JsonException(
                    $"$.answers.{id}.type does not match its request question"),
            };
        }

        double? modelMs = NumberProperty(root, "latency_ms");
        double? queueMs = job.QueuedAt.HasValue && job.InvocationStartedAt.HasValue
            ? Math.Max(0, (job.InvocationStartedAt.Value - job.QueuedAt.Value).TotalMilliseconds)
            : null;
        return new DecisionResponse
        {
            Answers = answers,
            Provider = Name,
            Model = _model,
            ModelRevision = _modelRevision,
            CalibrationRevision = _calibrationRevision,
            Timing = new DecisionTiming
            {
                QueueMs = queueMs,
                ModelMs = modelMs,
                TotalMs = Math.Round(totalMs, 3),
            },
        };
    }

    private static ChoiceAnswer NormalizeChoice(
        string id,
        ChoiceQuestion question,
        JsonElement answer)
    {
        var probabilities = Distribution(answer, id,
            question.Criteria.Keys.ToHashSet(StringComparer.Ordinal));
        var choice = StringProperty(answer, "choice")
            ?? throw new JsonException($"$.answers.{id}.choice is required");
        if (!probabilities.ContainsKey(choice))
            throw new JsonException($"$.answers.{id}.choice is not a declared option");
        var count = probabilities.Count;
        var concentration = count == 1 ? 1 :
            (probabilities.Values.Max() - 1d / count) / (1 - 1d / count);
        return new ChoiceAnswer
        {
            Choice = choice,
            Confidence = Math.Round(Math.Clamp(concentration, 0, 1), 6),
            Probabilities = probabilities,
        };
    }

    private static ScoreAnswer NormalizeScore(
        string id,
        ScoreQuestion question,
        JsonElement answer)
    {
        var keys = Enumerable.Range(0, question.Criteria.Count)
            .Select(index => index.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var probabilities = Distribution(answer, id, keys);
        var score = probabilities.Sum(pair => int.Parse(pair.Key) * pair.Value);
        var mode = probabilities.MaxBy(pair => pair.Value).Key;
        var modeIndex = int.Parse(mode);
        var confidence = 1 - probabilities.Sum(pair =>
            pair.Value * Math.Abs(int.Parse(pair.Key) - modeIndex)) /
            (question.Criteria.Count - 1);
        return new ScoreAnswer
        {
            Score = Math.Round(score, 6),
            Confidence = Math.Round(Math.Clamp(confidence, 0, 1), 6),
            Legend = question.Criteria
                .Select((value, index) => new
                {
                    Key = index.ToString(),
                    Value = Render(value),
                })
                .ToDictionary(item => item.Key, item => item.Value),
            Probabilities = probabilities,
        };
    }

    private static NoulAnswer NormalizeNoul(string id, JsonElement answer)
    {
        var noul = NumberProperty(answer, "noul")
            ?? throw new JsonException($"$.answers.{id}.noul is required");
        if (noul is < 0 or > 1)
            throw new JsonException($"$.answers.{id}.noul must be between 0 and 1");
        return new NoulAnswer
        {
            Noul = noul,
            Probabilities = new Dictionary<string, double>
            {
                ["false"] = Math.Round(1 - noul, 6),
                ["true"] = noul,
            },
        };
    }

    private static Dictionary<string, double> Distribution(
        JsonElement answer,
        string id,
        HashSet<string> expected)
    {
        if (!answer.TryGetProperty("probabilities", out var node) ||
            node.ValueKind != JsonValueKind.Object)
            throw new JsonException($"$.answers.{id}.probabilities is required");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (!expected.Contains(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetDouble(out var value) ||
                value is < 0 or > 1)
                throw new JsonException(
                    $"$.answers.{id}.probabilities.{property.Name} is invalid");
            result[property.Name] = value;
        }
        if (!expected.SetEquals(result.Keys))
            throw new JsonException(
                $"$.answers.{id}.probabilities keys do not match the declared criteria");
        var sum = result.Values.Sum();
        if (Math.Abs(sum - 1) > 0.05)
            throw new JsonException(
                $"$.answers.{id}.probabilities must sum to approximately 1");
        return result;
    }

    private HttpRequestMessage NewSidecarRequest(
        HttpMethod method,
        string relativePath,
        string? json = null)
    {
        var message = new HttpRequestMessage(method, new Uri(_endpoint!, relativePath));
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("RedCompute-Kev", "1.0"));
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        // Deliberately do not copy the inbound Authorization or any RedLeaf headers.
        return message;
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxSidecarResponseBytes)
            throw new JsonException("Kev response exceeds the 2 MiB limit");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaxSidecarResponseBytes)
                throw new JsonException("Kev response exceeds the 2 MiB limit");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task StopOwnedProcessAsync(CancellationToken ct)
    {
        var owned = Interlocked.Exchange(ref _ownedProcess, null);
        if (owned is not null)
            await _processController.StopAsync(owned, _log, ct);
    }

    private void ApplyLaunchDefaults()
    {
        _config.Extra ??= new Dictionary<string, object?>();
        _config.Extra["MergeLora"] = _mergeLora;
        _config.Extra["Dtype"] = _dtype;
        _config.Extra["KernelBackend"] = _kernelBackend;
    }

    private void MarkEndpointUnreachable()
    {
        _endpointReachable = false;
        _inferenceWarmed = false;
        _loadedRun = null;
        _loadedDtype = null;
        _loadedDevice = null;
        _loadedTemperature = null;
    }

    private void SetError(string code, string message)
    {
        _lastErrorCode = code;
        _lastError = message;
        _status = BackendStatus.Error;
        _log($"[Kev] {code}: {message}");
    }

    private static JobResult Failure(
        string code,
        string message,
        int status,
        object? details = null)
    {
        var error = JsonSerializer.Serialize(new
        {
            error = code,
            message,
            details,
            actionAuthority = false,
        }, JsonOptions);
        return new JobResult
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
            ErrorStatusCode = status,
            ResultJson = error,
            ContentType = "application/json",
        };
    }

    private static bool IsPrivateLocal(Uri uri)
    {
        if (uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(uri.Host, out var address))
            return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 10 ||
                bytes[0] == 127 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31,
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal || address.Equals(IPAddress.IPv6Loopback),
            _ => false,
        };
    }

    private static bool LooksLikeResourceBusy(string text)
        => text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("cuda oom", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("resource_busy", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("insufficient", StringComparison.OrdinalIgnoreCase) &&
           text.Contains("memory", StringComparison.OrdinalIgnoreCase);
    private static bool IsImmutableRevision(string? value)
        => value is { Length: 40 } &&
           value.All(character => character is >= '0' and <= '9'
               or >= 'a' and <= 'f');


    private static int MapUpstreamStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => 422,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => 504,
        _ => 502,
    };

    private static string Render(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.GetRawText();

    private static string? StringProperty(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? NumberProperty(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetDouble(out var number)
            ? number
            : null;

    private static string? ExtraString(ProviderConfig config, string key)
    {
        if (config.Extra is null || !config.Extra.TryGetValue(key, out var value) ||
            value is null) return null;
        return value is JsonElement json && json.ValueKind == JsonValueKind.String
            ? json.GetString()
            : value.ToString();
    }

    private static bool ExtraBool(ProviderConfig config, string key, bool fallback)
    {
        if (config.Extra is null || !config.Extra.TryGetValue(key, out var value) ||
            value is null) return fallback;
        if (value is bool flag) return flag;
        if (value is JsonElement json &&
            json.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return json.GetBoolean();
        return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }
}
