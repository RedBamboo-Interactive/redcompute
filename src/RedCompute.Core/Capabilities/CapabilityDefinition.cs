namespace RedCompute.Core.Capabilities;

public class CapabilityDefinition
{
    public required string Slug { get; init; }
    public required CapabilityType Type { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; set; } = true;
}
