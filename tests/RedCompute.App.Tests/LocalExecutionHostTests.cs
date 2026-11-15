using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero's execution host (design section 1): a spec becomes the start info its site built
/// before, field for field, and the host hands it to the entry point the site called.
/// </summary>
public sealed class LocalExecutionHostTests
{
    private static readonly Normalizer Plain = new();

    /// <summary>What the launch goldens see: the canonical form of a start info.</summary>
    private static string Canonical(ProcessStartInfo start)
        => LaunchGoldens.Canonical(LaunchGoldens.FromStartInfo(start, "spec", "none", Plain));

    private static void AssertSameLaunch(ProcessStartInfo before, HostSpawnSpec spec)
        => Assert.Equal(Canonical(before), Canonical(LocalExecutionHost.ToStartInfo(spec)));

    [Fact]
    public void An_argument_list_with_redirected_output_converts_as_the_wsl_sites_build_it()
    {
        var before = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-d", "Distro", "--", "bash", "-lc", "echo 'a b' \"$HOME\"" })
            before.ArgumentList.Add(argument);

        AssertSameLaunch(before, new HostSpawnSpec
        {
            FileName = "wsl.exe",
            ArgumentList = ["-d", "Distro", "--", "bash", "-lc", "echo 'a b' \"$HOME\""],
            CreateNoWindow = true,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "wsl",
        });
    }

    [Fact]
    public void Raw_arguments_and_a_working_directory_convert_as_the_native_sites_build_them()
    {
        var before = new ProcessStartInfo
        {
            FileName = @"C:\venv\Scripts\python.exe",
            Arguments = "main.py --listen 127.0.0.1 \"C:\\with space\"",
            WorkingDirectory = @"C:\Comfy UI",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        AssertSameLaunch(before, new HostSpawnSpec
        {
            FileName = @"C:\venv\Scripts\python.exe",
            RawArguments = "main.py --listen 127.0.0.1 \"C:\\with space\"",
            WorkingDirectory = LocalExecutionHost.Shared.Path(@"C:\Comfy UI"),
            CreateNoWindow = true,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "comfyui",
        });
    }

    [Fact]
    public void An_environment_overlay_touches_the_environment_and_none_leaves_it_untouched()
    {
        var touched = new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/d /s /c \"x\"" };
        touched.Environment["KEV_MERGE"] = "1";
        touched.Environment["KEV_DTYPE"] = "bfloat16";
        var spec = new HostSpawnSpec
        {
            FileName = "cmd.exe",
            RawArguments = "/d /s /c \"x\"",
            EnvironmentOverlay = new Dictionary<string, string> { ["KEV_MERGE"] = "1", ["KEV_DTYPE"] = "bfloat16" },
            Containment = ContainmentMode.None,
            OwnerLabel = "kev",
        };
        AssertSameLaunch(touched, spec);
        Assert.True(LaunchGoldens.EnvironmentTouched(LocalExecutionHost.ToStartInfo(spec)));

        var untouched = LocalExecutionHost.ToStartInfo(spec with { EnvironmentOverlay = null });
        Assert.False(LaunchGoldens.EnvironmentTouched(untouched));

        // An empty overlay sets nothing, as a site's loop over an empty dictionary touches nothing.
        Assert.False(LaunchGoldens.EnvironmentTouched(LocalExecutionHost.ToStartInfo(
            spec with { EnvironmentOverlay = new Dictionary<string, string>() })));
    }

    [Fact]
    public void Piped_stdio_and_encodings_convert_as_the_session_sites_build_them()
    {
        var utf8 = new UTF8Encoding(false);
        var before = new ProcessStartInfo
        {
            FileName = @"C:\cli\claude.exe",
            WorkingDirectory = @"C:\project",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        before.ArgumentList.Add("--print");
        before.Environment["REDLEAF_SCRATCH_DIR"] = @"C:\scratch";
        AssertSameLaunch(before, new HostSpawnSpec
        {
            FileName = @"C:\cli\claude.exe",
            ArgumentList = ["--print"],
            WorkingDirectory = LocalExecutionHost.Shared.Path(@"C:\project"),
            EnvironmentOverlay = new Dictionary<string, string> { ["REDLEAF_SCRATCH_DIR"] = @"C:\scratch" },
            CreateNoWindow = true,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            Containment = ContainmentMode.Session,
            OwnerLabel = "claude-code",
        });
    }

    [Fact]
    public void A_spec_with_no_arguments_and_no_directory_sets_neither()
    {
        var start = LocalExecutionHost.ToStartInfo(new HostSpawnSpec
        {
            FileName = "tool", Containment = ContainmentMode.None, OwnerLabel = "tool",
        });
        Assert.Equal("", start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.Equal("", start.WorkingDirectory);
        Assert.False(start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError);
        Assert.False(LaunchGoldens.EnvironmentTouched(start));
    }

    [Fact]
    public void A_spec_cannot_have_both_kinds_of_arguments()
    {
        Assert.Throws<ArgumentException>(() => LocalExecutionHost.ToStartInfo(new HostSpawnSpec
        {
            FileName = "tool", ArgumentList = ["a"], RawArguments = "a",
            Containment = ContainmentMode.None, OwnerLabel = "tool",
        }));
    }

    [Theory]
    [InlineData(ContainmentMode.Kept)]
    [InlineData(ContainmentMode.DetachedDesktop)]
    public void Kept_and_desktop_launches_are_not_plain_starts(ContainmentMode mode)
    {
        var host = new LocalExecutionHost("node_test") { StartProcess = (_, _, _) => throw new Exception("not reached") };
        Assert.Throws<ArgumentException>(() => host.Processes.Start(new HostSpawnSpec
        {
            FileName = "tool", Containment = mode, OwnerLabel = "tool",
        }));
    }

    [Fact]
    public void The_start_hook_receives_the_start_info_containment_and_owner()
    {
        var seen = new List<(ProcessStartInfo Start, ContainmentMode Mode, string Owner)>();
        var host = new LocalExecutionHost("node_test")
        {
            StartProcess = (start, mode, owner) => { seen.Add((start, mode, owner)); return null; },
        };
        var result = host.Processes.Start(new HostSpawnSpec
        {
            FileName = "codex", ArgumentList = ["app-server"], Containment = ContainmentMode.Session, OwnerLabel = "codex",
        });
        Assert.Null(result);
        var (start, mode, owner) = Assert.Single(seen);
        Assert.Equal("codex", start.FileName);
        Assert.Equal(["app-server"], start.ArgumentList);
        Assert.Equal(ContainmentMode.Session, mode);
        Assert.Equal("codex", owner);
        Assert.Equal("session:codex", LocalExecutionHost.DescribeContainment(mode, owner));
        Assert.Equal("none", LocalExecutionHost.DescribeContainment(ContainmentMode.None, "comfyui"));
    }

    /// <summary>
    /// Session readers and control-line writers (Claude's ReadStdout/ReadStderr/WriteControlLine) keep
    /// reading and writing the very objects the process itself hands out, on every access.
    /// </summary>
    [Fact]
    public void A_launched_process_hands_out_the_process_own_stream_objects()
    {
        var host = new LocalExecutionHost("node_test");
        using var hosted = host.Processes.Start(new HostSpawnSpec
        {
            FileName = TestProcesses.StdinSink,
            CreateNoWindow = true,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Containment = ContainmentMode.None,
            OwnerLabel = "test-sink",
        })!;
        var process = Assert.IsType<LocalHostProcess>(hosted).Process;
        try
        {
            Assert.Same(process.StandardOutput, hosted.StandardOutput);
            Assert.Same(process.StandardError, hosted.StandardError);
            Assert.Same(process.StandardInput, hosted.StandardInput);
            Assert.Same(hosted.StandardOutput, hosted.StandardOutput);
            Assert.Same(hosted.StandardInput, hosted.StandardInput);
            Assert.Equal(process.Id, hosted.Id);
        }
        finally
        {
            hosted.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task A_launched_process_forwards_its_streams_lines_and_exit()
    {
        var host = new LocalExecutionHost("node_test");
        using var process = host.Processes.Start(new HostSpawnSpec
        {
            FileName = TestProcesses.StdinSink,
            CreateNoWindow = true,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "test-sink",
        })!;
        Assert.Equal("node_test", process.NodeId);
        Assert.True(process.Id > 0);
        Assert.True(process.CreatedUtcTicks > 0);
        Assert.True(process.StartTimeUtc <= DateTime.UtcNow);

        var lines = new List<string?>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputLine += line =>
        {
            lock (lines) lines.Add(line);
            if (line is null) ended.TrySetResult();
        };
        process.ErrorLine += _ => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.WriteLine("hello host");
        process.StandardInput.Close();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        lock (lines)
        {
            Assert.Contains("hello host", lines);
            Assert.Null(lines[^1]);
        }
    }

    [WindowsFact("containment is a Windows Job Object")]
    public async Task Session_launches_are_contained_and_plain_launches_are_not()
    {
        var host = new LocalExecutionHost("node_test");
        HostSpawnSpec Sink(ContainmentMode mode) => new()
        {
            FileName = TestProcesses.StdinSink,
            CreateNoWindow = true,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            Containment = mode,
            OwnerLabel = "host-test",
        };
        using var contained = host.Processes.Start(Sink(ContainmentMode.Session))!;
        using var plain = host.Processes.Start(Sink(ContainmentMode.None))!;
        try
        {
            Assert.Equal("session:host-test", await ProbeHarness.ObserveContainmentAsync(contained.Id));
            Assert.True(host.Containment.IsSessionMember(contained.Id));
            var tree = host.Containment.FindByRoot(contained.Id);
            Assert.NotNull(tree);
            Assert.False(tree!.IsKept);
            Assert.Equal("host-test", tree.Owner);
            Assert.Equal(contained.CreatedUtcTicks, tree.RootCreatedUtcTicks);

            Assert.Null(host.Containment.FindByRoot(plain.Id));
            Assert.False(host.Containment.IsSessionMember(plain.Id));
        }
        finally
        {
            contained.Kill(entireProcessTree: true);
            plain.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// A transcript the CLI is still appending to (and may replace) opens for reading only with the
    /// writer's share: the plain OpenRead shares Read alone and is refused, as File.OpenRead is.
    /// </summary>
    [WindowsFact("sharing violations are Windows file semantics")]
    public void A_shared_read_opens_a_file_another_handle_is_writing()
    {
        var host = new LocalExecutionHost("node_test");
        var directory = host.Files.CreateTempDirectory("redcompute-host-share");
        try
        {
            var path = Path.Combine(directory.Value, "transcript.jsonl");
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write("{\"line\":1}\n"u8);
            writer.Flush();

            Assert.Throws<IOException>(() => host.Files.OpenRead(host.Path(path)).Dispose());
            using var shared = host.Files.OpenRead(host.Path(path), FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(shared, Encoding.UTF8);
            Assert.Equal("{\"line\":1}", reader.ReadLine());
            var stream = Assert.IsType<FileStream>(shared);
            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
        }
        finally
        {
            host.Files.DeleteDirectory(directory, recursive: true);
        }
    }

    [Fact]
    public void Files_forward_to_the_local_file_system()
    {
        var host = new LocalExecutionHost("node_test");
        var root = host.Files.CreateTempDirectory("host-files-test");
        try
        {
            Assert.StartsWith(Path.GetTempPath(), root.Value, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"host-files-test-[0-9a-f]{32}$", root.Value);
            Assert.Equal("node_test", root.NodeId);
            Assert.True(host.Files.DirectoryExists(root));

            var file = host.Path(Path.Combine(root.Value, "a.txt"));
            host.Files.WriteAllText(file, "one");
            Assert.True(host.Files.FileExists(file));
            Assert.Equal("one", host.Files.ReadAllText(file));
            host.Files.ReplaceAtomically(file, "two");
            Assert.Equal("two", host.Files.ReadAllText(file));
            Assert.False(File.Exists(file.Value + ".tmp"));

            var stat = host.Files.Stat(file);
            Assert.True(stat.Exists);
            Assert.False(stat.IsDirectory);
            Assert.Equal(3, stat.Length);
            Assert.True(host.Files.Stat(root).IsDirectory);
            Assert.False(host.Files.Stat(host.Path(Path.Combine(root.Value, "missing"))).Exists);

            var sub = host.Path(Path.Combine(root.Value, "sub"));
            host.Files.CreateDirectory(sub);
            Assert.Equal([sub], host.Files.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly));
            Assert.Equal([file], host.Files.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            host.Files.DeleteDirectory(root, recursive: true);
        }
        Assert.False(Directory.Exists(root.Value));
    }

    [Fact]
    public async Task Network_and_platform_are_the_local_machine()
    {
        var host = new LocalExecutionHost("node_test");
        var uri = new Uri("http://127.0.0.1:8188/");
        Assert.Same(uri, host.Network.ResolveServiceUri(uri));
        await using (var forward = await host.Network.ReverseForwardAsync(uri))
            Assert.Same(uri, forward.ServerSideUri);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Assert.True(host.Network.IsListening(((System.Net.IPEndPoint)listener.LocalEndpoint).Port));
        }
        finally
        {
            listener.Stop();
        }

        Assert.Equal(PlatformSupport.CanLaunchLocalBackends, host.Platform.CanLaunchLocalBackends);
        Assert.Equal(OperatingSystem.IsWindows(), host.Platform.IsWindows);
        Assert.Equal(Environment.MachineName, host.Platform.Info.MachineName);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            host.Platform.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Assert.Equal(Path.GetTempPath(), host.Files.TempPath.Value);
    }

    [Fact]
    public void Process_listing_finds_this_process()
    {
        var host = new LocalExecutionHost("node_test");
        using var self = Process.GetCurrentProcess();
        var found = host.Processes.Find(self.Id);
        Assert.NotNull(found);
        Assert.Equal(self.ProcessName, found!.Name);
        Assert.Equal(SessionProcessContainment.CreationUtcTicks(self), found.CreatedUtcTicks);
        Assert.Contains(host.Processes.List(), info => info.Pid == self.Id);
        Assert.Null(host.Processes.Find(int.MaxValue));
    }

    [Fact]
    public void The_registry_hands_out_the_local_host_for_every_local_id()
    {
        var identity = new NodeIdentityResult(
            new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_legacy"), Persisted: true, Created: false, null);
        var host = new LocalExecutionHost("node_now");
        INodeRegistry nodes = new NodeRegistry(identity, NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow,
            localHost: host);
        Assert.Same(host, nodes.HostFor(null));
        Assert.Same(host, nodes.HostFor("node_now"));
        Assert.Same(host, nodes.HostFor("node_legacy"));
        Assert.Same(host, nodes.HostFor(INodeRegistry.LocalAlias));
        Assert.Throws<KeyNotFoundException>(() => nodes.HostFor("node_elsewhere"));

        var defaulted = new NodeRegistry(identity, NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);
        Assert.Equal("node_now", defaulted.LocalHost.NodeId);
    }
}
