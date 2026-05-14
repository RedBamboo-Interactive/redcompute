using RedCompute.Core.Discovery;

namespace RedCompute.Core.Providers;

public interface IPluginProvider : IBackendProvider
{
    string DisplayName { get; }
    string ProviderType { get; }
    Dictionary<string, ParameterSchema> InputParameters { get; }
    ReturnSchema OutputSchema { get; }
    bool IsProxy { get; }
    string ProxyGeneratePath => "/generate";
    bool SupportsProgress { get; }
    bool SupportsRerun { get; }

    IReadOnlyList<EndpointManifest> GetCustomEndpointManifests() => [];
    Dictionary<string, string> ValidateParameters(Dictionary<string, object?> parameters) => new();
    Dictionary<string, object?> TransformParameters(Dictionary<string, object?> parameters) => parameters;
    void SetProgressCallback(Action<double>? callback) { }
}
