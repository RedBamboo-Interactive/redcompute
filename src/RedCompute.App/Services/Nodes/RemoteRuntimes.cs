using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// The runtime kinds this server knows (Phase 4, design 2.4, 3.1): what each serves and which provider type drives it.
/// A node's report names a kind and claims capabilities; the server trusts only its own table, so a node can never
/// make itself a candidate for a capability its kind does not serve (an unknown kind serves nothing).
/// </summary>
public static class RemoteRuntimeKinds
{
    public const string ComfyUi = "comfyui";

    /// <summary>The capability slugs a kind serves, as this server decides it.</summary>
    public static IReadOnlyList<string> Capabilities(string kind) => kind switch
    {
        ComfyUi => ["image-gen"],
        _ => [],
    };

    /// <summary>The provider type (<c>ProviderConfig.Type</c>) that drives a kind, or null.</summary>
    public static string? ProviderType(string kind) => kind switch
    {
        ComfyUi => "ComfyUI",
        _ => null,
    };

    public const string NodeOffline = "node_offline";
    public const string NodeNotEligible = "node_not_eligible";
    public const string RuntimeDisabled = "runtime_disabled";
    public const string RuntimeUnknown = "runtime_unknown";

    /// <summary>
    /// The provider name a runtime kind serves under (design 3.1): node zero's provider of that kind's type for the
    /// capability, so a caller's <c>provider</c> means the same on every node; the kind itself when node zero has none.
    /// </summary>
    public static string ProviderName(CapabilityEntry? entry, string kind)
    {
        var type = ProviderType(kind);
        if (entry is not null && type is not null)
            foreach (var (name, config) in entry.Config.Providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (string.Equals(config.Type, type, StringComparison.OrdinalIgnoreCase) && entry.Providers.ContainsKey(name))
                    return name;
        return kind;
    }

    /// <summary>
    /// A remote runtime's status and availability (design 3.1): the reported status while the node is eligible and the
    /// runtime enabled. An offline node's runtimes read Stopped (unavailable, not an error) with <c>node_offline</c>;
    /// an ineligible node's keep their status with <c>node_not_eligible</c>; a disabled runtime is <c>runtime_disabled</c>.
    /// </summary>
    public static (BackendStatus Status, string? Reason) Availability(NodePlacement placement, HostRuntime runtime)
    {
        if (!placement.Eligible)
            return placement.Reason == NodeUnavailableReasons.Offline
                ? (BackendStatus.Stopped, NodeOffline)
                : (runtime.Status, NodeNotEligible);
        return runtime.Enabled ? (runtime.Status, null) : (runtime.Status, RuntimeDisabled);
    }
}

/// <summary>
/// Reads a node's <c>runtime.list</c> entries and <c>runtime.changed</c> pushes (design 2.4). The node is not trusted:
/// every member is bounded, a malformed entry is dropped (never half-read), and the capabilities come from
/// <see cref="RemoteRuntimeKinds"/>, intersected with what the node claims.
/// </summary>
public static partial class RemoteRuntimeReport
{
    /// <summary>The most runtimes a node's list may carry; the rest are ignored.</summary>
    public const int MaxRuntimes = 32;

    /// <summary>The largest <c>runtime.changed</c> a node may push; a bigger one is dropped.</summary>
    public const int MaxEntryBytes = 16 * 1024;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex KindPattern();

    [GeneratedRegex("^(cpu|(cuda|rocm|xpu):[0-9]{1,2}|mps)$")]
    private static partial Regex AcceleratorPattern();

    /// <summary>A <c>runtime.list</c> answer: the well-formed entries, by id, each id once.</summary>
    public static IReadOnlyList<HostRuntime> ParseList(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        var runtimes = new Dictionary<string, HostRuntime>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray().Take(MaxRuntimes))
            if (Parse(item) is { } runtime) runtimes.TryAdd(runtime.Id, runtime);
        return runtimes.Values.OrderBy(runtime => runtime.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>One entry, or null when it is malformed.</summary>
    public static HostRuntime? Parse(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name, int max)
            => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
                ? text.Length <= max && !text.Any(char.IsControl) ? text : null
                : null;

        var id = Text("id", ForwardOpen.MaxRuntimeLength);
        if (!ForwardOpen.IsRuntimeId(id)) return null;
        var kind = Text("kind", 32);
        if (kind is null || !KindPattern().IsMatch(kind)) return null;
        HostRuntimeManagement managed;
        switch (Text("managed", 8))
        {
            case "spawn": managed = HostRuntimeManagement.Spawn; break;
            case "attach": managed = HostRuntimeManagement.Attach; break;
            default: return null;
        }
        if (!entry.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        if (Status(Text("status", 16)) is not { } status) return null;

        var accelerator = Text("accelerator", 16);
        if (accelerator is not null && !AcceleratorPattern().IsMatch(accelerator)) accelerator = null;

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (entry.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            foreach (var capability in capabilities.EnumerateArray().Take(16))
                if (capability.ValueKind == JsonValueKind.String && capability.GetString() is { } slug) claimed.Add(slug);
        var serves = RemoteRuntimeKinds.Capabilities(kind).Where(claimed.Contains).ToList();

        var ports = new List<int>();
        if (entry.TryGetProperty("ports", out var portList) && portList.ValueKind == JsonValueKind.Array)
            foreach (var port in portList.EnumerateArray().Take(8))
                if (port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number) && number is > 0 and <= 65535 && !ports.Contains(number))
                    ports.Add(number);

        DateTimeOffset? startedAt = Text("startedAt", 40) is { } started
            && DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

        return new HostRuntime(id!, kind, Clip(Text("label", 200), 80) ?? id!, managed, enabledValue.GetBoolean(), status,
            Clip(Text("statusDetail", 1000), 200), accelerator, serves, ports, startedAt)
        {
            Version = Clip(Text("version", 200), 64),
        };
    }

    /// <summary>The status a node spells (<c>running</c>, <c>starting</c>, …), or null for anything else.</summary>
    internal static BackendStatus? Status(string? text) => text switch
    {
        "running" => BackendStatus.Running,
        "starting" => BackendStatus.Starting,
        "stopped" => BackendStatus.Stopped,
        "error" => BackendStatus.Error,
        "draining" => BackendStatus.Draining,
        "unsupported" => BackendStatus.Unsupported,
        _ => null,
    };

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var trimmed && trimmed.Length <= max ? trimmed : trimmed[..max];
}

/// <summary>
/// A remote node's runtimes (design 2.4) for <see cref="RemoteExecutionHost"/>: <see cref="List"/> and
/// <see cref="Status"/> are the node's last report as the registry keeps it (the node pushes <c>runtime.changed</c>, the
/// server never polls); <see cref="StartAsync"/> and <see cref="StopAsync"/> are <c>runtime.start</c> and
/// <c>runtime.stop</c>, with exactly their members. A start takes an id and nothing else.
/// </summary>
public sealed class RemoteHostRuntimes : IHostRuntimes
{
    /// <summary>The node answers a start at once (the runtime then reports Starting, then Running).</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Beyond the grace: the node kills what did not exit, then answers.</summary>
    internal static readonly TimeSpan StopSlack = TimeSpan.FromSeconds(30);

    public const string StartMethod = "runtime.start";
    public const string StopMethod = "runtime.stop";
    public const string ListMethod = "runtime.list";
    public const string ChangedMethod = "runtime.changed";

    private readonly string _nodeId;
    private readonly NodeRegistry _registry;
    private readonly Func<LinkSession?> _session;

    internal RemoteHostRuntimes(string nodeId, NodeRegistry registry, Func<LinkSession?> session)
    {
        _nodeId = nodeId;
        _registry = registry;
        _session = session;
    }

    public event Action<HostRuntime>? Changed;

    internal void Raise(HostRuntime runtime)
    {
        try { Changed?.Invoke(runtime); }
        catch (Exception) { /* an observer's fault is its own */ }
    }

    public IReadOnlyList<HostRuntime> List() => _registry.RuntimesOf(_nodeId);

    public HostRuntime? Status(string id) => List().FirstOrDefault(runtime => runtime.Id == id);

    public async Task<HostRuntime> StartAsync(string id, CancellationToken ct = default)
    {
        var known = Known(id);
        var answer = await CallAsync(StartMethod, new JsonObject { ["id"] = id }, StartTimeout, ct);
        return Answered(known, answer);
    }

    public async Task<HostRuntime> StopAsync(string id, TimeSpan? grace = null, CancellationToken ct = default)
    {
        if (grace is { } wait && (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(60)))
            throw new ArgumentOutOfRangeException(nameof(grace), "The grace is 0 to 60 seconds.");
        var known = Known(id);
        var parameters = new JsonObject { ["id"] = id };
        if (grace is { } seconds) parameters["graceSeconds"] = (int)Math.Ceiling(seconds.TotalSeconds);
        var answer = await CallAsync(StopMethod, parameters, (grace ?? TimeSpan.FromSeconds(60)) + StopSlack, ct);
        return Answered(known, answer);
    }

    /// <summary>A runtime the node reported; an id it never named is unknown here, before anything is sent.</summary>
    private HostRuntime Known(string id)
        => ForwardOpen.IsRuntimeId(id) && Status(id) is { } runtime
            ? runtime
            : throw new HostRuntimeException(HostRuntimeException.Unknown, $"Node '{_nodeId}' reported no runtime '{id}'.");

    private HostRuntime Answered(HostRuntime known, JsonElement answer)
    {
        var status = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("status", out var value)
                     && value.ValueKind == JsonValueKind.String
            ? RemoteRuntimeReport.Status(value.GetString())
            : null;
        var current = Status(known.Id) ?? known;
        return status is { } answered ? current with { Status = answered } : current;
    }

    private async Task<JsonElement> CallAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        LinkSession? session;
        try
        {
            session = _session();
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            session = null; // the hub's link server is gone (shutdown)
        }
        if (session is null || session.IsReset)
            throw new NodeUnavailableException(_nodeId, NodeUnavailableReasons.Offline);
        if (!session.HasFeature(LinkFeatures.RuntimeV1))
            throw new NodeUnavailableException(_nodeId, NodeUnavailableReasons.NeedsUpdate);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await session.CallAsync(method, parameters, deadline.Token);
        }
        catch (LinkRpcException error) when (error.Code is HostRuntimeException.Unknown or HostRuntimeException.NotManaged
                                                 or HostRuntimeException.Disabled or HostRuntimeException.Stopping)
        {
            throw new HostRuntimeException(error.Code, error.Message);
        }
        catch (LinkRpcException error) when (error.Code == LinkErrorCodes.LinkReset)
        {
            throw new NodeUnavailableException(_nodeId, NodeUnavailableReasons.Offline);
        }
        catch (LinkClosedException)
        {
            throw new NodeUnavailableException(_nodeId, NodeUnavailableReasons.Offline);
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            // A session torn down under the call (RedCompute stopping, the link dropping): the node is offline.
            throw new NodeUnavailableException(_nodeId, NodeUnavailableReasons.Offline);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Node '{_nodeId}' did not answer {method} within {timeout.TotalSeconds:0} s.");
        }
    }
}
