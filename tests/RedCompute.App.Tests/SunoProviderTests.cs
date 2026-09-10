using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.Suno;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SunoProviderTests
{
    [Fact]
    public async Task GenerateRecordsActualCreditsAndReturnsNamedIngestedArtifactsWithoutLeakingCredential()
    {
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return ++creditReads == 1
                    ? Json("{\"code\":200,\"data\":{\"creditsRemaining\":\"100\"}}")
                    : Json("{\"code\":200,\"data\":{\"balance\":90}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"task-1\"}}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/generate/record-info"))
                return Json("""
                {"code":200,"data":{"status":"SUCCESS","response":{"sunoData":[
                  {"id":"audio-1","audioUrl":"https://1.1.1.1/one.mp3","imageUrl":"https://1.1.1.1/one.jpg","title":"Signal Below A","tags":"retro dubstep","duration":32.0},
                  {"id":"audio-2","audioUrl":"https://1.1.1.1/two.mp3","imageUrl":"https://1.1.1.1/two.jpg","title":"Signal Below B","tags":"retro dubstep","duration":32.0}
                ]}}}
                """);
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        var downloadRequests = new List<HttpRequestMessage>();
        var downloadHandler = new DelegateHandler(request =>
        {
            downloadRequests.Add(request);
            Assert.Null(request.Headers.Authorization);
            var isImage = request.RequestUri!.AbsolutePath.EndsWith(".jpg");
            return Bytes(isImage ? "image/jpeg" : "audio/mpeg", isImage ? [9, 8] : [1, 2, 3, 4]);
        });
        var config = Config(new Dictionary<string, object?>
        {
            ["BaseUrl"] = "https://api.sunoapi.org",
            ["Model"] = "V5",
        });
        await using var provider = new SunoProvider(
            config, "music-gen", _ => { }, new HttpClient(apiHandler), new HttpClient(downloadHandler),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)));
        Assert.True(await provider.StartAsync());

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "generate",
                ["prompt"] = "atmosphere into action",
                ["style"] = "retro industrial dubstep",
                ["title"] = "Signal Below",
                ["instrumental"] = true,
            },
        });

        Assert.NotNull(result);
        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal("clip-0", result.PrimaryOutputName);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(3, result.ExtraOutputs!.Count);
        Assert.Contains(result.ExtraOutputs, part => part.Name == "cover-0");
        Assert.Contains(result.ExtraOutputs, part => part.Name == "clip-1");
        Assert.Contains(result.ExtraOutputs, part => part.Name == "cover-1");
        var metadata = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.Equal(3, metadata["schemaVersion"]!.GetValue<int>());
        Assert.Equal(2, metadata["tracks"]!.AsArray().Count);
        Assert.Equal(4, metadata["artifacts"]!.AsArray().Count);
        Assert.Equal(10, metadata["credits"]!["consumed"]!.GetValue<int>());
        Assert.Equal("balance_delta", metadata["credits"]!["measurement"]!.GetValue<string>());
        Assert.Equal(4, downloadRequests.Count);

        result.OutputStream!.Dispose();
        foreach (var extra in result.ExtraOutputs) extra.Data.Dispose();
    }

    [Fact]
    public async Task SplitStemUsesDedicatedRecordEndpointAndPreservesRoles()
    {
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json($"{{\"code\":200,\"data\":{(++creditReads == 1 ? 200 : 150)}}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"stem-task\"}}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/record-info"))
                return Json("""
                {"code":200,"data":{"successFlag":"SUCCESS","response":{"originData":[
                  {"id":"drums-id","audio_url":"https://1.1.1.1/drums.mp3","stem_type_group_name":"Drums","duration":32.0},
                  {"id":"bass-id","audio_url":"https://1.1.1.1/bass.mp3","stem_type_group_name":"Bass","duration":32.0}
                ]}}}
                """);
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        var downloadHandler = new DelegateHandler(_ => Bytes("audio/mpeg", [1, 2, 3]));
        var config = Config(new Dictionary<string, object?>
        {
            ["BaseUrl"] = "https://api.sunoapi.org",
        });
        await using var provider = new SunoProvider(
            config, "music-gen", _ => { }, new HttpClient(apiHandler), new HttpClient(downloadHandler),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)));
        await provider.StartAsync();

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "split_stem",
                ["taskId"] = "source-task",
                ["audioId"] = "source-audio",
                ["separationType"] = "split_stem",
            },
        });

        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal("stem-drums", result.PrimaryOutputName);
        Assert.Single(result.ExtraOutputs!);
        Assert.Equal("stem-bass", result.ExtraOutputs![0].Name);
        var artifacts = JsonNode.Parse(result.ResultJson!)!["artifacts"]!.AsArray();
        Assert.Equal("Drums", artifacts[0]!["role"]!.GetValue<string>());
        Assert.Equal("Bass", artifacts[1]!["role"]!.GetValue<string>());
        result.OutputStream!.Dispose();
        foreach (var extra in result.ExtraOutputs) extra.Data.Dispose();
    }

    [Fact]
    public async Task SplitStemUsesItsLongerOperationSpecificPollTimeout()
    {
        var polls = 0;
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json($"{{\"code\":200,\"data\":{(++creditReads == 1 ? 100 : 50)}}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"slow-stem-task\"}}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/record-info"))
            {
                if (++polls == 1) return Json("{\"code\":200,\"data\":{\"status\":\"PENDING\"}}");
                return Json("""
                    {"code":200,"data":{"status":"SUCCESS","response":{"originData":[
                      {"id":"drums-id","audio_url":"https://1.1.1.1/drums.mp3","stem_type_group_name":"Drums","duration":32.0}
                    ]}}}
                    """);
            }
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        await using var provider = new SunoProvider(
            Config([]), "music-gen", _ => { }, new HttpClient(apiHandler),
            new HttpClient(new DelegateHandler(_ => Bytes("audio/mpeg", [1, 2, 3]))),
            new SunoProviderTiming(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromSeconds(5)));

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "split_stem",
                ["taskId"] = "source-task",
                ["audioId"] = "source-audio",
                ["separationType"] = "split_stem",
            },
        });

        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal(2, polls);
        result.OutputStream!.Dispose();
    }

    [Fact]
    public async Task OrdinaryOperationNeedsNoBillingControlInputs()
    {
        var provider = new SunoProvider(Config([]), "music-gen", _ => { });
        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["operation"] = "generate",
            ["prompt"] = "prompt",
            ["style"] = "style",
            ["title"] = "title",
        });
        Assert.Empty(errors);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitDurationIsRejectedForModelsThatDoNotSupportIt()
    {
        var provider = new SunoProvider(Config(new Dictionary<string, object?>
        {
            ["Model"] = "V5",
        }), "music-gen", _ => { });
        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["operation"] = "generate",
            ["prompt"] = "prompt",
            ["style"] = "style",
            ["title"] = "title",
            ["duration"] = 32,
        });

        Assert.Contains("duration", errors.Keys);
        await provider.DisposeAsync();
    }

    [Fact]
    public void Artifact_download_address_filter_rejects_local_and_reserved_networks()
    {
        Assert.True(SunoProvider.IsNonPublicAddress(IPAddress.Parse("127.0.0.1")));
        Assert.True(SunoProvider.IsNonPublicAddress(IPAddress.Parse("10.20.30.40")));
        Assert.True(SunoProvider.IsNonPublicAddress(IPAddress.Parse("169.254.4.5")));
        Assert.True(SunoProvider.IsNonPublicAddress(IPAddress.Parse("fc00::1234")));
        Assert.True(SunoProvider.IsNonPublicAddress(IPAddress.Parse("2001:db8::1")));
        Assert.False(SunoProvider.IsNonPublicAddress(IPAddress.Parse("1.1.1.1")));
    }

    [Fact]
    public async Task FailedOperationStillRecordsTheActualCreditDelta()
    {
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit", StringComparison.Ordinal))
                return Json($"{{\"code\":200,\"data\":{(++creditReads == 1 ? 20 : 17)}}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/generate"))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("provider failed", Encoding.UTF8, "text/plain"),
                };
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        await using var provider = new SunoProvider(
            Config([]),
            "music-gen", _ => { }, new HttpClient(apiHandler), new HttpClient(new DelegateHandler(_ => throw new Exception())),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)));

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "generate",
                ["prompt"] = "prompt",
                ["style"] = "style",
                ["title"] = "title",
            },
        });

        Assert.False(result!.Success);
        var audit = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.Equal("failed", audit["status"]!.GetValue<string>());
        Assert.Equal(20, audit["credits"]!["before"]!.GetValue<int>());
        Assert.Equal(17, audit["credits"]!["after"]!.GetValue<int>());
        Assert.Equal(3, audit["credits"]!["consumed"]!.GetValue<int>());
        Assert.Equal("balance_delta", audit["credits"]!["measurement"]!.GetValue<string>());
    }

    [Fact]
    public async Task RecoverExistingTaskDoesNotSubmitAgainAndFallsBackToStreamAudio()
    {
        var postCount = 0;
        var logs = new List<string>();
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
                throw new InvalidOperationException("Recovery must not submit a paid task");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/record-info"))
            {
                Assert.Contains("taskId=existing-task", request.RequestUri.Query, StringComparison.Ordinal);
                return Json("""
                {"code":200,"data":{"status":"SUCCESS","creditsConsumed":10,"creditsRemaining":"900.0","response":{"sunoData":[
                  {"id":"audio-1","audioUrl":"https://1.1.1.1/not-ready.mp3?token=do-not-log","streamAudioUrl":"https://1.1.1.1/ready-one.mp3?token=do-not-log","imageUrl":"https://1.1.1.1/cover-one.jpg?signature=do-not-log","title":"Recovered A","duration":32.0},
                  {"id":"audio-2","audioUrl":"https://1.1.1.1/ready-two.mp3","title":"Recovered B","duration":32.0}
                ]}}}
                """);
            }
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        var downloadRequests = new List<HttpRequestMessage>();
        var downloadHandler = new DelegateHandler(request =>
        {
            downloadRequests.Add(request);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("RedCompute/1.0", request.Headers.UserAgent.ToString());
            return request.RequestUri!.AbsolutePath is "/not-ready.mp3" or "/cover-one.jpg"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Bytes("audio/mpeg", [1, 2, 3, 4]);
        });
        await using var provider = new SunoProvider(
            Config([]), "music-gen", logs.Add, new HttpClient(apiHandler), new HttpClient(downloadHandler),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2))
            {
                MediaRetryInterval = TimeSpan.FromMilliseconds(1),
                MediaRetryTimeout = TimeSpan.FromMilliseconds(20),
            });

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "recover",
                ["sourceOperation"] = "generate",
                ["taskId"] = "existing-task",
            },
        });

        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal(0, postCount);
        var metadata = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.True(metadata["recovered"]!.GetValue<bool>());
        Assert.Equal("generate", metadata["operation"]!.GetValue<string>());
        Assert.Equal("existing-task", metadata["providerTaskId"]!.GetValue<string>());
        Assert.Equal(2, metadata["tracks"]!.AsArray().Count);
        Assert.Equal(2, metadata["artifacts"]!.AsArray().Count);
        Assert.Null(metadata["tracks"]![0]!["coverArtifact"]);
        Assert.Equal(10, metadata["credits"]!["consumed"]!.GetValue<int>());
        Assert.Equal(900, metadata["credits"]!["after"]!.GetValue<int>());
        Assert.Equal("provider_task", metadata["credits"]!["measurement"]!.GetValue<string>());
        Assert.Contains(downloadRequests, request => request.RequestUri!.AbsolutePath == "/ready-one.mp3");
        Assert.Contains(logs, line => line.Contains("Optional cover 'cover-0' was skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("do-not-log", StringComparison.Ordinal));

        result.OutputStream!.Dispose();
        foreach (var extra in result.ExtraOutputs!) extra.Data.Dispose();
    }

    [Fact]
    public async Task TransientMedia403IsRetriedWithSanitizedDiagnostics()
    {
        var creditReads = 0;
        var logs = new List<string>();
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json($"{{\"code\":200,\"data\":\"{(++creditReads == 1 ? "100.0" : "90.0")}\"}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"retry-task\"}}");
            if (request.RequestUri.AbsolutePath.EndsWith("/generate/record-info"))
                return Json("""
                {"code":200,"data":{"status":"SUCCESS","response":{"sunoData":[
                  {"id":"audio-1","audioUrl":"https://1.1.1.1/retry.mp3?signature=do-not-log","title":"Retry A","duration":32.0}
                ]}}}
                """);
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        var downloadCount = 0;
        var downloadHandler = new DelegateHandler(_ => ++downloadCount == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Bytes("audio/mpeg", [1, 2, 3]));
        await using var provider = new SunoProvider(
            Config([]), "music-gen", logs.Add, new HttpClient(apiHandler), new HttpClient(downloadHandler),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2))
            {
                MediaRetryInterval = TimeSpan.FromMilliseconds(1),
                MediaRetryTimeout = TimeSpan.FromMilliseconds(500),
            });

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "generate",
                ["prompt"] = "prompt",
                ["style"] = "style",
                ["title"] = "title",
            },
        });

        Assert.True(result!.Success, result.ErrorMessage);
        Assert.Equal(2, downloadCount);
        Assert.Equal(10, JsonNode.Parse(result.ResultJson!)!["credits"]!["consumed"]!.GetValue<int>());
        Assert.Contains(logs, line => line.Contains("HTTP 403", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("do-not-log", StringComparison.Ordinal));
        result.OutputStream!.Dispose();
    }

    [Fact]
    public async Task FailedIngestionRetainsProviderTaskCreditReceiptAndSanitizesSignedUrl()
    {
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json("{\"code\":200,\"data\":100}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"failed-ingest-task\"}}");
            if (request.RequestUri.AbsolutePath.EndsWith("/generate/record-info"))
                return Json("""
                {"code":200,"data":{"status":"SUCCESS","creditsConsumed":10,"creditsRemaining":90,"response":{"sunoData":[
                  {"id":"audio-1","audioUrl":"https://1.1.1.1/forbidden.mp3?signature=do-not-log","title":"Blocked","duration":32.0}
                ]}}}
                """);
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        await using var provider = new SunoProvider(
            Config([]), "music-gen", _ => { }, new HttpClient(apiHandler),
            new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2))
            {
                MediaRetryTimeout = TimeSpan.Zero,
            });

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "generate",
                ["prompt"] = "prompt",
                ["style"] = "style",
                ["title"] = "title",
            },
        });

        Assert.False(result!.Success);
        Assert.DoesNotContain("do-not-log", result.ErrorMessage, StringComparison.Ordinal);
        var audit = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.Equal("failed-ingest-task", audit["providerTaskId"]!.GetValue<string>());
        Assert.True(audit["recoverableProviderTask"]!.GetValue<bool>());
        Assert.Equal(10, audit["credits"]!["consumed"]!.GetValue<int>());
        Assert.Equal(90, audit["credits"]!["after"]!.GetValue<int>());
        Assert.Equal("provider_task", audit["credits"]!["measurement"]!.GetValue<string>());
    }

    [Fact]
    public async Task TerminalProviderFailureIsNotMarkedRecoverable()
    {
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json($"{{\"code\":200,\"data\":{(++creditReads == 1 ? 100 : 100)}}}");
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/generate"))
                return Json("{\"code\":200,\"data\":{\"taskId\":\"terminal-stem-task\"}}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/vocal-removal/record-info"))
                return Json("{\"code\":200,\"data\":{\"status\":\"FAILED\",\"errorMessage\":\"We couldn't verify your audio. Please try again.\"}}");
            throw new InvalidOperationException($"Unexpected API request {request.Method} {request.RequestUri}");
        });
        await using var provider = new SunoProvider(
            Config([]), "music-gen", _ => { }, new HttpClient(apiHandler),
            new HttpClient(new DelegateHandler(_ => throw new Exception())),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)));

        var result = await provider.ExecuteAsync(new JobRequest
        {
            CapabilitySlug = "music-gen",
            Parameters = new Dictionary<string, object?>
            {
                ["operation"] = "split_stem",
                ["taskId"] = "source-task",
                ["audioId"] = "source-audio",
                ["separationType"] = "split_stem",
            },
        });

        Assert.False(result!.Success);
        var audit = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.Equal("terminal-stem-task", audit["providerTaskId"]!.GetValue<string>());
        Assert.False(audit["recoverableProviderTask"]!.GetValue<bool>());
        Assert.Equal(0, audit["credits"]!["consumed"]!.GetValue<int>());
    }

    private static ProviderConfig Config(Dictionary<string, object?> extra) => new()
    {
        Type = "Suno",
        ApiKey = "secret-test-key",
        Extra = extra,
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(string contentType, byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) },
        },
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
