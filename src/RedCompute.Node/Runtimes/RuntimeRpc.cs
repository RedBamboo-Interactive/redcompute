using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.NodeLink;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Runtimes;

/// <summary>
/// The <c>runtime.v1</c> control methods (Phase 4 design 2.4). Parameters are exactly the listed members: any other
/// member is <c>invalid_params</c>, so the server can never pass a program, an argument, an environment or a folder.
/// </summary>
public static class RuntimeRpc
{
    public const string List = "runtime.list";
    public const string Start = "runtime.start";
    public const string Stop = "runtime.stop";
    public const string Status = "runtime.status";
    public const string Logs = "runtime.logs";
    public const string Changed = "runtime.changed";

    public static bool Handles(string method) => method is List or Start or Stop or Status or Logs;

    public static async Task<JsonNode> AnswerAsync(RuntimeManager runtimes, string method, JsonElement parameters, CancellationToken ct)
    {
        try
        {
            switch (method)
            {
                case List:
                    Members(parameters, required: [], optional: []);
                    return new JsonArray(runtimes.List().Select(r => (JsonNode)RuntimeManager.Json(r)).ToArray());
                case Start:
                {
                    var id = Members(parameters, required: ["id"], optional: []).Id;
                    var runtime = await runtimes.StartAsync(id, ct);
                    return new JsonObject { ["status"] = runtime.Status.ToString().ToLowerInvariant() };
                }
                case Stop:
                {
                    var read = Members(parameters, required: ["id"], optional: ["graceSeconds"]);
                    var runtime = await runtimes.StopAsync(read.Id, read.Grace, ct);
                    return new JsonObject { ["status"] = runtime.Status.ToString().ToLowerInvariant() };
                }
                case Status:
                {
                    var id = Members(parameters, required: ["id"], optional: []).Id;
                    return runtimes.Status(id) is { } runtime
                        ? RuntimeManager.Json(runtime)
                        : throw new LinkRpcException(HostRuntimeException.Unknown, $"No runtime '{id}' is registered on this node.");
                }
                case Logs:
                {
                    var read = Members(parameters, required: ["id", "tailLines"], optional: []);
                    var lines = runtimes.Logs(read.Id, read.TailLines);
                    return new JsonObject { ["lines"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) };
                }
                default:
                    throw new LinkRpcException(LinkErrorCodes.MethodNotAllowed, $"This node does not do '{method}'.");
            }
        }
        catch (HostRuntimeException error)
        {
            throw new LinkRpcException(error.Code, error.Message);
        }
    }

    private readonly record struct Read(string Id, TimeSpan? Grace, int TailLines);

    private static Read Members(JsonElement parameters, string[] required, string[] optional)
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            if (required.Length == 0) return default;
            throw Invalid($"Parameters: {string.Join(", ", required)}.");
        }
        if (parameters.ValueKind != JsonValueKind.Object) throw Invalid("Parameters are an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = null;
        TimeSpan? grace = null;
        var tail = 0;
        foreach (var property in parameters.EnumerateObject())
        {
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !optional.Contains(property.Name)))
                throw Invalid($"'{property.Name}' is not a parameter here (only {string.Join(", ", required.Concat(optional))}).");
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String || !RedCompute.NodeLink.Forward.ForwardOpen.IsRuntimeId(property.Value.GetString()))
                        throw Invalid("id is a runtime id.");
                    id = property.Value.GetString();
                    break;
                case "graceSeconds":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var seconds) || seconds is < 0 or > 60)
                        throw Invalid("graceSeconds is 0..60.");
                    grace = TimeSpan.FromSeconds(seconds);
                    break;
                case "tailLines":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out tail) || tail is < 1 or > RuntimeManager.MaxLogLines)
                        throw Invalid($"tailLines is 1..{RuntimeManager.MaxLogLines}.");
                    break;
            }
        }
        foreach (var name in required)
            if (!seen.Contains(name)) throw Invalid($"'{name}' is required.");
        return new Read(id ?? "", grace, tail);
    }

    private static LinkRpcException Invalid(string message) => new(LinkErrorCodes.InvalidParams, message);
}
