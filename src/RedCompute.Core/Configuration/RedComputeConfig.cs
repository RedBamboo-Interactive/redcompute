using System.Text.Json.Serialization;

namespace RedCompute.Core.Configuration;

public class RedComputeConfig
{
    public int ApiPort { get; set; } = 18800;
    public string LogLevel { get; set; } = "Info";
    public int JobRetentionDays { get; set; } = 7;
    public bool AutoStartWithWindows { get; set; }
    public TunnelConfig Tunnel { get; set; } = new();
    public Dictionary<string, CapabilityConfig> Capabilities { get; set; } = new();
}

public class TunnelConfig
{
    public bool Enabled { get; set; }
    public string? AccessToken { get; set; }
    public string? CloudflaredPath { get; set; }
    public string? TunnelToken { get; set; }
    public string? Hostname { get; set; }
}

public class CapabilityConfig
{
    public bool Enabled { get; set; } = true;
    public string? ActiveProvider { get; set; }
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

public class ProviderConfig
{
    public required string Type { get; set; }

    // Local provider settings
    public string? WslDistro { get; set; }
    public string? VenvPath { get; set; }
    public string? ServerPath { get; set; }
    public int? BackendPort { get; set; }
    public string? Model { get; set; }
    public string? VoicesBasePath { get; set; }
    public string? HealthEndpoint { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 180;

    // RunPod settings
    public string? ApiKey { get; set; }
    public string? PodId { get; set; }
    public int GpuCount { get; set; } = 1;
    public bool AutoStopOnExit { get; set; } = true;

    // Additional per-capability settings stored as key-value
    [JsonExtensionData]
    public Dictionary<string, object?>? Extra { get; set; }
}