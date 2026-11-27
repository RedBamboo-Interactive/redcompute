namespace RedCompute.PluginSdk;

/// <summary>
/// RedLeaf's MCP gateway, the managed MCP server, named <see cref="ServerName"/>.
/// It authenticates with the session's own execution token, so a launch without one gets no MCP server at
/// all, and the token is always referenced through <see cref="TokenVariable"/> where the harness allows it,
/// never written into a command line. Inherited user, account and project servers stay off (P0).
/// </summary>
public static class LeafMcpGateway
{
    public const string ServerName = "leaf";
    public const string Path = "/api/mcp";
    public const string TokenVariable = "REDLEAF_EXECUTION_TOKEN";

    /// <summary>
    /// The gateway URL for a launch whose environment carries a suite execution token and RedLeaf's URL
    /// (<see cref="SessionScratch.Environment"/> sets both together), else null: no token, no gateway.
    /// </summary>
    public static string? UrlFor(IReadOnlyDictionary<string, string>? environment)
        => environment is not null
            && environment.TryGetValue(TokenVariable, out var token) && !string.IsNullOrWhiteSpace(token)
            && environment.TryGetValue(SessionScratch.RedLeafUrlVariable, out var redLeaf) && !string.IsNullOrWhiteSpace(redLeaf)
                ? redLeaf.TrimEnd('/') + Path
                : null;

    /// <summary>The token itself, for a harness that cannot reference an environment variable in a header.</summary>
    public static string? TokenFor(IReadOnlyDictionary<string, string>? environment)
        => environment is not null && environment.TryGetValue(TokenVariable, out var token) && !string.IsNullOrWhiteSpace(token)
            ? token
            : null;
}
