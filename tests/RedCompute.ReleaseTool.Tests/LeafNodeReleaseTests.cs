using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.ReleaseTool;
using Xunit;

namespace RedCompute.ReleaseTool.Tests;

/// <summary>
/// The leaf-node release artifacts (Phase 3 R7, D3): deterministic archives, a strict manifest,
/// installers that verify before they install and never pair, and the pin that all of it is an
/// addition to RedCompute's release, which keeps exactly today's artifact and descriptors.
/// </summary>
public sealed class LeafNodeReleaseTests
{
    // ---- the additivity pin --------------------------------------------------------------------------

    /// <summary>What the Nightly's RedCompute recipe produces today (d8bb34ca), on each root.</summary>
    private static readonly string[] TodaysRedComputeFiles =
    [
        "redcompute-win-x64.zip", "redcompute-component-input.v1.json",
        "redcompute-linux-x64.zip", "redcompute-linux-x64-input.v1.json",
    ];

    [Fact]
    public void Leaf_node_files_are_five_additions_that_never_collide_with_todays_release()
    {
        Assert.Equal(
            ["leaf-node-win-x64.zip", "leaf-node-linux-x64.tar.gz", "leaf-node-manifest.v1.json",
             "leaf-node-install.ps1", "leaf-node-install.sh"],
            LeafNodeRuntime.AssetNames);
        foreach (var name in LeafNodeRuntime.AssetNames)
        {
            Assert.DoesNotContain(name, TodaysRedComputeFiles);
            Assert.StartsWith("leaf-node-", name, StringComparison.Ordinal);
            // Release Manager finds RedCompute's descriptor by these names; a leaf-node file never matches.
            Assert.False(name.EndsWith("component-input.v1.json", StringComparison.Ordinal), name);
            Assert.False(name.EndsWith("-input.v1.json", StringComparison.Ordinal), name);
            // The launcher's Linux release already carries install.sh beside it.
            Assert.NotEqual("install.sh", name);
        }

        // Today's RedCompute build is untouched: its runtime files and lock names are what they were.
        Assert.Equal(["RedCompute.exe", "RedCompute.deps.json", "RedCompute.runtimeconfig.json", "hostfxr.dll", "coreclr.dll"],
            RedComputeRuntime.RequiredFiles("win-x64"));
        Assert.Equal(["RedCompute", "RedCompute.deps.json", "RedCompute.runtimeconfig.json", "libhostfxr.so", "libcoreclr.so"],
            RedComputeRuntime.RequiredFiles("linux-x64"));
        Assert.Equal("packages.lock.json", RedComputeRuntime.LockFileName("win-x64"));
        Assert.Equal("packages.linux.lock.json", RedComputeRuntime.LockFileName("linux-x64"));
    }

    [Fact]
    public void Nothing_redcompute_publishes_builds_the_node_agent()
    {
        // RedCompute's artifact is the publish of RedCompute.App and the plugins it copies. The agent's
        // project (and its packaging files) is outside that graph, so it cannot change those bytes.
        var root = RepositoryRoot();
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([Path.Combine(root, "src", "RedCompute.App", "RedCompute.App.csproj")]);
        while (pending.Count > 0)
        {
            var project = Path.GetFullPath(pending.Pop());
            if (!reachable.Add(project) || !File.Exists(project)) continue;
            foreach (Match reference in Regex.Matches(File.ReadAllText(project), "<ProjectReference\\s+Include=\"([^\"$]+)\""))
                pending.Push(Path.Combine(Path.GetDirectoryName(project)!, reference.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
        }

        Assert.Contains(reachable, path => path.EndsWith("RedCompute.NodeLink.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(reachable, path => path.EndsWith("RedCompute.Node.csproj", StringComparison.OrdinalIgnoreCase));
        // The node's publish profiles live in its own project, never in the App's.
        Assert.False(Directory.Exists(Path.Combine(root, "src", "RedCompute.App", "Properties", "PublishProfiles")));
    }

    [Fact]
    public void The_recipe_only_writes_its_own_files_from_a_locked_restore()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "release", "leaf-node", "Build-LeafNodeArtifacts.ps1"));
        // RedCompute's own outputs are never named, read or written.
        foreach (var forbidden in new[] { "redcompute-win-x64", "redcompute-linux-x64", "component-input", "receipts", "evidence", "descriptor" })
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'--locked-mode', '--runtime', $rid", script, StringComparison.Ordinal);
        Assert.Contains("'--runtime', $rid, \"-p:PublishProfile=$rid\"", script, StringComparison.Ordinal);
        Assert.Contains("'-p:ContinuousIntegrationBuild=true'", script, StringComparison.Ordinal);
        Assert.Contains("OutputRoot must be empty", script, StringComparison.Ordinal);
        // The asset list is the five files, in the order LeafNodeRuntime names them.
        var listed = Regex.Match(script, "\\$assets = @\\(foreach \\(\\$name in ([^)]+)\\)").Groups[1].Value;
        Assert.Equal(LeafNodeRuntime.AssetNames, Regex.Matches(listed, "'([^']+)'").Select(match => match.Groups[1].Value));

        foreach (var rid in LeafNodeRuntime.Rids)
        {
            var profile = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "RedCompute.Node", "Properties", "PublishProfiles", rid + ".pubxml"));
            Assert.Contains($"<RuntimeIdentifier>{rid}</RuntimeIdentifier>", profile, StringComparison.Ordinal);
            Assert.Contains("<SelfContained>true</SelfContained>", profile, StringComparison.Ordinal);
            Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", profile, StringComparison.Ordinal);
        }
    }

    // ---- archives ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public void Node_archives_are_byte_identical_across_file_times(string rid)
    {
        using var temp = new TemporaryDirectory();
        var source = FakePublish(temp.Path, rid);
        var first = Path.Combine(temp.Path, "first", LeafNodeRuntime.ArchiveName(rid));
        var second = Path.Combine(temp.Path, "second", LeafNodeRuntime.ArchiveName(rid));
        foreach (var file in Directory.EnumerateFiles(source)) File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1));
        LeafNodeArchive.Create(source, first, rid);
        foreach (var file in Directory.EnumerateFiles(source)) File.SetLastWriteTimeUtc(file, new DateTime(2035, 5, 6));
        LeafNodeArchive.Create(source, second, rid);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void The_linux_archive_is_a_ustar_tarball_with_modes_and_no_owner()
    {
        using var temp = new TemporaryDirectory();
        var archive = Path.Combine(temp.Path, LeafNodeRuntime.ArchiveName("linux-x64"));
        LeafNodeArchive.Create(FakePublish(temp.Path, "linux-x64"), archive, "linux-x64");

        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<TarEntry>();
        while (reader.GetNextEntry() is { } entry) entries.Add(entry);

        Assert.Equal(entries.Select(entry => entry.Name).Order(StringComparer.Ordinal), entries.Select(entry => entry.Name));
        Assert.All(entries, entry =>
        {
            Assert.Equal(TarEntryFormat.Ustar, entry.Format);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            Assert.Equal(LeafNodeArchive.Timestamp, entry.ModificationTime);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
        });
        var modes = entries.ToDictionary(entry => entry.Name, entry => (int)entry.Mode);
        Assert.Equal(0x1ED, modes["leaf-node"]);          // the ELF apphost: 0755
        Assert.Equal(0x1ED, modes["libcoreclr.so"]);
        Assert.Equal(0x1A4, modes["leaf-node.dll"]);      // 0644
        Assert.Equal(0x1A4, modes["leaf-node.deps.json"]);
    }

    [Fact]
    public void The_windows_archive_is_the_zip_redcompute_ships()
    {
        using var temp = new TemporaryDirectory();
        var source = FakePublish(temp.Path, "win-x64");
        var archive = Path.Combine(temp.Path, LeafNodeRuntime.ArchiveName("win-x64"));
        var plain = Path.Combine(temp.Path, "plain.zip");
        LeafNodeArchive.Create(source, archive, "win-x64");
        DeterministicArchive.Create(source, plain);

        Assert.Equal(File.ReadAllBytes(plain), File.ReadAllBytes(archive));
        using var zip = ZipFile.OpenRead(archive);
        Assert.All(zip.Entries, entry => Assert.Equal(0, entry.ExternalAttributes));
    }

    [Theory]
    [InlineData("win-x64", "hostfxr.dll", null)]
    [InlineData("linux-x64", "leaf-node", null)]
    [InlineData("win-x64", null, "Microsoft.AspNetCore.dll")]
    [InlineData("linux-x64", null, "plugins/x.dll")]
    public void An_incomplete_publish_or_one_carrying_redcompute_is_refused(string rid, string? missing, string? extra)
    {
        using var temp = new TemporaryDirectory();
        var source = FakePublish(temp.Path, rid);
        if (missing is not null) File.Delete(Path.Combine(source, missing));
        if (extra is not null)
        {
            var path = Path.Combine(source, extra);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "server");
        }

        Assert.Throws<InvalidDataException>(() =>
            LeafNodeArchive.Create(source, Path.Combine(temp.Path, LeafNodeRuntime.ArchiveName(rid)), rid));
    }

    // ---- the manifest and the commands ---------------------------------------------------------------------

    [Fact]
    public void The_manifest_names_both_archives_and_is_strict()
    {
        using var temp = new TemporaryDirectory();
        var archives = LeafNodeRuntime.Rids.ToDictionary(rid => rid, rid =>
        {
            var path = Path.Combine(temp.Path, LeafNodeRuntime.ArchiveName(rid));
            LeafNodeArchive.Create(FakePublish(temp.Path, rid), path, rid);
            return path;
        });
        var commit = new string('a', 40);

        var manifest = LeafNodeManifests.Build("0.1.24", commit, archives);
        var json = ComponentJson.Serialize(manifest);
        var read = ComponentJson.Deserialize<LeafNodeManifest>(json);
        LeafNodeManifests.Validate(read);

        Assert.Equal("leaf-node", read.ComponentId);
        Assert.Equal(["win-x64", "linux-x64"], read.Artifacts.Select(artifact => artifact.Rid));
        Assert.Equal(ComponentJson.Inspect(archives["linux-x64"]).Sha256, read.Artifacts[1].Sha256);
        Assert.Throws<JsonException>(() => ComponentJson.Deserialize<LeafNodeManifest>(json.Replace("\"schemaVersion\"", "\"extra\": 1, \"schemaVersion\"")));
        Assert.Throws<InvalidDataException>(() => LeafNodeManifests.Validate(read with { Version = "0.1" }));
        Assert.Throws<InvalidDataException>(() => LeafNodeManifests.Validate(read with { Artifacts = [read.Artifacts[1], read.Artifacts[0]] }));
        Assert.Throws<InvalidDataException>(() => LeafNodeManifests.Validate(read with
        {
            Artifacts = [read.Artifacts[0], read.Artifacts[1] with { Name = "leaf-node-linux-x64.zip" }],
        }));
    }

    [Fact]
    public void The_commands_build_and_check_the_manifest_against_the_archives()
    {
        using var temp = new TemporaryDirectory();
        var out_ = Directory.CreateDirectory(Path.Combine(temp.Path, "out")).FullName;
        foreach (var rid in LeafNodeRuntime.Rids)
            Assert.Equal(0, ReleaseToolProgram.Run(["node", "archive", "--source", FakePublish(temp.Path, rid),
                "--output", Path.Combine(out_, LeafNodeRuntime.ArchiveName(rid)), "--rid", rid]));
        var manifest = Path.Combine(out_, LeafNodeRuntime.ManifestName);
        Assert.Equal(0, ReleaseToolProgram.Run(["node", "manifest", "--version", "0.1.24", "--source-commit", new string('b', 40),
            "--windows-archive", Path.Combine(out_, "leaf-node-win-x64.zip"),
            "--linux-archive", Path.Combine(out_, "leaf-node-linux-x64.tar.gz"), "--output", manifest]));
        Assert.Equal(0, ReleaseToolProgram.Run(["node", "validate", "--input", manifest, "--archives", out_]));

        // A wrong name, an unknown runtime, or an archive that changed after the manifest: refused.
        Assert.Equal(1, ReleaseToolProgram.Run(["node", "archive", "--source", FakePublish(temp.Path, "win-x64"),
            "--output", Path.Combine(temp.Path, "leaf-node.zip"), "--rid", "win-x64"]));
        Assert.Equal(1, ReleaseToolProgram.Run(["node", "archive", "--source", FakePublish(temp.Path, "win-x64"),
            "--output", Path.Combine(temp.Path, "leaf-node-osx-arm64.zip"), "--rid", "osx-arm64"]));
        File.AppendAllText(Path.Combine(out_, "leaf-node-linux-x64.tar.gz"), "x");
        Assert.Equal(1, ReleaseToolProgram.Run(["node", "validate", "--input", manifest, "--archives", out_]));
    }

    // ---- the installers ----------------------------------------------------------------------------------

    [Fact]
    public void The_linux_installer_verifies_before_it_installs_and_never_pairs()
    {
        var root = RepositoryRoot();
        var bytes = File.ReadAllBytes(Path.Combine(root, "release", "leaf-node", "leaf-node-install.sh"));
        Assert.DoesNotContain((byte)'\r', bytes);
        var script = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("#!/bin/sh\n", script, StringComparison.Ordinal);
        Assert.Contains("release/leaf-node/*.sh text eol=lf", File.ReadAllText(Path.Combine(root, ".gitattributes")), StringComparison.Ordinal);

        // Per user: refuses root. https only (file:// only for a LEAF_NODE_FEED_URL dry run).
        Assert.Contains("[ \"$(id -u)\" -ne 0 ] || die", script, StringComparison.Ordinal);
        Assert.Contains("curl -fsSL --proto '=https' --tlsv1.2", script, StringComparison.Ordinal);
        // The chain the kernel's install.sh follows: pointer -> record by hash; then the manifest of the
        // same release, bound to the record's version and commit; then the archive's size and hash.
        Assert.Contains("components/redcompute/channels/$CHANNEL.json", script, StringComparison.Ordinal);
        Assert.Contains("= \"$record_sha\" ]", script, StringComparison.Ordinal);
        Assert.Contains("m[\"version\"] == sys.argv[2] and m[\"sourceCommit\"] == sys.argv[3]", script, StringComparison.Ordinal);
        Assert.Contains("= \"$archive_size\" ]", script, StringComparison.Ordinal);
        Assert.Contains("= \"$archive_sha\" ]", script, StringComparison.Ordinal);

        AssertOnlyInstallAndUninstall(Code(script, '#', "say "), "leaf-node\" install", "\" uninstall");
    }

    [Fact]
    public void The_windows_installer_verifies_before_it_installs_and_never_pairs()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "release", "leaf-node", "leaf-node-install.ps1"));
        Assert.Contains("IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)", script, StringComparison.Ordinal);
        Assert.Contains("[Net.SecurityProtocolType]::Tls12", script, StringComparison.Ordinal);
        Assert.Contains("if ($uri.Scheme -cne 'https')", script, StringComparison.Ordinal);
        Assert.Contains("components/redcompute/channels/$channel.json", script, StringComparison.Ordinal);
        Assert.Contains("(Get-LeafNodeHash $recordFile) -cne [string]$pointer.sha256", script, StringComparison.Ordinal);
        Assert.Contains("$manifest.version -cne $record.version -or $manifest.sourceCommit -cne $record.sourceCommit", script, StringComparison.Ordinal);
        Assert.Contains("(Get-Item -LiteralPath $zip).Length -ne [long]$entry[0].sizeBytes -or (Get-LeafNodeHash $zip) -cne [string]$entry[0].sha256", script, StringComparison.Ordinal);

        var code = Code(script, '#', "Write-LeafNode ");
        Assert.Equal(["@('install')", "@('uninstall')"],
            Regex.Matches(code, "Invoke-LeafNodeExe \\S+ (@\\([^)]*\\))").Select(match => match.Groups[1].Value));
        AssertOnlyInstallAndUninstall(code);
    }

    [WindowsFact("Windows PowerShell 5.1 is what `irm | iex` runs the installer with")]
    public void The_powershell_scripts_parse_in_windows_powershell()
    {
        using var temp = new TemporaryDirectory();
        var check = Path.Combine(temp.Path, "parse.ps1");
        File.WriteAllText(check, """
            $failed = 0
            foreach ($path in $args) {
                $tokens = $null; $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                foreach ($e in $errors) { "$path : $($e.Message)"; $failed++ }
            }
            exit $failed
            """);
        var folder = Path.Combine(RepositoryRoot(), "release", "leaf-node");
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", check,
                     Path.Combine(folder, "leaf-node-install.ps1"), Path.Combine(folder, "Build-LeafNodeArtifacts.ps1") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, output);
    }

    /// <summary>No credential, no pairing: the only verbs the installers run are install and uninstall.</summary>
    private static void AssertOnlyInstallAndUninstall(string code, params string[] expected)
    {
        foreach (var word in new[] { "pair ", "--code", "--server", "secret-tool", "cmdkey", "Credential", "key.p8", "token" })
            Assert.DoesNotContain(word, code, StringComparison.OrdinalIgnoreCase);
        foreach (var call in expected) Assert.Contains(call, code, StringComparison.Ordinal);
    }

    /// <summary>The script without comment lines and without the lines that only print text.</summary>
    private static string Code(string script, char comment, string printer)
        => string.Join('\n', script.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith(comment) && !line.StartsWith(printer, StringComparison.Ordinal)
                           && !line.StartsWith("function " + printer.Trim(), StringComparison.Ordinal)));

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>A publish folder with the files a leaf-node build must hold (ELF magic for the Linux natives).</summary>
    private static string FakePublish(string root, string rid)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "publish-" + rid + "-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        byte[] elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];
        foreach (var name in LeafNodeRuntime.RequiredFiles(rid))
        {
            var path = Path.Combine(folder, name);
            if (rid == "linux-x64" && (name == "leaf-node" || name.EndsWith(".so", StringComparison.Ordinal))) File.WriteAllBytes(path, elf);
            else File.WriteAllText(path, name);
        }
        File.WriteAllText(Path.Combine(folder, "System.Private.CoreLib.dll"), "corelib");
        return folder;
    }

    private static string RepositoryRoot()
    {
        var configured = typeof(LeafNodeReleaseTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "RedComputeRepositoryRoot")?.Value;
        foreach (var candidate in new[] { configured, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            for (var directory = new DirectoryInfo(candidate); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "RedCompute.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"leaf-node-release-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
