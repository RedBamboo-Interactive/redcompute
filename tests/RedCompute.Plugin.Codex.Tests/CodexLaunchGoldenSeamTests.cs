using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Layer A launch goldens for Codex (node-zero design, section 4): the start info each launch seam is
/// handed, in canonical form, with the containment the seam's production default applies (pinned by
/// <see cref="Seam_defaults_contain_as_recorded"/>). Recorded on unchanged source.
/// </summary>
[Collection(CodexLaunchGoldenCollection.Name)]
public sealed class CodexLaunchGoldenSeamTests : IDisposable
{
    private const string Token = "golden-execution-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-launch-goldens", $"codex-seam-{Guid.NewGuid():N}");
    private readonly string _project;
    private readonly string _scratch;
    private readonly CodexConfig _config;
    private readonly Normalizer _normalizer;
    private readonly List<ProcessStartInfo> _launches = [];
    private readonly List<Process> _started = [];

    public CodexLaunchGoldenSeamTests()
    {
        _project = Path.Combine(_root, "project");
        _scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_scratch);
        var codex = Path.Combine(_root, "cli", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(codex)!);
        File.WriteAllText(codex, "seam only, never executed");
        _config = new CodexConfig { CodexPath = codex };
        _normalizer = new Normalizer().Add(_root, "<root>");
    }

    [WindowsFact("containment is a Windows Job Object")]
    public async Task Seam_defaults_contain_as_recorded()
    {
        var (interactive, exec) = Services(capture: false);
        await using (interactive)
        {
            var seams = new JsonObject
            {
                ["codex.LaunchAppServerProcess"] = await ContainmentOf(interactive.LaunchAppServerProcess),
                ["codex.LaunchExecProcess"] = await ContainmentOf(exec.LaunchExecProcess),
            };
            LaunchGoldens.Verify("A-codex-seam-defaults", seams);
        }
    }

    private async Task<string> ContainmentOf(Func<ProcessStartInfo, Process?> seam)
    {
        var process = seam(new ProcessStartInfo(TestProcesses.StdinSink)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        lock (_started) _started.Add(process);
        var containment = await ProbeHarness.ObserveContainmentAsync(process.Id);
        process.Kill(entireProcessTree: true);
        return containment;
    }

    [Fact]
    public async Task Codex_app_server_session()
    {
        var (interactive, _) = Services();
        await using (interactive)
        {
            using (SessionScratch.PushExecutionToken(Token))
                await interactive.StartSessionAsync(_project, null, "owner", null, null, null, null, null, null,
                    Provenance(), _scratch);
            Verify("A-codex-app-server-session", "codex.LaunchAppServerProcess", "session:codex");
        }
    }

    [Theory]
    [InlineData("A-codex-exec-read-only", "read-only", false, null)]
    [InlineData("A-codex-exec-workspace-write-network", "workspace-write", true, null)]
    [InlineData("A-codex-exec-danger-full-access", "danger-full-access", false, null)]
    [InlineData("A-codex-exec-docker", "read-only", false, "golden-container")]
    public async Task Codex_exec(string golden, string sandbox, bool networkAccess, string? container)
    {
        var (_, exec) = Services();
        await exec.ExecuteExecAsync("golden prompt", container, _project, "gpt-golden", sandbox, 60,
            CancellationToken.None, env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" },
            networkAccess: networkAccess, effort: "high", retryTransientLocks: false);
        Verify(golden, "codex.LaunchExecProcess", "session:codex-exec");
    }

    [Fact]
    public async Task Codex_generate()
    {
        var (_, exec) = Services();
        await exec.ExecuteGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low");
        Verify("A-codex-generate", "codex.LaunchExecProcess", "session:codex-exec", isolatedCwd: true);
    }

    /// <summary>
    /// A strict response schema and an image are written to their own isolated directories outside the
    /// empty working directory, and named on the command line. Their contents are part of the launch.
    /// </summary>
    [Fact]
    public async Task Codex_generate_with_schema_and_image()
    {
        var (_, exec) = Services(capture: false);
        var files = new JsonObject();
        exec.LaunchExecProcess = start =>
        {
            Capture(start);
            var schema = start.ArgumentList[start.ArgumentList.IndexOf("--output-schema") + 1];
            var image = start.ArgumentList.Single(a => a.StartsWith("--image=", StringComparison.Ordinal))["--image=".Length..];
            _normalizer.Add(Path.GetDirectoryName(schema), "<schema-dir>").Add(Path.GetDirectoryName(image), "<image-dir>");
            files["schema"] = File.ReadAllText(schema);
            files["image"] = Convert.ToHexString(File.ReadAllBytes(image));
            return null;
        };
        using var schemaJson = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");
        Assert.True(ResponseSchema.TryParse(schemaJson.RootElement, out var schema, out var errors), string.Join("; ", errors));
        await exec.ExecuteGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low", schema,
            [new GenerateImage("image/png", [0x89, 0x50, 0x4E, 0x47], 1, 1, "golden-sha")]);
        Verify("A-codex-generate-schema-image", "codex.LaunchExecProcess", "session:codex-exec", isolatedCwd: true,
            extra: golden => golden["files"] = files);
    }

    /// <summary>The tool-free generate points Codex at a loopback relay lease; its URL is per lease.</summary>
    [Fact]
    public async Task Codex_tool_free_generate()
    {
        await using var relay = new CodexToolFreeRelay((_, _) => { }, new RefuseUpstream(),
            new Uri("https://upstream.invalid/"), new Uri("https://upstream.invalid/v1/"));
        var (_, exec) = Services(capture: false);
        exec.ToolFreeRelay = relay;
        exec.LaunchExecProcess = start =>
        {
            Capture(start);
            const string prefix = "model_providers.redcompute_tool_free.base_url=";
            var url = start.ArgumentList.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim('"');
            _normalizer.Add(url.TrimEnd('/'), "<relay-base-url>");
            return null;
        };
        await exec.ExecuteToolFreeGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low");
        Verify("A-codex-tool-free-generate", "codex.LaunchExecProcess", "session:codex-exec", isolatedCwd: true);
    }

    private sealed class RefuseUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("the launch goldens never reach an upstream");
    }

    private (CodexInteractiveService Interactive, CodexSessionService Exec) Services(bool capture = true)
    {
        var jobs = new CodexLaunchGoldenProcessTests.GoldenJobTracker();
        var store = new CodexLaunchGoldenProcessTests.GoldenStore();
        var exec = new CodexSessionService(_config, jobs, store, (_, _) => { });
        var interactive = new CodexInteractiveService(
            _config, store, new CodexModelCatalog(_config, (_, _) => { }), exec, jobs,
            new CodexAccountUsageService(_config, (_, _) => { }), new CodexLaunchGoldenProcessTests.NoQualityModes(), () => null, (_, _) => { });
        if (capture)
        {
            exec.LaunchExecProcess = start => { Capture(start); return null; };
            interactive.LaunchAppServerProcess = start => { Capture(start); return null; };
        }
        return (interactive, exec);
    }

    private void Capture(ProcessStartInfo start)
    {
        lock (_launches) _launches.Add(start);
    }

    private void Verify(string golden, string seam, string containment, bool isolatedCwd = false,
        Action<JsonObject>? extra = null)
    {
        ProcessStartInfo start;
        lock (_launches) start = Assert.Single(_launches);
        // A generate runs in a fresh isolated directory: its name is the only run-specific part.
        if (isolatedCwd && !string.IsNullOrEmpty(start.WorkingDirectory))
            _normalizer.Add(start.WorkingDirectory, "<isolated-cwd>");
        var canonical = LaunchGoldens.FromStartInfo(start, seam, containment, _normalizer);
        extra?.Invoke(canonical);
        LaunchGoldens.Verify(golden, canonical);
    }

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "golden-request", null);

    public void Dispose()
    {
        lock (_started)
            foreach (var process in _started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        try { Directory.Delete(_root, true); } catch { }
    }
}
