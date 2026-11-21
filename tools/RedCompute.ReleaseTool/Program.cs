using System.Text;
using RedCompute.ReleaseTool;

return ReleaseToolProgram.Run(args);

public static class ReleaseToolProgram
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2) return Usage();
            var options = Arguments.Parse(args.Skip(2));
            return (args[0], args[1]) switch
            {
                ("archive", "create") => CreateArchive(options),
                ("evidence", "build") => BuildEvidence(options),
                ("evidence", "validate") => ValidateEvidence(options),
                ("descriptor", "build") => BuildDescriptor(options),
                ("descriptor", "validate") => ValidateDescriptor(options),
                ("node", "archive") => CreateNodeArchive(options),
                ("node", "manifest") => BuildNodeManifest(options),
                ("node", "validate") => ValidateNodeManifest(options),
                _ => Usage(),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Release tool failed: {exception.Message}");
            return 1;
        }
    }

    private static int CreateArchive(Arguments options)
    {
        options.EnsureOnly("source", "output", "target-os");
        // Without --target-os the archive is the Windows one, byte for byte (no Unix modes).
        var targetOs = options.Optional("target-os") ?? "windows";
        if (targetOs is not ("windows" or "linux"))
            throw new ArgumentException("--target-os must be windows or linux.");
        DeterministicArchive.Create(options.Required("source"), options.Required("output"),
            unixModes: targetOs == "linux");
        Console.WriteLine("Created deterministic archive from sorted regular-file inputs.");
        return 0;
    }

    private const string RedBambooRepositoryUrl = "https://github.com/RedBamboo-Interactive/redbamboo-packages";

    private static int BuildEvidence(Arguments options)
    {
        options.EnsureOnly("repository-root", "publish-root", "output", "version", "repository-url", "source-commit", "redbamboo", "redbamboo-commit", "provider-graph", "dotnet-sdk", "dotnet-runtime", "msbuild", "nuget", "target-rid");
        // The redbamboo-packages commit is a build input the builder supplies (Release Manager resolves origin/main
        // once per Nightly; the fallback workflow resolves main at dispatch). RedCompute commits none: the tool
        // checks it against the AppHost checkout the build used and records it.
        var appHostCommit = CheckoutCommit(options.Required("redbamboo"), options.Required("redbamboo-commit"),
            "--redbamboo", "--redbamboo-commit");
        // win-x64 unless --target-rid names the linux-x64 build.
        var rid = options.Optional("target-rid") ?? "win-x64";
        var lockFileName = RedComputeRuntime.LockFileName(rid);
        var repositoryRoot = Path.GetFullPath(options.Required("repository-root"));
        var publishRoot = Path.GetFullPath(options.Required("publish-root"));
        var output = Path.GetFullPath(options.Required("output"));
        EnsureInside(publishRoot, output, "Evidence output");

        var locks = EnumerateRepositoryFiles(repositoryRoot, lockFileName)
            .Select(path =>
            {
                var facts = ComponentJson.Inspect(path);
                var relative = Relative(repositoryRoot, path);
                return new DependencyLockInput
                {
                    Path = relative, Sha256 = facts.Sha256,
                };
            })
            .OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        if (locks.Length == 0) throw new InvalidDataException($"No committed {lockFileName} files were found.");
        var commit = options.Required("source-commit");
        var repositoryUrl = options.Required("repository-url");
        if (!locks.Any(x => x.Path == $"release/locks/redbamboo-apphost/{lockFileName}"))
            throw new InvalidDataException("The RedBamboo.AppHost NuGet lock is missing.");

        var graph = ComponentJson.Deserialize<ProviderGraphFile>(File.ReadAllText(options.Required("provider-graph")));
        if (graph.SchemaVersion != 1 || graph.Providers.Count == 0) throw new InvalidDataException("Invalid provider graph input.");
        var duplicateProvider = graph.Providers.GroupBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicateProvider is not null) throw new InvalidDataException($"Duplicate provider id '{duplicateProvider.Key}'.");
        var ids = graph.Providers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var provider in graph.Providers)
        foreach (var dependency in provider.Dependencies)
            if (!ids.Contains(dependency)) throw new InvalidDataException($"Provider '{provider.Id}' references unknown provider '{dependency}'.");

        VerifySelfContainedPublish(publishRoot, rid);
        var expectedPluginFiles = graph.Providers.Select(item =>
        {
            var projectPath = Path.Combine(repositoryRoot, item.Project.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath)) throw new InvalidDataException($"Provider project does not exist: {item.Project}");
            VerifyProviderIdentity(Path.GetDirectoryName(projectPath)!, item);
            var relative = $"plugins/{item.Assembly}";
            var path = Path.Combine(publishRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new InvalidDataException($"Published provider is missing: {relative}");
            return item.Assembly;
        }).Order(StringComparer.Ordinal).ToArray();
        var actualPluginFiles = Directory.EnumerateFiles(Path.Combine(publishRoot, "plugins"), "RedCompute.Plugin.*.dll")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!actualPluginFiles.SequenceEqual(expectedPluginFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Published provider DLL set does not exactly match the locked provider graph.");

        var evidence = new BuildEvidence
        {
            SchemaVersion = 1,
            EvidenceType = "redcompute-build-evidence",
            ComponentId = "redcompute",
            Version = options.Required("version"),
            Configuration = "Release",
            TargetRid = rid,
            Deterministic = true,
            SelfContained = true,
            WebShellIncluded = false,
            Repository = new SourceInput
            {
                Id = "redcompute", RepositoryUrl = repositoryUrl, Commit = commit,
                SourcePath = ".",
            },
            PluginSdk = new SourceInput
            {
                Id = "redcompute-plugin-sdk", RepositoryUrl = repositoryUrl, Commit = commit,
                SourcePath = "src/RedCompute.PluginSdk",
            },
            AppHost = new SourceInput
            {
                Id = "redbamboo-apphost", RepositoryUrl = RedBambooRepositoryUrl, Commit = appHostCommit,
                SourcePath = "dotnet/RedBamboo.AppHost",
            },
            Toolchain = new ToolchainInput
            {
                DotnetSdk = options.Required("dotnet-sdk"), Msbuild = options.Required("msbuild"),
                DotnetRuntime = options.Required("dotnet-runtime"), Nuget = options.Required("nuget"),
            },
            DependencyLocks = locks,
        };
        ComponentJson.Validate(evidence);
        WriteUtf8(output, ComponentJson.Serialize(evidence));
        Console.WriteLine($"Built compact evidence for {locks.Length} NuGet locks.");
        return 0;
    }

    private static int ValidateEvidence(Arguments options)
    {
        options.EnsureOnly("input");
        var value = ComponentJson.Deserialize<BuildEvidence>(File.ReadAllText(options.Required("input")));
        ComponentJson.Validate(value);
        Console.WriteLine("Validated RedCompute build evidence.");
        return 0;
    }

    private static int BuildDescriptor(Arguments options)
    {
        options.EnsureOnly("artifact", "evidence", "requires-kernel-api", "compatible-product-version", "provides-compute-api", "output");
        var evidence = ComponentJson.Deserialize<BuildEvidence>(File.ReadAllText(options.Required("evidence")));
        ComponentJson.Validate(evidence);
        var artifactPath = options.Required("artifact");
        var artifact = ComponentJson.Inspect(artifactPath);
        var value = new RedComputeComponentInput
        {
            SchemaVersion = 1,
            DescriptorType = "redcompute-component-input",
            ComponentId = "redcompute",
            ComponentKind = "compute",
            Version = evidence.Version,
            Artifact = new ComponentArtifactInput
            {
                FileName = Path.GetFileName(artifactPath), SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256,
                InstallPath = "redcompute", TargetRid = evidence.TargetRid,
                SelfContained = evidence.SelfContained, WebShellIncluded = evidence.WebShellIncluded,
            },
            Compatibility = new CompatibilityInput
            {
                RequiresKernelApi = options.Required("requires-kernel-api"),
                CompatibleProductVersion = options.Required("compatible-product-version"),
                ProvidesComputeApi = options.Required("provides-compute-api"),
                OperatingSystem = RedComputeRuntime.OperatingSystem(evidence.TargetRid), Architecture = "x64",
            },
            Evidence = evidence,
        };
        ComponentJson.Validate(value);
        WriteUtf8(options.Required("output"), ComponentJson.Serialize(value));
        Console.WriteLine($"Built strict RedCompute component input from artifact {value.Artifact.Sha256}.");
        return 0;
    }

    private static int ValidateDescriptor(Arguments options)
    {
        options.EnsureOnly("input");
        var value = ComponentJson.Deserialize<RedComputeComponentInput>(File.ReadAllText(options.Required("input")));
        ComponentJson.Validate(value);
        Console.WriteLine("Validated RedCompute component input.");
        return 0;
    }

    // ---- leaf-node (Phase 3 R7): additions beside RedCompute's artifact, never inputs to it ----

    private static int CreateNodeArchive(Arguments options)
    {
        options.EnsureOnly("source", "output", "rid");
        var rid = options.Required("rid");
        if (!LeafNodeRuntime.IsSupported(rid)) throw new ArgumentException("--rid must be win-x64 or linux-x64.");
        var output = options.Required("output");
        if (!string.Equals(Path.GetFileName(output), LeafNodeRuntime.ArchiveName(rid), StringComparison.Ordinal))
            throw new ArgumentException($"The {rid} archive must be named {LeafNodeRuntime.ArchiveName(rid)}.");
        LeafNodeArchive.Create(options.Required("source"), output, rid);
        Console.WriteLine($"Created deterministic leaf-node {rid} archive.");
        return 0;
    }

    private static int BuildNodeManifest(Arguments options)
    {
        options.EnsureOnly("version", "source-commit", "windows-archive", "linux-archive", "output");
        var output = Path.GetFullPath(options.Required("output"));
        if (!string.Equals(Path.GetFileName(output), LeafNodeRuntime.ManifestName, StringComparison.Ordinal))
            throw new ArgumentException($"The manifest must be named {LeafNodeRuntime.ManifestName}.");
        var manifest = LeafNodeManifests.Build(options.Required("version"), options.Required("source-commit"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["win-x64"] = Path.GetFullPath(options.Required("windows-archive")),
                ["linux-x64"] = Path.GetFullPath(options.Required("linux-archive")),
            });
        WriteUtf8(output, ComponentJson.Serialize(manifest));
        Console.WriteLine("Built leaf-node manifest.");
        return 0;
    }

    private static int ValidateNodeManifest(Arguments options)
    {
        options.EnsureOnly("input", "archives");
        var input = Path.GetFullPath(options.Required("input"));
        var manifest = ComponentJson.Deserialize<LeafNodeManifest>(File.ReadAllText(input));
        LeafNodeManifests.Validate(manifest);
        // With --archives, every archive the manifest names must be there with that size and hash.
        if (options.Optional("archives") is { } folder)
            foreach (var artifact in manifest.Artifacts)
            {
                var facts = ComponentJson.Inspect(Path.Combine(folder, artifact.Name));
                if (facts.Sha256 != artifact.Sha256 || facts.SizeBytes != artifact.SizeBytes)
                    throw new InvalidDataException($"{artifact.Name} does not match the manifest.");
            }
        Console.WriteLine("Validated leaf-node manifest.");
        return 0;
    }

    private static void VerifySelfContainedPublish(string root, string rid)
    {
        foreach (var relative in RedComputeRuntime.RequiredFiles(rid))
            if (!File.Exists(Path.Combine(root, relative))) throw new InvalidDataException($"Self-contained publish smoke failed: missing {relative}.");
        if (!Directory.Exists(Path.Combine(root, "plugins")) || !Directory.Exists(Path.Combine(root, "capabilities")))
            throw new InvalidDataException("Publish smoke failed: installed runtime content is missing.");
    }

    internal static void VerifyProviderIdentity(string projectDirectory, ProviderGraphEntry item)
    {
        if (item.IdentitySource is not ("providerId" or "providerType"))
            throw new InvalidDataException($"Provider '{item.Id}' has an unsupported identity source.");
        var source = string.Join("\n", Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(File.ReadAllText));
        var declaration = item.IdentitySource == "providerId"
            ? $"ProviderId => \"{item.RuntimeIdentity}\""
            : $"ProviderTypeName => \"{item.RuntimeIdentity}\"";
        if (item.IdentitySource == "providerType" && !source.Contains(declaration, StringComparison.Ordinal))
            declaration = $"ProviderType => \"{item.RuntimeIdentity}\"";
        if (!source.Contains(declaration, StringComparison.Ordinal))
            throw new InvalidDataException($"Provider '{item.Id}' does not declare {item.IdentitySource} '{item.RuntimeIdentity}' in runtime source.");
        var expectedId = item.IdentitySource == "providerId" ? item.RuntimeIdentity : item.RuntimeIdentity.ToLowerInvariant();
        if (item.Id != expectedId)
            throw new InvalidDataException($"Provider '{item.Id}' must use package id '{expectedId}' from its runtime identity.");
        foreach (var runtime in item.RuntimeDependencies)
        foreach (var marker in runtime.SourceMarkers)
            if (!source.Contains(marker, StringComparison.Ordinal))
                throw new InvalidDataException($"Provider '{item.Id}' runtime dependency '{runtime.Id}' marker was not found: {marker}");
    }

    private static IEnumerable<string> EnumerateRepositoryFiles(string root, string fileName)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Repository input is a reparse point: {directory}");
            foreach (var file in Directory.EnumerateFiles(directory, fileName)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or ".release-deps" or "bin" or "obj" or "artifacts") continue;
                pending.Push(child);
            }
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static void EnsureInside(string root, string path, string name) { if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{name} must be inside the publish root."); }
    /// <summary>
    /// The builder-supplied <paramref name="commit"/> once it is one full lowercase SHA and the HEAD of
    /// <paramref name="checkout"/>; otherwise the build refuses, naming both.
    /// </summary>
    private static string CheckoutCommit(string checkout, string commit, string checkoutOption, string commitOption)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$"))
            throw new ArgumentException($"{commitOption} must be one full lowercase commit SHA.");
        var root = Path.GetFullPath(checkout);
        if (!Directory.Exists(root)) throw new ArgumentException($"{checkoutOption} '{root}' does not exist.");
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "-C", root, "rev-parse", "--verify", "HEAD^{commit}" }) start.ArgumentList.Add(argument);
        using var git = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git to read the checkout HEAD.");
        var stderr = git.StandardError.ReadToEndAsync();
        var head = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        if (git.ExitCode != 0)
            throw new InvalidDataException($"{checkoutOption} '{root}' is not a git checkout: {stderr.Result.Trim()}");
        if (!string.Equals(head, commit, StringComparison.Ordinal))
            throw new InvalidDataException($"{checkoutOption} '{root}' is at {head}, not {commitOption} {commit}.");
        return commit;
    }

    private static void WriteUtf8(string path, string content) { var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(content)); }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: RedCompute.ReleaseTool archive create | evidence <build|validate> | descriptor <build|validate> | node <archive|manifest|validate>");
        return 2;
    }

    private sealed class Arguments
    {
        private readonly Dictionary<string, string?> _values;
        private Arguments(Dictionary<string, string?> values) => _values = values;
        public static Arguments Parse(IEnumerable<string> arguments)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            var items = arguments.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                if (!items[i].StartsWith("--", StringComparison.Ordinal) || items[i].Length == 2) throw new ArgumentException($"Unexpected argument '{items[i]}'.");
                var name = items[i][2..];
                if (i + 1 >= items.Length || items[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for '--{name}'.");
                values.Add(name, items[++i]);
            }
            return new Arguments(values);
        }
        public string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing required option '--{name}'.");
        public string? Optional(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        public void EnsureOnly(params string[] names)
        {
            var allowed = names.ToHashSet(StringComparer.Ordinal);
            var unknown = _values.Keys.Where(x => !allowed.Contains(x)).Order(StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0) throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(x => "--" + x))}.");
        }
    }
}
