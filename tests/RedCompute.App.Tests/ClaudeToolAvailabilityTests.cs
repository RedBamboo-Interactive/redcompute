using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeToolAvailabilityTests
{
    [Fact]
    public void ResolveClaudePath_prefers_managed_global_Claude_Code_package()
    {
        using var scratch = new ScratchDirectory();
        var globalCli = Path.Combine(scratch.Path, "npm", "node_modules", "@anthropic-ai",
            "claude-code", "bin", "claude.exe");
        var sdkCli = Path.Combine(scratch.Path, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(globalCli)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sdkCli)!);
        File.WriteAllText(globalCli, "global fixture");
        File.WriteAllText(sdkCli, "sdk fixture");

        var resolved = ClaudeSessionService.ResolveClaudePath(null, scratch.Path, null);

        Assert.Equal(globalCli, resolved);
    }

    [Fact]
    public void ResolveClaudePath_finds_newest_Claude_Desktop_bundle()
    {
        using var scratch = new ScratchDirectory();
        var older = Path.Combine(scratch.Path, "Claude", "claude-code", "2.1.99", "claude.exe");
        var newer = Path.Combine(scratch.Path, "Claude", "claude-code", "2.1.156", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
        File.WriteAllText(older, "fixture");
        File.WriteAllText(newer, "fixture");

        var resolved = ClaudeSessionService.ResolveClaudePath(null, scratch.Path, null);

        Assert.Equal(newer, resolved);
    }

    [Fact]
    public void Explicit_empty_tools_disables_builtins_and_ambient_mcp()
    {
        var args = new List<string>();

        ClaudeSessionService.AddAgentArgs(
            args, model: null, effort: null, maxTurns: 1,
            tools: [], allowedTools: [], addDirs: []);

        Assert.Equal("", ValueAfter(args, "--tools"));
        Assert.DoesNotContain("--bare", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("{\"mcpServers\":{}}", ValueAfter(args, "--mcp-config"));
    }

    [Fact]
    public void Omitted_tools_preserves_existing_agent_behavior()
    {
        var args = new List<string>();

        ClaudeSessionService.AddAgentArgs(
            args, model: null, effort: null, maxTurns: 1,
            tools: null, allowedTools: ["Read"], addDirs: null);

        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("--bare", args);
        Assert.Equal("Read", ValueAfter(args, "--allowed-tools"));
    }

    [Fact]
    public async Task Generate_uses_exact_no_tool_arguments_and_deletes_empty_working_directory()
    {
        using var fixture = new OneshotFixture(ToolFreeOutput([]));

        var result = await fixture.ExecuteAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", result.Text);
        Assert.Equal([
            "-p", "[User]: hello",
            "--output-format", "stream-json", "--verbose",
            "--model", "claude-test",
            "--no-session-persistence",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--thinking-display", "summarized",
            "--system-prompt", "system-test",
            "--effort", "low",
        ], fixture.Arguments);
        Assert.DoesNotContain("--allowedTools", fixture.Arguments);
        Assert.DoesNotContain("--allowed-tools", fixture.Arguments);
        Assert.True(fixture.WorkingDirectoryWasEmpty);
        Assert.False(Directory.Exists(fixture.WorkingDirectory));
        Assert.True(ClaudeCodeProvider.DeclaredCapabilities.HasFlag(SessionCapabilities.ToolFreeGenerate));
    }

    [Fact]
    public async Task Generate_fails_closed_and_returns_no_text_when_init_reports_a_tool()
    {
        using var fixture = new OneshotFixture(ToolFreeOutput(["Read"]));

        var result = await fixture.ExecuteAsync();

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("non-empty init tool list", result.Error);
        Assert.Contains("Read", result.Error);
        Assert.False(Directory.Exists(fixture.WorkingDirectory));
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"result\":\"text\"}", "init event was missing")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}", "did not report a tools array")]
    public void Generate_fails_closed_when_init_cannot_prove_the_tool_surface(string stream, string error)
        => Assert.Contains(error, ClaudeSessionService.ToolFreeInitError(stream));

    [Fact]
    public void Execute_is_verified_tool_free_only_for_an_empty_grant_with_an_empty_init_list()
    {
        Assert.Equal((SessionIsolation.VerifiedToolFree, "claude-empty-init-tools"),
            ClaudeCodeProvider.ExecuteIsolation([], ToolFreeOutput([])));
        // Execute always skips permissions, so anything short of a verified empty surface runs
        // whatever tools it has unprompted: an unverified empty grant (ambient plugin, CLI change,
        // missing init) and a non-empty grant are both unrestricted, never "sandboxed".
        Assert.Equal((SessionIsolation.Unrestricted, "claude-skip-permissions:empty-grant-unverified"),
            ClaudeCodeProvider.ExecuteIsolation([], ToolFreeOutput(["mcp__ambient__fetch"])));
        Assert.Equal(SessionIsolation.Unrestricted, ClaudeCodeProvider.ExecuteIsolation([], null).Isolation);
        Assert.Equal(SessionIsolation.Unrestricted, ClaudeCodeProvider.ExecuteIsolation([], "not json").Isolation);
        Assert.Equal((SessionIsolation.Unrestricted, "claude-skip-permissions:tool-grant"),
            ClaudeCodeProvider.ExecuteIsolation(["Read"], ToolFreeOutput(["Read"])));
        Assert.Equal((SessionIsolation.Unrestricted, "claude-skip-permissions:default-tools"),
            ClaudeCodeProvider.ExecuteIsolation(null, ToolFreeOutput([])));
        Assert.Contains("--dangerously-skip-permissions", AgentArgs(["Read"]));
        Assert.Contains("--dangerously-skip-permissions", AgentArgs([]));
    }

    [Theory]
    [InlineData("{\"type\":7,\"subtype\":\"init\",\"tools\":[]}")]
    [InlineData("{\"type\":\"system\",\"subtype\":[\"init\"],\"tools\":[]}")]
    [InlineData("{\"\\udc00\":1,\"type\":\"system\",\"subtype\":\"init\",\"tools\":[\"\\ud800\"]}")]
    [InlineData("[1,2]")]
    public void Init_check_fails_closed_on_lines_it_cannot_read_without_throwing(string line)
        => Assert.NotNull(ClaudeSessionService.ToolFreeInitError(line + "\n"));

    private static List<string> AgentArgs(string[] tools)
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 1, tools, null, null);
        return args;
    }

    private static string ValueAfter(IReadOnlyList<string> values, string key)
    {
        var index = values.IndexOf(key);
        Assert.InRange(index, 0, values.Count - 2);
        return values[index + 1];
    }

    private static string ToolFreeOutput(string[] tools)
        => JsonSerializer.Serialize(new { type = "system", subtype = "init", model = "claude-test", tools }) + "\n"
           + JsonSerializer.Serialize(new
           {
               type = "result",
               result = "ok",
               usage = new { input_tokens = 1, output_tokens = 1 },
           }) + "\n";

    private sealed class OneshotFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"redcompute-claude-generate-test-{Guid.NewGuid():N}");
        private readonly string _output;
        private readonly ClaudeSessionService _service;

        public List<string> Arguments { get; private set; } = [];
        public string WorkingDirectory { get; private set; } = "";
        public bool WorkingDirectoryWasEmpty { get; private set; }

        public OneshotFixture(string output)
        {
            _output = output;
            Directory.CreateDirectory(_root);
            var claudePath = System.IO.Path.Combine(_root, "claude.exe");
            File.WriteAllText(claudePath, "fixture");
            _service = new ClaudeSessionService(
                new ClaudeConfig { ClaudePath = claudePath },
                new MemoryJobTracker(), new EmptyStore(), new NullQualityModes(), () => null, (_, _) => { });
            _service.LaunchOneshotProcess = Launch;
        }

        public async Task<ClaudeSessionService.OneshotResult> ExecuteAsync()
        {
            using var document = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]");
            return await _service.ExecuteOneshotAsync(
                "claude-test", "system-test", document.RootElement, 32,
                CancellationToken.None, "low", 10);
        }

        private Process Launch(ProcessStartInfo requested)
        {
            Arguments = requested.ArgumentList.ToList();
            WorkingDirectory = requested.WorkingDirectory;
            WorkingDirectoryWasEmpty = Directory.Exists(WorkingDirectory)
                && !Directory.EnumerateFileSystemEntries(WorkingDirectory).Any();

            var fake = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            fake.ArgumentList.Add("-NoProfile");
            fake.ArgumentList.Add("-Command");
            fake.ArgumentList.Add("[Console]::Out.Write($env:RED_COMPUTE_TEST_OUTPUT)");
            fake.Environment["RED_COMPUTE_TEST_OUTPUT"] = _output;
            return Process.Start(fake)!;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class EmptyStore : IClaudeSessionStore
    {
        public ClaudeSessionRecord? FindSession(string sessionId) => null;
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<ClaudeSessionRecord> GetActiveSessions() => [];
        public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(ClaudeSessionRecord record) { }
        public void DismissSession(string sessionId) { }
        public void AddMessage(ClaudeMessageRecord message) { }
        public void AddMessages(List<ClaudeMessageRecord> messages) { }
        public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public DateTimeOffset GetLastMessageTimestamp(string sessionId) => default;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class MemoryJobTracker : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class NullQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"redcompute-claude-path-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class ReadOnlyListIndex
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T item)
    {
        for (var index = 0; index < values.Count; index++)
            if (EqualityComparer<T>.Default.Equals(values[index], item)) return index;
        return -1;
    }
}
