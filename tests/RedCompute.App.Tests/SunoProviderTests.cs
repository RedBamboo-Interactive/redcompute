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
    public async Task GenerateUsesSpendGateAndReturnsNamedIngestedArtifactsWithoutLeakingCredential()
    {
        var creditReads = 0;
        var apiHandler = new DelegateHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.RequestUri!.AbsolutePath.EndsWith("/generate/credit"))
                return Json($"{{\"code\":200,\"data\":{(++creditReads == 1 ? 100 : 90)}}}");
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
            ["Credits.Generate"] = 10,
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
                ["confirmPaid"] = true,
                ["approvedMaxCredits"] = 10,
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
        Assert.Equal(2, metadata["schemaVersion"]!.GetValue<int>());
        Assert.Equal(2, metadata["tracks"]!.AsArray().Count);
        Assert.Equal(4, metadata["artifacts"]!.AsArray().Count);
        Assert.Equal(10, metadata["credits"]!["consumed"]!.GetValue<int>());
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
            ["Credits.SplitStem"] = 50,
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
                ["confirmPaid"] = true,
                ["approvedMaxCredits"] = 50,
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
    public async Task MissingSpendApprovalIsRejectedBeforeAnyVendorCall()
    {
        var provider = new SunoProvider(Config([]), "music-gen", _ => { });
        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["operation"] = "generate",
            ["prompt"] = "prompt",
            ["style"] = "style",
            ["title"] = "title",
        });
        Assert.Contains("confirmPaid", errors.Keys);
        Assert.Contains("approvedMaxCredits", errors.Keys);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitDurationIsRejectedForModelsThatDoNotSupportIt()
    {
        var provider = new SunoProvider(Config(new Dictionary<string, object?>
        {
            ["Model"] = "V5",
            ["Credits.Generate"] = 10,
        }), "music-gen", _ => { });
        var errors = provider.ValidateParameters(new Dictionary<string, object?>
        {
            ["operation"] = "generate",
            ["prompt"] = "prompt",
            ["style"] = "style",
            ["title"] = "title",
            ["duration"] = 32,
            ["confirmPaid"] = true,
            ["approvedMaxCredits"] = 10,
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
    public async Task Rejected_admission_keeps_a_credit_audit_in_the_failed_result()
    {
        var apiHandler = new DelegateHandler(request =>
        {
            Assert.EndsWith("/generate/credit", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Json("{\"code\":200,\"data\":20}");
        });
        await using var provider = new SunoProvider(
            Config(new Dictionary<string, object?> { ["Credits.Generate"] = 50 }),
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
                ["confirmPaid"] = true,
                ["approvedMaxCredits"] = 50,
            },
        });

        Assert.False(result!.Success);
        var audit = JsonNode.Parse(result.ResultJson!)!.AsObject();
        Assert.Equal("failed", audit["status"]!.GetValue<string>());
        Assert.Equal(20, audit["credits"]!["before"]!.GetValue<int>());
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
