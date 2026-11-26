using System.IO;
using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.Nodes;

internal sealed record RemoteMoveWorkspace(string Workspace, string Revision, string ProviderId, string ProviderInstanceId,
    string AccessLevel, string MaxAccessLevel);

internal sealed class RemoteSessionRuntimeConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IExecutionHost _host;
    private readonly RemoteSessionRuntimeConfig _config;
    private readonly Func<string, ISessionProvider?> _getProvider;

    internal RemoteSessionRuntimeConfiguration(RemoteSessionRuntimeConfig config, Func<string, ISessionProvider?> getProvider,
        IExecutionHost? host = null)
    {
        if (!ForwardOpen.IsRuntimeId(config.RuntimeId) || config.Port is < 1 or > 65535
            || !Path.IsPathFullyQualified(config.PairingStatePath) || config.Workspaces is not { Count: > 0 }
            || config.Workspaces.Any(workspace => string.IsNullOrWhiteSpace(workspace.Reference)
                || string.IsNullOrWhiteSpace(workspace.OwnerId) || !Path.IsPathFullyQualified(workspace.ProjectPath)
                || string.IsNullOrWhiteSpace(workspace.ProviderInstanceId) || !SessionAccessLevels.IsKnown(workspace.AccessLevel))
            || config.Workspaces.GroupBy(workspace => (workspace.OwnerId, workspace.Reference, workspace.ProviderInstanceId))
                .Any(group => group.Count() != 1))
            throw new ArgumentException("Invalid local remote session runtime configuration.");

        _host = host ?? LocalExecutionHost.Shared;
        _config = config;
        _getProvider = getProvider;
    }

    internal RemoteSessionRuntimeIdentity? CurrentPairing()
    {
        try
        {
            using var file = _host.Files.OpenRead(_host.Path(_config.PairingStatePath));
            if (file.Length > 64 * 1024) return null;
            var pair = JsonSerializer.Deserialize<PairingFile>(file, Json)?.Pairing;
            if (pair is not { NodeId.Length: > 0, LinkPin: { } pin }
                || pin.Protocol != NoiseProtocol.Name || string.IsNullOrEmpty(pin.PublicKey))
                return null;
            var key = new NodeLinkKeyInfo(pin.Protocol, pin.PublicKey, pin.Fingerprint, pin.KeyId).PublicKeyBytes();
            return key is not null && NodeLinkKeyInfo.For(key).KeyId == pin.KeyId ? new(pair.NodeId, _config.RuntimeId, key) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal bool IsReady() => CurrentPairing() is not null && _config.Workspaces.Any(workspace =>
        _host.Files.DirectoryExists(_host.Path(workspace.ProjectPath)) && _getProvider(workspace.ProviderInstanceId) is { } provider
            && NativeSessionBindingPreparer.Supports(provider));

    internal RemoteSessionRuntimeWorkspace? ResolveConfigured(SessionRuntimeScope scope, RemoteSessionLifecycleRequest request)
    {
        var workspace = _config.Workspaces.FirstOrDefault(candidate => candidate.OwnerId == scope.OwnerId
            && candidate.Reference == scope.AccessScope && candidate.ProviderInstanceId == request.ProviderInstanceId);
        if (workspace is null || _getProvider(workspace.ProviderInstanceId) is not { } provider
            || !_host.Files.DirectoryExists(_host.Path(workspace.ProjectPath)))
            return null;

        return new(workspace.OwnerId, workspace.Reference, request.Revision, workspace.ProjectPath, workspace.ProviderInstanceId,
            provider, new(workspace.AccessLevel, workspace.AccessLevel), workspace.RepositoryId);
    }

    internal async Task<RemoteSessionRuntimeWorkspace?> ResolveAsync(SessionRuntimeScope scope,
        RemoteSessionLifecycleRequest request, CancellationToken ct)
    {
        var workspace = ResolveConfigured(scope, request);
        if (workspace is null) return null;
        try
        {
            var head = request.MovePreview ? await HostGit.ReadCleanHeadAsync(_host, workspace.ProjectPath, ct)
                : (await HostGit.RunAsync(_host, workspace.ProjectPath, ["rev-parse", "--verify", "HEAD"], ct))?.SingleOrDefault();
            if (!string.Equals(head, request.Revision, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException)
        {
            return null;
        }

        return workspace;
    }

    internal async Task<IReadOnlyList<RemoteMoveWorkspace>> ListMoveWorkspacesAsync(string owner, CancellationToken ct)
    {
        var choices = new List<RemoteMoveWorkspace>();
        foreach (var workspace in _config.Workspaces.Where(workspace => workspace.OwnerId == owner).Take(32))
        {
            if (_getProvider(workspace.ProviderInstanceId) is not { } provider || !NativeSessionBindingPreparer.Supports(provider))
                continue;
            var commit = await HostGit.ReadCleanHeadAsync(_host, workspace.ProjectPath, ct);
            if (string.IsNullOrWhiteSpace(commit)) continue;
            choices.Add(new(workspace.Reference, commit, provider.ProviderId, workspace.ProviderInstanceId,
                workspace.AccessLevel, workspace.AccessLevel));
        }
        return choices;
    }

    private sealed record PairingFile(PairingEntry? Pairing);
    private sealed record PairingEntry(string NodeId, LinkPinEntry? LinkPin);
    private sealed record LinkPinEntry(string PublicKey, string Fingerprint, string KeyId, string Protocol = NoiseProtocol.Name);
}
