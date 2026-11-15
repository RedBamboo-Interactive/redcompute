using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Layer A launch goldens for Codex (node-zero design, section 4): the start info of each launch, in
/// canonical form, with the containment it gets. Recorded on unchanged source, when the seams were
/// typed on the start info; since the execution host (C12) every launch is captured where the local
/// host hands its start info to the OS, through the seams' production defaults, and must match the
/// recording byte for byte. <see cref="Seam_defaults_contain_as_recorded"/> pins what each default does
/// with its production spec on a real process.
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
    private readonly List<(ProcessStartInfo Start, string Containment)> _launches = [];
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

    /// <summary>
    /// The containment each Codex seam's production default gives the spec its production launch builds
    /// (the spec carries its containment since the execution host): each spec is taken from a real
    /// session start and exec call, then handed to a fresh service's default seam.
    /// </summary>
    [WindowsFact("containment is a Windows Job Object")]
    public async Task Seam_defaults_contain_as_recorded()
    {
        var (appServer, execSpec) = await CodexProductionSpecs();
        var (interactive, exec) = Services(capture: false);
        await using (interactive)
        {
            var seams = new JsonObject
            {
                ["codex.LaunchAppServerProcess"] = await ContainmentOf(interactive.LaunchAppServerProcess, appServer),
                ["codex.LaunchExecProcess"] = await ContainmentOf(exec.LaunchExecProcess, execSpec),
            };
            LaunchGoldens.Verify("A-codex-seam-defaults", seams);
        }
    }

    /// <summary>The specs a session start and an exec call hand their seams, launching nothing.</summary>
    private async Task<(HostSpawnSpec AppServer, HostSpawnSpec Exec)> CodexProductionSpecs()
    {
        HostSpawnSpec? appServer = null, exec = null;
        var (interactiveService, execService) = Services(capture: false);
        await using (interactiveService)
        {
            interactiveService.LaunchAppServerProcess = spec => { appServer = spec; return null; };
            execService.LaunchExecProcess = spec => { exec = spec; return null; };
            using (SessionScratch.PushExecutionToken(Token))
                await interactiveService.StartSessionAsync(_project, null, "owner", null, null, null, null, null, null,
                    Provenance(), _scratch);
            await execService.ExecuteExecAsync("golden prompt", null, _project, "gpt-golden", "read-only", 60,
                CancellationToken.None, retryTransientLocks: false);
        }
        return (appServer!, exec!);
    }

    /// <summary>
    /// The containment a spec seam's production default gives the provider's own spec: the spec's
    /// launch target is swapped for a stand-in that stays alive, everything else is the provider's.
    /// </summary>
    private static async Task<string> ContainmentOf(Func<HostSpawnSpec, IHostProcess?> seam, HostSpawnSpec production)
    {
        using var process = seam(production with
        {
            FileName = TestProcesses.StdinSink,
            ArgumentList = null,
            RawArguments = null,
            WorkingDirectory = null,
            EnvironmentOverlay = null,
            CreateNoWindow = true,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
        })!;
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
            Verify("A-codex-app-server-session", "codex.LaunchAppServerProcess");
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
        Verify(golden, "codex.LaunchExecProcess");
    }

    [Fact]
    public async Task Codex_generate()
    {
        var (_, exec) = Services();
        await exec.ExecuteGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low");
        Verify("A-codex-generate", "codex.LaunchExecProcess", isolatedCwd: true);
    }

    /// <summary>
    /// A strict response schema and an image are written to their own isolated directories outside the
    /// empty working directory, and named on the command line. Their contents are part of the launch.
    /// </summary>
    [Fact]
    public async Task Codex_generate_with_schema_and_image()
    {
        var files = new JsonObject();
        // Read while the launch is being handed to the OS: the files exist only for the call.
        var (_, exec) = Services(onCapture: start =>
        {
            var schema = start.ArgumentList[start.ArgumentList.IndexOf("--output-schema") + 1];
            var image = start.ArgumentList.Single(a => a.StartsWith("--image=", StringComparison.Ordinal))["--image=".Length..];
            _normalizer.Add(Path.GetDirectoryName(schema), "<schema-dir>").Add(Path.GetDirectoryName(image), "<image-dir>");
            files["schema"] = File.ReadAllText(schema);
            files["image"] = Convert.ToHexString(File.ReadAllBytes(image));
        });
        using var schemaJson = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}");
        Assert.True(ResponseSchema.TryParse(schemaJson.RootElement, out var schema, out var errors), string.Join("; ", errors));
        await exec.ExecuteGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low", schema,
            [new GenerateImage("image/png", [0x89, 0x50, 0x4E, 0x47], 1, 1, "golden-sha")]);
        Verify("A-codex-generate-schema-image", "codex.LaunchExecProcess", isolatedCwd: true,
            extra: golden => golden["files"] = files);
    }

    /// <summary>The tool-free generate points Codex at a loopback relay lease; its URL is per lease.</summary>
    [Fact]
    public async Task Codex_tool_free_generate()
    {
        await using var relay = new CodexToolFreeRelay((_, _) => { }, new RefuseUpstream(),
            new Uri("https://upstream.invalid/"), new Uri("https://upstream.invalid/v1/"));
        var (_, exec) = Services(onCapture: start =>
        {
            const string prefix = "model_providers.redcompute_tool_free.base_url=";
            var url = start.ArgumentList.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim('"');
            _normalizer.Add(url.TrimEnd('/'), "<relay-base-url>");
        });
        exec.ToolFreeRelay = relay;
        await exec.ExecuteToolFreeGenerateAsync("golden prompt", "gpt-golden", 60, CancellationToken.None, "low");
        Verify("A-codex-tool-free-generate", "codex.LaunchExecProcess", isolatedCwd: true);
    }

    private sealed class RefuseUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("the launch goldens never reach an upstream");
    }

    /// <summary>
    /// Codex services on one host. Capturing (the default), their seams keep their production default
    /// and every launch is captured where the local host hands its start info to the OS, with the
    /// containment it applies; <paramref name="onCapture"/> sees the start info at that moment. Not
    /// capturing, they run on node zero's shared local host.
    /// </summary>
    private (CodexInteractiveService Interactive, CodexSessionService Exec) Services(bool capture = true,
        Action<ProcessStartInfo>? onCapture = null)
    {
        var host = capture ? CapturingHost(onCapture) : null;
        var jobs = new CodexLaunchGoldenProcessTests.GoldenJobTracker();
        var store = new CodexLaunchGoldenProcessTests.GoldenStore();
        var exec = new CodexSessionService(_config, jobs, store, (_, _) => { }, host);
        var interactive = new CodexInteractiveService(
            _config, store, new CodexModelCatalog(_config, (_, _) => { }, host), exec, jobs,
            new CodexAccountUsageService(_config, (_, _) => { }, host), new CodexLaunchGoldenProcessTests.NoQualityModes(),
            () => null, (_, _) => { }, host);
        return (interactive, exec);
    }

    private LocalExecutionHost CapturingHost(Action<ProcessStartInfo>? onCapture) => new("golden-node")
    {
        StartProcess = (start, mode, owner) =>
        {
            onCapture?.Invoke(start);
            lock (_launches) _launches.Add((start, LocalExecutionHost.DescribeContainment(mode, owner)));
            return null;
        },
    };

    private void Verify(string golden, string seam, bool isolatedCwd = false, Action<JsonObject>? extra = null)
    {
        (ProcessStartInfo Start, string Containment) launch;
        lock (_launches) launch = Assert.Single(_launches);
        var start = launch.Start;
        // A generate runs in a fresh isolated directory: its name is the only run-specific part.
        if (isolatedCwd && !string.IsNullOrEmpty(start.WorkingDirectory))
            _normalizer.Add(start.WorkingDirectory, "<isolated-cwd>");
        var canonical = LaunchGoldens.FromStartInfo(start, seam, launch.Containment, _normalizer);
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
