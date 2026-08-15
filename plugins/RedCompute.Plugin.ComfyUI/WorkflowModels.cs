namespace RedCompute.Plugin.ComfyUI;

/// <summary>
/// Parameter-to-node mapping carried in a frozen RedLeaf workflow snapshot.
/// Workflow discovery and persistence belong to RedLeaf's provider-comfyui
/// extension; RedCompute only executes the submitted graph.
/// </summary>
public sealed class WorkflowParameter
{
    public required string Name { get; init; }
    public required string NodeId { get; init; }
    public required string Field { get; init; }
    public string Type { get; init; } = "string";
}

public sealed class WorkflowDefinition
{
    public required string Name { get; init; }
    public string MediaType { get; init; } = "image";
    public required string OutputNode { get; init; }
    public required List<WorkflowParameter> Parameters { get; init; }
}
