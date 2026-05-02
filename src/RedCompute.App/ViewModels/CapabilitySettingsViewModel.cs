namespace RedCompute.App.ViewModels;

public class CapabilitySettingsViewModel
{
    public string Slug { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ActiveProvider { get; init; } = "";
    public int? BackendPort { get; init; }
    public string? WslDistro { get; init; }
    public string? ServerPath { get; init; }
    public string? Model { get; init; }
    public string? HealthEndpoint { get; init; }
}
