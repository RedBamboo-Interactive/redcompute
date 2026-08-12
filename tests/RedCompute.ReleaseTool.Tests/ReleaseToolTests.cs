using System.Security.Cryptography;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.ReleaseTool;
using Xunit;

namespace RedCompute.ReleaseTool.Tests;

public sealed class ReleaseToolTests
{
    [Fact]
    public void Archive_IsByteIdenticalAcrossFileTimes()
    {
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var file = Path.Combine(source, "payload.txt");
        File.WriteAllText(file, "same bytes");
        var first = Path.Combine(temp.Path, "first.zip");
        var second = Path.Combine(temp.Path, "second.zip");
        var priorRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var priorTime = Environment.GetEnvironmentVariable("CI_BUILT_AT");
        var priorChannel = Environment.GetEnvironmentVariable("RELEASE_CHANNEL");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", "1");
            Environment.SetEnvironmentVariable("CI_BUILT_AT", "2001-01-01T00:00:00Z");
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", "nightly");
            File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1));
            DeterministicArchive.Create(source, first);
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", "999");
            Environment.SetEnvironmentVariable("CI_BUILT_AT", "2035-05-06T12:34:56Z");
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", "stable");
            File.SetLastWriteTimeUtc(file, new DateTime(2035, 5, 6));
            DeterministicArchive.Create(source, second);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", priorRun);
            Environment.SetEnvironmentVariable("CI_BUILT_AT", priorTime);
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", priorChannel);
        }

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Descriptor_IsByteIdenticalAndChannelRunTimeNeutral()
    {
        using var temp = new TemporaryDirectory();
        var artifact = Path.Combine(temp.Path, "redcompute.zip");
        var evidencePath = Path.Combine(temp.Path, "evidence.json");
        File.WriteAllBytes(artifact, "artifact"u8.ToArray());
        File.WriteAllText(evidencePath, ComponentJson.Serialize(Evidence()));
        var first = Path.Combine(temp.Path, "first.json");
        var second = Path.Combine(temp.Path, "second.json");
        var priorRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var priorTime = Environment.GetEnvironmentVariable("CI_BUILT_AT");
        var priorChannel = Environment.GetEnvironmentVariable("RELEASE_CHANNEL");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", "100");
            Environment.SetEnvironmentVariable("CI_BUILT_AT", "2000-01-01T00:00:00Z");
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", "nightly");
            Assert.Equal(0, BuildDescriptor(artifact, evidencePath, first));
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", "999999");
            Environment.SetEnvironmentVariable("CI_BUILT_AT", "2040-01-01T00:00:00Z");
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", "stable");
            Assert.Equal(0, BuildDescriptor(artifact, evidencePath, second));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", priorRun);
            Environment.SetEnvironmentVariable("CI_BUILT_AT", priorTime);
            Environment.SetEnvironmentVariable("RELEASE_CHANNEL", priorChannel);
        }

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        var json = File.ReadAllText(first);
        Assert.DoesNotContain("channel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buildId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("builtAt", json, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictJson_RejectsUnknownProperties()
    {
        var json = ComponentJson.Serialize(Evidence()).TrimEnd().TrimEnd('}') + ",\"unexpected\":true}";
        Assert.Throws<JsonException>(() => ComponentJson.Deserialize<BuildEvidence>(json));
    }

    [Fact]
    public void ProviderGraph_UsesEveryDeclaredRuntimeIdentity()
    {
        var root = RepositoryRoot();
        var graph = ComponentJson.Deserialize<ProviderGraphFile>(File.ReadAllText(
            Path.Combine(root, "release", "redcompute-provider-graph.v1.json")));
        Assert.Equal(9, graph.Providers.Count);
        Assert.Contains(graph.Providers, x => x.Id == "opencode" && x.RuntimeIdentity == "opencode" && x.IdentitySource == "providerId");
        Assert.All(graph.Providers, x => Assert.NotEmpty(x.RuntimeDependencies));
        Assert.Equal(["managedByRedCompute", "systemPrerequisite", "userExternalService"], graph.Providers.SelectMany(x => x.RuntimeDependencies).Select(x => x.Management).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Contains(graph.Providers.SelectMany(x => x.RuntimeDependencies), x => x.VersionConstraint.StartsWith("unresolved", StringComparison.Ordinal));
        foreach (var item in graph.Providers)
        {
            var project = Path.Combine(root, item.Project.Replace('/', Path.DirectorySeparatorChar));
            ReleaseToolProgram.VerifyProviderIdentity(Path.GetDirectoryName(project)!, item);
        }
    }

    [Fact]
    public void RepositoryRootIsOutputIndependent_AndWorkflowPinsActionsAndTreatsDispatchInputsAsData()
    {
        using var isolatedOutput = new TemporaryDirectory();
        var configuredRoot = ConfiguredRepositoryRoot();
        var root = ResolveRepositoryRoot(configuredRoot, isolatedOutput.Path);
        Assert.Equal(Path.GetFullPath(configuredRoot!), root);

        var workflow = File.ReadAllLines(Path.Combine(root, ".github", "workflows", "release-redcompute-component.yml"));
        var uses = workflow.Select(x => x.Trim()).Where(x => x.StartsWith("uses: ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(uses);
        Assert.All(uses, value => Assert.Matches("^uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[a-f0-9]{40}(?: # .+)?$", value));

        var inRun = false;
        var runIndent = 0;
        foreach (var line in workflow)
        {
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            if (trimmed.StartsWith("run:", StringComparison.Ordinal)) { inRun = true; runIndent = indent; continue; }
            if (inRun && trimmed.Length > 0 && indent <= runIndent) inRun = false;
            if (inRun) Assert.DoesNotContain("${{ inputs.", line, StringComparison.Ordinal);
        }
        var yaml = string.Join("\n", workflow);
        Assert.DoesNotContain("\n      channel:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("github.sha", yaml, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.workflow_sha }}", yaml, StringComparison.Ordinal);
        Assert.Contains("REDCOMPUTE_SHA: ${{ github.workflow_sha }}", yaml, StringComparison.Ordinal);
        Assert.Contains("name: redcompute-${{ github.workflow_sha }}-${{ inputs.version }}-win-x64-candidate-${{ github.run_id }}-${{ github.run_attempt }}", yaml, StringComparison.Ordinal);
        Assert.Contains("dotnet restore RedCompute.sln --locked-mode", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CycloneDX", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--sbom", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("repository-tree", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source-tree", yaml, StringComparison.OrdinalIgnoreCase);

        var publishStepStart = Array.FindIndex(workflow, line => line == "      - name: Produce two clean self-contained RedCompute runtimes with compact evidence");
        Assert.True(publishStepStart >= 0, "The self-contained publish step is missing.");
        var publishStepEnd = Array.FindIndex(workflow, publishStepStart + 1, line => line.StartsWith("      - name: ", StringComparison.Ordinal));
        if (publishStepEnd < 0) publishStepEnd = workflow.Length;
        var publishStep = workflow[publishStepStart..publishStepEnd];
        var publishRun = Array.FindIndex(publishStep, line => line.Trim() == "run: |");
        var publishCommand = Array.FindIndex(publishStep, line => line.Trim() == "dotnet publish src/RedCompute.App/RedCompute.App.csproj `");
        var publishConfiguration = Array.FindIndex(publishStep, line => line.Trim() == "--configuration Release `");
        var publishRuntime = Array.FindIndex(publishStep, line => line.Trim() == "--runtime win-x64 `");
        var publishSelfContained = Array.FindIndex(publishStep, line => line.Trim() == "--self-contained true `");
        var publishNoRestore = Array.FindIndex(publishStep, line => line.Trim() == "--no-restore `");
        var publishOutput = Array.FindIndex(publishStep, line => line.Trim() == "--output \"$publishRoot\" `");
        var publishPathMap = Array.FindIndex(publishStep, line => line.Trim() == "-p:PathMap=\"$pathMap\" `");
        var publishVersion = Array.FindIndex(publishStep, line => line.Trim() == "-p:Version=\"$env:COMPONENT_VERSION\" `");
        var publishCi = Array.FindIndex(publishStep, line => line.Trim() == "-p:ContinuousIntegrationBuild=true `");
        var publishDeterministic = Array.FindIndex(publishStep, line => line.Trim() == "-p:Deterministic=true");
        Assert.True(
            publishRun < publishCommand &&
            publishCommand < publishConfiguration &&
            publishConfiguration < publishRuntime &&
            publishRuntime < publishSelfContained &&
            publishSelfContained < publishNoRestore &&
            publishNoRestore < publishOutput &&
            publishOutput < publishPathMap &&
            publishPathMap < publishVersion &&
            publishVersion < publishCi &&
            publishCi < publishDeterministic,
            "The publish step must contain the complete deterministic self-contained win-x64 dotnet publish command before --no-restore.");

        const string toolProject = "tools/RedCompute.ReleaseTool/RedCompute.ReleaseTool.csproj";
        var toolRestore = yaml.IndexOf($"dotnet restore {toolProject} --locked-mode", StringComparison.Ordinal);
        var toolBuild = yaml.IndexOf($"dotnet build {toolProject}", StringComparison.Ordinal);
        var firstToolUse = yaml.IndexOf("dotnet \"$env:RELEASE_TOOL_DLL\"", StringComparison.Ordinal);
        Assert.True(toolRestore >= 0 && toolRestore < toolBuild && toolBuild < firstToolUse);
        Assert.Single(Regex.Matches(yaml, Regex.Escape($"dotnet restore {toolProject} --locked-mode")).Cast<Match>());
        Assert.Single(Regex.Matches(yaml, Regex.Escape($"dotnet build {toolProject}")).Cast<Match>());
        Assert.Equal(4, Regex.Matches(yaml, Regex.Escape("dotnet \"$env:RELEASE_TOOL_DLL\"")).Count);
        Assert.DoesNotContain("dotnet run --project tools/RedCompute.ReleaseTool", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerAcquisitionContract_IsMinimalPinnedAndFailClosed()
    {
        var root = RepositoryRoot();
        var inputPath = Path.Combine(root, "release", "redleaf-release-tool-input.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(inputPath));
        var input = document.RootElement;
        Assert.True(input.EnumerateObject().Count() == 4);
        Assert.Equal(1, input.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("https://github.com/RedBamboo-Interactive/redleaf", input.GetProperty("repositoryUrl").GetString());
        const string exactCommit = "3d14703d3f98f7c2c99b4bc7ada319eb4fe058c7";
        Assert.Equal(exactCommit, input.GetProperty("commit").GetString());
        Assert.Equal("redcompute-win-x64-{artifactSha256}.zip", input.GetProperty("centralArtifactFileNameTemplate").GetString());

        var schemaPath = Path.Combine(root, "schemas", "redleaf-release-tool-input.v1.schema.json");
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var schema = schemaDocument.RootElement;
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("^[a-f0-9]{40}$", schema.GetProperty("properties").GetProperty("commit").GetProperty("pattern").GetString());

        var resolved = RunPowerShell(root,
            "./release/Resolve-RedLeafReleaseToolInput.ps1", "-InputPath", "release/redleaf-release-tool-input.v1.json");
        Assert.Equal(0, resolved.ExitCode);
        Assert.Equal(exactCommit, resolved.StandardOutput.Trim());

        using var temp = new TemporaryDirectory();
        var unresolvedInput = Path.Combine(temp.Path, "redleaf-release-tool-input.v1.json");
        File.WriteAllText(unresolvedInput, File.ReadAllText(inputPath).Replace(
            exactCommit, "UNRESOLVED", StringComparison.Ordinal));
        var rejected = RunPowerShell(root,
            "./release/Resolve-RedLeafReleaseToolInput.ps1", "-InputPath", unresolvedInput);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("audited RedLeaf ReleaseTool commit is unresolved", rejected.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowBuildsExactRedLeafToolThenProducesAndBridgesUnsignedGenericCandidate()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-redcompute-component.yml"));
        var resolve = workflow.IndexOf("Resolve required audited RedLeaf ReleaseTool input before build", StringComparison.Ordinal);
        var setup = workflow.IndexOf("Set up exact .NET SDK", StringComparison.Ordinal);
        var firstBuild = workflow.IndexOf("dotnet build", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && resolve < setup && resolve < firstBuild, "The unresolved durable pin must fail before setup or build.");
        Assert.Contains("repository: RedBamboo-Interactive/redleaf", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ steps.redleaf-tool.outputs.commit }}", workflow, StringComparison.Ordinal);
        Assert.Contains("git -C ../redleaf-release-tool-source rev-parse HEAD", workflow, StringComparison.Ordinal);
        Assert.Contains("candidate ingest-redcompute", workflow, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(workflow, Regex.Escape("candidate ingest-redcompute")).Cast<Match>());
        Assert.Contains("foreach ($name in @('first', 'second'))", workflow, StringComparison.Ordinal);
        Assert.Contains("Generic candidate path reproduction failed", workflow, StringComparison.Ordinal);
        Assert.Contains("--signature-input", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--sbom", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("CycloneDX", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REDLEAF_RELEASE_SIGNING_KEY", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate sign", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key derive", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$candidate.status -ne 'unsigned' -or $null -ne $candidate.artifact.signature", workflow, StringComparison.Ordinal);
        Assert.Contains("https://github.com/RedBamboo-Interactive/redcompute", workflow, StringComparison.Ordinal);
        Assert.Contains("https://github.com/RedBamboo-Interactive/redcompute/releases/download/redcompute-unsigned-candidates/$artifactName", workflow, StringComparison.Ordinal);
        Assert.Contains("$candidate.artifact.url -cne $artifactUrl", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--release-root", workflow, StringComparison.Ordinal);
        Assert.Contains("RELEASE_TOOL_DLL=$(Join-Path $env:RUNNER_TEMP", workflow, StringComparison.Ordinal);
        Assert.Contains("REDLEAF_RELEASE_TOOL_DLL=$(Join-Path $env:RUNNER_TEMP", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ runner.temp }}", workflow, StringComparison.Ordinal);
        Assert.Equal(2, workflow.Split("token: ${{ secrets.CROSS_REPO_TOKEN || github.token }}", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("release_id", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("release_tag", workflow, StringComparison.Ordinal);

        Assert.Contains("needs: build", workflow, StringComparison.Ordinal);
        Assert.Contains("group: redcompute-unsigned-candidate-bridge", workflow, StringComparison.Ordinal);
        Assert.Contains("actions: read", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: write", workflow, StringComparison.Ordinal);
        Assert.Contains("redcompute-unsigned-candidates", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("$visibility -cne 'public'", workflow, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one RedCompute ZIP matching the unsigned descriptor", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($existingNames -notcontains $file.Name)", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release upload $tag $file.FullName --clobber", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release download $tag --pattern $file.Name", workflow, StringComparison.Ordinal);
        Assert.Contains("Rolling prerelease already contains different bytes", workflow, StringComparison.Ordinal);
        Assert.Contains("bridge-assets/${candidateId}.candidate.json", workflow, StringComparison.Ordinal);
        Assert.Contains("bridge-assets/$producerArtifactName", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_IsStrictAndChannelNeutral()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "schemas", "redcompute-component-input.v1.schema.json")));
        var root = document.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.False(root.GetProperty("properties").TryGetProperty("channel", out _));
        Assert.False(root.GetProperty("properties").TryGetProperty("sboms", out _));
        Assert.False(root.GetProperty("$defs").GetProperty("evidence").GetProperty("properties").TryGetProperty("builtAt", out _));
        Assert.False(root.GetProperty("$defs").GetProperty("evidence").GetProperty("properties").TryGetProperty("buildId", out _));
        var evidence = root.GetProperty("$defs").GetProperty("evidence").GetProperty("properties");
        Assert.False(evidence.TryGetProperty("dependencyLockSetSha256", out _));
        Assert.False(evidence.TryGetProperty("providers", out _));
        Assert.False(evidence.TryGetProperty("files", out _));
        var source = root.GetProperty("$defs").GetProperty("source").GetProperty("properties");
        Assert.False(source.TryGetProperty("repositoryTree", out _));
        Assert.False(source.TryGetProperty("sourceTree", out _));
        Assert.False(source.TryGetProperty("lockIdentity", out _));
    }

    [Fact]
    public void EveryOwnedProjectHasALockAndExactPackageVersions()
    {
        var root = RepositoryRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.release-deps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"Missing lock for {project}");
            var xml = File.ReadAllText(project);
            foreach (Match match in Regex.Matches(xml, "<PackageReference[^>]+Version=\\\"([^\\\"]+)\\\""))
                Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$", match.Groups[1].Value);
        }
    }

    [Fact]
    public void PublishCopyTargetsUseRuntimeSpecificPluginOutputs()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "RedCompute.App", "RedCompute.App.csproj"));
        Assert.Contains("net9.0\\$(RuntimeIdentifier)\\RedCompute.Plugin.*.dll", project, StringComparison.Ordinal);
        Assert.Contains("Condition=\"'$(RuntimeIdentifier)' != ''\"", project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("directory")]
    [InlineData("file")]
    public void ArchiveEnumeration_RejectsReparsePointsBeforeDescent(string kind)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "redcompute-archive-fixture"));
        var directory = Path.Combine(root, "linked-directory");
        var file = Path.Combine(root, "linked-file");
        var attributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [root] = kind == "root" ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory,
            [directory] = FileAttributes.Directory | FileAttributes.ReparsePoint,
            [file] = FileAttributes.ReparsePoint,
        };
        IEnumerable<string> Entries(string path)
        {
            if (path != root) throw new Xunit.Sdk.XunitException("Traversal entered a reparse point.");
            return kind == "directory" ? [directory] : kind == "file" ? [file] : [];
        }
        Assert.Throws<InvalidDataException>(() => ArchiveInputs.Enumerate(root, path => attributes[path], Entries));
    }

    private static int BuildDescriptor(string artifact, string evidence, string output)
        => ReleaseToolProgram.Run([
            "descriptor", "build", "--artifact", artifact, "--evidence", evidence,
            "--requires-kernel-api", "^1.2.0",
            "--provides-compute-api", "1.1.0", "--compatible-product-version", ">=2.0.0 <2.1.0", "--output", output,
        ]);

    [Fact]
    public void RedLeafFollowupRangeFixture_AllowsIndependentPatchCompatibility()
    {
        const string range = ">=2.4.0 <2.5.0";
        Assert.True(Phase1VersionRange.Satisfies("2.4.1", range));
        Assert.True(Phase1VersionRange.Satisfies("2.4.19", range));
        Assert.False(Phase1VersionRange.Satisfies("2.5.0", range));
    }

    private static BuildEvidence Evidence()
    {
        var hash = new string('a', 64);
        var commit = new string('b', 40);
        var dependencyLock = new DependencyLockInput { Path = "src/App/packages.lock.json", Sha256 = hash };
        return new BuildEvidence
        {
            SchemaVersion = 1, EvidenceType = "redcompute-build-evidence", ComponentId = "redcompute", Version = "3.0.0",
            Configuration = "Release", TargetRid = "win-x64", Deterministic = true, SelfContained = true, WebShellIncluded = false,
            Repository = Source("redcompute", commit), PluginSdk = Source("redcompute-plugin-sdk", commit, "src/RedCompute.PluginSdk"),
            AppHost = Source("redbamboo-apphost", commit, "dotnet/RedBamboo.AppHost"),
            Toolchain = new ToolchainInput { DotnetSdk = "9.0.303", DotnetRuntime = "9.0.7", Msbuild = "17.14.13+65391c53b", Nuget = "6.14.0.116" },
            DependencyLocks = [dependencyLock],
        };
    }

    private static SourceInput Source(string id, string commit, string path = ".") => new()
    {
        Id = id, RepositoryUrl = "https://github.com/redbamboo-interactive/source", Commit = commit,
        SourcePath = path,
    };

    private static string RepositoryRoot()
    {
        return ResolveRepositoryRoot(ConfiguredRepositoryRoot(), Environment.CurrentDirectory);
    }

    private static string? ConfiguredRepositoryRoot()
    {
        return typeof(ReleaseToolTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "RedComputeRepositoryRoot")
            ?.Value;
    }

    private static string ResolveRepositoryRoot(string? configuredRoot, string currentDirectory)
    {
        foreach (var candidate in new[] { configuredRoot, currentDirectory })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
                directory = directory.Parent;
            if (directory is not null) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from compiled test metadata or the current directory.");
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunPowerShell(
        string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"redcompute-release-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
