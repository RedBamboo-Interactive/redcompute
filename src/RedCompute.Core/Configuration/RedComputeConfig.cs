using System.Text.Json.Serialization;

namespace RedCompute.Core.Configuration;

// Tunnel and autostart config are gone: RedCompute is a headless child of the Leaf
// kernel, which owns the one tunnel and the one autostart entry. Stale keys in an
// existing config.json are simply ignored on load.
public class RedComputeConfig
{
    public int ApiPort { get; set; } = 18800;
    public string LogLevel { get; set; } = "Info";
    public double ElectricityRatePerKwh { get; set; } = 0.15;
    /// <summary>Eagerly bring Docker Desktop up at launch. Off by default: Docker is only
    /// needed for sessions that ask for a container, and those start the daemon on demand.
    /// Turn this on to pay the daemon's startup cost at boot instead of on first use.</summary>
    public bool AutoStartDocker { get; set; } = false;
    public string CodeRedUrl { get; set; } = "http://127.0.0.1:18801";
    public string RedLeafUrl { get; set; } = "http://127.0.0.1:18804";
    public long InputAttachmentMaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public int InputAttachmentMaxCount { get; set; } = 10;
    public long InputAttachmentMaxTurnSizeBytes { get; set; } = 50 * 1024 * 1024;
    public int InputAttachmentTtlMinutes { get; set; } = 24 * 60;
    public Dictionary<string, CapabilityConfig> Capabilities { get; set; } = new();
}

public class CapabilityConfig
{
    public string? DisplayName { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string? ActiveProvider { get; set; }
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

public class ProviderConfig
{
    public required string Type { get; set; }

    // Entity-backed runtime identity. These values let a provider plugin remain
    // independent from the harness it wraps and distinguish dedicated instances
    // from shared backend profiles such as Ollama and Muse Spark over OpenCode.
    public string? EntityId { get; set; }
    public string? EntitySlug { get; set; }
    public string? DisplayName { get; set; }
    public string? Backend { get; set; }
    public string RuntimeBinding { get; set; } = "shared";

    // Local provider settings
    public string? WslDistro { get; set; }
    public string? VenvPath { get; set; }
    public string? ServerPath { get; set; }
    public int? BackendPort { get; set; }
    public string? Model { get; set; }
    public string? VoicesBasePath { get; set; }
    public string? HealthEndpoint { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 180;
    // Structured local inference provider settings
    public string? Endpoint { get; set; }
    public string? ModelRevision { get; set; }
    public string? CalibrationRevision { get; set; }
    public int? TimeoutSeconds { get; set; }
    /// <summary>
    /// Optional explicit launch command. Null means RedCompute only attaches to the
    /// configured endpoint and never provisions or downloads anything.
    /// </summary>
    public string? LaunchCommand { get; set; }


    // RunPod settings
    public string? ApiKey { get; set; }
    public string? PodId { get; set; }
    public int GpuCount { get; set; } = 1;
    public bool AutoStopOnExit { get; set; } = true;

    // Additional per-capability settings stored as key-value
    [JsonExtensionData]
    public Dictionary<string, object?>? Extra { get; set; }
}
