namespace RedCompute.Core.Capabilities;

public class CapabilityDefinition
{
    public required string Slug { get; init; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string? Category { get; set; }
}
