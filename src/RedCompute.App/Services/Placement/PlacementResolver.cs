using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// A placement resolved on its node, expressed as the launch inputs the endpoints use today (design
/// 2.2.6): the working directory (<c>projectPath</c> / <c>workingDir</c>), the scratch directory
/// (<c>scratchDir</c>), the scratch environment (<c>env</c>), <c>addDirs</c> and the repository id.
/// Nothing downstream of endpoint parsing has to know a placement exists.
/// </summary>
/// <param name="WorkingDirectory">Null when the placement names no working directory (it then has none to offer).</param>
/// <param name="AddDirs">Execute only: the scratch folder when <c>scratchAs</c> is add-dir, else null.</param>
public sealed record ResolvedPlacement(
    PlacementRequest Request,
    string NodeId,
    string? WorkingDirectory,
    string? ScratchDirectory,
    IReadOnlyDictionary<string, string>? ScratchEnvironment,
    IReadOnlyList<string>? AddDirs,
    Guid? RepositoryId,
    RepositoryResolution? Repository,
    WorkspaceResolution? Workspace,
    IReadOnlyList<ResolvedReference>? References = null);

/// <summary>
/// A <c>placement.references</c> entry resolved on the node: the folder it names, read-only by design.
/// Nothing is mounted for it yet; it is exposed in the session's placement document so a later phase can.
/// </summary>
/// <param name="Kind"><c>repository</c> or <c>workspace</c>.</param>
/// <param name="Id">The repository's or the Agent's entity id.</param>
public sealed record ResolvedReference(string Kind, Guid Id, Guid? CheckoutId, string? Branch, string Path);

/// <summary>
/// Resolves a <see cref="PlacementRequest"/> on its node, in the design's order: node, then workspace or
/// repository, then scratch. A resolver this host does not have (no kernel URL) is 503
/// <c>placement_resolution_unavailable</c>, never a guess.
/// </summary>
public sealed class PlacementResolver
{
    private readonly INodeRegistry? _nodes;
    private readonly IWorkspaceResolver? _workspaces;
    private readonly IHostScratch? _scratch;
    private readonly RepositoryPlacementResolver? _repositories;
    private readonly Func<string?>? _defaultNode;

    /// <param name="defaultNode">The capability's <c>defaultNodeId</c> (design 2.3, step three), read at each resolution.</param>
    public PlacementResolver(INodeRegistry? nodes, IWorkspaceResolver? workspaces, IHostScratch? scratch,
        RepositoryPlacementResolver? repositories, Func<string?>? defaultNode = null)
    {
        _nodes = nodes;
        _workspaces = workspaces;
        _scratch = scratch;
        _repositories = repositories;
        _defaultNode = defaultNode;
    }

    /// <param name="caller">
    /// Who the work is for: an explicit node must be one they may place on, and Automatic considers only
    /// node zero and their person's own nodes (<see cref="NodeAccess"/>). Null is no person: node zero only.
    /// </param>
    public async Task<Resolution<ResolvedPlacement>> ResolveAsync(PlacementRequest placement, PlacementRoute route,
        ClaimsPrincipal? caller, CancellationToken ct = default)
    {
        if (PlacementParser.ResolveNode(placement, _nodes, caller, out var requested) is { } nodeError)
            return new(null, nodeError);
        // Sessions and execute run on node zero only in Phase 4: a remote node has no provider for them (design 1.2's 422).
        if (PlacementParser.IsRemote(requested, _nodes))
            return Resolution<ResolvedPlacement>.Fail(StatusCodes.Status422UnprocessableEntity, "provider_unavailable_on_node",
                $"'{Api.Endpoints.UnifiedSessionEndpoints.SessionCapability}' has no provider on node '{requested}'");
        string nodeId;
        if (requested is not null) nodeId = requested;
        else
        {
            var routed = await RouteAsync(placement, caller, ct);
            if (routed.Error is not null) return new(null, routed.Error);
            nodeId = routed.NodeId;
        }
        IExecutionHost host;
        try
        {
            host = _nodes?.HostFor(nodeId) ?? PluginSdk.Hosting.Local.LocalExecutionHost.Shared;
        }
        catch (RedCompute.Core.Nodes.NodeUnavailableException error)
        {
            // The node went offline or was revoked between ResolveNode and here.
            return new(null, PlacementError.Unavailable(error));
        }
        var legacyNode = _nodes is null
            || string.Equals(nodeId, _nodes.LocalNodeId, StringComparison.Ordinal)
            || string.Equals(nodeId, _nodes.LegacyNodeId, StringComparison.Ordinal);

        WorkspaceResolution? workspace = null;
        RepositoryResolution? repository = null;
        if (placement.Workspace is { } requestedWorkspace)
        {
            if (_workspaces is null) return Unavailable("workspace");
            var resolved = await _workspaces.ResolveAsync(requestedWorkspace, ct);
            if (!resolved.Ok) return new(null, resolved.Error);
            workspace = resolved.Value;
        }
        else if (placement.Repository is { } requestedRepository)
        {
            if (_repositories is null) return Unavailable("repository");
            var resolved = await _repositories.ResolveAsync(requestedRepository, nodeId, legacyNode, host, ct);
            if (!resolved.Ok) return new(null, resolved.Error);
            repository = resolved.Value;
        }

        ScratchAllocation? scratch = null;
        if (placement.Scratch is { } requestedScratch)
        {
            if (_scratch is null) return Unavailable("scratch");
            var resolved = await _scratch.PrepareAsync(requestedScratch, ct);
            if (!resolved.Ok) return new(null, resolved.Error);
            scratch = resolved.Value;
        }

        string? workingDirectory;
        if (repository is not null) workingDirectory = repository.WorkingDirectory;
        else if (workspace is { Kind: WorkspaceResolution.Mounted }) workingDirectory = workspace.Path;
        else if (workspace is not null || placement.ScratchAs == PlacementRequest.ScratchAsCwd)
        {
            // An Agent without a mounted workspace works in its scratch folder, where Nova writes the
            // disposable workspace on node zero; scratchAs cwd asks for exactly that folder.
            if (scratch is null)
                return Resolution<ResolvedPlacement>.Fail(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                    "The Agent has no workspace on this node; placement.scratch gives it a working folder");
            workingDirectory = scratch.Path;
        }
        else workingDirectory = null;

        var references = new List<ResolvedReference>();
        for (var index = 0; index < (placement.References?.Count ?? 0); index++)
        {
            var reference = await ResolveReferenceAsync(placement.References![index], index, nodeId, legacyNode, host, ct);
            if (reference.Error is not null) return new(null, reference.Error);
            references.Add(reference.Value!);
        }

        var addDirs = route == PlacementRoute.Execute && scratch is not null
            && placement.ScratchAs == PlacementRequest.ScratchAsAddDir
                ? new[] { scratch.Path }
                : null;
        return Resolution<ResolvedPlacement>.Of(new ResolvedPlacement(placement, nodeId, workingDirectory, scratch?.Path,
            scratch?.Environment, addDirs, repository?.RepositoryId, repository, workspace,
            references.Count == 0 ? null : references));
    }

    /// <summary>
    /// A reference as a read-only folder on the node. A reference that is unknown, inactive, not on this node or
    /// unreadable answers one and the same 404 <c>reference_not_found</c> naming its place in the array, so an
    /// answer never tells which ids exist or why one cannot be read; only a resolver that cannot answer is 503.
    /// </summary>
    private async Task<Resolution<ResolvedReference>> ResolveReferenceAsync(PlacementReference reference, int index,
        string nodeId, bool legacyNode, IExecutionHost host, CancellationToken ct)
    {
        var member = $"placement.references[{index}]";
        Resolution<ResolvedReference> NotFound() => new(null, new PlacementError(StatusCodes.Status404NotFound,
            "reference_not_found", $"{member} does not name an entity this node can read",
            Fields: new Dictionary<string, string> { [member] = "not found" }));

        if (reference.Workspace is { } workspace)
        {
            if (_workspaces is null) return Unavailable<ResolvedReference>("workspace");
            var resolved = await _workspaces.ResolveAsync(workspace, ct);
            if (!resolved.Ok) return resolved.Error!.Status == StatusCodes.Status503ServiceUnavailable ? new(null, resolved.Error) : NotFound();
            return resolved.Value is { Kind: WorkspaceResolution.Mounted, Path: { } path }
                ? Resolution<ResolvedReference>.Of(new("workspace", workspace.AgentId, null, null, path))
                : NotFound();
        }

        var repository = reference.Repository!;
        if (_repositories is null) return Unavailable<ResolvedReference>("repository");
        var folder = await _repositories.ResolveAsync(repository, nodeId, legacyNode, host, ct);
        if (!folder.Ok) return folder.Error!.Status == StatusCodes.Status503ServiceUnavailable ? new(null, folder.Error) : NotFound();
        return Resolution<ResolvedReference>.Of(new("repository", repository.RepositoryId, folder.Value!.CheckoutId,
            repository.Branch, folder.Value.WorkingDirectory));
    }

    /// <summary>
    /// Automatic (design 2.3): the repository's checkout node when it has exactly one and the caller may use it,
    /// else the capability's default node, else the local node (the first node that can serve, node zero first).
    /// With one node nothing is asked: every step yields node zero.
    /// </summary>
    private async Task<(string NodeId, PlacementError? Error)> RouteAsync(PlacementRequest placement, ClaimsPrincipal? caller,
        CancellationToken ct)
    {
        if (_nodes is null) return (INodeRegistry.LocalAlias, null);
        var remoteNodes = _nodes.Nodes.Any(node => node.Kind != NodeKind.Local);
        if (remoteNodes && placement.Repository is { } repository && _repositories is not null)
        {
            var holders = await _repositories.CheckoutNodesAsync(repository.RepositoryId, ct);
            if (holders.Error is not null) return ("", holders.Error);
            var ids = holders.Value!.Select(id => id is null ? _nodes.LegacyNodeId : id).Distinct(StringComparer.Ordinal).ToList();
            if (ids is [var only] && UsableNode(only, caller) is { } holder) return (holder, null);
        }
        if (_defaultNode?.Invoke() is { Length: > 0 } configured && UsableNode(configured, caller) is { } preferred)
            return (preferred, null);
        return (NodeAccess.RouteAutomatic(caller, _nodes, node => NodeAccess.HostAvailable(_nodes, node)), null);
    }

    /// <summary>The node's id when the caller may place on it and it can run work now; else null (the chain moves on).</summary>
    private string? UsableNode(string id, ClaimsPrincipal? caller)
    {
        var node = _nodes!.Find(id);
        return node is not null && NodeAccess.Allows(caller, node, NodeUse.Place) && NodeAccess.HostAvailable(_nodes, node)
            ? node.Id
            : null;
    }

    private static Resolution<T> Unavailable<T>(string member) where T : class => Resolution<T>.Fail(
        StatusCodes.Status503ServiceUnavailable, "placement_resolution_unavailable",
        $"Placement resolution is unavailable: this RedCompute has no {member} resolver (no RedLeaf URL)");

    private static Resolution<ResolvedPlacement> Unavailable(string member) => Resolution<ResolvedPlacement>.Fail(
        StatusCodes.Status503ServiceUnavailable, "placement_resolution_unavailable",
        $"Placement resolution is unavailable: this RedCompute has no {member} resolver (no RedLeaf URL)");
}
