using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Numerics;

namespace RedCompute.ReleaseTool;

public sealed record RedComputeComponentInput
{
    public required int SchemaVersion { get; init; }
    public required string DescriptorType { get; init; }
    public required string ComponentId { get; init; }
    public required string ComponentKind { get; init; }
    public required string Version { get; init; }
    public required ComponentArtifactInput Artifact { get; init; }
    public required CompatibilityInput Compatibility { get; init; }
    public required BuildEvidence Evidence { get; init; }
}

public sealed record ComponentArtifactInput
{
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string InstallPath { get; init; }
    public required string TargetRid { get; init; }
    public required bool SelfContained { get; init; }
    public required bool WebShellIncluded { get; init; }
}

public sealed record CompatibilityInput
{
    public required string RequiresKernelApi { get; init; }
    public required string CompatibleProductVersion { get; init; }
    public required string ProvidesComputeApi { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
}

public sealed record BuildEvidence
{
    public required int SchemaVersion { get; init; }
    public required string EvidenceType { get; init; }
    public required string ComponentId { get; init; }
    public required string Version { get; init; }
    public required string Configuration { get; init; }
    public required string TargetRid { get; init; }
    public required bool Deterministic { get; init; }
    public required bool SelfContained { get; init; }
    public required bool WebShellIncluded { get; init; }
    public required SourceInput Repository { get; init; }
    public required SourceInput PluginSdk { get; init; }
    public required SourceInput AppHost { get; init; }
    public required ToolchainInput Toolchain { get; init; }
    public required IReadOnlyList<DependencyLockInput> DependencyLocks { get; init; }
}

public sealed record SourceInput
{
    public required string Id { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string Commit { get; init; }
    public required string SourcePath { get; init; }
}

public sealed record ToolchainInput
{
    public required string DotnetSdk { get; init; }
    public required string DotnetRuntime { get; init; }
    public required string Msbuild { get; init; }
    public required string Nuget { get; init; }
}

public sealed record DependencyLockInput
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record RuntimeDependencyInput
{
    public required string Id { get; init; }
    public required string Management { get; init; }
    public required string Requirement { get; init; }
    public required string VersionConstraint { get; init; }
    public required string Interface { get; init; }
    public required IReadOnlyList<string> SourceMarkers { get; init; }
}

public sealed record SourceDependencyFile
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<SourceInput> Dependencies { get; init; }
}

public sealed record ProviderGraphFile
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<ProviderGraphEntry> Providers { get; init; }
}

public sealed record ProviderGraphEntry
{
    public required string Id { get; init; }
    public required string IdentitySource { get; init; }
    public required string RuntimeIdentity { get; init; }
    public required string Assembly { get; init; }
    public required string Project { get; init; }
    public required IReadOnlyList<string> Dependencies { get; init; }
    public required IReadOnlyList<RuntimeDependencyInput> RuntimeDependencies { get; init; }
}

public readonly record struct FileFacts(long SizeBytes, string Sha256);

public static partial class ComponentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static string Serialize<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = indented })
           + (indented ? "\n" : "");

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"{typeof(T).Name} deserialized to null.");

    public static FileFacts Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new FileFacts(stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    public static void Validate(RedComputeComponentInput value)
    {
        if (value.SchemaVersion != 1 || value.DescriptorType != "redcompute-component-input")
            throw new InvalidDataException("Unsupported RedCompute component-input contract.");
        if (value.ComponentId != "redcompute" || value.ComponentKind != "compute")
            throw new InvalidDataException("The descriptor must identify the redcompute compute component.");
        Semver(value.Version, "version");
        Validate(value.Artifact);
        if (!Phase1VersionRange.IsValid(value.Compatibility.RequiresKernelApi)) throw new InvalidDataException("Invalid requiresKernelApi range.");
        if (!Phase1VersionRange.IsValid(value.Compatibility.CompatibleProductVersion)) throw new InvalidDataException("Invalid compatibleProductVersion range.");
        Semver(value.Compatibility.ProvidesComputeApi, "providesComputeApi");
        if (value.Compatibility.OperatingSystem != "windows" || value.Compatibility.Architecture != "x64")
            throw new InvalidDataException("Phase 1B component input must target windows/x64.");
        Validate(value.Evidence);
        if (value.Version != value.Evidence.Version || value.Artifact.TargetRid != value.Evidence.TargetRid)
            throw new InvalidDataException("Descriptor and evidence version/RID must agree.");
    }

    public static void Validate(BuildEvidence value)
    {
        if (value.SchemaVersion != 1 || value.EvidenceType != "redcompute-build-evidence" || value.ComponentId != "redcompute")
            throw new InvalidDataException("Unsupported build-evidence contract.");
        Semver(value.Version, "evidence version");
        if (value.TargetRid != "win-x64" || !value.Deterministic || !value.SelfContained || value.WebShellIncluded)
            throw new InvalidDataException("Evidence must describe the deterministic, self-contained win-x64 runtime without a web shell.");
        foreach (var source in new[] { value.Repository, value.PluginSdk, value.AppHost }) Validate(source);
        if (value.Repository.Id != "redcompute" || value.PluginSdk.Id != "redcompute-plugin-sdk" || value.AppHost.Id != "redbamboo-apphost") throw new InvalidDataException("Required source identities are missing.");
        if (value.Repository.Commit != value.PluginSdk.Commit || value.Repository.RepositoryUrl != value.PluginSdk.RepositoryUrl || value.PluginSdk.SourcePath != "src/RedCompute.PluginSdk") throw new InvalidDataException("PluginSdk source must be tied to the RedCompute repository input.");
        if (new[] { value.Toolchain.DotnetSdk, value.Toolchain.DotnetRuntime, value.Toolchain.Msbuild, value.Toolchain.Nuget }.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Exact toolchain and runtime versions are required.");
        if (value.DependencyLocks.Count == 0) throw new InvalidDataException("At least one dependency lock is required.");
        EnsureSorted(value.DependencyLocks, x => x.Path, "dependency locks");
        if (value.DependencyLocks.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != value.DependencyLocks.Count) throw new InvalidDataException("Dependency lock paths must be unique.");
        foreach (var item in value.DependencyLocks) { Relative(item.Path, "lock path"); Sha256(item.Sha256, "lock sha256"); }
    }

    private static void Validate(ComponentArtifactInput value)
    {
        FileName(value.FileName, "artifact fileName"); Facts(value.SizeBytes, value.Sha256, "artifact"); Relative(value.InstallPath, "installPath");
        if (value.TargetRid != "win-x64" || !value.SelfContained || value.WebShellIncluded)
            throw new InvalidDataException("Artifact facts do not describe the required RedCompute runtime.");
    }

    private static void Validate(SourceInput value)
    {
        Id(value.Id, "source id"); Https(value.RepositoryUrl); Commit(value.Commit, "commit");
        Relative(value.SourcePath, "source path", allowDot: true);
    }

    private static void Facts(long size, string sha, string name) { if (size <= 0) throw new InvalidDataException($"{name} size must be positive."); Sha256(sha, $"{name} sha256"); }
    private static void Sha256(string value, string name) { if (!Sha256Regex().IsMatch(value)) throw new InvalidDataException($"Invalid {name}."); }
    private static void Commit(string value, string name) { if (!GitObjectRegex().IsMatch(value)) throw new InvalidDataException($"Invalid {name}."); }
    private static void Semver(string value, string name) { if (!SemverRegex().IsMatch(value)) throw new InvalidDataException($"Invalid {name}."); }
    private static void Id(string value, string name) { if (!IdRegex().IsMatch(value)) throw new InvalidDataException($"Invalid {name}."); }
    private static void Https(string value) { if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Repository URL must be credential-free HTTPS."); }
    private static void FileName(string value, string name) { if (Path.GetFileName(value) != value || value is "." or "..") throw new InvalidDataException($"Invalid {name}."); }
    private static void Relative(string value, string name, bool allowDot = false) { if (allowDot && value == ".") return; if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException($"Invalid {name}."); }
    private static void EnsureSorted<T>(IReadOnlyList<T> values, Func<T, string> key, string name)
    {
        var keys = values.Select(key).ToArray();
        if (!keys.SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new InvalidDataException($"{name} must use ordinal order.");
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256Regex();
    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)] private static partial Regex GitObjectRegex();
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$", RegexOptions.CultureInvariant)] private static partial Regex IdRegex();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)] private static partial Regex SemverRegex();
}

// This is scalar contract validation for the Phase 1 grammar used by RedLeaf. It does
// not select components or resolve a suite graph; RedLeaf remains the only resolver.
public static class Phase1VersionRange
{
    private static readonly string[] Operators = [">=", "<=", ">", "<", "=", "^", "~"];

    public static bool IsValid(string? range) => TryParse(range, out _);

    public static bool Satisfies(string version, string? range)
        => SemanticVersion.TryParse(version, out var parsed)
           && TryParse(range, out var comparators)
           && comparators.All(x => x.Matches(parsed));

    private static bool TryParse(string? range, out IReadOnlyList<Comparator> comparators)
    {
        comparators = [];
        if (range == "*") return true;
        if (string.IsNullOrWhiteSpace(range) || range.Contains("||", StringComparison.Ordinal)) return false;
        var parsed = new List<Comparator>();
        foreach (var part in range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = Operators.FirstOrDefault(x => part.StartsWith(x, StringComparison.Ordinal)) ?? "";
            if (!SemanticVersion.TryParse(part[op.Length..], out var version)) return false;
            parsed.Add(new Comparator(op, version));
        }
        comparators = parsed;
        return parsed.Count > 0;
    }

    private readonly record struct Comparator(string Operator, SemanticVersion Version)
    {
        public bool Matches(SemanticVersion value)
        {
            var comparison = value.CompareTo(Version);
            return Operator switch
            {
                "" or "=" => comparison == 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                "^" => comparison >= 0 && value.CompareTo(Version.CaretUpperBound()) < 0,
                "~" => comparison >= 0 && value.CompareTo(new SemanticVersion(Version.Major, Version.Minor + 1, 0, [])) < 0,
                _ => false,
            };
        }
    }

    private readonly record struct SemanticVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Prerelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = default;
            var match = Regex.Match(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            if (prerelease.Any(x => x.All(char.IsDigit) && x.Length > 1 && x[0] == '0')) return false;
            version = new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value), prerelease);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            var result = Major.CompareTo(other.Major); if (result == 0) result = Minor.CompareTo(other.Minor); if (result == 0) result = Patch.CompareTo(other.Patch); if (result != 0) return result;
            if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
            if (other.Prerelease.Length == 0) return -1;
            for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
            {
                var left = Prerelease[i]; var right = other.Prerelease[i]; var leftNumber = left.All(char.IsDigit); var rightNumber = right.All(char.IsDigit);
                result = leftNumber && rightNumber ? (left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right))
                    : leftNumber ? -1 : rightNumber ? 1 : string.CompareOrdinal(left, right);
                if (result != 0) return result;
            }
            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        public SemanticVersion CaretUpperBound() => Major > 0 ? new(Major + 1, 0, 0, []) : Minor > 0 ? new(0, Minor + 1, 0, []) : new(0, 0, Patch + 1, []);
    }
}
