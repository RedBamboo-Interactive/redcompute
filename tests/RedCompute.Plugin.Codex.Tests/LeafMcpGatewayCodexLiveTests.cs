using System.Net.Http.Headers;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Skipped unless REDCOMPUTE_LIVE_CODEX=1, REDLEAF_EXECUTION_TOKEN holds a suite execution token (as in a
/// Leaf-launched session) and REDCOMPUTE_LIVE_LEAF_URL names the running RedLeaf. Its own variable, because
/// every test host clears REDLEAF_URL at load (LaunchGoldens). Spends real Codex tokens and makes one call
/// through that RedLeaf's MCP gateway.
/// </summary>
public sealed class LiveLeafMcpFactAttribute : FactAttribute
{
    public LiveLeafMcpFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDCOMPUTE_LIVE_CODEX") != "1")
            Skip = "Set REDCOMPUTE_LIVE_CODEX=1 to run against the installed Codex CLI and its existing login";
        else if (LeafMcpGatewayCodexLiveTests.SuiteEnvironment() is null)
            Skip = "Needs REDLEAF_EXECUTION_TOKEN and REDCOMPUTE_LIVE_LEAF_URL for a running RedLeaf";
    }
}

public sealed class LeafMcpGatewayCodexLiveTests(ITestOutputHelper output) : IDisposable
{
    private const string ElicitationMethod = "mcpServer/elicitation/request";
    private const string AuditStream = "os:mcp-calls";
    private const string LiveLeafUrlVariable = "REDCOMPUTE_LIVE_LEAF_URL";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"redcompute-leaf-mcp-{Guid.NewGuid():N}");

    internal static Dictionary<string, string>? SuiteEnvironment()
    {
        var token = Environment.GetEnvironmentVariable(LeafMcpGateway.TokenVariable);
        var redLeaf = Environment.GetEnvironmentVariable(LiveLeafUrlVariable);
        return string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(redLeaf)
            ? null
            : new(StringComparer.OrdinalIgnoreCase)
            {
                [LeafMcpGateway.TokenVariable] = token,
                [SessionScratch.RedLeafUrlVariable] = redLeaf,
            };
    }

    [LiveLeafMcpFact]
    public async Task A_leaf_mcp_call_from_codex_reaches_the_gateway_without_an_approval_prompt_and_is_audited()
    {
        Directory.CreateDirectory(_workspace);
        var environment = SuiteEnvironment()!;
        var startedAt = DateTimeOffset.UtcNow;
        var serverRequests = new List<string>();
        var turnCompleted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var conn = await CodexAppServerConnection.StartAsync(
            null, _workspace, (line, _) => output.WriteLine(line), environment);
        conn.LeafMcpUrl = LeafMcpGateway.UrlFor(environment);
        Assert.NotNull(conn.LeafMcpUrl);

        // What the interactive service does with a request it does not handle: decline it. An approval
        // prompt for a leaf tool would land here and fail the call, which is the bug this guards.
        conn.ServerRequest += (id, method, parameters) =>
        {
            lock (serverRequests) serverRequests.Add(method);
            var declined = conn.RespondErrorAsync(id, $"Unhandled request: {method}");
        };
        conn.Notification += (method, parameters) =>
        {
            if (method == "turn/completed") turnCompleted.TrySetResult(parameters.Clone());
        };

        var info = new CodexSessionInfo
        {
            Id = "leaf-mcp-live",
            ProjectName = "leaf-mcp-live",
            ProjectPath = _workspace,
            StartedAt = startedAt,
            ExecutionProfile = SessionExecutionProfile.Default,
            AccessLevel = SessionAccessLevels.Full,
            Effort = "low",
        };
        var threadConfig = await CodexInteractiveService.ReadThreadConfigAsync(conn, info);
        var thread = await conn.SendRequestAsync("thread/start",
            CodexInteractiveService.BuildThreadStartParams(info, threadConfig), timeoutSeconds: 60);
        info.ThreadId = thread.GetProperty("thread").GetProperty("id").GetString();

        // The approval policy a Leaf session's turns use outside conversation-only.
        await conn.SendRequestAsync("turn/start", new
        {
            threadId = info.ThreadId,
            input = CodexInteractiveService.BuildTurnInput(
                "Call the MCP tool leaf_mcp_call exactly once with the arguments " +
                "{\"server\":\"galaxus\",\"tool\":\"search_products\",\"arguments\":{\"query\":\"kettle\",\"first\":1}}, " +
                "then reply with the first 120 characters of its result or error, verbatim. Do nothing else.", null),
            effort = "low",
            approvalPolicy = "on-request",
        }, timeoutSeconds: 120);

        await turnCompleted.Task.WaitAsync(TimeSpan.FromMinutes(4));
        lock (serverRequests)
        {
            output.WriteLine($"server requests: [{string.Join(", ", serverRequests)}]");
            Assert.DoesNotContain(ElicitationMethod, serverRequests);
        }

        // Reaching the gateway leaves an audit record, served or refused (galaxus may not be attached
        // where the token's discussion is); a call Codex stopped itself leaves none.
        var record = await FindAuditRecordAsync(environment, startedAt);
        output.WriteLine($"audit: {record}");
        Assert.NotNull(record);
    }

    private static async Task<string?> FindAuditRecordAsync(Dictionary<string, string> environment, DateTimeOffset since)
    {
        using var http = new HttpClient { BaseAddress = new Uri(environment[SessionScratch.RedLeafUrlVariable].TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", environment[LeafMcpGateway.TokenVariable]);
        using var response = await http.GetAsync($"api/streams/{AuditStream}/records?limit=20");
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("createdAt").GetDateTimeOffset() < since.AddSeconds(-2)) continue;
            using var data = JsonDocument.Parse(item.GetProperty("data").GetString()!);
            var root = data.RootElement;
            if (root.GetProperty("via").GetString() == "leaf_mcp_call"
                && root.GetProperty("tool").GetString() == "search_products")
                return $"{root.GetProperty("outcome").GetString()} {root.GetProperty("server").GetString()} at {item.GetProperty("createdAt").GetString()}";
        }
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
