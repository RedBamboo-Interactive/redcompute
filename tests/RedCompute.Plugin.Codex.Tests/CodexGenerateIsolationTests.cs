using System.Diagnostics;
using System.Reflection;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexGenerateIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"redcompute-codex-generate-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Generate_keeps_current_exec_arguments_in_an_empty_temporary_working_directory()
    {
        Directory.CreateDirectory(_root);
        var codexPath = Path.Combine(_root, "codex.exe");
        File.WriteAllText(codexPath, "fixture");
        var service = new CodexSessionService(
            new CodexConfig { CodexPath = codexPath },
            DispatchProxy.Create<IJobTracker, NullJobTracker>(), new EmptyStore(), (_, _) => { });
        ProcessStartInfo? captured = null;
        var wasEmpty = false;
        service.LaunchExecProcess = requested =>
        {
            captured = requested;
            wasEmpty = Directory.Exists(requested.WorkingDirectory)
                && !Directory.EnumerateFileSystemEntries(requested.WorkingDirectory).Any();
            return StartOutputProcess(
                "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n" +
                "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"ok\"}}\n" +
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}\n");
        };

        var result = await service.ExecuteGenerateAsync(
            "hello", "gpt-test", 10, CancellationToken.None, "low");

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", result.Text);
        Assert.Equal(SessionIsolation.SandboxedBestEffort, result.Isolation);
        Assert.NotNull(captured);
        Assert.Equal([
            "exec", "-", "--json", "--skip-git-repo-check",
            "--sandbox", "read-only",
            "-c", "model_reasoning_effort=\"low\"",
            "--model", "gpt-test",
        ], captured.ArgumentList.ToList());
        Assert.StartsWith(Path.GetTempPath(), captured.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redcompute-codex-generate-", captured.WorkingDirectory);
        Assert.True(wasEmpty);
        Assert.False(Directory.Exists(captured.WorkingDirectory));
    }

    internal static Process StartOutputProcess(string output)
    {
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
        fake.Environment["RED_COMPUTE_TEST_OUTPUT"] = output;
        return Process.Start(fake)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public class NullJobTracker : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    internal sealed class EmptyStore : ICodexSessionStore
    {
        public CodexSessionRecord? FindSession(string sessionId) => null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(CodexSessionRecord record) { }
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { }
        public void AddMessages(List<CodexMessageRecord> messages) { }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
