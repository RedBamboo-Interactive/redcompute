using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The execution-host guard (node-zero design, section 7): no provider or execution-side App type
/// may start or list processes, touch files or ask the OS for folders, temp paths or listeners
/// except through <c>IExecutionHost</c>. It reads the IL of every built RedCompute assembly, so it
/// sees lambdas, async state machines, <c>using static</c> and aliases, and runs on Linux too.
/// <para>
/// The allowlist is a ratchet. It names every type that does this today; each move commit deletes
/// its own entries, and an entry whose type no longer does it fails too, so the list only shrinks
/// and every shrink shows in the diff that earned it.
/// </para>
/// </summary>
public sealed class ExecutionHostGuardTests
{
    /// <summary>Allowed types, grouped by why (or by the move that removes them).</summary>
    internal static readonly (string Why, string[] Types)[] Allowlist =
    [
        // ---- final: the host implementation ----------------------------------------------------------
        ("the local execution host", [
            "RedCompute.PluginSdk.Hosting.Local.LocalExecutionHost",
        ]),
        ("the local host's moved code; its static facades become internal to Hosting.Local (C19)", [
            "RedCompute.PluginSdk.ContainedProcessTree",
            "RedCompute.PluginSdk.OwnedProcessKiller",
            "RedCompute.PluginSdk.SessionProcessContainment",
        ]),
        // ---- final: RedCompute's own state under %LOCALAPPDATA%/RedCompute, on the server ------------
        ("server state", [
            "RedCompute.App.Api.Endpoints.GenericCapabilityEndpoints",
            "RedCompute.App.Api.Endpoints.JobApiResponse",
            "RedCompute.App.Api.Endpoints.SettingsEndpoints",
            "RedCompute.App.Data.RedComputeDbContext",
            "RedCompute.App.Services.BadgeSigning",
            "RedCompute.App.Services.CapabilityManifestLoader",
            "RedCompute.App.Services.ConfigManager",
            "RedCompute.App.Services.DeploymentVerification",
            "RedCompute.App.Services.InputAttachmentStore",
            "RedCompute.App.Services.Jobs.JobArtifactStore",
            "RedCompute.App.Services.Jobs.JobTrackingService",
            "RedCompute.App.Services.Nodes.NodeIdentityStore",
            "RedCompute.App.Services.ProviderConfigService",
            "RedCompute.App.Services.ProviderDiscovery",
            "RedCompute.App.Services.QualityModeService",
            "RedCompute.App.Services.SessionCallbackStore",
            "RedCompute.App.Services.SessionInputQueueStore",
            "RedCompute.App.Services.SessionRollbackLedger",
            "RedCompute.App.Services.SessionTitles.SessionTitleStore",
            "RedCompute.App.Services.TranscriptSequenceStore",
            "RedCompute.Core.Sessions.PlannedRestartCheckpoint",
        ]),
        ("server state: the plugin session stores live with RedCompute", [
            "RedCompute.Plugin.ClaudeCode.ClaudeDbContext",
            "RedCompute.Plugin.Codex.CodexDbContext",
            "RedCompute.Plugin.OpenCode.OpenCodeDbContext",
        ]),
        ("server bootstrap", [
            "RedCompute.App.Api.RelayServer",
        ]),
        // ---- final: server-only processes ------------------------------------------------------------
        ("server-only processes (elevation, desktop handoff)", [
            "RedCompute.App.Services.DefenderExclusionService",
            "RedCompute.App.Services.MaintenanceDeploymentCoordinator",
        ]),
        // ---- moves still to come: each move commit deletes its own group -----------------------------
        ("C9-C11 Claude Code", [
            "RedCompute.Plugin.ClaudeCode.ClaudeChain",
            "RedCompute.Plugin.ClaudeCode.ClaudeCodeProvider",
            "RedCompute.Plugin.ClaudeCode.ClaudeSessionService",
            "RedCompute.Plugin.ClaudeCode.ClaudeTranscriptPaths",
        ]),
        ("C12 Codex", [
            "RedCompute.Plugin.Codex.CodexAppServerConnection",
            "RedCompute.Plugin.Codex.CodexCliEnvironment",
            "RedCompute.Plugin.Codex.CodexCliLocator",
            "RedCompute.Plugin.Codex.CodexInteractiveService",
            "RedCompute.Plugin.Codex.CodexSessionService",
        ]),
        ("C13 OpenCode", [
            "RedCompute.Plugin.OpenCode.OpenCodeNativeSessionReader",
            "RedCompute.Plugin.OpenCode.OpenCodeSessionService",
        ]),
        ("C14 hardware (and the node descriptor's processor name)", [
            "RedCompute.App.Services.Hardware.HardwareMonitorService",
            "RedCompute.App.Services.Nodes.NodePlatform",
        ]),
        ("C15 long-lived registry", [
            "RedCompute.App.Services.LongLivedProcessRegistry",
        ]),
        ("C16 repository validator, conversation workspace, scratch, isolated directories", [
            "RedCompute.App.Api.Endpoints.UnifiedSessionEndpoints",
            "RedCompute.App.Services.RepositoryReferenceValidator",
            "RedCompute.PluginSdk.IsolatedWorkingDirectory",
            "RedCompute.PluginSdk.SessionScratch",
        ]),
        ("C17 Docker", [
            "RedCompute.App.Services.DockerContainerService",
            "RedCompute.App.Services.DockerDesktopService",
        ]),
        ("C18 CLI version manager", [
            "RedCompute.PluginSdk.NpmCliVersionManager",
            "RedCompute.PluginSdk.ProcessCliCommandRunner",
            "RedCompute.PluginSdk.SystemCliProcessInspector",
        ]),
    ];

    [Fact]
    public void Only_the_host_and_allowlisted_types_reach_the_OS_directly()
    {
        var hits = ScanAll();
        var allowed = Allowlist.SelectMany(group => group.Types).ToHashSet(StringComparer.Ordinal);

        var offending = hits.Where(hit => !allowed.Contains(hit.Type)).ToList();
        Assert.True(offending.Count == 0,
            "Direct OS access outside the execution host (move it behind IExecutionHost, or allowlist it with a reason):\n"
            + Describe(offending) + "\n\nEvery type with a hit now:\n" + string.Join("\n",
                hits.Select(hit => hit.Type).Distinct().Order(StringComparer.Ordinal).Select(type => $"\"{type}\",")));
    }

    [Fact]
    public void The_allowlist_only_names_types_that_still_need_it()
    {
        var hits = ScanAll();
        var hitTypes = hits.Select(hit => hit.Type).ToHashSet(StringComparer.Ordinal);
        var defined = ScannedAssemblies().SelectMany(DefinedTopLevelTypes).ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist.SelectMany(group => group.Types)
            .Where(type => !hitTypes.Contains(type))
            // The Linux build has no WPF shell: a Windows-only type is stale only where it is built.
            .Where(type => defined.Contains(type) || OperatingSystem.IsWindows())
            .ToList();
        Assert.True(stale.Count == 0,
            "Allowlist entries no longer needed; delete them in the commit that moved them:\n" + string.Join("\n", stale));
        var duplicates = Allowlist.SelectMany(group => group.Types).GroupBy(type => type).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_plugin_is_scanned()
    {
        var pluginsRoot = Path.Combine(RepoRoot(), "plugins");
        var expected = Directory.EnumerateDirectories(pluginsRoot, "RedCompute.Plugin.*")
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj").Any())
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var scanned = ScannedAssemblies().Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.StartsWith("RedCompute.Plugin.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, scanned);
    }

    /// <summary>The guard's own test: a type with one forbidden call is found, and by its own name.</summary>
    [Fact]
    public void A_forbidden_call_is_found_and_attributed_to_its_type()
    {
        var hits = Scan(typeof(ExecutionHostGuardTests).Assembly.Location);
        var fixture = hits.Where(hit => hit.Type == typeof(GuardFixture).FullName).ToList();
        Assert.Contains(fixture, hit => hit.Target == "System.IO.File::Exists");
        // A lambda's and an async state machine's hits belong to the type that wrote them.
        Assert.Contains(fixture, hit => hit.Target == "System.Diagnostics.Process::GetProcesses");
        Assert.Contains(fixture, hit => hit.Target == "System.IO.Directory::CreateDirectory");
        Assert.Contains(fixture, hit => hit.Target == "System.IO.Path::GetTempPath");
        Assert.DoesNotContain(hits, hit => hit.Type.Contains('<') || hit.Type.Contains('+'));
    }

    // ---- scanner ---------------------------------------------------------------------------------

    internal sealed record Hit(string Assembly, string Type, string Method, string Target);

    private static readonly HashSet<string> WholeTypes = new(StringComparer.Ordinal)
    {
        "System.IO.File",
        "System.IO.Directory",
    };

    private static readonly HashSet<string> Members = new(StringComparer.Ordinal)
    {
        "System.Diagnostics.Process::Start",
        "System.Diagnostics.Process::GetProcesses",
        "System.Diagnostics.Process::GetProcessById",
        "System.Diagnostics.Process::GetProcessesByName",
        "System.Diagnostics.ProcessStartInfo::.ctor",
        "System.IO.FileInfo::.ctor",
        "System.IO.DirectoryInfo::.ctor",
        "System.IO.FileStream::.ctor",
        "System.Environment::GetFolderPath",
        "System.IO.Path::GetTempPath",
        "System.Net.NetworkInformation.IPGlobalProperties::GetActiveTcpListeners",
        "Microsoft.Data.Sqlite.SqliteConnection::.ctor",
    };

    private static bool IsForbidden(string type, string member)
        => WholeTypes.Contains(type) || Members.Contains($"{type}::{member}");

    internal static List<Hit> ScanAll() => ScannedAssemblies().SelectMany(Scan).ToList();

    /// <summary>Core, PluginSdk, App and every plugin, as built beside these tests.</summary>
    internal static IReadOnlyList<string> ScannedAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        var core = new[]
        {
            typeof(RedCompute.Core.Jobs.JobRecord).Assembly.Location,
            typeof(RedCompute.PluginSdk.OwnedProcessKiller).Assembly.Location,
            typeof(RedCompute.App.RedComputeHost).Assembly.Location, // RedCompute.dll
        };
        return core
            .Concat(Directory.EnumerateFiles(directory, "RedCompute.Plugin.*.dll").Order(StringComparer.Ordinal))
            .ToList();
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    internal static List<Hit> Scan(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = Path.GetFileNameWithoutExtension(path);
        var hits = new List<Hit>();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var owner = TopLevelName(metadata, type);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                while (il.RemainingBytes > 0)
                {
                    var value = (short)il.ReadByte();
                    if (value == 0xFE) value = unchecked((short)(0xFE00 | il.ReadByte()));
                    var code = OpCodesByValue[value];
                    switch (code.OperandType)
                    {
                        case OperandType.InlineMethod:
                            var token = il.ReadInt32();
                            if (Target(metadata, MetadataTokens.EntityHandle(token)) is { } target
                                && IsForbidden(target.Type, target.Member))
                                hits.Add(new Hit(assembly, owner, metadata.GetString(method.Name),
                                    $"{target.Type}::{target.Member}"));
                            break;
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            il.ReadByte();
                            break;
                        case OperandType.InlineVar:
                            il.ReadInt16();
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            il.ReadInt64();
                            break;
                        case OperandType.InlineSwitch:
                            var count = il.ReadInt32();
                            for (var i = 0; i < count; i++) il.ReadInt32();
                            break;
                        default:
                            il.ReadInt32();
                            break;
                    }
                }
            }
        }
        return hits;
    }

    private static (string Type, string Member)? Target(MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                return member.Parent.Kind == HandleKind.TypeReference
                    ? (TypeReferenceName(metadata, (TypeReferenceHandle)member.Parent), metadata.GetString(member.Name))
                    : null;
            case HandleKind.MethodSpecification:
                return Target(metadata, metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default:
                // A method of the scanned assembly itself, never one of the forbidden BCL members.
                return null;
        }
    }

    private static string TypeReferenceName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        var name = metadata.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceName(metadata, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
        var ns = metadata.GetString(reference.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// The outermost type around a method: lambdas, local functions, iterators and async state
    /// machines are nested types of the type whose code they are.
    /// </summary>
    private static string TopLevelName(MetadataReader metadata, TypeDefinition type)
    {
        while (!type.GetDeclaringType().IsNil) type = metadata.GetTypeDefinition(type.GetDeclaringType());
        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static IEnumerable<string> DefinedTopLevelTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Where(type => type.GetDeclaringType().IsNil)
            .Select(type => TopLevelName(metadata, type))
            .ToList();
    }

    private static string Describe(IEnumerable<Hit> hits)
        => string.Join("\n", hits.GroupBy(hit => (hit.Assembly, hit.Type)).OrderBy(g => g.Key.Type, StringComparer.Ordinal)
            .Select(g => $"  {g.Key.Assembly}: {g.Key.Type}\n" + string.Join("\n",
                g.Select(hit => $"      {hit.Method} -> {hit.Target}").Distinct().Order(StringComparer.Ordinal))));

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("RedCompute.sln not found above " + file);
    }
}

/// <summary>What the guard must catch: never called, only scanned.</summary>
internal sealed class GuardFixture
{
    public static bool Direct(string path) => File.Exists(path);

    public static Func<int> InALambda() => () => System.Diagnostics.Process.GetProcesses().Length;

    public static async Task InAnAsyncMethod(string path)
    {
        await Task.Yield();
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), path));
    }
}
