using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.ComfyUI;
using RedCompute.Plugin.Kev;
using RedCompute.Plugin.LocalWsl;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
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

    /// <summary>
    /// The containment each Claude seam's production default gives the spec its production launch
    /// builds (the spec carries its containment since the execution host): each spec is taken from a
    /// real start, one-shot and agent call, then handed to a fresh service's default seam.
    /// </summary>
    [WindowsFact("containment is a Windows Job Object")]
    public async Task Seam_defaults_contain_as_recorded()
    {
        var specs = await ClaudeProductionSpecs();
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore(), capture: false);
        var seams = new JsonObject
        {
            ["claude.LaunchSessionProcess"] = await ContainmentOf(service.LaunchSessionProcess, specs.Session),
            ["claude.LaunchOneshotProcess"] = await ContainmentOf(service.LaunchOneshotProcess, specs.Oneshot),
            ["claude.LaunchAgentProcess"] = await ContainmentOf(service.LaunchAgentProcess, specs.Agent),
        };
        LaunchGoldens.Verify("A-seam-defaults", seams);
    }

    /// <summary>The specs a session start, a one-shot and an agent call hand their seams, launching nothing.</summary>
    private async Task<(HostSpawnSpec Session, HostSpawnSpec Oneshot, HostSpawnSpec Agent)> ClaudeProductionSpecs()
    {
        HostSpawnSpec? session = null, oneshot = null, agent = null;
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore(), capture: false);
        service.LaunchSessionProcess = spec => { session = spec; return null; };
        service.LaunchOneshotProcess = spec => { oneshot = spec; return null; };
        service.LaunchAgentProcess = spec => { agent = spec; return null; };
        using (SessionScratch.PushExecutionToken(Token))
            await service.StartSessionAsync(_project, "claude-golden-model", "owner", null, null, "high", null, null,
                null, Provenance(), scratchDirectory: _scratch);
        using (var messages = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]"))
            await service.ExecuteOneshotAsync("claude-golden-model", "be brief", messages.RootElement, 32,
                CancellationToken.None, "low", 60);
        await service.ExecuteAgentAsync("golden prompt", null, _project, "claude-golden-model", "high", 3,
            null, null, null, 60, CancellationToken.None);
        return (session!, oneshot!, agent!);
    }

    // ---- Claude Code ------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_session_start()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        using (SessionScratch.PushExecutionToken(Token))
            await service.StartSessionAsync(_project, "claude-golden-model", "owner", null, null, "high", null, null,
                null, Provenance(), scratchDirectory: _scratch, developerInstructions: "Golden developer instructions.");
        VerifyHosted("A-claude-session-start", "claude.LaunchSessionProcess");
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
        VerifyHosted($"A-claude-session-start-{level}", "claude.LaunchSessionProcess");
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
        VerifyHosted("A-claude-session-resume", "claude.LaunchSessionProcess");
    }

    [Fact]
    public async Task Claude_oneshot()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        using var messages = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]");
        await service.ExecuteOneshotAsync("claude-golden-model", "be brief", messages.RootElement, 32,
            CancellationToken.None, "low", 60);
        VerifyHosted("A-claude-oneshot", "claude.LaunchOneshotProcess");
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
        VerifyHosted(golden, "claude.LaunchAgentProcess");
    }

    [Fact]
    public async Task Claude_fork_probe()
    {
        var store = LaunchGoldenProcessTests.SeedRollback(_project, _scratch);
        var projects = Path.Combine(_root, "projects");
        LaunchGoldenProcessTests.WriteTwoTurnChain(Path.Combine(projects, "golden"));
        // The probe's production default, captured where the host hands it to the OS: uncontained,
        // as B-claude-fork-probe proves on a real process. The capture starts nothing, so the probe
        // reports the CLI unavailable; only the launch is pinned here.
        var service = Claude(store);
        service.ProjectsRootOverride = projects;
        using (SessionScratch.PushExecutionToken(Token))
            await service.RollbackAsync(LaunchGoldenProcessTests.RollbackRequest());
        VerifyHosted("A-claude-fork-probe", "claude.ProbeFork");
    }

    // ---- ComfyUI and LocalWsl: the start info each backend's StartAsync launches ------------------------

    [Theory]
    [InlineData("A-comfyui-start-native", false)]
    [InlineData("A-comfyui-start-wsl", true)]
    public void ComfyUI_start(string golden, bool wsl)
    {
        var provider = new ComfyUIProvider(ComfyConfig(wsl), "image-gen", _ => { });
        HostConverts(provider.BuildSpawnSpec());
        VerifyHosted(golden, "comfyui.server");
    }

    [Theory]
    [InlineData("A-localwsl-start-native", false)]
    [InlineData("A-localwsl-start-wsl", true)]
    public void LocalWsl_start(string golden, bool wsl)
    {
        var provider = new LocalWslProvider(LocalWslConfig(wsl), "tts", _ => { });
        HostConverts(provider.BuildSpawnSpec());
        VerifyHosted(golden, "localwsl.server");
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
        // Before launching, StartAsync only probes 127.0.0.1:18999 over HTTP; the host spawns nothing.
        var provider = new ComfyUIProvider(ComfyConfig(wsl), "image-gen", _ => { }, host: CapturingHost());
        Assert.False(await provider.StartAsync());
        VerifyHosted(golden, "comfyui.server");
    }

    /// <summary>
    /// In WSL mode StartAsync first resolves the distro's address (<c>wsl.exe hostname -I</c>); that
    /// launch now goes through the host too, so it is answered with nothing instead of reaching a
    /// real wsl.exe, and only the server launch is captured.
    /// </summary>
    [Theory]
    [InlineData("A-localwsl-start-native", false)]
    [InlineData("A-localwsl-start-wsl", true)]
    public async Task LocalWsl_start_through_its_seam(string golden, bool wsl)
    {
        var provider = new LocalWslProvider(LocalWslConfig(wsl), "tts", _ => { },
            host: CapturingHost(captures: owner => owner == "localwsl"));
        Assert.False(await provider.StartAsync());
        VerifyHosted(golden, "localwsl.server");
    }

    // ---- stop: recorded through a seam on the pre-move code, then where the host hands it over ---------

    /// <summary>The WSL stop kills ComfyUI's python inside the distro (<c>wsl.exe pkill</c>).</summary>
    [WindowsFact("the stop runs only after a Windows launch")]
    public async Task ComfyUI_stop_wsl()
    {
        var config = ComfyConfig(wsl: true);
        config.StartupTimeoutSeconds = 1;
        // The server launch gets a real process (the stop runs only for a live one); the stop is captured.
        var provider = new ComfyUIProvider(config, "image-gen", _ => { },
            host: CapturingHost(runs: owner => owner == "comfyui"));
        Assert.False(await provider.StartAsync());
        await provider.StopAsync();
        VerifyHosted("A-comfyui-stop-wsl", "comfyui.stop");
    }

    /// <summary>The distro address lookup that precedes every WSL backend start (<c>wsl.exe hostname -I</c>).</summary>
    [WindowsFact("the lookup runs only before a Windows launch")]
    public async Task LocalWsl_hostname()
    {
        var provider = new LocalWslProvider(LocalWslConfig(wsl: true), "tts", _ => { },
            host: CapturingHost(captures: owner => owner == "localwsl-hostname"));
        Assert.False(await provider.StartAsync());
        VerifyHosted("A-localwsl-hostname", "localwsl.hostname");
    }

    /// <summary>
    /// The WSL stop kills the backend's process group inside the distro, only while its start time
    /// still matches the identity the launch script printed.
    /// </summary>
    [WindowsFact("the stop runs only after a Windows launch")]
    public async Task LocalWsl_stop_wsl()
    {
        var config = LocalWslConfig(wsl: true);
        config.StartupTimeoutSeconds = 1;
        // The server launch gets a live stand-in that prints the identity marker; the lookup gets nothing.
        var provider = new LocalWslProvider(config, "tts", _ => { },
            host: CapturingHost(
                runs: owner => owner == "localwsl",
                firstLine: "REDCOMPUTE_LOCALWSL_PROCESS=4242:987654",
                captures: owner => owner == "localwsl-stop"));
        // Never healthy: the start gives up and stops the backend it launched.
        Assert.False(await provider.StartAsync());
        VerifyHosted("A-localwsl-stop-wsl", "localwsl.stop");
    }

    // ---- Kev: the sidecar launch and its WSL stop ---------------------------------------------------------

    /// <summary>
    /// The Kev sidecar launch: in WSL a wrapper that exports the quality knobs and prints the owned
    /// process identity; natively cmd.exe with the knobs in the environment.
    /// </summary>
    [WindowsTheory("Kev launches through wsl.exe/cmd.exe on Windows")]
    [InlineData("A-kev-start-native", false)]
    [InlineData("A-kev-start-wsl", true)]
    public async Task Kev_start(string golden, bool wsl)
    {
        var controller = new KevProcessController(CapturingHost());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(KevConfig(wsl), _ => { }));
        VerifyHosted(golden, "kev.server");
    }

    /// <summary>The WSL stop kills the sidecar's process group, only while its start time still matches.</summary>
    [WindowsFact("the stop runs only after a Windows launch")]
    public async Task Kev_stop_wsl()
    {
        // The sidecar launch gets a live stand-in that prints the identity marker; the stop is captured.
        var controller = new KevProcessController(CapturingHost(
            runs: owner => owner == "kev", firstLine: "REDCOMPUTE_KEV_PROCESS=4242:987654"));
        var owned = await controller.StartAsync(KevConfig(wsl: true), _ => { });
        await controller.StopAsync(owned, _ => { });
        VerifyHosted("A-kev-stop-wsl", "kev.stop");
    }

    internal static ProviderConfig KevConfig(bool wsl) => new()
    {
        Type = "KevLocalQuality",
        WslDistro = wsl ? "Golden-Distro" : null,
        LaunchCommand = wsl
            ? "cd /home/golden/kev && ./venv/bin/python serve.py --port 18996"
            : "cd /d \"C:\\Golden Kev\" && python serve.py --port 18996",
        Extra = new Dictionary<string, object?>
        {
            ["MergeLora"] = "true",
            ["Dtype"] = "float16",
        },
    };

    /// <summary>A real process that keeps running, redirected like a backend, optionally printing a first line.</summary>
    private Process Running(string? firstLine = null)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add((firstLine is null ? "" : $"echo {firstLine}& ") + "ping -n 120 127.0.0.1 >nul");
        var process = Process.Start(start)!;
        lock (_started) _started.Add(process);
        return process;
    }

    // ---- OpenCode, captured where the local host hands its start info to the OS ----------------------

    [Fact]
    public async Task OpenCode_acp_session()
    {
        var service = OpenCode();
        try
        {
            using (SessionScratch.PushExecutionToken(Token))
                await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                    null, null, Provenance(), _scratch);
        }
        catch (NullReferenceException)
        {
            // The ACP launch dereferences the process it was handed; a capturing host hands none.
        }
        (ProcessStartInfo Start, string Containment) launch;
        lock (_hosted) launch = Assert.Single(_hosted);
        var start = launch.Start;
        // The access note of the default level is written for the new session and named in
        // OPENCODE_CONFIG_CONTENT (as in B-opencode-acp): record that file, then delete exactly it.
        var config = JsonNode.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!)!;
        var instructions = config["instructions"]!.AsArray().Single()!.GetValue<string>();
        _normalizer.Add(Path.GetFileNameWithoutExtension(instructions), "<session-id>");
        var golden = LaunchGoldens.FromStartInfo(start, "opencode.LaunchAcpProcess", launch.Containment, _normalizer);
        golden["instructionFile"] = _normalizer.Apply(File.ReadAllText(instructions));
        File.Delete(instructions);
        LaunchGoldens.Verify("A-opencode-acp", golden);
    }

    [Fact]
    public async Task OpenCode_exec()
    {
        var service = OpenCode();
        await service.ExecuteAsync("golden prompt", null, _project, "golden/model", 60, CancellationToken.None,
            env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        VerifyHosted("A-opencode-exec", "opencode.LaunchExecProcess");
    }

    /// <summary>At Full there is no access note, so no instruction file and no OPENCODE_CONFIG_CONTENT.</summary>
    [Fact]
    public async Task OpenCode_acp_session_at_full()
    {
        var service = OpenCode();
        try
        {
            using (SessionAccessScope.Push(new SessionAccessState(SessionAccessLevels.Full)))
            using (SessionScratch.PushExecutionToken(Token))
                await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                    null, null, Provenance(), _scratch);
        }
        catch (NullReferenceException)
        {
            // The ACP launch dereferences the process it was handed; a capturing host hands none.
        }
        VerifyHosted("A-opencode-acp-full", "opencode.LaunchAcpProcess");
    }

    [Fact]
    public async Task OpenCode_exec_docker()
    {
        // Docker exec refuses a configured OpenCodeDataHome, so this service has none.
        var service = OpenCode(dataHome: false);
        await service.ExecuteAsync("golden prompt", "golden-container", _project, "golden/model", 60,
            CancellationToken.None, env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        VerifyHosted("A-opencode-exec-docker", "opencode.LaunchExecProcess");
    }

    [WindowsFact("containment is a Windows Job Object")]
    public async Task Node_zero_seam_defaults_contain_as_recorded()
    {
        var (acp, exec) = await OpenCodeProductionSpecs();
        var seams = new JsonObject
        {
            ["opencode.LaunchAcpProcess"] = await ContainmentOf(OpenCode(capture: false).LaunchAcpProcess, acp),
            ["opencode.LaunchExecProcess"] = await ContainmentOf(OpenCode(capture: false).LaunchExecProcess, exec),
            ["comfyui.LaunchServerProcess"] = await ContainmentOf(Comfy().LaunchServerProcess, Comfy().BuildSpawnSpec()),
            ["localwsl.LaunchServerProcess"] = await ContainmentOf(LocalWsl().LaunchServerProcess, LocalWsl().BuildSpawnSpec()),
        };
        LaunchGoldens.Verify("A-seam-defaults-node-zero", seams);
    }

    /// <summary>
    /// The specs a session start and an exec hand their seams, launching nothing. The start writes the
    /// default level's access note for its session; exactly that file is deleted.
    /// </summary>
    private async Task<(HostSpawnSpec Acp, HostSpawnSpec Exec)> OpenCodeProductionSpecs()
    {
        HostSpawnSpec? acp = null, exec = null;
        var service = OpenCode(capture: false);
        service.LaunchAcpProcess = spec => { acp = spec; return null; };
        service.LaunchExecProcess = spec => { exec = spec; return null; };
        try
        {
            using (SessionScratch.PushExecutionToken(Token))
                await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                    null, null, Provenance(), _scratch);
        }
        catch (NullReferenceException)
        {
            // The ACP launch dereferences the process it was handed; the capturing seam hands none.
        }
        await service.ExecuteAsync("golden prompt", null, _project, "golden/model", 60, CancellationToken.None);
        var config = JsonNode.Parse(acp!.EnvironmentOverlay!["OPENCODE_CONFIG_CONTENT"])!;
        File.Delete(config["instructions"]!.AsArray().Single()!.GetValue<string>());
        return (acp, exec!);
    }

    private ComfyUIProvider Comfy() => new(ComfyConfig(false), "image-gen", _ => { });

    private LocalWslProvider LocalWsl() => new(LocalWslConfig(false), "tts", _ => { });

    /// <summary>
    /// The containment a spec seam's production default gives the provider's own spec: the spec's
    /// launch target is swapped for a stand-in that stays alive, everything else is the provider's.
    /// </summary>
    private async Task<string> ContainmentOf(Func<HostSpawnSpec, IHostProcess?> seam, HostSpawnSpec production)
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

    /// <summary>
    /// An OpenCode service, by default on a capturing host: its seams keep their production default, so
    /// every launch is captured where the local host hands its start info to the OS, with the
    /// containment it applies.
    /// </summary>
    private OpenCodeSessionService OpenCode(bool dataHome = true, bool capture = true)
    {
        var path = Path.Combine(_root, "cli", "opencode.exe");
        File.WriteAllText(path, "seam only, never executed");
        var home = dataHome ? Path.Combine(_root, "opencode-data") : null;
        if (home != null) Directory.CreateDirectory(home);
        return new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path, OpenCodeDataHome = home },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), new LaunchGoldenProcessTests.GoldenOpenCodeStore(),
            (_, _) => { }, host: capture ? CapturingHost() : null);
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

    // ---- Repository validation: git proves worktree membership ---------------------------------------

    /// <summary>
    /// The two git reads that prove a worktree belongs to a repository: the repository's common git
    /// dir, then the candidate's git dir, common dir and prefix. The first answers with a common dir
    /// so the second runs; the second gets no process, which the validator reports as unavailable.
    /// </summary>
    [WindowsFact("the stand-in for git's answer is cmd.exe")]
    public async Task Repository_worktree_git()
    {
        var repository = Path.Combine(_root, "repository");
        var worktree = Path.Combine(_root, "repository.worktrees", "feature");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(worktree);
        var answered = false;
        var host = new LocalExecutionHost("golden-node")
        {
            StartProcess = (start, mode, owner) =>
            {
                lock (_hosted) _hosted.Add((start, LocalExecutionHost.DescribeContainment(mode, owner)));
                if (answered) return null;
                answered = true;
                return Exits($"{repository}/.git");
            },
        };
        var validator = new RepositoryReferenceValidator(new HttpClient(new RepositoryEntity(repository))
        {
            BaseAddress = new Uri("http://redleaf/"),
        }, host);
        var result = await validator.ValidateAsync(Guid.NewGuid().ToString("D"), worktree);
        Assert.Equal(503, result.StatusCode);

        List<(ProcessStartInfo Start, string Containment)> launches;
        lock (_hosted) launches = [.. _hosted];
        Assert.Equal(2, launches.Count);
        LaunchGoldens.Verify("A-git-repository-common-dir",
            LaunchGoldens.FromStartInfo(launches[0].Start, "app.RepositoryReferenceValidator", launches[0].Containment, _normalizer));
        LaunchGoldens.Verify("A-git-worktree-candidate",
            LaunchGoldens.FromStartInfo(launches[1].Start, "app.RepositoryReferenceValidator", launches[1].Containment, _normalizer));
    }

    // ---- Docker: managed containers and Docker Desktop -----------------------------------------------

    /// <summary>
    /// A container for an image nobody runs yet: is it running, is a stale one there, then run it.
    /// No docker answers, so it is never running, there is nothing stale, and the run fails.
    /// </summary>
    [Fact]
    public async Task Docker_ensure_container()
    {
        var docker = new DockerContainerService((_, _) => { }, CapturingHost());
        await Assert.ThrowsAsync<InvalidOperationException>(() => docker.EnsureContainerAsync("ghcr.io/golden/tools:1.2"));
        VerifyAllHosted("A-docker-ensure-container", "app.DockerContainerService");
    }

    /// <summary>A managed container is stopped, then removed.</summary>
    [Fact]
    public async Task Docker_stop_container()
    {
        var docker = new DockerContainerService((_, _) => { }, CapturingHost());
        await docker.StopAsync("rc-tools-1.2");
        VerifyAllHosted("A-docker-stop-container", "app.DockerContainerService");
    }

    /// <summary>
    /// Docker Desktop on demand: the daemon does not answer, so Desktop is started, and the next
    /// probe (one poll later) finds it ready.
    /// </summary>
    [WindowsFact("Docker Desktop is a Windows host concern")]
    public async Task Docker_desktop_start()
    {
        if (!File.Exists(@"C:\Program Files\Docker\Docker\Docker Desktop.exe")) return; // starts only when installed
        var probes = 0;
        var host = new LocalExecutionHost("golden-node")
        {
            StartProcess = (start, mode, owner) =>
            {
                lock (_hosted) _hosted.Add((start, LocalExecutionHost.DescribeContainment(mode, owner)));
                if (start.FileName != "docker") return null; // Desktop itself: never launched by the test
                return probes++ == 0 ? null : Exits("ready");
            },
        };
        await DockerDesktopService.EnsureRunningAsync(_ => { }, host);
        VerifyAllHosted("A-docker-desktop-start", "app.DockerDesktopService");
    }

    /// <summary>Every launch the host handed the OS, in order, as one golden.</summary>
    private void VerifyAllHosted(string golden, string seam)
    {
        List<(ProcessStartInfo Start, string Containment)> launches;
        lock (_hosted) launches = [.. _hosted];
        Assert.NotEmpty(launches);
        LaunchGoldens.Verify(golden, new JsonArray(launches
            .Select(launch => (JsonNode)LaunchGoldens.FromStartInfo(launch.Start, seam, launch.Containment, _normalizer)).ToArray()));
    }

    /// <summary>A Repository entity whose checkout is <paramref name="localPath"/>.</summary>
    private sealed class RepositoryEntity(string localPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    typeSlug = "repository",
                    name = "golden",
                    data = JsonSerializer.Serialize(new { status = "active", local_path = localPath }),
                }), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A real process with piped stdio that prints <paramref name="line"/> and exits 0.</summary>
    private Process Exits(string line)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add($"echo {line}");
        var process = Process.Start(start)!;
        lock (_started) _started.Add(process);
        return process;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private readonly List<ProcessStartInfo> _launches = [];
    private readonly List<(ProcessStartInfo Start, string Containment)> _hosted = [];

    /// <summary>
    /// An execution host that captures, at the single point where it hands a start info to the OS,
    /// every launch and the containment it would apply. Launches are picked by owner label:
    /// <paramref name="runs"/> get a real, long-running stand-in printing <paramref name="firstLine"/>
    /// (a stop runs only for a live process); when <paramref name="captures"/> is given, any other
    /// launch is answered with no process and not recorded.
    /// </summary>
    private LocalExecutionHost CapturingHost(
        Func<string, bool>? runs = null, string? firstLine = null, Func<string, bool>? captures = null) => new("golden-node")
    {
        StartProcess = (start, mode, owner) =>
        {
            if (runs?.Invoke(owner) == true) return Running(firstLine);
            if (captures is not null && !captures(owner)) return null;
            lock (_hosted) _hosted.Add((start, LocalExecutionHost.DescribeContainment(mode, owner)));
            return null;
        },
    };

    /// <summary>A spec as the local host converts it, with the containment it would apply.</summary>
    private void HostConverts(HostSpawnSpec spec)
    {
        lock (_hosted)
            _hosted.Add((LocalExecutionHost.ToStartInfo(spec), LocalExecutionHost.DescribeContainment(spec.Containment, spec.OwnerLabel)));
    }

    private void VerifyHosted(string golden, string seam)
    {
        (ProcessStartInfo Start, string Containment) launch;
        lock (_hosted) launch = Assert.Single(_hosted);
        // A one-shot runs in a fresh isolated directory: its name is the only run-specific part.
        if (seam == "claude.LaunchOneshotProcess" && !string.IsNullOrEmpty(launch.Start.WorkingDirectory))
            _normalizer.Add(launch.Start.WorkingDirectory, "<isolated-cwd>");
        LaunchGoldens.Verify(golden, LaunchGoldens.FromStartInfo(launch.Start, seam, launch.Containment, _normalizer));
    }

    private void Capture(ProcessStartInfo start)
    {
        lock (_launches) _launches.Add(start);
    }

    private void Verify(string golden, string seam, string containment)
    {
        ProcessStartInfo start;
        lock (_launches) start = Assert.Single(_launches);
        LaunchGoldens.Verify(golden, LaunchGoldens.FromStartInfo(start, seam, containment, _normalizer));
    }

    /// <summary>
    /// A Claude service on a capturing host: its seams keep their production default, so every launch
    /// is captured where the local host hands its start info to the OS, with the containment it applies.
    /// </summary>
    private ClaudeSessionService Claude(IClaudeSessionStore store, bool capture = true)
        => new(new ClaudeConfig { ClaudePath = _claudePath },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store,
            new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { },
            host: capture ? CapturingHost() : null);

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
