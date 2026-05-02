namespace RedCompute.Core.ComfyUI;

public class WorkflowParameter
{
    public required string Name { get; init; }
    public required string NodeId { get; init; }
    public required string Field { get; init; }
    public object? Default { get; init; }
}
