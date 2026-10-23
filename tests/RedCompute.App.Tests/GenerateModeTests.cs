using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class GenerateModeTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"mode\":\"oneshot\"}", true)]
    [InlineData("{\"mode\":\"ONESHOT\"}", true)]
    [InlineData("{\"mode\":\"session\"}", false)]
    [InlineData("{\"mode\":null}", false)]
    [InlineData("{\"mode\":42}", false)]
    public void GenerateEndpoint_AcceptsOnlyStatelessMode(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, UnifiedSessionEndpoints.IsStatelessGenerateMode(document.RootElement));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"confidential\":false}", false)]
    [InlineData("{\"confidential\":true}", true)]
    [InlineData("{\"confidential\":\"true\"}", false)]
    [InlineData("{\"confidential\":null}", false)]
    public void GenerateEndpoint_ConfidentialityIsExplicitAndTighteningOnly(
        string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected,
            UnifiedSessionEndpoints.IsConfidentialGenerate(document.RootElement));
    }

    [Fact]
    public async Task GenerateEndpoint_AcceptsRequireToolFreeOnCapableRuntime()
    {
        var result = await InvokeGenerateAsync(
            SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate,
            configure: provider => provider.Isolation = SessionIsolation.VerifiedToolFree);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(1, result.GenerateCalls);
        Assert.True(result.Provider.LastRequest!.RequireToolFree);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("generated", response.RootElement.GetProperty("text").GetString());
        Assert.Equal(SessionIsolation.VerifiedToolFree, response.RootElement.GetProperty("isolation").GetString());
        Assert.Equal("test-mechanism", response.RootElement.GetProperty("isolationMechanism").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SessionIsolation.SandboxedBestEffort)]
    [InlineData(SessionIsolation.Unverified)]
    public async Task GenerateEndpoint_WithholdsTextWhenACapableRuntimeDoesNotVerifyTheCall(string? reported)
    {
        var result = await InvokeGenerateAsync(
            SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate,
            configure: provider => provider.Isolation = reported);

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(1, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("tool_free_unverified", response.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("generated", result.Body);
    }

    [Fact]
    public async Task GenerateEndpoint_RefusesRequireToolFreeOnIncapableRuntime()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("tool_free_unavailable", response.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_KeepsExistingBehaviorWhenRequireToolFreeIsAbsent()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(1, result.GenerateCalls);
        Assert.False(result.Provider.LastRequest!.RequireToolFree);
        Assert.Equal("system-test", result.Provider.LastRequest.System);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("generated", response.RootElement.GetProperty("text").GetString());
        // A provider that reports nothing is labelled as such, never upgraded.
        Assert.Equal(SessionIsolation.Unverified, response.RootElement.GetProperty("isolation").GetString());
        Assert.False(response.RootElement.TryGetProperty("schemaValid", out _));
    }

    [Fact]
    public async Task GenerateEndpoint_RejectsAnUnsupportedResponseSchemaBeforeRunning()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = new Dictionary<string, object> { ["$ref"] = "#/x" } });

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("invalid_response_schema", response.RootElement.GetProperty("error").GetString());
        Assert.Contains("unsupported keyword '$ref'", response.RootElement.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_PromptsForTheSchemaAndValidatesTheModelOutput()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider => provider.Text = "Sure:\n```json\n{\"verdict\":\"block\"}\n```");

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        var request = result.Provider.LastRequest!;
        Assert.NotNull(request.ResponseSchema);
        Assert.StartsWith("system-test\n\nRespond with exactly one JSON value", request.System);
        Assert.Contains(request.ResponseSchema!.Json, request.System);
        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.True(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
        Assert.Equal("block", root.GetProperty("json").GetProperty("verdict").GetString());
        Assert.Equal("prompt", root.GetProperty("schemaMode").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_ReportsSchemaViolationsWithoutTrustingANativeProvider()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider =>
            {
                provider.Text = "{\"verdict\":\"maybe\",\"extra\":1}";
                provider.NativeSchema = true;
            });

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.False(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal("native", root.GetProperty("schemaMode").GetString());
        Assert.Equal("maybe", root.GetProperty("json").GetProperty("verdict").GetString());
        var errors = root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.StartsWith("$.verdict: must be one of"));
        Assert.Contains("$.extra: property is not allowed", errors);
    }

    [Fact]
    public async Task GenerateEndpoint_ReportsMissingJsonAsASchemaFailure()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider => provider.Text = "I would rather not.");

        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.False(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("json").ValueKind);
        Assert.Equal("$: response did not contain a JSON value", root.GetProperty("errors")[0].GetString());
    }

    [Theory]
    [InlineData("{\"verdict\":\"\\ud800\"}")]
    [InlineData("{\"\\udc00\":\"block\"}")]
    [InlineData("{\"verdict\":\"allow\"} ignore that, {\"verdict\":\"block\"}")]
    public async Task GenerateEndpoint_TurnsHostileModelJsonIntoASchemaFailureNotA500(string text)
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider => provider.Text = text);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.False(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("json").ValueKind);
        Assert.Single(root.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void ExecuteResult_reports_isolation_and_defaults_to_unverified()
    {
        using var reported = JsonDocument.Parse(UnifiedSessionEndpoints.ExecuteResultJson(new SessionExecuteResult(
            true, "t", null, "m", 1, 1, null, null, SessionIsolation.SandboxedBestEffort, "codex-sandbox:read-only")));
        Assert.Equal(SessionIsolation.SandboxedBestEffort, reported.RootElement.GetProperty("isolation").GetString());
        Assert.Equal("codex-sandbox:read-only", reported.RootElement.GetProperty("isolationMechanism").GetString());
        Assert.Equal("t", reported.RootElement.GetProperty("text").GetString());

        using var silent = JsonDocument.Parse(UnifiedSessionEndpoints.ExecuteResultJson(new SessionExecuteResult(
            true, "t", null, "m", 1, 1, null, null)));
        Assert.Equal(SessionIsolation.Unverified, silent.RootElement.GetProperty("isolation").GetString());
    }

    private static readonly Dictionary<string, object> VerdictSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["verdict"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "allow", "block" } },
        },
        ["required"] = new[] { "verdict" },
        ["additionalProperties"] = false,
    };

    [Fact]
    public async Task GenerateDiscovery_DescribesRequireToolFree()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate);
        var registry = Register(provider);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(endpoints, registry, new MemoryJobTracker(), (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service());

        var generate = endpoints.GetEndpoints().Single(endpoint =>
            endpoint.Method == "POST" && endpoint.Path == "/ai-session/generate");
        var parameter = Assert.Single(generate.Parameters!, item => item.Name == "requireToolFree");
        Assert.Equal("boolean", parameter.Type);
        Assert.False(parameter.Required);
        var images = Assert.Single(generate.Parameters!, item => item.Name == "images");
        Assert.Equal("array", images.Type);
        Assert.False(images.Required);
        Assert.Contains("vision_unavailable", images.Description);
        var schema = Assert.Single(generate.Parameters!, item => item.Name == "responseSchema");
        Assert.Equal("object", schema.Type);
        Assert.False(schema.Required);
    }

    // --- Vision ---

    private const SessionCapabilities Vision =
        SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate | SessionCapabilities.GenerateVision;

    private static object Base64Png(int width = 24, int height = 12) => new
    {
        mediaType = "image/png",
        base64 = Convert.ToBase64String(GenerateImageValidatorTests.Encode(
            new System.Windows.Media.Imaging.PngBitmapEncoder(), width, height)),
    };

    [Fact]
    public async Task GenerateEndpoint_RefusesImagesForAProviderWithoutVisionBeforeDecoding()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate,
            extra: new() { ["images"] = new object[] { new { mediaType = "image/png", base64 = "not even base64" } } });

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("vision_unavailable", response.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_DeliversValidatedImagesAndReportsVision()
    {
        var result = await InvokeGenerateAsync(Vision,
            extra: new() { ["images"] = new[] { Base64Png(), Base64Png(30, 10) } },
            configure: provider => provider.Isolation = SessionIsolation.VerifiedToolFree);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        var images = result.Provider.LastRequest!.Images!;
        Assert.Equal(2, images.Count);
        Assert.Equal((24, 12), (images[0].Width, images[0].Height));
        Assert.Equal((30, 10), (images[1].Width, images[1].Height));
        Assert.True(result.Provider.LastRequest.RequireToolFree);
        using var response = JsonDocument.Parse(result.Body);
        Assert.True(response.RootElement.GetProperty("vision").GetBoolean());
        Assert.Equal(2, response.RootElement.GetProperty("imageCount").GetInt32());
        Assert.Equal(SessionIsolation.VerifiedToolFree, response.RootElement.GetProperty("isolation").GetString());

        // The job keeps each image's identity, never its bytes.
        var input = result.Jobs.LastSubmission!.InputJson;
        Assert.Contains(images[0].Sha256, input);
        Assert.DoesNotContain(images[0].Base64[..32], input);
    }

    [Fact]
    public async Task GenerateEndpoint_ResolvesStagedImageAttachments()
    {
        var png = GenerateImageValidatorTests.Encode(new System.Windows.Media.Imaging.PngBitmapEncoder(), 16, 16);
        var result = await InvokeGenerateAsync(Vision, requireToolFree: null,
            stage: async (store, extra) =>
            {
                var staged = await store.UploadBytesAsync(png, "photo.png", "image/png", "local-user");
                var other = await store.UploadBytesAsync(png, "theirs.png", "image/png", "someone-else");
                extra["images"] = new object[] { new { attachmentId = staged.Id } };
                extra["otherId"] = other.Id;
            });

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(png, Assert.Single(result.Provider.LastRequest!.Images!).Bytes);

        var foreign = await InvokeGenerateAsync(Vision, requireToolFree: null,
            stage: async (store, extra) =>
            {
                var other = await store.UploadBytesAsync(png, "theirs.png", "image/png", "someone-else");
                extra["images"] = new object[] { new { attachmentId = other.Id } };
            });
        Assert.Equal(422, foreign.StatusCode);
        Assert.Contains("images[0]: attachment is not available to this caller", foreign.Body);
    }

    public static TheoryData<object, string> RefusedImages => new()
    {
        { "not an array", "images must be an array" },
        { Enumerable.Range(0, 5).Select(_ => Base64Png()).ToArray(), "at most 4 images" },
        { new object[] { 7 }, "images[0]: must be an object" },
        { new object[] { new { base64 = "AAAA" } }, "images[0]: base64 images require mediaType" },
        { new object[] { new { mediaType = "image/bmp", base64 = "AAAA" } }, "is not one of" },
        { new object[] { new { mediaType = "image/png", base64 = "@@@" } }, "base64 is not valid" },
        { new object[] { new { mediaType = "image/png", base64 = "AAAA", attachmentId = "x" } }, "exactly one of" },
        { new object[] { new { mediaType = "image/png", base64 = new string('A', GenerateImageValidator.MaxBase64Length + 4) } }, "exceeds" },
        { new object[] { Base64Png(), new { mediaType = "image/png", base64 = Convert.ToBase64String(new byte[64]) } }, "images[1]: image is not PNG" },
        { new object[] { new { mediaType = "image/jpeg", base64 = Convert.ToBase64String(GenerateImageValidatorTests.Encode(
            new System.Windows.Media.Imaging.PngBitmapEncoder(), 4, 4)) } }, "does not match the image data" },
    };

    [Theory]
    [MemberData(nameof(RefusedImages))]
    public async Task GenerateEndpoint_RefusesInvalidImagesWithoutCallingTheProvider(object images, string expected)
    {
        var result = await InvokeGenerateAsync(Vision, extra: new() { ["images"] = images });

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("invalid_images", response.RootElement.GetProperty("error").GetString());
        Assert.Contains(response.RootElement.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains(expected));
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(40_000)]
    public async Task GenerateEndpoint_RefusesAGifBombAsInvalidImagesNotA500(int side)
    {
        var gif = GifWithHugeFrame(side);

        var result = await InvokeGenerateAsync(Vision,
            extra: new() { ["images"] = new[] { new { mediaType = "image/gif", base64 = Convert.ToBase64String(gif) } } });

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        Assert.Contains($"images[0]: GIF frame is {side}x{side}", result.Body);
    }

    [Fact]
    public async Task GenerateEndpoint_TurnsAnUnexpectedImageReadFailureIntoInvalidImages()
    {
        var png = GenerateImageValidatorTests.Encode(new System.Windows.Media.Imaging.PngBitmapEncoder(), 8, 8);
        var result = await InvokeGenerateAsync(Vision,
            stage: async (store, extra) =>
            {
                var staged = await store.UploadBytesAsync(png, "gone.png", "image/png", "local-user");
                // The staged file vanishes between staging and use (cleanup, disk error, ...).
                File.SetAttributes(staged.StoredPath, FileAttributes.Normal);
                File.Delete(staged.StoredPath);
                extra["images"] = new object[] { new { attachmentId = staged.Id } };
            });

        Assert.Equal(422, result.StatusCode);
        Assert.Contains("images[0]: image could not be read (FileNotFoundException)", result.Body);
    }

    [Fact]
    public async Task GenerateBody_RaisesOnlyItsOwnLimitAndAnswers413WhenExceeded()
    {
        var limit = new BodySizeFeature();
        var allowed = new DefaultHttpContext();
        allowed.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(limit);
        allowed.Request.ContentType = "application/json";
        allowed.Request.Body = new MemoryStream("{}"u8.ToArray());
        var (_, error) = await UnifiedSessionEndpoints.ReadGenerateBodyAsync(allowed);
        Assert.Null(error);
        Assert.Equal(UnifiedSessionEndpoints.MaxGenerateBodyBytes, limit.MaxRequestBodySize);
        Assert.True(UnifiedSessionEndpoints.MaxGenerateBodyBytes >
            GenerateImageValidator.MaxImages * (long)GenerateImageValidator.MaxBase64Length + 1_000_000);

        var declared = new DefaultHttpContext();
        declared.Request.ContentType = "application/json";
        declared.Request.ContentLength = UnifiedSessionEndpoints.MaxGenerateBodyBytes + 1;
        Assert.Equal(413, await StatusOf(declared));

        var streamed = new DefaultHttpContext();
        streamed.Request.ContentType = "application/json";
        streamed.Request.Body = new ThrowingBody(new BadHttpRequestException("too large", 413));
        Assert.Equal(413, await StatusOf(streamed));

        var malformed = new DefaultHttpContext();
        malformed.Request.ContentType = "application/json";
        malformed.Request.Body = new MemoryStream("{not json"u8.ToArray());
        Assert.Equal(400, await StatusOf(malformed));
    }

    [Fact]
    public async Task GenerateOverKestrel_AcceptsABodyAboveTheServerDefaultAndRefusesOneAboveItsLimit()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(Vision);
        var builder = WebApplication.CreateSlimBuilder();
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, "http://127.0.0.1:0");
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), Register(provider), new MemoryJobTracker(),
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());
        await app.StartAsync();
        var address = app.Urls.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        async Task<int> PostAsync(int padding, bool chunked, bool expectContinue = false)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["provider"] = "test",
                ["messages"] = new[] { new { role = "user", content = "hello" } },
                ["padding"] = new string('x', padding),
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/ai-session/generate");
            request.Headers.Add("X-System-Reason", "body limit contract test");
            request.Headers.ExpectContinue = expectContinue;
            request.Content = chunked
                ? new StreamContent(new NonSeekable(json))
                : new ByteArrayContent(json);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(request);
            return (int)response.StatusCode;
        }

        // Kestrel's own default (30,000,000 bytes) would refuse this one.
        Assert.Equal(200, await PostAsync(31_000_000, chunked: false));
        Assert.Equal(200, await PostAsync(31_000_000, chunked: true));
        // A refused upload is answered before the client finishes sending. Only a client that
        // waits for 100-continue reliably reads that 413 instead of a reset connection; the
        // streamed-over-limit path is covered by GenerateBody_RaisesOnlyItsOwnLimitAndAnswers413WhenExceeded.
        Assert.Equal(413, await PostAsync(34_000_000, chunked: false, expectContinue: true));
        await app.StopAsync();
    }

    private static async Task<int> StatusOf(HttpContext context)
    {
        var (_, error) = await UnifiedSessionEndpoints.ReadGenerateBodyAsync(context);
        Assert.NotNull(error);
        context.RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
            Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
        context.Response.Body = new MemoryStream();
        await error.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static byte[] GifWithHugeFrame(int side)
    {
        var encoder = new System.Windows.Media.Imaging.GifBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
            2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var gif = stream.ToArray();
        var offset = 13 + ((gif[10] & 0x80) != 0 ? 3 << ((gif[10] & 7) + 1) : 0);
        while (gif[offset] == 0x21)
        {
            offset += 2;
            while (gif[offset] != 0) offset += gif[offset] + 1;
            offset++;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(offset + 5), (ushort)side);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(offset + 7), (ushort)side);
        return gif;
    }

    private sealed class BodySizeFeature : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    private sealed class ThrowingBody(Exception error) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw error;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw error;
    }

    /// <summary>Forces chunked transfer: HttpClient cannot learn its length.</summary>
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [Fact]
    public async Task GenerateEndpoint_WithoutImagesReportsVisionCapabilityAndZeroImages()
    {
        var result = await InvokeGenerateAsync(Vision, configure: provider => provider.Isolation = SessionIsolation.VerifiedToolFree,
            extra: new() { ["images"] = Array.Empty<object>() });

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Provider.LastRequest!.Images);
        using var response = JsonDocument.Parse(result.Body);
        Assert.True(response.RootElement.GetProperty("vision").GetBoolean());
        Assert.Equal(0, response.RootElement.GetProperty("imageCount").GetInt32());
    }

    private static async Task<(int StatusCode, string Body, int GenerateCalls, GenerateProvider Provider, MemoryJobTracker Jobs)> InvokeGenerateAsync(
        SessionCapabilities capabilities, bool? requireToolFree = true,
        Dictionary<string, object?>? extra = null, Action<GenerateProvider>? configure = null,
        Func<InputAttachmentStore, Dictionary<string, object?>, Task>? stage = null)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(capabilities);
        var proxy = (GenerateProvider)(object)provider;
        configure?.Invoke(proxy);
        extra ??= [];
        if (stage != null) await stage(f.Attachments, extra);
        var registry = Register(provider);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var jobs = new MemoryJobTracker();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, jobs,
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/ai-session/generate"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-System-Reason"] = "tool-free endpoint contract test";
        var request = new Dictionary<string, object?>
        {
            ["mode"] = "oneshot",
            ["provider"] = "test",
            ["messages"] = new[] { new { role = "user", content = "hello" } },
            ["system"] = "system-test",
        };
        if (requireToolFree.HasValue) request["requireToolFree"] = requireToolFree.Value;
        foreach (var (key, value) in extra) request[key] = value;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, response, proxy.GenerateCalls, proxy, jobs);
    }

    private static ITestProvider CreateProvider(SessionCapabilities capabilities)
    {
        var provider = DispatchProxy.Create<ITestProvider, GenerateProvider>();
        ((GenerateProvider)(object)provider).Capabilities = capabilities;
        return provider;
    }

    private static CapabilityRegistry Register(ITestProvider provider)
    {
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition
        {
            Slug = "ai-session",
            DisplayName = "AI Session",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        return registry;
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class GenerateProvider : DispatchProxy
    {
        public SessionCapabilities Capabilities { get; set; }
        public int GenerateCalls { get; private set; }
        public string? Isolation { get; set; }
        public string Text { get; set; } = "generated";
        public bool NativeSchema { get; set; }
        public SessionGenerateRequest? LastRequest { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method!.Name switch
            {
                "get_ProviderId" => "test",
                "get_ProviderDisplayName" => "Test",
                "get_Capabilities" => Capabilities,
                "get_Name" => "Test",
                "get_CapabilitySlug" => "ai-session",
                "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
                nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
                nameof(ISessionProvider.GenerateAsync) => Generate(args),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<SessionGenerateResult> Generate(object?[]? args)
        {
            GenerateCalls++;
            LastRequest = args?[0] as SessionGenerateRequest;
            return Task.FromResult(new SessionGenerateResult(
                true, Text, "stream", "test-model", 1, 1, null, null,
                Isolation, Isolation == null ? null : "test-mechanism", NativeSchema));
        }
    }

    private sealed class MemoryJobTracker : IJobTracker
    {
        public JobSubmission? LastSubmission { get; private set; }

        public JobRecord CreateJob(JobSubmission submission)
        {
            LastSubmission = submission;
            return new() { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        }
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class BodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
