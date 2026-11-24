using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Placement;

/// <summary>The route a placement arrives on: each accepts a different subset of members.</summary>
public enum PlacementRoute
{
    /// <summary><c>POST /ai-session/sessions</c>: every member; <c>scratchAs</c> env or cwd.</summary>
    Session,

    /// <summary><c>POST /ai-session/execute</c>: every member; <c>scratchAs</c> env, add-dir or cwd.</summary>
    Execute,

    /// <summary><c>POST /{slug}/generate</c>: <c>nodeId</c> only in Phase 2.</summary>
    Generate,
}

/// <summary>
/// Where a piece of work runs and on what, by reference (Phase 2 design, section 2.1): a node, at most
/// one of an Agent workspace or a repository checkout, and a kernel scratch allocation. It carries ids
/// only, never paths: each reference is resolved on the node's own host. RedCompute records it
/// with the session; resolved paths are display-only.
/// </summary>
/// <param name="NodeId">A registry node id, the alias <c>local</c>, or null to route (2.3).</param>
/// <param name="ScratchAs">What the caller does with the scratch folder: env (default), add-dir or cwd.</param>
/// <param name="References">
/// Entities the work may read, by id: each a repository (checkout, branch, subPath) or an Agent workspace.
/// Validated and resolved to read-only paths in the session's placement document; nothing is mounted.
/// </param>
public sealed record PlacementRequest(
    string? NodeId,
    WorkspacePlacement? Workspace,
    RepositoryPlacement? Repository,
    ScratchPlacement? Scratch,
    string ScratchAs = PlacementRequest.ScratchAsEnv,
    IReadOnlyList<PlacementReference>? References = null)
{
    /// <summary>The body member that carries it.</summary>
    public const string Member = "placement";

    public const string ScratchAsEnv = "env";
    public const string ScratchAsAddDir = "add-dir";
    public const string ScratchAsCwd = "cwd";

    /// <summary>The canonical reference form: ids in their canonical text, absent members omitted.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["nodeId"] = NodeId };
        if (Workspace is not null)
            json["workspace"] = new JsonObject { ["agentId"] = Workspace.AgentId.ToString("D") };
        if (Repository is not null)
        {
            var repository = new JsonObject { ["repositoryId"] = Repository.RepositoryId.ToString("D") };
            if (Repository.CheckoutId is { } checkout) repository["checkoutId"] = checkout.ToString("D");
            if (Repository.Branch is not null) repository["branch"] = Repository.Branch;
            if (Repository.SubPath is not null) repository["subPath"] = Repository.SubPath;
            json["repository"] = repository;
        }
        if (Scratch is not null)
        {
            json["scratch"] = new JsonObject { ["owner"] = Scratch.Owner, ["executionKey"] = Scratch.ExecutionKey };
            json["scratchAs"] = ScratchAs;
        }
        if (References is { Count: > 0 })
            json["references"] = new JsonArray(References.Select(reference => (JsonNode)reference.ToJson()).ToArray());
        return json;
    }
}

/// <summary>
/// One entity the work may read: exactly one of an Agent workspace or a repository checkout (the same
/// member shapes as the placement's own). Read-only by design.
/// </summary>
public sealed record PlacementReference(WorkspacePlacement? Workspace, RepositoryPlacement? Repository)
{
    public JsonObject ToJson()
    {
        if (Workspace is not null)
            return new JsonObject { ["workspace"] = new JsonObject { ["agentId"] = Workspace.AgentId.ToString("D") } };
        var repository = new JsonObject { ["repositoryId"] = Repository!.RepositoryId.ToString("D") };
        if (Repository.CheckoutId is { } checkout) repository["checkoutId"] = checkout.ToString("D");
        if (Repository.Branch is not null) repository["branch"] = Repository.Branch;
        if (Repository.SubPath is not null) repository["subPath"] = Repository.SubPath;
        return new JsonObject { ["repository"] = repository };
    }
}

/// <summary>An Agent's workspace, resolved by the owner of the node's workspace volume.</summary>
public sealed record WorkspacePlacement(Guid AgentId);

/// <summary>
/// A Repository's checkout on the node, optionally a branch's linked worktree of it and a relative
/// folder inside (Code's nested repositories).
/// </summary>
public sealed record RepositoryPlacement(Guid RepositoryId, Guid? CheckoutId, string? Branch, string? SubPath);

/// <summary>
/// A kernel scratch allocation, the same pair callers pass the kernel's <c>PrepareExecution</c> today.
/// The kernel maps both to folder names, so any non-empty text is valid here.
/// </summary>
public sealed record ScratchPlacement(string Owner, string ExecutionKey);

/// <summary>A refused placement: status, a stable code, and the references involved, never paths.</summary>
public sealed record PlacementError(int Status, string Code, string Message, string? NodeId = null,
    Dictionary<string, string>? Fields = null, string? Reason = null)
{
    public IResult ToResult() => Results.Json(new PlacementErrorResponse
    {
        Error = Code,
        Message = Message,
        NodeId = NodeId,
        Reason = Reason,
        Fields = Fields,
    }, statusCode: Status);

    /// <summary>
    /// A known node that cannot run work (Phase 2 design, error table): 409 <c>node_offline</c> (retryable)
    /// when its link is down, else 409 <c>node_not_eligible</c> with the registry's reason.
    /// </summary>
    public static PlacementError Unavailable(NodeUnavailableException error) => error.Retryable
        ? new(StatusCodes.Status409Conflict, "node_offline",
            $"Node '{error.NodeId}' is offline; try again when it is back", NodeId: error.NodeId, Reason: error.Reason)
        : new(StatusCodes.Status409Conflict, "node_not_eligible",
            $"Node '{error.NodeId}' cannot run work: {error.Reason}", NodeId: error.NodeId, Reason: error.Reason);

    internal static PlacementError Invalid(string member, string message) => new(
        StatusCodes.Status422UnprocessableEntity, "validation_failed", $"{member} {message}",
        Fields: new Dictionary<string, string> { [member] = message });
}

/// <summary>The error body every route already uses, plus the node a placement named.</summary>
public sealed class PlacementErrorResponse : ErrorResponse
{
    [JsonPropertyName("nodeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeId { get; init; }

    /// <summary>Why the node cannot run work (<c>offline</c>, <c>revoked</c>, <c>not_confirmed</c>, …), on a 409.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>Parses and validates <c>placement</c>, and resolves its node against the registry.</summary>
public static class PlacementParser
{
    private const int MaxTextLength = 512;

    private const int MaxReferences = 16;

    private static readonly string[] TopMembers = ["nodeId", "workspace", "repository", "scratch", "scratchAs", "references"];

    /// <summary>
    /// Reads <c>placement</c> from a request body. Absent or null is no placement (true, null). A
    /// malformed one is a 422 naming the member. Members a route does not take are refused:
    /// <c>/{slug}/generate</c> takes only <c>nodeId</c> (422 <c>placement_member_not_supported</c>), and a
    /// conversation-only session allocates its own workspace, so it takes only <c>nodeId</c> too.
    /// </summary>
    public static bool TryParse(JsonElement body, PlacementRoute route, bool conversationOnly,
        out PlacementRequest? placement, out PlacementError? error)
    {
        placement = null;
        error = null;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(PlacementRequest.Member, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return true;
        return TryParseValue(value, route, conversationOnly, out placement, out error);
    }

    /// <summary>The same rules for a value already taken out of the body.</summary>
    public static bool TryParseValue(JsonElement value, PlacementRoute route, bool conversationOnly,
        out PlacementRequest? placement, out PlacementError? error)
    {
        placement = null;
        error = null;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object)
            return Fail(out error, PlacementError.Invalid("placement", "must be an object"));

        foreach (var property in value.EnumerateObject())
        {
            if (!TopMembers.Contains(property.Name, StringComparer.Ordinal))
                return Fail(out error, PlacementError.Invalid($"placement.{property.Name}", "is not a placement member"));
            if (property.Value.ValueKind == JsonValueKind.Null || property.Name == "nodeId") continue;
            if (route == PlacementRoute.Generate)
                return Fail(out error, new PlacementError(StatusCodes.Status422UnprocessableEntity,
                    "placement_member_not_supported",
                    $"placement.{property.Name} is not supported on this route; only placement.nodeId is",
                    Fields: new Dictionary<string, string> { [$"placement.{property.Name}"] = "not supported on this route" }));
            if (conversationOnly)
                return Fail(out error, PlacementError.Invalid($"placement.{property.Name}",
                    "must be omitted: conversation-only sessions allocate their own isolated workspace"));
        }

        if (!TryText(value, "nodeId", "placement.nodeId", required: false, out var nodeId, out error)) return false;
        if (!TryWorkspace(value, "placement", out var workspace, out error)) return false;
        if (!TryRepository(value, "placement", out var repository, out error)) return false;
        if (!TryScratch(value, out var scratch, out error)) return false;
        if (!TryReferences(value, out var references, out error)) return false;
        if (!TryText(value, "scratchAs", "placement.scratchAs", required: false, out var scratchAs, out error)) return false;

        if (workspace is not null && repository is not null)
            return Fail(out error, PlacementError.Invalid("placement",
                "names both a workspace and a repository; at most one decides the working directory"));

        if (scratchAs is not null)
        {
            if (scratch is null)
                return Fail(out error, PlacementError.Invalid("placement.scratchAs", "needs placement.scratch"));
            if (scratchAs is not (PlacementRequest.ScratchAsEnv or PlacementRequest.ScratchAsAddDir or PlacementRequest.ScratchAsCwd))
                return Fail(out error, PlacementError.Invalid("placement.scratchAs", "must be env, add-dir or cwd"));
            if (scratchAs == PlacementRequest.ScratchAsAddDir && route != PlacementRoute.Execute)
                return Fail(out error, PlacementError.Invalid("placement.scratchAs",
                    "add-dir applies to /ai-session/execute only; a session reaches its scratch through scratchDir"));
            if (scratchAs == PlacementRequest.ScratchAsCwd && (workspace is not null || repository is not null))
                return Fail(out error, PlacementError.Invalid("placement.scratchAs",
                    "cwd makes the scratch folder the working directory, so it cannot be combined with a workspace or a repository"));
        }

        placement = new PlacementRequest(nodeId, workspace, repository, scratch, scratchAs ?? PlacementRequest.ScratchAsEnv, references);
        return true;
    }

    /// <summary>
    /// The node a placement names, as the registry knows it and as the caller may use it: the local node for
    /// <c>local</c>, its own id or the legacy id; 404 <c>node_not_found</c> for an unknown id and, with exactly
    /// the same answer, for a node the caller may not place on (<see cref="NodeAccess"/>), so an answer never
    /// tells which ids exist; 409 <c>node_offline</c> / <c>node_not_eligible</c> for a node the caller may use that
    /// cannot run work (<c>NodeRegistry.UnavailableReason</c>). An eligible remote node resolves; whether a provider
    /// serves the work there is the route's next check (422). Without a registry (hosts and tests with no node
    /// identity) only <c>local</c> is known. A null node id is routed later (<see cref="NodeAccess.RouteAutomatic"/>)
    /// and resolves to null here.
    /// </summary>
    public static PlacementError? ResolveNode(PlacementRequest placement, INodeRegistry? nodes, ClaimsPrincipal? caller,
        out string? nodeId)
    {
        nodeId = null;
        if (placement.NodeId is not { } requested) return null;
        if (nodes is null)
        {
            if (string.Equals(requested, INodeRegistry.LocalAlias, StringComparison.OrdinalIgnoreCase))
            {
                nodeId = INodeRegistry.LocalAlias;
                return null;
            }
        }
        else if (nodes.Find(requested) is { } node)
        {
            if (!NodeAccess.Allows(caller, node, NodeUse.Place)) return NodeNotFound(requested);
            nodeId = node.Id;
            if (node.Kind == NodeKind.Local) return null;
            try
            {
                nodes.HostFor(node.Id);
                return null;
            }
            catch (NodeUnavailableException error)
            {
                nodeId = null;
                return PlacementError.Unavailable(error);
            }
        }
        else if (string.Equals(requested, nodes.LegacyNodeId, StringComparison.Ordinal))
        {
            nodeId = nodes.LocalNodeId;
            return null;
        }
        return NodeNotFound(requested);
    }

    /// <summary>True when <paramref name="resolvedNodeId"/> (what <see cref="ResolveNode"/> found) is a remote node, not node zero.</summary>
    public static bool IsRemote(string? resolvedNodeId, INodeRegistry? nodes)
        => nodes is not null && resolvedNodeId is not null && resolvedNodeId != INodeRegistry.LocalAlias
           && resolvedNodeId != nodes.LocalNodeId;

    /// <summary>
    /// Where a request with <c>placement</c> runs, from the node <see cref="ResolveNode"/> found (null:
    /// no <c>nodeId</c>, so Automatic, which is node zero while no remote node runs work).
    /// </summary>
    public static PlacementResolution Resolution(string? resolvedNodeId, INodeRegistry? nodes)
    {
        var local = nodes?.LocalNodeId ?? INodeRegistry.LocalAlias;
        if (resolvedNodeId is null) return new PlacementResolution(local, IsLocal: true, PlacementResolution.Local);
        var isLocal = resolvedNodeId == INodeRegistry.LocalAlias || resolvedNodeId == nodes?.LocalNodeId;
        return new PlacementResolution(isLocal ? local : resolvedNodeId, isLocal, PlacementResolution.Explicit);
    }

    /// <summary>The one answer for an unknown node and for a node the caller may not use.</summary>
    private static PlacementError NodeNotFound(string requested) => new(StatusCodes.Status404NotFound, "node_not_found",
        $"No node has the id '{requested}'", NodeId: requested);

    private static bool TryWorkspace(JsonElement placement, string prefix, out WorkspacePlacement? workspace, out PlacementError? error)
    {
        workspace = null;
        error = null;
        if (!TryObject(placement, prefix, "workspace", ["agentId"], out var value, out error)) return false;
        if (value is not { } element) return true;
        if (!TryGuid(element, "agentId", $"{prefix}.workspace.agentId", required: true, out var agentId, out error)) return false;
        workspace = new WorkspacePlacement(agentId!.Value);
        return true;
    }

    private static bool TryRepository(JsonElement placement, string prefix, out RepositoryPlacement? repository, out PlacementError? error)
    {
        repository = null;
        error = null;
        if (!TryObject(placement, prefix, "repository", ["repositoryId", "checkoutId", "branch", "subPath"], out var value, out error))
            return false;
        if (value is not { } element) return true;
        if (!TryGuid(element, "repositoryId", $"{prefix}.repository.repositoryId", required: true, out var repositoryId, out error)
            || !TryGuid(element, "checkoutId", $"{prefix}.repository.checkoutId", required: false, out var checkoutId, out error)
            || !TryText(element, "branch", $"{prefix}.repository.branch", required: false, out var branch, out error)
            || !TryText(element, "subPath", $"{prefix}.repository.subPath", required: false, out var subPath, out error))
            return false;
        if (subPath is not null && SubPathProblem(subPath) is { } problem)
            return Fail(out error, PlacementError.Invalid($"{prefix}.repository.subPath", problem));
        repository = new RepositoryPlacement(repositoryId!.Value, checkoutId, branch, subPath);
        return true;
    }

    private static bool TryScratch(JsonElement placement, out ScratchPlacement? scratch, out PlacementError? error)
    {
        scratch = null;
        error = null;
        if (!TryObject(placement, "placement", "scratch", ["owner", "executionKey"], out var value, out error)) return false;
        if (value is not { } element) return true;
        if (!TryText(element, "owner", "placement.scratch.owner", required: true, out var owner, out error)
            || !TryText(element, "executionKey", "placement.scratch.executionKey", required: true, out var key, out error))
            return false;
        scratch = new ScratchPlacement(owner!, key!);
        return true;
    }

    /// <summary>
    /// <c>references</c>: an array of at most 16 objects, each exactly one of <c>workspace</c> or
    /// <c>repository</c> (the placement's own member shapes).
    /// </summary>
    private static bool TryReferences(JsonElement placement, out IReadOnlyList<PlacementReference>? references, out PlacementError? error)
    {
        references = null;
        error = null;
        if (!placement.TryGetProperty("references", out var array) || array.ValueKind == JsonValueKind.Null) return true;
        if (array.ValueKind != JsonValueKind.Array)
            return Fail(out error, PlacementError.Invalid("placement.references", "must be an array"));
        if (array.GetArrayLength() > MaxReferences)
            return Fail(out error, PlacementError.Invalid("placement.references", $"may hold at most {MaxReferences} references"));
        var list = new List<PlacementReference>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var prefix = $"placement.references[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
                return Fail(out error, PlacementError.Invalid(prefix, "must be an object"));
            foreach (var property in item.EnumerateObject())
                if (property.Name is not ("workspace" or "repository"))
                    return Fail(out error, PlacementError.Invalid($"{prefix}.{property.Name}", "is not a reference member"));
            if (!TryWorkspace(item, prefix, out var workspace, out error)) return false;
            if (!TryRepository(item, prefix, out var repository, out error)) return false;
            if ((workspace is null) == (repository is null))
                return Fail(out error, PlacementError.Invalid(prefix, "must name exactly one of workspace or repository"));
            list.Add(new PlacementReference(workspace, repository));
        }
        references = list.Count == 0 ? null : list;
        return true;
    }

    /// <summary>
    /// A relative folder inside the checkout, checked by its text alone (both separators, any host): the
    /// host's own path rules apply it when it is resolved.
    /// </summary>
    internal static string? SubPathProblem(string subPath)
    {
        if (subPath.StartsWith('/') || subPath.StartsWith('\\') || subPath.Contains(':'))
            return "must be relative to the checkout";
        var segments = subPath.Split('/', '\\');
        if (segments.Any(segment => segment is ".."))
            return "must stay inside the checkout";
        if (segments.All(segment => segment.Length == 0 || segment == "."))
            return "must name a folder inside the checkout";
        return null;
    }

    private static bool TryObject(JsonElement parent, string prefix, string name, string[] members, out JsonElement? value, out PlacementError? error)
    {
        value = null;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Object)
            return Fail(out error, PlacementError.Invalid($"{prefix}.{name}", "must be an object"));
        foreach (var property in element.EnumerateObject())
            if (!members.Contains(property.Name, StringComparer.Ordinal))
                return Fail(out error, PlacementError.Invalid($"{prefix}.{name}.{property.Name}", $"is not a {prefix}.{name} member"));
        value = element;
        return true;
    }

    private static bool TryText(JsonElement parent, string name, string member, bool required, out string? text, out PlacementError? error)
    {
        text = null;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return !required || Fail(out error, PlacementError.Invalid(member, "is required"));
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            return Fail(out error, PlacementError.Invalid(member, "must be a non-empty string"));
        var value = element.GetString()!;
        if (value.Length > MaxTextLength || value.Any(char.IsControl))
            return Fail(out error, PlacementError.Invalid(member, $"must be at most {MaxTextLength} characters, without control characters"));
        text = value;
        return true;
    }

    private static bool TryGuid(JsonElement parent, string name, string member, bool required, out Guid? id, out PlacementError? error)
    {
        id = null;
        if (!TryText(parent, name, member, required, out var text, out error)) return false;
        if (text is null) return true;
        if (!Guid.TryParse(text, out var parsed))
            return Fail(out error, PlacementError.Invalid(member, "must be an entity UUID"));
        id = parsed;
        return true;
    }

    private static bool Fail(out PlacementError? error, PlacementError value)
    {
        error = value;
        return false;
    }
}
