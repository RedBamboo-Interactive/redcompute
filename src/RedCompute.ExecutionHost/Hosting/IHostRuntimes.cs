using RedCompute.Core.Providers;

namespace RedCompute.PluginSdk.Hosting;

/// <summary>Who starts a runtime: the host (<c>spawn</c>), or the person, the host only reaching it (<c>attach</c>).</summary>
public enum HostRuntimeManagement
{
    Spawn,
    Attach,
}

/// <summary>
/// One runtime a host serves (Phase 4, design 2.4): a ComfyUI or a speech server, with what it serves
/// and its state. The shape of a <c>runtime.list</c> entry.
/// </summary>
public sealed record HostRuntime(
    string Id,
    string Kind,
    string Label,
    HostRuntimeManagement Managed,
    bool Enabled,
    BackendStatus Status,
    string? StatusDetail,
    string? Accelerator,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<int> Ports,
    DateTimeOffset? StartedAt)
{
    public string? Version { get; init; }
}

/// <summary>
/// Why a runtime call was refused: <c>runtime_unknown</c>, <c>runtime_not_managed</c>, <c>runtime_disabled</c> or
/// <c>runtime_stopping</c> (a start while a stop is still in progress; retry once it reports stopped).
/// </summary>
public sealed class HostRuntimeException(string code, string message) : Exception(message)
{
    public const string Unknown = "runtime_unknown";
    public const string NotManaged = "runtime_not_managed";
    public const string Disabled = "runtime_disabled";
    public const string Stopping = "runtime_stopping";

    public string Code { get; } = code;
}

/// <summary>
/// The runtimes of one host (Phase 4, design 2.4). On node zero they are the backends its providers
/// launch, registered by those providers (<see cref="Local.LocalHostRuntimes"/>); on a remote node
/// they are the runtimes its owner registered there, reached over <c>runtime.v1</c>. A start takes an
/// id and nothing else: what runs is the host's recipe, never the caller's.
/// </summary>
public interface IHostRuntimes
{
    IReadOnlyList<HostRuntime> List();

    /// <summary>One runtime, or null when the host has no runtime with that id.</summary>
    HostRuntime? Status(string id);

    /// <exception cref="HostRuntimeException">Unknown, attach-mode or disabled.</exception>
    Task<HostRuntime> StartAsync(string id, CancellationToken ct = default);

    /// <param name="grace">How long the runtime may take to exit before it is killed (at most 60 s).</param>
    /// <exception cref="HostRuntimeException">Unknown or attach-mode.</exception>
    Task<HostRuntime> StopAsync(string id, TimeSpan? grace = null, CancellationToken ct = default);

    /// <summary>A runtime's entry, each time it changes (a remote node pushes <c>runtime.changed</c>).</summary>
    event Action<HostRuntime>? Changed;
}
