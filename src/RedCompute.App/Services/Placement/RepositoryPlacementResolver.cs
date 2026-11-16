using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// Where a repository placement runs on a node. <paramref name="CheckoutPath"/> is the checkout,
/// <paramref name="WorkingDirectory"/> the folder the work starts in (the checkout, the branch's linked
/// worktree, or a folder inside either).
/// </summary>
/// <param name="CheckoutId">Null while the repository has no checkout entities: its <c>local_path</c> is the one checkout.</param>
public sealed record RepositoryResolution(Guid RepositoryId, string Name, Guid? CheckoutId, string CheckoutPath,
    string WorkingDirectory, string? Branch);

/// <summary>
/// Resolves <c>placement.repository</c> on a node (design 2.2.4): the Repository entity, the checkout on
/// that node (a <c>repository-checkout</c> entity, else the repository's <c>local_path</c> as the legacy
/// node's one checkout), the branch with git on the node (<c>worktree list --porcelain</c>: the checkout
/// itself when the branch is checked out there, else its linked worktree; never a path guess), then
/// <c>subPath</c> with the node's path rules. RedCompute never creates a worktree.
/// </summary>
public sealed class RepositoryPlacementResolver
{
    internal const string CheckoutType = "repository-checkout";

    private readonly HttpClient _http;

    public RepositoryPlacementResolver(string redLeafBaseUrl, JwtService jwtService)
        : this(new HttpClient(new BadgeBearerHandler(
            () => jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"])))
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        })
    {
    }

    internal RepositoryPlacementResolver(HttpClient http) => _http = http;

    /// <param name="nodeId">The node's registry id.</param>
    /// <param name="legacyNode">Whether that node is the legacy node (a checkout without a node id is on it).</param>
    public async Task<Resolution<RepositoryResolution>> ResolveAsync(RepositoryPlacement placement, string nodeId,
        bool legacyNode, IExecutionHost host, CancellationToken ct = default)
    {
        var repository = await GetEntityAsync(placement.RepositoryId, ct);
        if (repository.Error is not null) return new(null, repository.Error);
        if (repository.Entity is not { } entity)
            return Fail(404, "repository_not_found", "Repository entity not found");
        if (!string.Equals(entity.Type, "repository", StringComparison.OrdinalIgnoreCase))
            return Fail(422, "validation_failed", "placement.repository.repositoryId must reference a Repository entity");
        if (!string.Equals(entity.Text("status"), "active", StringComparison.OrdinalIgnoreCase))
            return Fail(409, "repository_inactive", "Repository entity is not active");

        var checkout = await CheckoutAsync(placement, entity, nodeId, legacyNode, ct);
        if (checkout.Error is not null) return new(null, checkout.Error);
        var (checkoutId, checkoutPath) = checkout.Value!;
        if (!host.PathRules.IsPathFullyQualified(checkoutPath))
            return Fail(409, "project_path_missing", "The checkout's folder is not an absolute path on this node");
        checkoutPath = Trim(host, host.PathRules.GetFullPath(checkoutPath));
        if (!host.Files.DirectoryExists(host.Path(checkoutPath)))
            return Fail(409, "project_path_missing", "The checkout's folder is missing on this node");

        var directory = checkoutPath;
        if (placement.Branch is { } branch)
        {
            var worktree = await BranchFolderAsync(host, checkoutPath, branch, ct);
            if (worktree.Error is not null) return new(null, worktree.Error);
            directory = worktree.Value!;
            if (!PathsEqual(host, directory, checkoutPath)
                && string.Equals(entity.Text("branch_work")?.Trim(), "main-only", StringComparison.OrdinalIgnoreCase))
                return Fail(409, "main_only", "Repository is main-only; sessions must use its checkout");
            if (!host.Files.DirectoryExists(host.Path(directory)))
                return Fail(409, "worktree_not_found", "The branch's worktree folder is missing (prunable)");
        }

        if (placement.SubPath is { } subPath)
        {
            if (PlacementParser.SubPathProblem(subPath) is { } problem)
                return Fail(422, "validation_failed", $"placement.repository.subPath {problem}");
            var inner = Trim(host, host.PathRules.GetFullPath(host.PathRules.Combine(directory, subPath)));
            if (!inner.StartsWith(directory + host.PathRules.DirectorySeparator, host.PathRules.Comparison))
                return Fail(422, "validation_failed", "placement.repository.subPath must stay inside the checkout");
            if (!host.Files.DirectoryExists(host.Path(inner)))
                return Fail(409, "project_path_missing", "placement.repository.subPath is not a folder in the checkout");
            directory = inner;
        }

        return Resolution<RepositoryResolution>.Of(new(placement.RepositoryId, entity.Name ?? placement.RepositoryId.ToString("D"),
            checkoutId, checkoutPath, directory, placement.Branch));
    }

    /// <summary>
    /// The checkout on this node: the named one (it must belong to the repository and be on this node),
    /// else the one active checkout entity on this node, else, while the repository has none, its
    /// <c>local_path</c> on the legacy node.
    /// </summary>
    private async Task<Resolution<Tuple<Guid?, string>>> CheckoutAsync(RepositoryPlacement placement, Entity repository,
        string nodeId, bool legacyNode, CancellationToken ct)
    {
        bool OnThisNode(Entity checkout) => checkout.Text("node_id") is { } id
            ? string.Equals(id, nodeId, StringComparison.Ordinal)
            : legacyNode;

        if (placement.CheckoutId is { } checkoutId)
        {
            var named = await GetEntityAsync(checkoutId, ct);
            if (named.Error is not null) return new(null, named.Error);
            if (named.Entity is not { } checkout
                || !string.Equals(checkout.Type, CheckoutType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(checkout.Text("repository"), placement.RepositoryId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return Fail<Tuple<Guid?, string>>(422, "validation_failed",
                    "placement.repository.checkoutId must reference a checkout of that Repository");
            if (!OnThisNode(checkout))
                return Fail<Tuple<Guid?, string>>(409, "checkout_not_on_node", "The checkout is on another node");
            return checkout.Text("path") is { Length: > 0 } path
                ? Resolution<Tuple<Guid?, string>>.Of(Tuple.Create<Guid?, string>(checkout.Id, path))
                : Fail<Tuple<Guid?, string>>(409, "project_path_missing", "The checkout has no folder");
        }

        var listed = await ListCheckoutsAsync(placement.RepositoryId, ct);
        if (listed.Error is not null) return new(null, listed.Error);
        var checkouts = listed.Entities!;
        if (checkouts.Count == 0)
        {
            if (!legacyNode)
                return Fail<Tuple<Guid?, string>>(409, "checkout_not_on_node", "The Repository has no checkout on this node");
            return repository.Text("local_path") is { Length: > 0 } local
                ? Resolution<Tuple<Guid?, string>>.Of(Tuple.Create<Guid?, string>(null, local))
                : Fail<Tuple<Guid?, string>>(409, "project_path_missing", "The Repository has no local_path");
        }

        var here = checkouts.Where(OnThisNode)
            .Where(c => !string.Equals(c.Text("status"), "archived", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return here.Count switch
        {
            1 when here[0].Text("path") is { Length: > 0 } path => Resolution<Tuple<Guid?, string>>.Of(Tuple.Create<Guid?, string>(here[0].Id, path)),
            1 => Fail<Tuple<Guid?, string>>(409, "project_path_missing", "The checkout has no folder"),
            0 => Fail<Tuple<Guid?, string>>(409, "checkout_not_on_node", "The Repository has no checkout on this node"),
            _ => Fail<Tuple<Guid?, string>>(409, "checkout_ambiguous",
                "The Repository has several checkouts on this node; name one with placement.repository.checkoutId"),
        };
    }

    /// <summary>
    /// The folder where <paramref name="branch"/> is checked out, from git on the node: the checkout itself
    /// or one of its linked worktrees. A worktree git reports as prunable (its folder is gone) does not count.
    /// </summary>
    internal static async Task<Resolution<string>> BranchFolderAsync(IExecutionHost host, string checkout, string branch,
        CancellationToken ct)
    {
        List<string>? lines;
        try
        {
            lines = await HostGit.RunAsync(host, checkout, ["worktree", "list", "--porcelain"], ct);
        }
        catch (TimeoutException)
        {
            return Fail<string>(503, "placement_resolution_unavailable", "Placement resolution is unavailable: git timed out");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Fail<string>(503, "placement_resolution_unavailable", $"Placement resolution is unavailable: git failed to run ({ex.GetType().Name})");
        }
        if (lines is null)
            return Fail<string>(409, "project_path_missing", "The checkout is not a git repository");

        var wanted = "refs/heads/" + branch;
        var worktrees = ParseWorktrees(lines);
        for (var i = 0; i < worktrees.Count; i++)
        {
            var worktree = worktrees[i];
            if (worktree.Prunable || !string.Equals(worktree.Branch, wanted, StringComparison.Ordinal)) continue;
            // The main worktree is the checkout itself, in the form it was named.
            if (i == 0) return Resolution<string>.Of(checkout);
            return Resolution<string>.Of(InCheckoutForm(host, Trim(host, host.PathRules.GetFullPath(worktrees[0].Path)), checkout,
                Trim(host, host.PathRules.GetFullPath(worktree.Path))));
        }
        return Fail<string>(409, "worktree_not_found", $"Branch '{branch}' is not checked out in the checkout or any of its worktrees");
    }

    /// <summary>
    /// git prints worktree paths in their physical form (a mapped or substituted drive resolved), while
    /// callers name the checkout through its alias. The prefix git puts where the alias was is swapped
    /// back, so a linked worktree reads as the caller would name it; a worktree elsewhere is kept as git
    /// prints it.
    /// </summary>
    internal static string InCheckoutForm(IExecutionHost host, string gitCheckout, string checkout, string gitWorktree)
    {
        if (string.Equals(gitCheckout, checkout, host.PathRules.Comparison)) return gitWorktree;
        var separator = host.PathRules.DirectorySeparator;
        var physical = gitCheckout.Split(separator);
        var alias = checkout.Split(separator);
        var common = 0;
        while (common < physical.Length - 1 && common < alias.Length - 1
               && string.Equals(physical[^(common + 1)], alias[^(common + 1)], host.PathRules.Comparison))
            common++;
        if (common == 0) return gitWorktree;
        var physicalPrefix = string.Join(separator, physical[..^common]);
        var aliasPrefix = string.Join(separator, alias[..^common]);
        return gitWorktree.StartsWith(physicalPrefix + separator, host.PathRules.Comparison)
            ? aliasPrefix + gitWorktree[physicalPrefix.Length..]
            : gitWorktree;
    }

    internal sealed record Worktree(string Path, string? Branch, bool Prunable);

    /// <summary><c>git worktree list --porcelain</c>: blank-line separated records, the main worktree first.</summary>
    internal static List<Worktree> ParseWorktrees(IEnumerable<string> lines)
    {
        var result = new List<Worktree>();
        string? path = null, branch = null;
        var prunable = false;
        void Flush()
        {
            if (path is not null) result.Add(new Worktree(path, branch, prunable));
            path = branch = null;
            prunable = false;
        }
        foreach (var line in lines)
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { Flush(); path = line["worktree ".Length..]; }
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal)) prunable = true;
        }
        Flush();
        return result;
    }

    // ── RedLeaf entities ─────────────────────────────────────────────────────────────────────────

    private sealed record Entity(Guid Id, string? Type, string? Name, JsonElement Data)
    {
        public string? Text(string member)
            => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private async Task<(Entity? Entity, PlacementError? Error)> GetEntityAsync(Guid id, CancellationToken ct)
    {
        var (document, error) = await GetJsonAsync($"api/entities/{id:D}", notFoundIsEmpty: true, ct);
        if (error is not null || document is null) return (null, error);
        using (document) return (ReadEntity(document.RootElement), null);
    }

    private async Task<(List<Entity>? Entities, PlacementError? Error)> ListCheckoutsAsync(Guid repositoryId, CancellationToken ct)
    {
        var (document, error) = await GetJsonAsync(
            $"api/entities?type={CheckoutType}&data.repository={repositoryId:D}&limit=500", notFoundIsEmpty: true, ct);
        if (error is not null) return (null, error);
        if (document is null) return ([], null);
        using (document)
        {
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return (null, Unavailable("RedLeaf answered a malformed entity list"));
            return (items.EnumerateArray().Select(ReadEntity).OfType<Entity>().ToList(), null);
        }
    }

    private async Task<(JsonDocument? Document, PlacementError? Error)> GetJsonAsync(string uri, bool notFoundIsEmpty,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsEmpty) return (null, null);
            if (!response.IsSuccessStatusCode)
                return (null, Unavailable($"RedLeaf answered {(int)response.StatusCode}"));
            return (JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, Unavailable($"RedLeaf could not be reached ({ex.GetType().Name})"));
        }
        catch (JsonException)
        {
            return (null, Unavailable("RedLeaf answered malformed JSON"));
        }
    }

    /// <summary>An entity as <c>/api/entities</c> returns it; its <c>data</c> is JSON text there.</summary>
    private static Entity? ReadEntity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var idValue) || !Guid.TryParse(idValue.GetString(), out var id))
            return null;
        string? Str(string member) => element.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var data = element.TryGetProperty("data", out var raw)
            ? raw.ValueKind == JsonValueKind.String ? ParseData(raw.GetString()) : raw.Clone()
            : default;
        return new Entity(id, Str("typeSlug"), Str("name"), data);
    }

    private static JsonElement ParseData(string? text)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    private static string Trim(IExecutionHost host, string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        // A drive or filesystem root keeps its separator.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    private static bool PathsEqual(IExecutionHost host, string left, string right)
        => string.Equals(Trim(host, left), Trim(host, right), host.PathRules.Comparison);

    private static PlacementError Unavailable(string reason) => new(StatusCodes.Status503ServiceUnavailable,
        "placement_resolution_unavailable", $"Placement resolution is unavailable: {reason}");

    private static Resolution<RepositoryResolution> Fail(int status, string code, string message)
        => Resolution<RepositoryResolution>.Fail(status, code, message);

    private static Resolution<T> Fail<T>(int status, string code, string message) where T : class
        => Resolution<T>.Fail(status, code, message);
}
