using Microsoft.AspNetCore.Http;
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
    WorkspaceResolution? Workspace);

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

    public PlacementResolver(INodeRegistry? nodes, IWorkspaceResolver? workspaces, IHostScratch? scratch,
        RepositoryPlacementResolver? repositories)
    {
        _nodes = nodes;
        _workspaces = workspaces;
        _scratch = scratch;
        _repositories = repositories;
    }

    public async Task<Resolution<ResolvedPlacement>> ResolveAsync(PlacementRequest placement, PlacementRoute route,
        CancellationToken ct = default)
    {
        if (PlacementParser.ResolveNode(placement, _nodes, out var requested) is { } nodeError)
            return new(null, nodeError);
        // Phase 2 has one node, so every routing step (design 2.3) yields it.
        var nodeId = requested ?? _nodes?.LocalNodeId ?? INodeRegistry.LocalAlias;
        var host = _nodes?.HostFor(nodeId) ?? PluginSdk.Hosting.Local.LocalExecutionHost.Shared;
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

        var addDirs = route == PlacementRoute.Execute && scratch is not null
            && placement.ScratchAs == PlacementRequest.ScratchAsAddDir
                ? new[] { scratch.Path }
                : null;
        return Resolution<ResolvedPlacement>.Of(new ResolvedPlacement(placement, nodeId, workingDirectory, scratch?.Path,
            scratch?.Environment, addDirs, repository?.RepositoryId, repository, workspace));
    }

    private static Resolution<ResolvedPlacement> Unavailable(string member) => Resolution<ResolvedPlacement>.Fail(
        StatusCodes.Status503ServiceUnavailable, "placement_resolution_unavailable",
        $"Placement resolution is unavailable: this RedCompute has no {member} resolver (no RedLeaf URL)");
}
