using System.Text.Json;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Runtimes;

/// <summary>The runtime kinds the agent knows (Phase 4 design 2.4). Each has its own route allowlist.</summary>
public static class RuntimeKinds
{
    public const string ComfyUi = "comfyui";

    public static IReadOnlyList<string> All { get; } = [ComfyUi, SessionRuntimeProtocol.Kind];

    /// <summary>What a kind serves, for <c>runtime.list</c>.</summary>
    public static IReadOnlyList<string> Capabilities(string kind) => kind switch
    {
        ComfyUi => ["image-gen"],
        SessionRuntimeProtocol.Kind => [SessionRuntimeProtocol.Kind],
        _ => [],
    };
}

/// <summary>
/// One runtime the node's owner registered with <c>leaf-node runtime add</c> (design 2.4). Only the owner, on this
/// machine, adds, edits or removes one; the server can start, stop and reach it, never change it. A spawn-mode
/// runtime is launched from the shared recipe (<see cref="RuntimeRecipes"/>) with these settings; an attach-mode one
/// is the owner's own ComfyUI, which the agent only probes and forwards to.
/// </summary>
public sealed record RuntimeRegistration
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public required HostRuntimeManagement Managed { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>The runtime's own port on 127.0.0.1. The server never names a host or a port.</summary>
    public required int Port { get; init; }

    /// <summary>What the owner says it runs on: <c>cpu</c>, <c>cuda:0</c>, … (reported, never trusted for access).</summary>
    public string Accelerator { get; init; } = "cpu";

    // ---- spawn mode (ComfyUI): the owner's local settings, fed to RuntimeRecipes.ComfyUi
    public string? ServerPath { get; init; }
    public string? VenvPath { get; init; }
    public string? WslDistro { get; init; }
    public string? ServerArgs { get; init; }
    public string? ModelsDirectory { get; init; }

    /// <summary>
    /// ComfyUI runs the node types installed in it, whatever a graph asks; the owner accepted that with
    /// <c>--accept-graphs-run-installed-nodes</c> (design 2.5). A ComfyUI without it is never registered.
    /// </summary>
    public bool AcceptedGraphsRunInstalledNodes { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Id : Label!;

    /// <summary>Null when the registration is usable, else why not.</summary>
    public string? Validate()
    {
        if (!ForwardOpen.IsRuntimeId(Id)) return $"A runtime id is 1-{ForwardOpen.MaxRuntimeLength} of a-z, 0-9, '.', '_', '@' or '-', starting with a letter or digit.";
        if (!RuntimeKinds.All.Contains(Kind)) return $"Unknown runtime kind '{Kind}' (this agent knows: {string.Join(", ", RuntimeKinds.All)}).";
        if (Port is < 1 or > 65535) return "The port is 1-65535.";
        if (Kind == SessionRuntimeProtocol.Kind && Managed != HostRuntimeManagement.Attach)
            return "An AI Session runtime must be attached to an owner-run loopback service.";
        if (Kind == RuntimeKinds.ComfyUi && !AcceptedGraphsRunInstalledNodes)
            return "A ComfyUI runs whatever node types are installed in it, including ones that run code. "
                   + "Add it with --accept-graphs-run-installed-nodes to say you accept that.";
        if (Managed == HostRuntimeManagement.Spawn && Kind == RuntimeKinds.ComfyUi && string.IsNullOrWhiteSpace(ServerPath))
            return "A ComfyUI the agent starts needs --server-path (the folder with main.py); use --attach for one you run yourself.";
        if (Label is { Length: > 80 }) return "A label is at most 80 characters.";
        return null;
    }
}

/// <summary><c>runtimes.json</c>: the owner's registrations, written atomically through the node's host.</summary>
public sealed class RuntimeRegistry(IExecutionHost host, NodePaths paths)
{
    private readonly object _gate = new();

    private sealed record File(List<RuntimeRegistration> Runtimes);

    public IReadOnlyList<RuntimeRegistration> Load()
    {
        lock (_gate)
        {
            var file = host.Path(paths.RuntimesFile);
            if (!host.Files.FileExists(file)) return [];
            try
            {
                var runtimes = JsonSerializer.Deserialize<File>(host.Files.ReadAllText(file), NodeStateStore.Json)?.Runtimes ?? [];
                return runtimes.Where(r => r.Validate() is null).ToList();
            }
            catch (JsonException error)
            {
                throw new NodeCommandException("bad_runtimes", $"{paths.RuntimesFile} is not readable ({error.Message}).",
                    NodeExitCodes.Configuration, error);
            }
        }
    }

    public void Save(IEnumerable<RuntimeRegistration> runtimes)
    {
        lock (_gate)
        {
            host.Files.CreateDirectory(host.Path(paths.Data));
            host.Files.ReplaceAtomically(host.Path(paths.RuntimesFile),
                JsonSerializer.Serialize(new File(runtimes.OrderBy(r => r.Id, StringComparer.Ordinal).ToList()), NodeStateStore.Json));
        }
    }

    public RuntimeRegistration Add(RuntimeRegistration runtime)
    {
        if (runtime.Validate() is { } error) throw new NodeCommandException("bad_runtime", error, NodeExitCodes.Usage);
        lock (_gate)
        {
            var all = Load().ToList();
            if (all.Any(r => r.Id == runtime.Id))
                throw new NodeCommandException("runtime_exists", $"A runtime '{runtime.Id}' is already registered. Remove it first.");
            if (all.Any(r => r.Port == runtime.Port))
                throw new NodeCommandException("port_taken", $"Runtime '{all.First(r => r.Port == runtime.Port).Id}' already uses port {runtime.Port}.");
            all.Add(runtime);
            Save(all);
            return runtime;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var all = Load().ToList();
            if (all.RemoveAll(r => r.Id == id) == 0) return false;
            Save(all);
            return true;
        }
    }

    public RuntimeRegistration SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var all = Load().ToList();
            var index = all.FindIndex(r => r.Id == id);
            if (index < 0) throw new NodeCommandException("runtime_unknown", $"No runtime '{id}' is registered here.");
            all[index] = all[index] with { Enabled = enabled };
            Save(all);
            return all[index];
        }
    }
}
