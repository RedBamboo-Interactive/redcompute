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
using RedCompute.Plugin.OpenCode;
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

    [Theory]
    [InlineData(SessionAccessLevels.Plan)]
    [InlineData(SessionAccessLevels.Ask)]
    [InlineData(SessionAccessLevels.Auto)]
    [InlineData(SessionAccessLevels.Full)]
    public async Task Claude_session_start_at_access_level(string level)
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        using (SessionAccessScope.Push(new SessionAccessState(level)))
        using (SessionScratch.PushExecutionToken(Token))
            await service.StartSessionAsync(_project, "claude-golden-model", "owner", null, null, "high", null, null,
                null, Provenance(), scratchDirectory: _scratch);
        Verify($"A-claude-session-start-{level}", "claude.LaunchSessionProcess", "session:claude-code");
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

    // ---- through the start seams: the same goldens, captured where StartAsync hands them over ------------

    [Theory]
    [InlineData("A-comfyui-start-native", false)]
    [InlineData("A-comfyui-start-wsl", true)]
    public async Task ComfyUI_start_through_its_seam(string golden, bool wsl)
    {
        // Before launching, StartAsync only probes 127.0.0.1:18999 over HTTP; the seam spawns nothing.
        var provider = new ComfyUIProvider(ComfyConfig(wsl), "image-gen", _ => { })
        {
            LaunchServerProcess = start => { Capture(start); return null; },
        };
        Assert.False(await provider.StartAsync());
        Verify(golden, "comfyui.server", "none");
    }

    /// <summary>
    /// Native only: in WSL mode StartAsync first resolves the distro's address by running
    /// wsl.exe, a real process outside this harness. The WSL start info is pinned by the
    /// builder golden above.
    /// </summary>
    [Fact]
    public async Task LocalWsl_native_start_through_its_seam()
    {
        var provider = new LocalWslProvider(LocalWslConfig(wsl: false), "tts", _ => { })
        {
            LaunchServerProcess = start => { Capture(start); return null; },
        };
        Assert.False(await provider.StartAsync());
        Verify("A-localwsl-start-native", "localwsl.server", "none");
    }

    // ---- OpenCode, through the seams added in node zero (G0b) -----------------------------------------

    [Fact]
    public async Task OpenCode_acp_session()
    {
        var service = OpenCode();
        service.LaunchAcpProcess = start => { Capture(start); return null; };
        try
        {
            using (SessionScratch.PushExecutionToken(Token))
                await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                    null, null, Provenance(), _scratch);
        }
        catch (NullReferenceException)
        {
            // The ACP launch dereferences the process it was handed; a capturing seam hands none.
        }
        ProcessStartInfo start;
        lock (_launches) start = Assert.Single(_launches);
        // The access note of the default level is written for the new session and named in
        // OPENCODE_CONFIG_CONTENT (as in B-opencode-acp): record that file, then delete exactly it.
        var config = JsonNode.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!)!;
        var instructions = config["instructions"]!.AsArray().Single()!.GetValue<string>();
        _normalizer.Add(Path.GetFileNameWithoutExtension(instructions), "<session-id>");
        var golden = LaunchGoldens.FromStartInfo(start, "opencode.LaunchAcpProcess", "session:opencode", _normalizer);
        golden["instructionFile"] = _normalizer.Apply(File.ReadAllText(instructions));
        File.Delete(instructions);
        LaunchGoldens.Verify("A-opencode-acp", golden);
    }

    [Fact]
    public async Task OpenCode_exec()
    {
        var service = OpenCode();
        service.LaunchExecProcess = start => { Capture(start); return null; };
        await service.ExecuteAsync("golden prompt", null, _project, "golden/model", 60, CancellationToken.None,
            env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        Verify("A-opencode-exec", "opencode.LaunchExecProcess", "session:opencode-exec");
    }

    /// <summary>At Full there is no access note, so no instruction file and no OPENCODE_CONFIG_CONTENT.</summary>
    [Fact]
    public async Task OpenCode_acp_session_at_full()
    {
        var service = OpenCode();
        service.LaunchAcpProcess = start => { Capture(start); return null; };
        try
        {
            using (SessionAccessScope.Push(new SessionAccessState(SessionAccessLevels.Full)))
            using (SessionScratch.PushExecutionToken(Token))
                await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                    null, null, Provenance(), _scratch);
        }
        catch (NullReferenceException)
        {
            // The ACP launch dereferences the process it was handed; a capturing seam hands none.
        }
        Verify("A-opencode-acp-full", "opencode.LaunchAcpProcess", "session:opencode");
    }

    [Fact]
    public async Task OpenCode_exec_docker()
    {
        // Docker exec refuses a configured OpenCodeDataHome, so this service has none.
        var service = OpenCode(dataHome: false);
        service.LaunchExecProcess = start => { Capture(start); return null; };
        await service.ExecuteAsync("golden prompt", "golden-container", _project, "golden/model", 60,
            CancellationToken.None, env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        Verify("A-opencode-exec-docker", "opencode.LaunchExecProcess", "session:opencode-exec");
    }

    [WindowsFact("containment is a Windows Job Object")]
    public async Task Node_zero_seam_defaults_contain_as_recorded()
    {
        var seams = new JsonObject
        {
            ["opencode.LaunchAcpProcess"] = await ContainmentOf(OpenCode().LaunchAcpProcess),
            ["opencode.LaunchExecProcess"] = await ContainmentOf(OpenCode().LaunchExecProcess),
            ["comfyui.LaunchServerProcess"] = await ContainmentOf(
                new ComfyUIProvider(ComfyConfig(false), "image-gen", _ => { }).LaunchServerProcess),
            ["localwsl.LaunchServerProcess"] = await ContainmentOf(
                new LocalWslProvider(LocalWslConfig(false), "tts", _ => { }).LaunchServerProcess),
        };
        LaunchGoldens.Verify("A-seam-defaults-node-zero", seams);
    }

    private OpenCodeSessionService OpenCode(bool dataHome = true)
    {
        var path = Path.Combine(_root, "cli", "opencode.exe");
        File.WriteAllText(path, "seam only, never executed");
        var home = dataHome ? Path.Combine(_root, "opencode-data") : null;
        if (home != null) Directory.CreateDirectory(home);
        return new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path, OpenCodeDataHome = home },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), new LaunchGoldenProcessTests.GoldenOpenCodeStore(),
            (_, _) => { });
    }

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
