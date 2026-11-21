using System.Security.Claims;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.Nodes;

/// <summary>What a caller does with a node: read it, or run work on it.</summary>
public enum NodeUse
{
    /// <summary>List it, read it, see its id in an answer.</summary>
    See,

    /// <summary>Name it in <c>placement.nodeId</c>, start or stop a capability on it, or be routed to it.</summary>
    Place,
}

/// <summary>
/// The one rule for who may see a node and who may run work on it (Phase 4 design, section 1; D11: owner
/// and admins). Node zero is everyone's, for both. A remote node is decided from a signed subject: its
/// owner and admins may see it and place on it. An Agent's execution token carries the subject of the
/// person it works for, so it counts as that person. The kernel itself (<c>service:redleaf</c>), the source
/// of the node entities, sees every node but places only on node zero: work placed without a person runs
/// on the server. The anonymous local identity (<c>LocalDefault</c>, <c>local-user</c>), <c>system</c>, other
/// services and anonymous callers have no subject, whatever roles they carry, so they see and place on node
/// zero only. A node a caller may not see is answered exactly as an unknown id.
/// </summary>
public static class NodeAccess
{
    public static bool Allows(ClaimsPrincipal? caller, NodeDescriptor node, NodeUse use)
    {
        if (node.Kind == NodeKind.Local) return true;
        if (caller is null) return false;
        if (SettingsEndpoints.IsRedLeafService(caller)) return use == NodeUse.See;
        if (Subject(caller) is not { } subject) return false;
        return Owns(node, subject) || IsAdmin(caller);
    }

    /// <summary>
    /// The nodes Automatic may route this caller's work to, in preference order: node zero first, then the
    /// remote nodes the caller's person owns, by name then id. Never someone else's node, even for an admin
    /// (D3: an admin who wants another person's machine names it).
    /// </summary>
    public static IReadOnlyList<NodeDescriptor> AutomaticCandidates(ClaimsPrincipal? caller, INodeRegistry nodes)
    {
        var candidates = new List<NodeDescriptor>();
        if (nodes.Find(nodes.LocalNodeId) is { } zero) candidates.Add(zero);
        var subject = caller is null || SettingsEndpoints.IsRedLeafService(caller) ? null : Subject(caller);
        if (subject is null) return candidates;
        candidates.AddRange(nodes.Nodes
            .Where(node => node.Kind != NodeKind.Local && Owns(node, subject) && Allows(caller, node, NodeUse.Place))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Id, StringComparer.Ordinal));
        return candidates;
    }

    /// <summary>
    /// Where Automatic sends the work: the first candidate that can serve it, else node zero. Node zero comes first
    /// and always serves, so the answer is node zero: the GPU preference (design 3.3, step 3: the person's own node
    /// with a running GPU runtime) is not in this release, and a remote node runs work only when it is named.
    /// </summary>
    public static string RouteAutomatic(ClaimsPrincipal? caller, INodeRegistry nodes, Func<NodeDescriptor, bool> canServe)
        => AutomaticCandidates(caller, nodes).FirstOrDefault(canServe)?.Id ?? nodes.LocalNodeId;

    /// <summary>True when the node's host can run work now: node zero always; a remote node when the registry gives a host.</summary>
    public static bool HostAvailable(INodeRegistry nodes, NodeDescriptor node)
    {
        if (node.Kind == NodeKind.Local) return true;
        try
        {
            nodes.HostFor(node.Id);
            return true;
        }
        catch (NodeUnavailableException)
        {
            return false;
        }
    }

    private static bool Owns(NodeDescriptor node, string subject)
        => node.OwnerUserId is { } owner && string.Equals(owner, subject, StringComparison.OrdinalIgnoreCase);

    private static bool IsAdmin(ClaimsPrincipal caller)
        => ExecutionIdentityClaims.ReadRoles(caller).Contains("admin", StringComparer.OrdinalIgnoreCase);

    /// <summary>The signed subject a node is decided for, or null (anonymous, LocalDefault, local-user, system, a service).</summary>
    internal static string? Subject(ClaimsPrincipal caller)
    {
        if (caller.Identity?.IsAuthenticated != true
            || string.Equals(caller.Identity.AuthenticationType, ConfidentialResourcePolicy.LocalDefaultAuthenticationType, StringComparison.Ordinal))
            return null;
        var subject = caller.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject is "local-user" or "system"
            || subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase))
            return null;
        return subject;
    }
}
