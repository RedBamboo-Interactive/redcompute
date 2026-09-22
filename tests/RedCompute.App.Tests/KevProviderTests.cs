using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.Kev;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class KevProviderTests
{
    [Fact]
    public async Task Proxies_typed_decision_without_forwarding_redleaf_bearer()
    {
        var postCount = 0;
        var handler = new DelegateHandler(async (request, ct) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Compute-Provenance"));
            if (request.Method == HttpMethod.Get)
                return Json("""
                {"models":[{"id":"kev-latest","run":"jaredpalmer/kev-9b","device":"cuda:0",
                  "dtype":"bfloat16","temperature":2.2973967099940698}]}
                """);

            postCount++;
            var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("kev-latest", sent["model"]!.GetValue<string>());
            Assert.Equal("choice", sent["questions"]!["route"]!["type"]!.GetValue<string>());
            return Json("""
            {
              "model":"kev-latest",
              "answers":{
                "route":{"type":"choice","choice":"billing","confidence":0.1,
                  "probabilities":{"billing":0.7,"shipping":0.3}},
                "urgent":{"type":"noul","noul":0.8},
                "severity":{"type":"score","score":1.2,"confidence":0.1,
                  "legend":{"0":"low","1":"medium","2":"high"},
                  "probabilities":{"0":0.1,"1":0.2,"2":0.7}}
              },
              "latency_ms":125
            }
            """);
        });
        await using var provider = Provider(new HttpClient(handler));

        Assert.True(await provider.StartAsync());
        var before = JsonNode.Parse(JsonSerializer.Serialize(
            await provider.GetStatusDetailsAsync()))!;
        Assert.Equal("process-ready", before["readiness"]!.GetValue<string>());
        Assert.Equal(2.2973967099940698,
            before["observed"]!["temperature"]!.GetValue<double>(), 12);

        var result = await provider.ExecuteAsync(Request());

        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal(1, postCount);
        var body = JsonNode.Parse(result.ResultJson!)!;
        Assert.Equal("kev-local-quality", body["provider"]!.GetValue<string>());
        Assert.Equal("2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c", body["modelRevision"]!.GetValue<string>());
        Assert.Equal("calibration-test", body["calibrationRevision"]!.GetValue<string>());
        Assert.False(body["actionAuthority"]!.GetValue<bool>());
        Assert.Equal(0.4, body["answers"]!["route"]!["confidence"]!.GetValue<double>(), 6);
        Assert.Equal(0.2, body["answers"]!["urgent"]!["probabilities"]!["false"]!.GetValue<double>(), 6);
        Assert.Equal(1.6, body["answers"]!["severity"]!["score"]!.GetValue<double>(), 6);
        Assert.Equal(125, body["timing"]!["modelMs"]!.GetValue<double>());

        var after = JsonNode.Parse(JsonSerializer.Serialize(
            await provider.GetStatusDetailsAsync()))!;
        Assert.Equal("inference-warmed", after["readiness"]!.GetValue<string>());
        Assert.False(after["configured"]!["mergeLora"]!.GetValue<bool>());
        Assert.Equal("bfloat16", after["configured"]!["dtype"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_request_never_reaches_inference()
    {
        var posts = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json("""{"models":[{"id":"kev-latest"}]}"""));
            posts++;
            return Task.FromResult(Json("{}"));
        });
        await using var provider = Provider(new HttpClient(handler));
        Assert.True(await provider.StartAsync());

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "decision",
            Parameters = Parse("""
            {"state":"x","questions":{"bad":{"type":"score","instructions":"x","criteria":["only"]}}}
            """),
        });

        Assert.False(result!.Success);
        Assert.Equal("validation_failed", result.ErrorCode);
        Assert.Equal(422, result.ErrorStatusCode);
        Assert.Equal(0, posts);
        Assert.Contains("questions.bad.criteria", result.ResultJson);
    }

    [Fact]
    public async Task Capacity_failure_is_resource_busy_without_eviction()
    {
        var handler = new DelegateHandler((request, _) =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? Json("""{"models":[{"id":"kev-latest"}]}""")
                : Json("""{"detail":"CUDA out of memory"}""",
                    HttpStatusCode.InsufficientStorage)));
        await using var provider = Provider(new HttpClient(handler));
        Assert.True(await provider.StartAsync());

        var result = await provider.ExecuteAsync(Request());

        Assert.False(result!.Success);
        Assert.Equal("resource_busy", result.ErrorCode);
        Assert.Equal(409, result.ErrorStatusCode);
        Assert.Contains("no other provider was evicted", result.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_targets_the_exact_job_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"models":[{"id":"kev-latest"}]}""");
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{}");
        });
        await using var provider = Provider(new HttpClient(handler));
        Assert.True(await provider.StartAsync());
        var jobId = Guid.NewGuid();
        var task = provider.ExecuteAsync(Request(jobId));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        provider.CancelJob(jobId.ToString());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task Lifecycle_stops_only_the_process_handle_it_started()
    {
        var ready = false;
        var handler = new DelegateHandler((request, _) => Task.FromResult(
            ready
                ? Json("""{"models":[{"id":"kev-latest"}]}""")
                : Json("{}", HttpStatusCode.ServiceUnavailable)));
        var controller = new FakeProcessController(() => ready = true);
        var config = Config();
        config.LaunchCommand = "exec uv run python -m kev.serve --run /models/kev-9b --port 8008";
        await using var provider = new KevLocalQualityProvider(
            config, "decision", _ => { }, new HttpClient(handler), controller);

        Assert.True(await provider.StartAsync());
        var owned = Assert.IsType<KevOwnedProcess>(controller.Started);
        Assert.Equal(Process.GetCurrentProcess().Id, provider.ProcessId);

        await provider.StopAsync();

        Assert.Same(owned, controller.Stopped);
        Assert.Equal(1, controller.StopCount);
        var stopScript = KevProcessController.BuildWslStopScript(4242, "987654");
        Assert.Contains("pid=4242", stopScript);
        Assert.Contains("kill -TERM --", stopScript);
        Assert.Contains("987654", stopScript);
        Assert.DoesNotContain("pkill", stopScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uvicorn|python", stopScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Permutation_instability_is_diagnostic_not_validation_failure()
    {
        var handler = new DelegateHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"models":[{"id":"kev-latest"}]}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/permute"))
                return Json("""{"runs":[],"argmax_stable":false,"spread":{"shipping":0.31,"billing":0.31}}""");
            _ = await request.Content!.ReadAsStringAsync(ct);
            return Json("""
            {"answers":{
              "route":{"type":"choice","choice":"shipping","probabilities":{"shipping":0.6,"billing":0.4}},
              "urgent":{"type":"noul","noul":0.5},
              "severity":{"type":"score","score":1,"probabilities":{"0":0.2,"1":0.6,"2":0.2}}
            },"latency_ms":125}
            """);
        });
        await using var provider = Provider(new HttpClient(handler));
        Assert.True(await provider.StartAsync());

        var result = await provider.ValidateProviderAsync(ValidationContext());

        Assert.True(result.Success, result.Message);
        var body = JsonNode.Parse(result.ResultJson!)!;
        Assert.False(body["permutation"]!["argmax_stable"]!.GetValue<bool>());
        Assert.False(body["permutationInstabilityIsProviderFailure"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Validation_uses_job_identity_for_substatus_and_cancellation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"models":[{"id":"kev-latest"}]}""");
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{}");
        });
        await using var provider = Provider(new HttpClient(handler));
        Assert.True(await provider.StartAsync());
        var context = ValidationContext();

        var pending = provider.ValidateProviderAsync(context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("inference",
            provider.GetJobSubStatuses([context.JobId])[context.JobId]);

        provider.CancelJob(context.JobId.ToString());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending);
        Assert.Empty(provider.GetJobSubStatuses([context.JobId]));
    }

    [Theory]
    [InlineData(false, "main")]
    [InlineData(false, "jaredpalmer/kev-9b@2026-09-21")]
    [InlineData(false, "2629C06A5AEB0FEB3B9783BAFED17ED8F39ECF5C")]
    [InlineData(true, "latest")]
    [InlineData(true, "Qwen/Qwen3.5-9B-Base@2026-09-21")]
    public async Task Mutable_or_noncanonical_model_revisions_are_rejected(
        bool baseRevision,
        string value)
    {
        var probes = 0;
        var config = Config();
        if (baseRevision)
            config.Extra!["BaseModelRevision"] = value;
        else
            config.ModelRevision = value;
        var handler = new DelegateHandler((_, _) =>
        {
            probes++;
            return Task.FromResult(Json("""{"models":[]}"""));
        });
        await using var provider = new KevLocalQualityProvider(
            config, "decision", _ => { }, new HttpClient(handler));

        Assert.False(await provider.StartAsync());
        Assert.Equal(0, probes);
        Assert.Equal(RedCompute.Core.Providers.BackendStatus.Error,
            await provider.GetStatusAsync());
    }


    [Fact]
    public void Provider_entity_overlay_maps_structured_decision_settings()
    {
        using var document = JsonDocument.Parse("""
        {
          "endpoint": "http://127.0.0.1:8008",
          "model": "jaredpalmer/kev-9b@revision",
          "modelRevision": "2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c",
          "calibrationRevision": "temperature-2.2973967099940698",
          "timeoutSeconds": "300",
          "launchCommand": "~/.leaf/envs/provider-kev-local/bin/python -m kev.serve",
          "extra": { "BaseModelRevision": "68c46c4b3498877f3ef123c856ecfde50c39f404" }
        }
        """);
        var entity = new ProviderEntityConfig(
            "provider-id", "kev-local-quality", "KEV", "KevLocalQuality",
            null, null, null, null, "active", null)
        {
            ProviderType = "KevLocalQuality",
            Capabilities = ["decision"],
            Settings = document.RootElement.Clone(),
        };
        var config = new ProviderConfig { Type = "KevLocalQuality" };

        ProviderConfigService.ApplyEntityToProvider(entity, config);

        Assert.Equal("http://127.0.0.1:8008", config.Endpoint);
        Assert.Equal("jaredpalmer/kev-9b@revision", config.Model);
        Assert.Equal("2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c", config.ModelRevision);
        Assert.Equal("temperature-2.2973967099940698", config.CalibrationRevision);
        Assert.Equal(300, config.TimeoutSeconds);
        Assert.Equal("~/.leaf/envs/provider-kev-local/bin/python -m kev.serve", config.LaunchCommand);
        Assert.Equal(
            "68c46c4b3498877f3ef123c856ecfde50c39f404",
            Assert.IsType<string>(config.Extra!["BaseModelRevision"]));
    }

    private static KevLocalQualityProvider Provider(HttpClient client)
        => new(Config(), "decision", _ => { }, client);

    private static ProviderConfig Config() => new()
    {
        Type = "KevLocalQuality",
        Endpoint = "http://127.0.0.1:8008",
        Model = "kev-latest",
        ModelRevision = "2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c",
        CalibrationRevision = "calibration-test",
        TimeoutSeconds = 10,
        Extra = new Dictionary<string, object?>
        {
            ["MergeLora"] = false,
            ["BaseModelRevision"] = "68c46c4b3498877f3ef123c856ecfde50c39f404",
            ["Dtype"] = "bfloat16",
            ["KernelBackend"] = "flash-qla-sm120",
        },
    };

    private static JobRequest Request(Guid? jobId = null) => new()
    {
        JobId = jobId,
        CapabilitySlug = "decision",
        QueuedAt = DateTimeOffset.UtcNow.AddMilliseconds(-5),
        InvocationStartedAt = DateTimeOffset.UtcNow,
        Parameters = Parse("""
        {
          "state":{"ticket":"late and charged twice"},
          "questions":{
            "route":{"type":"choice","instructions":"Which team?","criteria":{"billing":"Payments","shipping":"Delivery"}},
            "urgent":{"type":"noul","instructions":"Urgent?"},
            "severity":{"type":"score","instructions":"Severity?","criteria":["low","medium","high"]}
          }
        }
        """),
    };
    private static ProviderValidationContext ValidationContext()
    {
        var now = DateTimeOffset.UtcNow;
        return new ProviderValidationContext(
            Guid.NewGuid(),
            now.AddMilliseconds(-5),
            now,
            JobProvenance.DirectRedCompute(
                "POST", "/decision/validate",
                new JobBeneficiary("user", "test-user"), "request-1", null));
    }


    private static Dictionary<string, object?> Parse(string json)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    private static HttpResponseMessage Json(
        string json,
        HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, ct) => Task.FromResult(handler(request, ct))) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class FakeProcessController(Action onStart) : IKevProcessController
    {
        public KevOwnedProcess? Started { get; private set; }
        public KevOwnedProcess? Stopped { get; private set; }
        public int StopCount { get; private set; }

        public Task<KevOwnedProcess> StartAsync(
            ProviderConfig config,
            Action<string> log,
            CancellationToken ct = default)
        {
            onStart();
            Started = new KevOwnedProcess(Process.GetCurrentProcess(), "Ubuntu-24.04", 4242, "987654");
            return Task.FromResult(Started);
        }

        public Task StopAsync(
            KevOwnedProcess process,
            Action<string> log,
            CancellationToken ct = default)
        {
            Stopped = process;
            StopCount++;
            return Task.CompletedTask;
        }
    }
}
