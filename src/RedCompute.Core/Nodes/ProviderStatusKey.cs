namespace RedCompute.Core.Nodes;

/// <summary>
/// A provider's status is the status of one provider on one node. Phase 1 has one node, so every key
/// is (name, node zero); the key is what status change detection and the APIs group by.
/// </summary>
public readonly record struct ProviderStatusKey(string Provider, string NodeId)
{
    public override string ToString() => $"{Provider}@{NodeId}";
}
