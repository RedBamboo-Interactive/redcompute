namespace RedCompute.Core.Configuration;

/// <summary>Locally owned enablement and workspace grants for an attached session runtime.</summary>
public sealed class RemoteSessionRuntimeConfig
{
    public int Port { get; set; }
    public string RuntimeId { get; set; } = "";
    public string PairingStatePath { get; set; } = "";
    public List<RemoteSessionWorkspaceConfig> Workspaces { get; set; } = [];
}

public sealed record RemoteSessionWorkspaceConfig(string Reference, string OwnerId, string ProjectPath,
    string ProviderInstanceId, string AccessLevel, Guid? RepositoryId = null);
