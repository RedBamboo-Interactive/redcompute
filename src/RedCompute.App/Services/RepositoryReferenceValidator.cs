using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Services;

public sealed record RepositoryReference(Guid Id, string Name, string Path);

public sealed record RepositoryValidationResult(
    RepositoryReference? Repository,
    int StatusCode = StatusCodes.Status200OK,
    string? Error = null)
{
    public bool Ok => Repository is not null;
}

/// <summary>
/// Resolves a stable RedLeaf Repository entity and binds it to the exact physical
/// checkout supplied for execution. The entity is identity; the path is a
/// machine-local execution snapshot and must never be accepted from a different
/// repository reference.
///
/// A session may run in the repository's registered checkout (<c>local_path</c>, exact match, no git call)
/// or in a git worktree of it. Worktree membership is proved with git, never with path prefixes: the
/// candidate must be the root of a <em>linked</em> working tree (<c>--git-dir</c> differs from
/// <c>--git-common-dir</c>, empty <c>--show-prefix</c>) whose <c>--git-common-dir</c> is the same directory as
/// the one git reports for <c>local_path</c>. Both sides are read from git, so drive aliases (git prints the
/// physical form for both) and case differences compare equal. A worktree whose folder is gone (prunable) is rejected,
/// as is any non-exact path when the repository's <c>branch_work</c> is <c>main-only</c>.
/// </summary>
public sealed class RepositoryReferenceValidator
{
    private readonly HttpClient _http;

    public RepositoryReferenceValidator(string redLeafBaseUrl, JwtService jwtService)
    {
        _http = new HttpClient(new BadgeBearerHandler(
            () => jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"])))
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    internal RepositoryReferenceValidator(HttpClient http) => _http = http;

    public async Task<RepositoryValidationResult> ValidateAsync(
        string repositoryId,
        string projectPath,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(repositoryId, out var id))
            return Invalid("repositoryId must be a valid Repository entity UUID");

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"api/entities/{id:D}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(null, StatusCodes.Status503ServiceUnavailable,
                $"Repository validation is unavailable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Invalid("Repository entity not found");
            if (!response.IsSuccessStatusCode)
                return new(null, StatusCodes.Status503ServiceUnavailable,
                    $"Repository validation failed with RedLeaf status {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var entity = document.RootElement;
            if (!string.Equals(Str(entity, "typeSlug"), "repository", StringComparison.OrdinalIgnoreCase))
                return Invalid("repositoryId must reference a Repository entity");

            using var data = ParseData(entity);
            if (!string.Equals(Str(data.RootElement, "status"), "active", StringComparison.OrdinalIgnoreCase))
                return Invalid("Repository entity is not active");

            var repositoryPath = Str(data.RootElement, "local_path");
            if (!PathsEqual(repositoryPath, projectPath))
            {
                var rejection = await ValidateWorktreeAsync(
                    repositoryPath, projectPath, Str(data.RootElement, "branch_work"), ct);
                if (rejection is not null) return rejection;
            }
            else if (!Directory.Exists(Path.GetFullPath(projectPath)))
                return Invalid("Referenced Repository checkout is unavailable");

            return new(new RepositoryReference(
                id,
                Str(entity, "name") ?? id.ToString("D"),
                Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }
    }

    /// <summary>Null when projectPath is a linked worktree of the repository at repositoryPath.</summary>
    private async Task<RepositoryValidationResult?> ValidateWorktreeAsync(
        string? repositoryPath, string projectPath, string? branchWork, CancellationToken ct)
    {
        const string NotMatching = "projectPath does not match the referenced Repository checkout";
        if (string.Equals(branchWork?.Trim(), "main-only", StringComparison.OrdinalIgnoreCase))
            return Invalid("Repository is main-only; sessions must use its checkout");
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(projectPath))
            return Invalid($"{NotMatching} and is not one of its git worktrees");

        string fullProject;
        try { fullProject = Path.GetFullPath(projectPath); }
        catch { return Invalid($"{NotMatching} and is not one of its git worktrees"); }
        if (!Directory.Exists(fullProject))
            return Invalid(
                "Worktree folder is missing (prunable): projectPath does not exist and is not the referenced Repository checkout");
        if (!Directory.Exists(Path.GetFullPath(repositoryPath)))
            return Invalid("Referenced Repository checkout is unavailable");

        try
        {
            var repo = await GitAsync(repositoryPath, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
            if (repo is not { Count: >= 1 })
                return Invalid($"{NotMatching}; git could not read the Repository checkout, so worktree membership cannot be proved");
            var candidate = await GitAsync(fullProject,
                ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-prefix"], ct);
            if (candidate is not { Count: >= 2 })
                return Invalid($"{NotMatching} and is not a git worktree");

            var gitDir = candidate[0];
            var commonDir = candidate[1];
            var prefix = candidate.Count > 2 ? candidate[2] : "";
            if (!PathsEqual(commonDir, repo[0]))
                return Invalid($"{NotMatching} and is not a git worktree of this Repository (different git common dir)");
            if (PathsEqual(gitDir, commonDir))
                return Invalid($"{NotMatching} and is not a linked git worktree (it is a checkout, not a worktree)");
            if (prefix.Length > 0)
                return Invalid($"{NotMatching}; projectPath is a subfolder of a worktree, not the worktree root");
            return null;
        }
        catch (TimeoutException)
        {
            return new(null, StatusCodes.Status503ServiceUnavailable, "Repository validation is unavailable: git timed out");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(null, StatusCodes.Status503ServiceUnavailable, $"Repository validation is unavailable: git failed to run ({ex.Message})");
        }
    }

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs git without a shell; returns stdout lines, or null on a non-zero exit.</summary>
    private static async Task<List<string>?> GitAsync(string workingDirectory, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(workingDirectory);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            if (process.ExitCode != 0) return null;
            // Output ends with a newline; drop only that final empty element (an empty --show-prefix line before it is a value).
            var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("git timed out");
        }
    }

    private static RepositoryValidationResult Invalid(string message) =>
        new(null, StatusCodes.Status422UnprocessableEntity, message);

    private static JsonDocument ParseData(JsonElement entity)
    {
        if (!entity.TryGetProperty("data", out var data)) return JsonDocument.Parse("{}");
        return data.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(data.GetString() ?? "{}")
            : JsonDocument.Parse(data.GetRawText());
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var leftPath = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightPath = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
