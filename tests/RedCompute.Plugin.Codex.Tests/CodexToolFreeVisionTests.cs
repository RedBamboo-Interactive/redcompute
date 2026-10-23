using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexToolFreeVisionTests : IAsyncLifetime
{
    private const string MessageSse =
        "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\"}]}}\n\n";

    private const string AgentMessageJsonl =
        "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"ok\"}}\n" +
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"redcompute-codex-vision-test-{Guid.NewGuid():N}");
    private readonly Upstream _upstream = new();
    private CodexToolFreeRelay _relay = null!;

    public Task InitializeAsync()
    {
        _relay = new CodexToolFreeRelay((_, _) => { }, _upstream,
            new Uri("https://chatgpt.test/backend-api/codex/"), new Uri("https://api.test/v1/"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    internal static GenerateImage Orchid()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vision", "vision-orchid.png"));
        return new GenerateImage("image/png", bytes, 480, 160, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void Codex_claims_generate_vision()
        => Assert.True(CodexProvider.DeclaredCapabilities.HasFlag(SessionCapabilities.GenerateVision));

    [Fact]
    public void Sanitizer_keeps_only_text_parts_and_appends_exactly_the_validated_images_to_the_last_user_message()
    {
        var image = Orchid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "gpt-test",
            input = new object[]
            {
                new { type = "message", role = "developer", content = new object[]
                {
                    new { type = "input_text", text = "dev" },
                    new { type = "input_image", image_url = "data:image/png;base64,Zm9yZWlnbg==" },
                } },
                new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "env" } } },
                new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "earlier" } } },
                new { type = "message", role = "user", content = new object[]
                {
                    new { type = "input_text", text = "<image name=[Image #1] path=\"x.png\">" },
                    new { type = "input_image", image_url = "https://attacker.example/pixel.png" },
                    new { type = "input_file", file_url = "file:///C:/secret.txt" },
                    new { type = "input_text", text = "describe" },
                } },
            },
        });

        var sanitized = CodexToolFreeRelay.SanitizeRequest(body, [image]);

        Assert.Null(sanitized.Error);
        Assert.Equal(3, sanitized.DroppedContentParts);
        Assert.Equal([image.Sha256], sanitized.InjectedImageHashes);
        var input = JsonNode.Parse(sanitized.Body!)!["input"]!.AsArray();
        var images = input.SelectMany(item => item!["content"]!.AsArray())
            .Where(part => part!["type"]!.GetValue<string>() == "input_image").ToList();
        var only = Assert.Single(images);
        Assert.Equal(image.DataUrl, only!["image_url"]!.GetValue<string>());
        Assert.Same(input[3]!["content"]!.AsArray(), only.Parent);
        Assert.DoesNotContain("attacker.example", Encoding.UTF8.GetString(sanitized.Body!));
        Assert.DoesNotContain("Zm9yZWlnbg", Encoding.UTF8.GetString(sanitized.Body!));
        Assert.DoesNotContain("input_file", Encoding.UTF8.GetString(sanitized.Body!));
        Assert.Single(input[0]!["content"]!.AsArray());
    }

    [Fact]
    public void Sanitizer_turns_bare_string_content_into_text_before_the_images()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { input = new object[] { new { role = "user", content = "describe" } } });

        var sanitized = CodexToolFreeRelay.SanitizeRequest(body, [Orchid()]);

        var content = JsonNode.Parse(sanitized.Body!)!["input"]![0]!["content"]!.AsArray();
        Assert.Equal(["input_text", "input_image"], content.Select(part => part!["type"]!.GetValue<string>()));
        Assert.Equal("describe", content[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Sanitizer_without_images_strips_any_image_codex_sends()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new object[] { new { type = "message", role = "user", content = new object[]
            {
                new { type = "input_text", text = "hi" },
                new { type = "input_image", image_url = "data:image/png;base64,AAAA" },
            } } },
        });

        var sanitized = CodexToolFreeRelay.SanitizeRequest(body);

        Assert.Equal(1, sanitized.DroppedContentParts);
        Assert.Empty(sanitized.InjectedImageHashes!);
        Assert.DoesNotContain("input_image", Encoding.UTF8.GetString(sanitized.Body!));
    }

    [Fact]
    public void Sanitizer_refuses_images_when_there_is_no_user_message_to_carry_them()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            input = new object[] { new { type = "message", role = "developer", content = "dev" } },
        });

        Assert.Contains("no user message", CodexToolFreeRelay.SanitizeRequest(body, [Orchid()]).Error);
    }

    [Fact]
    public void A_request_that_did_not_carry_every_validated_image_is_not_verified()
    {
        var evidence = new ToolFreeEvidence { ExpectedImages = 2 };
        evidence.RecordRequest(0, 0, [], 0, ["only-one"]);
        evidence.RecordResponse(["message"]);

        Assert.Contains("did not carry all 2", evidence.VerificationError);
    }

    [Fact]
    public async Task Relay_forwards_exactly_the_registered_image_and_records_its_hash()
    {
        var image = Orchid();
        using var lease = await _relay.OpenAsync(CancellationToken.None, [image]);

        var status = await PostAsync(lease.BaseUrl, foreignImage: true);

        Assert.Equal(HttpStatusCode.OK, status);
        var sent = JsonNode.Parse(Assert.Single(_upstream.Bodies))!;
        var parts = sent["input"]!.AsArray().SelectMany(item => item!["content"]!.AsArray()).ToList();
        Assert.Equal(image.DataUrl, Assert.Single(parts, part => part!["type"]!.GetValue<string>() == "input_image")!["image_url"]!.GetValue<string>());
        Assert.Equal([[image.Sha256]], lease.Evidence.InjectedImages);
        Assert.Equal(1, lease.Evidence.DroppedContentParts);
        Assert.Null(lease.Evidence.VerificationError);
    }

    [Fact]
    public async Task Tool_free_generate_gives_codex_no_image_file_and_verifies_the_injected_image()
    {
        var image = Orchid();
        var service = CreateService(out var launches, startInfo =>
        {
            var baseUrl = startInfo.ArgumentList.Single(arg => arg.StartsWith("model_providers.redcompute_tool_free.base_url="))
                .Split('=', 2)[1].Trim('"');
            PostAsync(new Uri(baseUrl), foreignImage: false).GetAwaiter().GetResult();
            return CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl);
        });
        ToolFreeEvidence? evidence = null;

        var result = await service.ExecuteToolFreeGenerateAsync("describe", null, 10, CancellationToken.None,
            evidence: e => evidence = e, images: [image]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(SessionIsolation.VerifiedToolFree, result.Isolation);
        var args = Assert.Single(launches).ArgumentList;
        Assert.DoesNotContain(args, arg => arg.StartsWith("--image") || arg == "-i");
        Assert.Equal([[image.Sha256]], evidence!.InjectedImages);
        Assert.Contains(image.DataUrl, Assert.Single(_upstream.Bodies));
    }

    [Fact]
    public async Task Sandboxed_generate_passes_images_as_private_files_outside_the_working_directory()
    {
        var image = Orchid();
        string? path = null;
        byte[]? written = null;
        var service = CreateService(out var launches, startInfo =>
        {
            var arg = startInfo.ArgumentList.Single(a => a.StartsWith("--image="));
            path = arg["--image=".Length..];
            written = File.ReadAllBytes(path);
            Assert.False(path.StartsWith(startInfo.WorkingDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(Directory.EnumerateFileSystemEntries(startInfo.WorkingDirectory));
            return CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl);
        });

        var result = await service.ExecuteGenerateAsync("describe", null, 10, CancellationToken.None, images: [image]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(SessionIsolation.SandboxedBestEffort, result.Isolation);
        Assert.Equal(image.Bytes, written);
        Assert.False(File.Exists(path));
        Assert.Single(launches);
    }

    [Fact]
    public void A_failed_image_write_leaves_no_temporary_directory_behind()
    {
        static HashSet<string> ImageDirectories() => Directory
            .EnumerateDirectories(Path.GetTempPath(), "redcompute-codex-images-*").ToHashSet();
        var before = ImageDirectories();
        var original = CodexSessionService.ImageFiles.WriteFile;
        var writes = 0;
        CodexSessionService.ImageFiles.WriteFile = (path, bytes) =>
        {
            if (++writes == 2) throw new IOException("disk full");
            original(path, bytes);
        };
        try
        {
            Assert.Throws<IOException>(() => CodexSessionService.ImageFiles.Create([Orchid(), Orchid()]));
        }
        finally
        {
            CodexSessionService.ImageFiles.WriteFile = original;
        }

        Assert.Equal(2, writes);
        Assert.Empty(ImageDirectories().Except(before));
    }

    // --- helpers ---

    private CodexSessionService CreateService(out List<ProcessStartInfo> launches, Func<ProcessStartInfo, Process?> launch)
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "fixture");
        var service = new CodexSessionService(new CodexConfig { CodexPath = codexPath },
            DispatchProxy.Create<IJobTracker, CodexGenerateIsolationTests.NullJobTracker>(),
            new CodexGenerateIsolationTests.EmptyStore(), (_, _) => { })
        {
            ToolFreeRelay = _relay,
        };
        var captured = new List<ProcessStartInfo>();
        launches = captured;
        service.LaunchExecProcess = startInfo =>
        {
            captured.Add(startInfo);
            return launch(startInfo);
        };
        return service;
    }

    private static async Task<HttpStatusCode> PostAsync(Uri baseUrl, bool foreignImage)
    {
        var content = new List<object> { new { type = "input_text", text = "describe" } };
        if (foreignImage) content.Add(new { type = "input_image", image_url = "data:image/png;base64,Zm9yZWlnbg==" });
        var body = JsonSerializer.Serialize(new
        {
            model = "gpt-test",
            stream = true,
            input = new object[] { new { type = "message", role = "user", content } },
        });
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer fixture-token");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", "acct-fixture");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class Upstream : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MessageSse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
