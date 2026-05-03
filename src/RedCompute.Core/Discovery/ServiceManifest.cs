using System.Text.Json.Serialization;

namespace RedCompute.Core.Discovery;

public class ServiceManifest
{
    [JsonPropertyName("service")]
    public string Service { get; init; } = "RedCompute";

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("apiBase")]
    public required string ApiBase { get; init; }

    [JsonPropertyName("capabilities")]
    public required List<CapabilityManifest> Capabilities { get; init; }
}

public class CapabilityManifest
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("endpoints")]
    public required List<EndpointManifest> Endpoints { get; init; }
}

public class EndpointManifest
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, ParameterSchema>? Parameters { get; init; }

    [JsonPropertyName("returns")]
    public ReturnSchema? Returns { get; init; }
}

public class ParameterSchema
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; init; }

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; init; }

    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; init; }

    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }
}

public class ReturnSchema
{
    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    [JsonPropertyName("mediaCategory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaCategory { get; init; }

    [JsonPropertyName("outputEndpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputEndpoint { get; init; }
}
