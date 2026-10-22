using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexToolFreeGenerateTests : IAsyncLifetime
{
    private const string MessageSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"r1\",\"tools\":[],\"tool_choice\":\"none\",\"output\":[]}}\n\n" +
        "event: response.output_item.done\n" +
        "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"summary\":[]}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n" +
        "event: response.output_item.done\n" +
        "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"output\":[{\"type\":\"message\"}]}}\n\n";

    private const string FunctionCallSse =
        "event: response.output_item.added\n" +
        "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"name\":\"shell\",\"call_id\":\"c1\",\"arguments\":\"\"}}\n\n" +
        "event: response.function_call_arguments.delta\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{\\\"cmd\\\":\\\"type C:\\\\\\\\secret\\\"}\"}\n\n";

    private const string AgentMessageJsonl =
        "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n" +
        "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"ok\"}}\n" +
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"redcompute-codex-toolfree-test-{Guid.NewGuid():N}");
    private readonly FakeUpstream _upstream = new();
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

    // --- Capability and contract ---

    [Fact]
    public void Codex_claims_tool_free_generate()
        => Assert.True(CodexProvider.DeclaredCapabilities.HasFlag(SessionCapabilities.ToolFreeGenerate));

    [Theory]
    [InlineData("read-only", SessionIsolation.SandboxedBestEffort)]
    [InlineData("workspace-write", SessionIsolation.SandboxedBestEffort)]
    [InlineData("danger-full-access", SessionIsolation.Unrestricted)]
    public void Execute_reports_its_sandbox_and_never_claims_tool_free(string sandbox, string expected)
    {
        var (isolation, mechanism) = CodexProvider.ExecuteIsolation(sandbox);

        Assert.Equal(expected, isolation);
        Assert.Equal($"codex-sandbox:{sandbox}", mechanism);
    }

    // --- Request sanitizer ---

    [Fact]
    public void Sanitizer_removes_every_tool_definition_and_keeps_only_messages()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["model"] = "gpt-test",
            ["stream"] = true,
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["tools"] = new object[] { new { type = "function", name = "shell" } },
            ["prompt"] = new { id = "pmpt_with_tools" },
            ["input"] = new object[]
            {
                new { type = "additional_tools", role = "developer", tools = new object[]
                {
                    new { type = "namespace", name = "functions", tools = new object[]
                    {
                        new { type = "custom", name = "exec" },
                        new { type = "function", name = "apply_patch" },
                    } },
                    new { type = "web_search", name = "web_search" },
                } },
                new { type = "message", role = "developer", content = new object[] { new { type = "input_text", text = "dev" } } },
                new { role = "user", content = "hello" },
                new { type = "function_call_output", call_id = "c0", output = "stale" },
            },
            ["text"] = new { format = new { type = "json_schema", strict = true, name = "s", schema = new { type = "object" } } },
        });

        var sanitized = CodexToolFreeRelay.SanitizeRequest(body);

        Assert.Null(sanitized.Error);
        var root = JsonNode.Parse(sanitized.Body!)!.AsObject();
        Assert.Empty(root["tools"]!.AsArray());
        Assert.Equal("none", root["tool_choice"]!.GetValue<string>());
        Assert.False(root["parallel_tool_calls"]!.GetValue<bool>());
        Assert.False(root.ContainsKey("prompt"));
        Assert.Equal(["prompt"], sanitized.DroppedFields);
        var input = root["input"]!.AsArray();
        Assert.Equal(2, input.Count);
        Assert.Equal("dev", input[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("hello", input[1]!["content"]!.GetValue<string>());
        Assert.Equal(2, sanitized.DroppedInputItems);
        Assert.True(sanitized.StrippedToolDefinitions >= 4, $"stripped {sanitized.StrippedToolDefinitions}");
        Assert.Equal("json_schema", root["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("\"shell\"", Encoding.UTF8.GetString(sanitized.Body!));
        Assert.DoesNotContain("apply_patch", Encoding.UTF8.GetString(sanitized.Body!));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    [InlineData("{\"input\":{\"type\":\"message\"}}")]
    public void Sanitizer_refuses_bodies_it_cannot_reason_about(string body)
        => Assert.NotNull(CodexToolFreeRelay.SanitizeRequest(Encoding.UTF8.GetBytes(body)).Error);

    // --- Response inspector ---

    [Fact]
    public void Inspector_passes_messages_and_reasoning()
    {
        var inspection = CodexToolFreeRelay.InspectResponse(Encoding.UTF8.GetBytes(MessageSse), "text/event-stream");

        Assert.Null(inspection.Violation);
        Assert.Equal(["reasoning", "message"], inspection.OutputItemTypes);
    }

    [Theory]
    [InlineData("{\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"name\":\"shell\"}}", "function_call")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"custom_tool_call\",\"name\":\"exec\"}}", "custom_tool_call")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"local_shell_call\"}}", "local_shell_call")]
    [InlineData("{\"type\":\"response.web_search_call.in_progress\"}", "web_search_call")]
    [InlineData("{\"type\":\"response.mcp_list_tools.completed\"}", "mcp")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"message\"},{\"type\":\"mcp_call\"}]}}", "mcp_call")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"something_new\"}}", "something_new")]
    public void Inspector_names_tool_activity_and_unknown_items(string payload, string expected)
    {
        var sse = $"data: {payload}\n\n";

        var inspection = CodexToolFreeRelay.InspectResponse(Encoding.UTF8.GetBytes(sse), "text/event-stream");

        Assert.NotNull(inspection.Violation);
        Assert.Contains(expected, inspection.Violation);
    }

    [Fact]
    public void Inspector_checks_non_streamed_responses()
    {
        var json = "{\"id\":\"r1\",\"output\":[{\"type\":\"message\"},{\"type\":\"web_search_call\"}]}";

        var inspection = CodexToolFreeRelay.InspectResponse(Encoding.UTF8.GetBytes(json), "application/json");

        Assert.Contains("web_search_call", inspection.Violation);
    }

    // --- Relay over HTTP ---

    [Fact]
    public async Task Relay_forwards_a_sanitized_request_with_the_callers_credentials_and_verifies_the_call()
    {
        _upstream.Respond(MessageSse);
        using var lease = await _relay.OpenAsync(CancellationToken.None);

        var response = await PostResponsesAsync(lease.BaseUrl, chatGpt: true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(MessageSse, response.Body);
        var sent = Assert.Single(_upstream.Requests);
        Assert.Equal("https://chatgpt.test/backend-api/codex/responses", sent.Uri.ToString());
        Assert.Equal("Bearer fixture-token", sent.Headers["Authorization"]);
        Assert.Equal("acct-fixture", sent.Headers["chatgpt-account-id"]);
        Assert.False(sent.Headers.ContainsKey("Host"));
        var forwarded = JsonNode.Parse(sent.Body)!.AsObject();
        Assert.Empty(forwarded["tools"]!.AsArray());
        Assert.Equal("none", forwarded["tool_choice"]!.GetValue<string>());
        Assert.Single(forwarded["input"]!.AsArray());
        Assert.Null(lease.Evidence.VerificationError);
        Assert.Equal(1, lease.Evidence.Responses);
        Assert.Equal(2, lease.Evidence.StrippedToolDefinitions);
    }

    [Fact]
    public async Task Relay_uses_the_api_key_upstream_without_a_chatgpt_account()
    {
        _upstream.Respond(MessageSse);
        using var lease = await _relay.OpenAsync(CancellationToken.None);

        await PostResponsesAsync(lease.BaseUrl, chatGpt: false);

        Assert.Equal("https://api.test/v1/responses", Assert.Single(_upstream.Requests).Uri.ToString());
    }

    [Fact]
    public async Task Relay_withholds_a_tool_call_and_answers_with_a_terminal_error()
    {
        _upstream.Respond(FunctionCallSse);
        using var lease = await _relay.OpenAsync(CancellationToken.None);

        var response = await PostResponsesAsync(lease.BaseUrl, chatGpt: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.DoesNotContain("function_call\"", response.Body);
        Assert.DoesNotContain("secret", response.Body);
        Assert.Contains("withheld", response.Body);
        Assert.Contains("function_call", lease.Evidence.VerificationError);
        Assert.Equal(0, lease.Evidence.Responses);
    }

    [Fact]
    public async Task Relay_refuses_compressed_bodies_unknown_routes_and_revoked_leases()
    {
        using var lease = await _relay.OpenAsync(CancellationToken.None);
        using var client = new HttpClient();

        using var compressed = new HttpRequestMessage(HttpMethod.Post, $"{lease.BaseUrl}/responses")
        {
            Content = new ByteArrayContent([0x28, 0xb5, 0x2f, 0xfd]),
        };
        compressed.Content.Headers.ContentEncoding.Add("zstd");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(compressed)).StatusCode);

        var other = await client.PostAsync($"{lease.BaseUrl}/responses/compact", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Empty(_upstream.Requests);
        Assert.Equal(2, lease.Evidence.Refusals.Count);

        var stranger = new Uri(lease.BaseUrl.ToString().Replace(lease.BaseUrl.Segments[1].TrimEnd('/'), "0000"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{stranger}/models")).StatusCode);

        lease.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{lease.BaseUrl}/models")).StatusCode);
    }

    [Fact]
    public void Verification_requires_at_least_one_relayed_response()
        => Assert.NotNull(new ToolFreeEvidence().VerificationError);

    // --- Generate through the service ---

    [Fact]
    public async Task Tool_free_generate_points_codex_at_a_private_relay_route_and_reports_verified()
    {
        _upstream.Respond(MessageSse);
        var service = CreateService(out var launches, startInfo =>
        {
            SimulateCodexModelCall(startInfo);
            return CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl);
        });

        var result = await service.ExecuteToolFreeGenerateAsync("hello", "gpt-test", 10, CancellationToken.None, "low");

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", result.Text);
        Assert.Equal(SessionIsolation.VerifiedToolFree, result.Isolation);
        Assert.Equal("codex-model-boundary-relay", result.IsolationMechanism);
        var args = Assert.Single(launches).ArgumentList.ToList();
        Assert.Equal(["exec", "-", "--json", "--skip-git-repo-check", "--sandbox", "read-only"], args.Take(6));
        foreach (var flag in new[] { "--ephemeral", "--ignore-user-config", "--ignore-rules" })
            Assert.Contains(flag, args);
        Assert.Contains("enable_request_compression", args);
        Assert.Contains("model_provider=\"redcompute_tool_free\"", args);
        Assert.Contains("model_providers.redcompute_tool_free.requires_openai_auth=true", args);
        Assert.StartsWith("http://127.0.0.1:", RelayBaseUrl(launches[0]));
        Assert.DoesNotContain("--output-schema", args);
    }

    [Fact]
    public async Task Tool_free_generate_fails_closed_when_no_model_call_crossed_the_relay()
    {
        var service = CreateService(out _, _ => CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl));

        var result = await service.ExecuteToolFreeGenerateAsync("hello", null, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Null(result.Isolation);
        Assert.Contains("no model response passed through the tool-free relay", result.Error);
    }

    [Fact]
    public async Task Tool_free_generate_fails_closed_when_codex_reports_a_tool_item()
    {
        _upstream.Respond(MessageSse);
        var service = CreateService(out _, startInfo =>
        {
            SimulateCodexModelCall(startInfo);
            return CodexGenerateIsolationTests.StartOutputProcess(
                "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"dir\"}}\n" + AgentMessageJsonl);
        });

        var result = await service.ExecuteToolFreeGenerateAsync("hello", null, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("commandExecution", result.Error);
    }

    [Fact]
    public async Task Tool_free_generate_reports_the_withheld_tool_call()
    {
        _upstream.Respond(FunctionCallSse);
        var service = CreateService(out _, startInfo =>
        {
            SimulateCodexModelCall(startInfo);
            return CodexGenerateIsolationTests.StartOutputProcess(
                "{\"type\":\"turn.failed\",\"error\":{\"message\":\"withheld\"}}\n");
        });

        var result = await service.ExecuteToolFreeGenerateAsync("hello", null, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("function_call", result.Error);
    }

    private const string StrictSchema =
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"," +
        "\"properties\":{\"v\":{\"type\":\"string\"}},\"required\":[\"v\"],\"additionalProperties\":false}";

    [Fact]
    public async Task Generate_passes_only_the_strict_schema_without_root_schema_keyword_from_outside_the_working_directory()
    {
        string? schemaText = null;
        string? schemaPath = null;
        var service = CreateService(out var launches, startInfo =>
        {
            var args = startInfo.ArgumentList.ToList();
            schemaPath = args[args.IndexOf("--output-schema") + 1];
            schemaText = File.ReadAllText(schemaPath);
            Assert.False(schemaPath.StartsWith(startInfo.WorkingDirectory, StringComparison.OrdinalIgnoreCase));
            return CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl);
        });

        var result = await service.ExecuteGenerateAsync("hello", null, 10, CancellationToken.None,
            responseSchema: Schema(StrictSchema));

        Assert.True(result.Success, result.Error);
        Assert.True(result.NativeResponseSchema);
        Assert.Equal("{\"type\":\"object\",\"properties\":{\"v\":{\"type\":\"string\"}},\"required\":[\"v\"],\"additionalProperties\":false}", schemaText);
        Assert.False(File.Exists(schemaPath));
        Assert.Single(launches);
    }

    [Theory]
    [InlineData("{\"type\":\"object\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"v\":{\"type\":\"string\",\"pattern\":\"^a$\"}},\"required\":[\"v\"],\"additionalProperties\":false}")]
    public async Task Generate_never_passes_a_non_strict_schema_on_either_path(string schema)
    {
        _upstream.Respond(MessageSse);
        var service = CreateService(out var launches, startInfo =>
        {
            if (startInfo.ArgumentList.Any(arg => arg.StartsWith("model_providers.redcompute_tool_free.base_url=")))
                SimulateCodexModelCall(startInfo);
            return CodexGenerateIsolationTests.StartOutputProcess(AgentMessageJsonl);
        });

        var plain = await service.ExecuteGenerateAsync("hello", null, 10, CancellationToken.None, responseSchema: Schema(schema));
        var toolFree = await service.ExecuteToolFreeGenerateAsync("hello", null, 10, CancellationToken.None, responseSchema: Schema(schema));

        Assert.True(plain.Success && toolFree.Success, plain.Error ?? toolFree.Error);
        Assert.False(plain.NativeResponseSchema || toolFree.NativeResponseSchema);
        Assert.All(launches, launch => Assert.DoesNotContain("--output-schema", launch.ArgumentList));
    }

    [Fact]
    public async Task A_schema_the_model_api_rejects_falls_back_to_prompt_mode_once()
    {
        const string rejected =
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"{\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"message\\\":\\\"Invalid schema\\\"," +
            "\\\"type\\\":\\\"invalid_request_error\\\",\\\"param\\\":\\\"text.format.schema\\\",\\\"code\\\":\\\"invalid_json_schema\\\"},\\\"status\\\":400}\"}}\n";
        var service = CreateService(out var launches, startInfo =>
            CodexGenerateIsolationTests.StartOutputProcess(
                startInfo.ArgumentList.Contains("--output-schema") ? rejected : AgentMessageJsonl));

        var result = await service.ExecuteGenerateAsync("hello", null, 10, CancellationToken.None,
            responseSchema: Schema(StrictSchema));

        Assert.True(result.Success, result.Error);
        Assert.False(result.NativeResponseSchema);
        Assert.Equal(2, launches.Count);
        Assert.Contains("--output-schema", launches[0].ArgumentList);
        Assert.DoesNotContain("--output-schema", launches[1].ArgumentList);
    }

    [Fact]
    public void Only_codexs_own_turn_failure_counts_as_a_schema_rejection()
    {
        static CodexSessionService.ExecuteResult Failed(string stream)
            => new(false, null, stream, null, 0, 0, null, "failed");

        // The model can put the words in a message, but a message is an item, not a turn error.
        Assert.False(CodexSessionService.IsSchemaRejection(Failed(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"\\\"invalid_json_schema\\\" \\\"text.format.schema\\\"\"}}\n" +
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"withheld\"}}\n")));
        Assert.False(CodexSessionService.IsSchemaRejection(
            new CodexSessionService.ExecuteResult(true, "ok", "{\"type\":\"turn.failed\",\"error\":{\"message\":\"\\\"invalid_json_schema\\\" \\\"text.format.schema\\\"\"}}", null, 0, 0, null, null)));
        Assert.True(CodexSessionService.IsSchemaRejection(Failed(
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"\\\"param\\\":\\\"text.format.schema\\\",\\\"code\\\":\\\"invalid_json_schema\\\"\"}}")));
    }

    [Fact]
    public void Tool_free_exec_disables_codex_retries_and_outlasts_the_call_timeout_with_its_idle_timeout()
    {
        var args = CodexSessionService.ToolFreeExecArgs(new Uri("http://127.0.0.1:1/n/v1"), 300);

        Assert.Contains("model_providers.redcompute_tool_free.request_max_retries=0", args);
        Assert.Contains("model_providers.redcompute_tool_free.stream_max_retries=0", args);
        Assert.Contains("model_providers.redcompute_tool_free.stream_idle_timeout_ms=360000", args);
        Assert.True(CodexSessionService.ToolFreeStreamIdleTimeoutMs(600) > 600_000);
    }

    [Fact]
    public async Task Tool_free_generate_never_reruns_codex_after_a_failure()
    {
        var service = CreateService(out var launches, _ => CodexGenerateIsolationTests.StartOutputProcess(
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"The process cannot access the file because it is being used by another process\"}}\n"));

        var result = await service.ExecuteToolFreeGenerateAsync("hello", null, 10, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(launches);
    }

    // --- Codex JSONL inspection never throws and fails closed ---

    [Theory]
    [InlineData("{\"type\":7,\"item\":{\"type\":\"agent_message\"}}", "(uninspectable)")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":7}}", "(untyped)")]
    [InlineData("{\"type\":\"item.completed\",\"item\":\"command\"}", "(uninspectable)")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_exec", "(unparseable)")]
    public void Exec_item_inspection_reports_what_it_cannot_read(string line, string expected)
        => Assert.Equal([expected], CodexSessionService.ExecToolItemTypes(line + "\n"));

    [Fact]
    public void Exec_item_inspection_survives_undecodable_names()
    {
        Assert.Empty(CodexSessionService.ExecToolItemTypes(
            "{\"\\udc00\":1,\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\"}}\n"));
        Assert.Equal(["commandExecution"], CodexSessionService.ExecToolItemTypes(
            "{\"\\udc00\":1,\"type\":\"item.completed\",\"item\":{\"\\ud800x\":2,\"type\":\"command_execution\"}}\n"));
    }

    [Fact]
    public void Exec_item_inspection_ignores_non_json_noise_and_plain_items()
        => Assert.Empty(CodexSessionService.ExecToolItemTypes("warning: something\n[]\n" + AgentMessageJsonl));

    // --- Relay hardening ---

    [Fact]
    public void Upstream_redirects_are_never_followed()
        => Assert.False(CodexToolFreeRelay.CreateUpstreamHandler().AllowAutoRedirect);

    [Fact]
    public async Task Relay_refuses_an_upstream_redirect_instead_of_passing_it_to_codex()
    {
        _upstream.Redirect("https://elsewhere.test/steal");
        using var lease = await _relay.OpenAsync(CancellationToken.None);

        var response = await PostResponsesAsync(lease.BaseUrl, chatGpt: true);

        Assert.Equal(HttpStatusCode.BadGateway, response.Status);
        Assert.DoesNotContain("elsewhere", response.Body);
        Assert.Contains("redirect 302", lease.Evidence.VerificationError);
    }

    [Theory]
    [InlineData("localhost", null)]
    [InlineData("attacker.example", null)]
    [InlineData(null, "https://attacker.example")]
    [InlineData(null, "null")]
    public async Task Relay_serves_only_its_own_loopback_authority_without_an_origin(string? host, string? origin)
    {
        _upstream.Respond(MessageSse);
        using var lease = await _relay.OpenAsync(CancellationToken.None);
        using var client = new HttpClient();
        using var request = ResponsesRequest(lease.BaseUrl, chatGpt: true);
        if (host != null) request.Headers.Host = $"{host}:{lease.BaseUrl.Port}";
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.Requests);
        Assert.NotNull(lease.Evidence.VerificationError);
    }

    [Fact]
    public async Task Relay_evidence_names_what_crossed_the_boundary_without_values()
    {
        _upstream.Respond(MessageSse);
        using var lease = await _relay.OpenAsync(CancellationToken.None);
        using var client = new HttpClient();
        using var request = ResponsesRequest(lease.BaseUrl, chatGpt: true, extraField: true);

        await client.SendAsync(request);

        var evidence = lease.Evidence;
        Assert.Contains("chatgpt-account-id", evidence.ForwardedHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Authorization", evidence.ForwardedHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host", evidence.ForwardedHeaders, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["prompt"], evidence.DroppedFields);
        Assert.Equal(["https://chatgpt.test/backend-api/codex/responses"], evidence.Upstreams);
        Assert.Equal([200], evidence.UpstreamStatuses);
        Assert.DoesNotContain("fixture-token", string.Join(",", evidence.ForwardedHeaders));
    }

    [Fact]
    public void Undecodable_text_is_refused_by_the_sanitizer_and_the_inspector_without_throwing()
    {
        var request = Encoding.UTF8.GetBytes("{\"input\":[{\"type\":\"\\ud800\"}]}");
        Assert.NotNull(CodexToolFreeRelay.SanitizeRequest(request).Error);

        var sse = Encoding.UTF8.GetBytes("data: {\"\\udc00\":1,\"type\":\"response.output_item.done\",\"item\":{\"type\":\"\\ud800\"}}\n\n");
        Assert.NotNull(CodexToolFreeRelay.InspectResponse(sse, "text/event-stream").Violation);
    }

    // --- Helpers ---

    private static RedCompute.Core.Sessions.ResponseSchema Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(RedCompute.Core.Sessions.ResponseSchema.TryParse(document.RootElement, out var schema, out var errors),
            string.Join("; ", errors));
        return schema!;
    }

    private CodexSessionService CreateService(out List<ProcessStartInfo> launches, Func<ProcessStartInfo, Process?> launch)
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "fixture");
        var service = new CodexSessionService(
            new CodexConfig { CodexPath = codexPath },
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

    private static string RelayBaseUrl(ProcessStartInfo startInfo)
    {
        const string prefix = "model_providers.redcompute_tool_free.base_url=";
        var value = startInfo.ArgumentList.Single(arg => arg.StartsWith(prefix, StringComparison.Ordinal));
        return value[prefix.Length..].Trim('"');
    }

    /// <summary>Plays Codex's part: one Responses call, with tools, through the configured provider.</summary>
    private static void SimulateCodexModelCall(ProcessStartInfo startInfo)
    {
        using var client = new HttpClient();
        using var request = ResponsesRequest(new Uri(RelayBaseUrl(startInfo)), chatGpt: true);
        using var response = client.Send(request);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostResponsesAsync(Uri baseUrl, bool chatGpt)
    {
        using var client = new HttpClient();
        using var request = ResponsesRequest(baseUrl, chatGpt);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage ResponsesRequest(Uri baseUrl, bool chatGpt, bool extraField = false)
    {
        var body = JsonSerializer.Serialize(new
        {
            prompt = extraField ? new { id = "pmpt_1" } : null,
            model = "gpt-test",
            stream = true,
            tool_choice = "auto",
            tools = new object[] { new { type = "function", name = "shell" } },
            input = new object[]
            {
                new { type = "additional_tools", tools = new object[] { new { type = "custom", name = "exec" } } },
                new { type = "message", role = "user", content = "hello" },
            },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer fixture-token");
        if (chatGpt) request.Headers.TryAddWithoutValidation("chatgpt-account-id", "acct-fixture");
        return request;
    }

    private sealed class FakeUpstream : HttpMessageHandler
    {
        private string _sse = MessageSse;
        private string? _location;

        public List<(Uri Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        public void Respond(string sse) => _sse = sse;

        public void Redirect(string location) => _location = location;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!, headers, body));
            if (_location != null)
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri(_location) },
                    Content = new StringContent(_location),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
