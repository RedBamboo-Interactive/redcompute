using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedCompute.PluginSdk;

/// <summary>
/// Opt-in contract for CLI-backed providers whose npm package can be managed through RedCompute.
/// </summary>
public interface ICliVersionManagedProvider
{
    string ProviderId { get; }
    string ProviderDisplayName { get; }
    NpmCliVersionManager CliVersionManager { get; }
}

public sealed record CliCommand(string FileName, IReadOnlyList<string> Arguments);

public sealed record CliCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(value => value.Length > 0));
}

public interface ICliCommandRunner
{
    Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default);
}

public sealed class ProcessCliCommandRunner : ICliCommandRunner
{
    public async Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(command) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new CliCommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static ProcessStartInfo CreateStartInfo(CliCommand command)
    {
        ProcessStartInfo startInfo;
        var extension = Path.GetExtension(command.FileName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(FormatWindowsBatchCommand(command));
        }
        else
        {
            startInfo = new ProcessStartInfo(command.FileName);
            foreach (var argument in command.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    private static string FormatWindowsBatchCommand(CliCommand command)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        return string.Join(" ", new[] { Quote(command.FileName) }.Concat(command.Arguments.Select(Quote)));
    }
}

public sealed record CliInstalledVersionResult(string? Version, string? Error);

public sealed record CliVersionStatus(
    string PackageName,
    string? InstalledVersion,
    string? DetectionError,
    int ActiveSessionCount,
    bool InstallAllowed,
    string? BlockedReason,
    CliInstallOperation? Operation);

public sealed record CliInstallOperation(
    Guid Id,
    string Status,
    string RequestedVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? InstalledVersion = null,
    string? ErrorCode = null,
    string? Error = null);

public sealed class CliInstallRejectedException : Exception
{
    public string Code { get; }
    public int? ActiveSessionCount { get; }

    public CliInstallRejectedException(string code, string message, int? activeSessionCount = null)
        : base(message)
    {
        Code = code;
        ActiveSessionCount = activeSessionCount;
    }
}

/// <summary>
/// Shared npm-backed version manager. It never stops provider sessions; install requests are
/// refused when the provider reports a Starting or Active session and are checked again in the
/// asynchronous operation immediately before npm is launched.
/// </summary>
public sealed class NpmCliVersionManager
{
    private static readonly Regex VersionPattern = new(
        @"(?<![0-9A-Za-z])v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExactVersionPattern = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string?> _resolveExecutable;
    private readonly Func<string?> _resolveNpmExecutable;
    private readonly Func<int> _activeSessionCount;
    private readonly ICliCommandRunner _runner;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CliInstallOperation> _operations = [];
    private Guid? _currentOperationId;

    public string PackageName { get; }

    public NpmCliVersionManager(
        string packageName,
        Func<string?> resolveExecutable,
        Func<int> activeSessionCount,
        ICliCommandRunner? runner = null,
        Func<string?>? resolveNpmExecutable = null)
    {
        PackageName = packageName;
        _resolveExecutable = resolveExecutable;
        _activeSessionCount = activeSessionCount;
        _runner = runner ?? new ProcessCliCommandRunner();
        _resolveNpmExecutable = resolveNpmExecutable ?? ResolveNpmExecutable;
    }

    public async Task<CliVersionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var installed = await GetInstalledVersionAsync(ct);
        var activeCount = Math.Max(0, _activeSessionCount());
        var operation = CurrentOperation();
        var installing = operation?.Status is "queued" or "running";
        var blockedReason = activeCount > 0 ? FormatSessionCount(activeCount)
            : installing ? "Install already in progress"
            : null;
        return new CliVersionStatus(
            PackageName,
            installed.Version,
            installed.Error,
            activeCount,
            blockedReason is null,
            blockedReason,
            operation);
    }

    public async Task<CliInstalledVersionResult> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        var executable = _resolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
            return new CliInstalledVersionResult(null, "CLI executable not found");

        try
        {
            var result = await _runner.RunAsync(new CliCommand(executable, ["--version"]), ct);
            if (result.ExitCode != 0)
                return new CliInstalledVersionResult(null,
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? $"Version command exited with code {result.ExitCode}"
                        : result.CombinedOutput);

            var version = ParseInstalledVersion(result.CombinedOutput);
            return version is null
                ? new CliInstalledVersionResult(null, $"Could not parse CLI version from: {result.CombinedOutput.Trim()}")
                : new CliInstalledVersionResult(version, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CliInstalledVersionResult(null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableVersionsAsync(CancellationToken ct = default)
    {
        var npm = _resolveNpmExecutable()
            ?? throw new InvalidOperationException("npm executable not found");
        var result = await _runner.RunAsync(
            new CliCommand(npm, ["view", PackageName, "versions", "--json"]), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"npm view exited with code {result.ExitCode}"
                : result.CombinedOutput);
        return ParseAvailableVersions(result.StandardOutput);
    }

    public CliInstallOperation StartInstall(string requestedVersion)
    {
        requestedVersion = requestedVersion.Trim();
        if (!requestedVersion.Equals("latest", StringComparison.OrdinalIgnoreCase)
            && !ExactVersionPattern.IsMatch(requestedVersion))
            throw new CliInstallRejectedException("invalid_version", "Version must be 'latest' or an npm semantic version");

        CliInstallOperation operation;
        lock (_gate)
        {
            var current = CurrentOperationUnsafe();
            if (current?.Status is "queued" or "running")
                throw new CliInstallRejectedException("install_in_progress", "Install already in progress");

            var activeCount = Math.Max(0, _activeSessionCount());
            if (activeCount > 0)
                throw new CliInstallRejectedException("sessions_running", FormatSessionCount(activeCount), activeCount);

            operation = new CliInstallOperation(
                Guid.NewGuid(), "queued",
                requestedVersion.Equals("latest", StringComparison.OrdinalIgnoreCase) ? "latest" : requestedVersion,
                DateTimeOffset.UtcNow);
            _operations[operation.Id] = operation;
            _currentOperationId = operation.Id;
        }

        _ = Task.Run(() => RunInstallAsync(operation.Id));
        return operation;
    }

    public CliInstallOperation? GetOperation(Guid id)
    {
        lock (_gate)
            return _operations.GetValueOrDefault(id);
    }

    public static string? ParseInstalledVersion(string output)
        => VersionPattern.Match(output) is { Success: true } match
            ? match.Groups["version"].Value
            : null;

    public static IReadOnlyList<string> ParseAvailableVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var versions = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(document.RootElement.GetString())
                => [document.RootElement.GetString()!],
            _ => [],
        };
        versions.Sort(SemanticVersionDescendingComparer.Instance);
        return versions;
    }

    public static CliCommand BuildInstallCommand(
        string npmExecutable, string packageName, string requestedVersion)
        => new(npmExecutable,
            ["install", "--global", $"{packageName}@{requestedVersion}"]);

    private async Task RunInstallAsync(Guid operationId)
    {
        try
        {
            var activeCount = Math.Max(0, _activeSessionCount());
            if (activeCount > 0)
            {
                Complete(operationId, "refused", "sessions_running", FormatSessionCount(activeCount));
                return;
            }

            Update(operationId, operation => operation with
            {
                Status = "running",
                StartedAt = DateTimeOffset.UtcNow,
            });

            var operation = GetOperation(operationId)!;
            var npm = _resolveNpmExecutable()
                ?? throw new InvalidOperationException("npm executable not found");
            var command = BuildInstallCommand(npm, PackageName, operation.RequestedVersion);
            var result = await _runner.RunAsync(command, CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? $"npm install exited with code {result.ExitCode}"
                    : result.CombinedOutput);

            var installed = await GetInstalledVersionAsync(CancellationToken.None);
            Update(operationId, current => current with
            {
                Status = "completed",
                CompletedAt = DateTimeOffset.UtcNow,
                InstalledVersion = installed.Version,
                Error = installed.Error,
            });
        }
        catch (Exception ex)
        {
            Complete(operationId, "failed", "npm_install_failed", ex.Message);
        }
    }

    private void Complete(Guid id, string status, string errorCode, string error)
        => Update(id, operation => operation with
        {
            Status = status,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorCode = errorCode,
            Error = error,
        });

    private void Update(Guid id, Func<CliInstallOperation, CliInstallOperation> update)
    {
        lock (_gate)
        {
            if (_operations.TryGetValue(id, out var operation))
                _operations[id] = update(operation);
        }
    }

    private CliInstallOperation? CurrentOperation()
    {
        lock (_gate)
            return CurrentOperationUnsafe();
    }

    private CliInstallOperation? CurrentOperationUnsafe()
        => _currentOperationId is { } id ? _operations.GetValueOrDefault(id) : null;

    private static string FormatSessionCount(int count)
        => $"{count} {(count == 1 ? "session" : "sessions")} running";

    private static string? ResolveNpmExecutable()
    {
        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "npm.cmd", "npm.exe", "npm" }
            : new[] { "npm" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    private sealed class SemanticVersionDescendingComparer : IComparer<string>
    {
        public static readonly SemanticVersionDescendingComparer Instance = new();

        public int Compare(string? left, string? right) => -CompareAscending(left, right);

        private static int CompareAscending(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var l = ParsedVersion.Parse(left);
            var r = ParsedVersion.Parse(right);
            for (var i = 0; i < 3; i++)
            {
                var numeric = l.Core[i].CompareTo(r.Core[i]);
                if (numeric != 0) return numeric;
            }

            if (l.Prerelease.Count == 0 && r.Prerelease.Count > 0) return 1;
            if (l.Prerelease.Count > 0 && r.Prerelease.Count == 0) return -1;
            for (var i = 0; i < Math.Max(l.Prerelease.Count, r.Prerelease.Count); i++)
            {
                if (i >= l.Prerelease.Count) return -1;
                if (i >= r.Prerelease.Count) return 1;
                var lToken = l.Prerelease[i];
                var rToken = r.Prerelease[i];
                var lNumeric = long.TryParse(lToken, out var ln);
                var rNumeric = long.TryParse(rToken, out var rn);
                if (lNumeric && rNumeric)
                {
                    var comparison = ln.CompareTo(rn);
                    if (comparison != 0) return comparison;
                }
                else if (lNumeric != rNumeric)
                {
                    return lNumeric ? -1 : 1;
                }
                else
                {
                    var comparison = string.CompareOrdinal(lToken, rToken);
                    if (comparison != 0) return comparison;
                }
            }
            return 0;
        }

        private sealed record ParsedVersion(long[] Core, IReadOnlyList<string> Prerelease)
        {
            public static ParsedVersion Parse(string value)
            {
                var withoutBuild = value.Split('+', 2)[0];
                var pieces = withoutBuild.Split('-', 2);
                var core = pieces[0].Split('.').Select(part => long.TryParse(part, out var n) ? n : 0L)
                    .Concat([0L, 0L, 0L]).Take(3).ToArray();
                var prerelease = pieces.Length > 1 ? pieces[1].Split('.') : [];
                return new ParsedVersion(core, prerelease);
            }
        }
    }
}
