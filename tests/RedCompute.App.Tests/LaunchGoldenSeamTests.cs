using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.ComfyUI;
using RedCompute.Plugin.LocalWsl;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Layer A launch goldens (node-zero design, section 4): what each launch seam or start-info builder
/// is handed, in canonical form, with the containment its production default applies (pinned by
/// <see cref="Seam_defaults_contain_as_recorded"/>). Recorded on unchanged source; after the move the
/// same goldens are captured where the execution host hands the start info to the OS.
/// </summary>
[Collection(LaunchGoldenCollection.Name)]
public sealed class LaunchGoldenSeamTests : IDisposable
{
    private const string Token = "golden-execution-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-launch-goldens", $"seam-{Guid.NewGuid():N}");
    private readonly string _project;
    private readonly string _scratch;
    private readonly string _claudePath;
    private readonly Normalizer _normalizer;
    private readonly List<Process> _started = [];

    public LaunchGoldenSeamTests()
    {
        _project = Path.Combine(_root, "project");
        _scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_scratch);
        _claudePath = Path.Combine(_root, "cli", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_claudePath)!);
        File.WriteAllText(_claudePath, "seam only, never executed");
        // The WSL form too: ComfyUI hands wsl.exe its paths as /mnt/<drive>/... .
        _normalizer = new Normalizer().Add(_root, "<root>").Add(ProviderHelpers.ConvertToWslPath(_root), "<root>");
    }

    // ---- seam defaults: which containment each seam's production default applies ---------------------

    [WindowsFact("containment is a Windows Job Object")]
    public async Task Seam_defaults_contain_as_recorded()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore(), capture: false);
        var seams = new JsonObject
        {
            ["claude.LaunchSessionProcess"] = await ContainmentOf(service.LaunchSessionProcess),
            ["claude.LaunchOneshotProcess"] = await ContainmentOf(service.LaunchOneshotProcess),
            ["claude.LaunchAgentProcess"] = await ContainmentOf(service.LaunchAgentProcess),
        };
        LaunchGoldens.Verify("A-seam-defaults", seams);
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

    // ---- Claude Code ------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_session_start()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        using (SessionScratch.PushExecutionToken(Token))
            await service.StartSessionAsync(_project, "claude-golden-model", "owner", null, null, "high", null, null,
                null, Provenance(), scratchDirectory: _scratch, developerInstructions: "Golden developer instructions.");
        Verify("A-claude-session-start", "claude.LaunchSessionProcess", "session:claude-code");
    }

    [Fact]
    public async Task Claude_session_resume()
    {
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "golden-resume", ProjectName = "Golden", ProjectPath = _project, Status = "Stopped",
            ClaudeSessionId = "c1a0de00-0000-4000-8000-000000000001", MessageCount = 1, Model = "claude-golden-model",
            Effort = "medium", ScratchDirectory = _scratch, StartedAt = DateTimeOffset.UtcNow,
            StopReason = SessionStopReasons.IdleTimeout,
        });
        var service = Claude(store);
        using (SessionScratch.PushExecutionToken(Token))
            await service.ResumeSessionAsync("golden-resume", Provenance());
        Verify("A-claude-session-resume", "claude.LaunchSessionProcess", "session:claude-code");
    }

    [Fact]
    public async Task Claude_oneshot()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        using var messages = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]");
        await service.ExecuteOneshotAsync("claude-golden-model", "be brief", messages.RootElement, 32,
            CancellationToken.None, "low", 60);
        Verify("A-claude-oneshot", "claude.LaunchOneshotProcess", "session:claude-code-oneshot");
    }

    [Theory]
    [InlineData("A-claude-agent-native", null, false)]
    [InlineData("A-claude-agent-docker", "golden-container", false)]
    [InlineData("A-claude-agent-stdin-prompt", null, true)]
    public async Task Claude_agent(string golden, string? container, bool longPrompt)
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        var prompt = longPrompt ? new string('p', 33 * 1024) : "golden prompt";
        await service.ExecuteAgentAsync(prompt, container, _project, "claude-golden-model", "high", 3,
            ["Read", "Bash"], ["Read"], [_scratch], 60, CancellationToken.None,
            env: new Dictionary<string, string> { ["GOLDEN_AGENT_ENV"] = "1" });
        Verify(golden, "claude.LaunchAgentProcess", "session:claude-code-agent");
    }

    [Fact]
    public async Task Claude_fork_probe()
    {
        var store = LaunchGoldenProcessTests.SeedRollback(_project, _scratch);
        var projects = Path.Combine(_root, "projects");
        LaunchGoldenProcessTests.WriteTwoTurnChain(Path.Combine(projects, "golden"));
        var service = Claude(store);
        service.ProjectsRootOverride = projects;
        service.ProbeFork = (start, _) =>
        {
            Capture(start);
            return Task.FromResult(new ClaudeForkProbeResult(ClaudeForkProbeStatus.Accepted));
        };
        using (SessionScratch.PushExecutionToken(Token))
            await service.RollbackAsync(LaunchGoldenProcessTests.RollbackRequest());
        // ProbeForkWithProcessAsync: a raw Process.Start (Rollback.cs:196), proven by B-claude-fork-probe.
        Verify("A-claude-fork-probe", "claude.ProbeFork", "none");
    }

    // ---- ComfyUI and LocalWsl: the start info each backend's StartAsync launches ------------------------

    [Theory]
    [InlineData("A-comfyui-start-native", false)]
    [InlineData("A-comfyui-start-wsl", true)]
    public void ComfyUI_start(string golden, bool wsl)
    {
        var provider = new ComfyUIProvider(ComfyConfig(wsl), "image-gen", _ => { });
        _launches.Add(provider.BuildStartInfo());
        Verify(golden, "comfyui.server", "none");
    }

    [Theory]
    [InlineData("A-localwsl-start-native", false)]
    [InlineData("A-localwsl-start-wsl", true)]
    public void LocalWsl_start(string golden, bool wsl)
    {
        var provider = new LocalWslProvider(LocalWslConfig(wsl), "tts", _ => { });
        _launches.Add(provider.BuildStartInfo());
        Verify(golden, "localwsl.server", "none");
    }

    internal ProviderConfig ComfyConfig(bool wsl) => new()
    {
        Type = "ComfyUI",
        ServerPath = Path.Combine(_root, "Comfy UI"),
        VenvPath = wsl ? "~/comfy env" : Path.Combine(_root, "venv"),
        WslDistro = wsl ? "Golden-Distro" : null,
        BackendPort = 18999,
        Extra = new Dictionary<string, object?>
        {
            ["ModelsDirectory"] = Path.Combine(_root, "models"),
            ["OutputDirectory"] = Path.Combine(_root, "output"),
            ["UserDirectory"] = Path.Combine(_root, "user"),
            ["ServerArgs"] = "--preview-method auto",
        },
    };

    internal ProviderConfig LocalWslConfig(bool wsl) => new()
    {
        Type = "LocalWsl",
        ServerPath = wsl ? "/home/golden/server" : Path.Combine(_root, "server"),
        VenvPath = wsl ? "/home/golden/venv" : null,
        WslDistro = wsl ? "Golden-Distro" : null,
        BackendPort = 18998,
        StartupTimeoutSeconds = 5,
    };

    // ---- CLI version manager: the npm runner's start info ---------------------------------------------

    [Theory]
    [InlineData("A-npm-runner-cmd-shim", "npm.cmd", new[] { "install", "-g", "@anthropic-ai/claude-code@latest" })]
    [InlineData("A-npm-runner-exe", "claude.exe", new[] { "--version" })]
    public void Npm_runner(string golden, string file, string[] args)
    {
        var create = typeof(ProcessCliCommandRunner).GetMethod("CreateStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        _launches.Add((ProcessStartInfo)create.Invoke(null, [new CliCommand(Path.Combine(_root, "npm", file), args)])!);
        Verify(golden, "cli.ProcessCliCommandRunner", "none");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private readonly List<ProcessStartInfo> _launches = [];

    private void Capture(ProcessStartInfo start)
    {
        lock (_launches) _launches.Add(start);
    }

    private void Verify(string golden, string seam, string containment)
    {
        ProcessStartInfo start;
        lock (_launches) start = Assert.Single(_launches);
        // A one-shot runs in a fresh isolated directory: its name is the only run-specific part.
        if (seam == "claude.LaunchOneshotProcess" && !string.IsNullOrEmpty(start.WorkingDirectory))
            _normalizer.Add(start.WorkingDirectory, "<isolated-cwd>");
        LaunchGoldens.Verify(golden, LaunchGoldens.FromStartInfo(start, seam, containment, _normalizer));
    }

    private ClaudeSessionService Claude(IClaudeSessionStore store, bool capture = true)
    {
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = _claudePath },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store,
            new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { });
        if (capture)
        {
            service.LaunchSessionProcess = start => { Capture(start); return null; };
            service.LaunchOneshotProcess = start => { Capture(start); return null; };
            service.LaunchAgentProcess = start => { Capture(start); return null; };
        }
        return service;
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
