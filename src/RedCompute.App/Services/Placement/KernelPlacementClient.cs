using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Services.Placement;

/// <summary>A resolved reference, or why it could not be resolved (never a path in the error).</summary>
public sealed record Resolution<T>(T? Value, PlacementError? Error) where T : class
{
    public bool Ok => Error is null && Value is not null;

    public static Resolution<T> Of(T value) => new(value, null);

    public static Resolution<T> Fail(int status, string code, string message) => new(null, new PlacementError(status, code, message));
}

/// <summary>An Agent's workspace on a node: mounted at a path, or the Agent has none.</summary>
/// <param name="Kind"><see cref="Mounted"/> or <see cref="None"/>.</param>
public sealed record WorkspaceResolution(string Kind, string? Path)
{
    public const string Mounted = "mounted";
    public const string None = "none";
}

/// <summary>A prepared scratch folder on a node and the environment that points a process at it.</summary>
public sealed record ScratchAllocation(string Path, IReadOnlyDictionary<string, string> Environment);

/// <summary>Resolves an Agent's workspace on the node whose workspace volume it owns.</summary>
public interface IWorkspaceResolver
{
    Task<Resolution<WorkspaceResolution>> ResolveAsync(WorkspacePlacement workspace, CancellationToken ct = default);
}

/// <summary>
/// Prepares a kernel scratch allocation on a node: the folder exists, carries its execution marker
/// (the daily sweep's keep-alive) and is returned with its environment, exactly as the kernel's
/// <c>PrepareExecution</c> gives it to callers today.
/// </summary>
public interface IHostScratch
{
    Task<Resolution<ScratchAllocation>> PrepareAsync(ScratchPlacement scratch, CancellationToken ct = default);
}

/// <summary>
/// Node zero's workspace and scratch, resolved by the kernel that owns them (K2:
/// <c>POST api/compute/placement/resolve</c>, RedCompute's system identity, as repository validation uses).
/// <para>Contract (one reference per call):</para>
/// <list type="bullet">
/// <item><c>{"workspace":{"agentId"}}</c> → 200 <c>{"workspace":{"kind":"mounted","path"}}</c> (mounted if needed) or
/// <c>{"workspace":{"kind":"none"}}</c>; 404 <c>agent_not_found</c>; 503 <c>workspace_unavailable</c>.</item>
/// <item><c>{"scratch":{"owner","executionKey"}}</c> → 200 <c>{"scratch":{"path","environment":{...}}}</c>;
/// 422 <c>validation_failed</c>; 503 <c>scratch_unavailable</c>.</item>
/// </list>
/// A kernel without the route (404 without one of those codes), an unreachable kernel and any malformed
/// answer are 503 <c>placement_resolution_unavailable</c>: the caller keeps its legacy fields.
/// </summary>
public sealed class KernelPlacementClient : IWorkspaceResolver, IHostScratch
{
    internal const string Route = "api/compute/placement/resolve";

    private static readonly string[] ScratchVariables = ["REDLEAF_SCRATCH_ROOT", "REDLEAF_SCRATCH_DIR", "TEMP", "TMP", "TMPDIR"];

    private readonly HttpClient _http;

    public KernelPlacementClient(string redLeafBaseUrl, JwtService jwtService)
        : this(new HttpClient(new BadgeBearerHandler(
            () => jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"])))
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        })
    {
    }

    internal KernelPlacementClient(HttpClient http) => _http = http;

    public async Task<Resolution<WorkspaceResolution>> ResolveAsync(WorkspacePlacement workspace, CancellationToken ct = default)
    {
        var (answer, error) = await PostAsync(new JsonObject
        {
            ["workspace"] = new JsonObject { ["agentId"] = workspace.AgentId.ToString("D") },
        }, ["agent_not_found", "workspace_unavailable"], ct);
        if (error is not null) return new(null, error);

        var value = answer!["workspace"];
        var kind = Text(value, "kind");
        var path = Text(value, "path");
        return kind switch
        {
            WorkspaceResolution.Mounted when IsAbsolute(path) => Resolution<WorkspaceResolution>.Of(new(kind, path)),
            WorkspaceResolution.None => Resolution<WorkspaceResolution>.Of(new(kind, null)),
            _ => Malformed<WorkspaceResolution>("workspace"),
        };
    }

    public async Task<Resolution<ScratchAllocation>> PrepareAsync(ScratchPlacement scratch, CancellationToken ct = default)
    {
        var (answer, error) = await PostAsync(new JsonObject
        {
            ["scratch"] = new JsonObject { ["owner"] = scratch.Owner, ["executionKey"] = scratch.ExecutionKey },
        }, ["validation_failed", "scratch_unavailable"], ct);
        if (error is not null) return new(null, error);

        var value = answer!["scratch"];
        var path = Text(value, "path");
        if (!IsAbsolute(path) || value?["environment"] is not JsonObject environment)
            return Malformed<ScratchAllocation>("scratch");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, item) in environment)
        {
            if (item is not JsonValue text || !text.TryGetValue<string>(out var s)) return Malformed<ScratchAllocation>("scratch");
            variables[key] = s;
        }
        if (ScratchVariables.Any(name => !variables.ContainsKey(name))) return Malformed<ScratchAllocation>("scratch");
        return Resolution<ScratchAllocation>.Of(new(path!, variables));
    }

    /// <summary>The kernel's answer, or the error to report: its own code when it is one it may send.</summary>
    private async Task<(JsonObject? Answer, PlacementError? Error)> PostAsync(JsonObject request, string[] codes,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(Route, request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, Unavailable($"RedLeaf could not be reached ({ex.GetType().Name})"));
        }

        using (response)
        {
            JsonObject? body = null;
            try { body = await response.Content.ReadFromJsonAsync<JsonObject>(ct); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }

            if (response.IsSuccessStatusCode)
                return body is null ? (null, Unavailable("RedLeaf answered without a placement document")) : (body, null);

            var code = Text(body, "error");
            if (code is not null && codes.Contains(code, StringComparer.Ordinal))
                return (null, new PlacementError((int)response.StatusCode, code, Text(body, "message") ?? code));
            return (null, response.StatusCode == HttpStatusCode.NotFound
                ? Unavailable("this RedLeaf does not resolve placements yet")
                : Unavailable($"RedLeaf answered {(int)response.StatusCode}"));
        }
    }

    private static PlacementError Unavailable(string reason) => new(StatusCodes.Status503ServiceUnavailable,
        "placement_resolution_unavailable", $"Placement resolution is unavailable: {reason}");

    private static Resolution<T> Malformed<T>(string member) where T : class
        => new(null, Unavailable($"RedLeaf answered a malformed {member}"));

    private static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static string? Text(JsonNode? node, string member)
        => node is JsonObject obj && obj[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
