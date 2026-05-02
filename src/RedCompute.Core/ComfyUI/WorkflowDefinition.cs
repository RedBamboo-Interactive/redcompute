namespace RedCompute.Core.ComfyUI;

public class WorkflowDefinition
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public string Description { get; init; } = "";
    public string MediaType { get; init; } = "image";
    public required string OutputNode { get; init; }
    public required List<WorkflowParameter> Parameters { get; init; }
}
