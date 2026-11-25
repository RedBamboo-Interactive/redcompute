namespace RedCompute.PluginSdk;

/// <summary>
/// Optional for a provider whose cheap, read-only state (a model list read from its CLI, say) should be ready as soon
/// as the provider is registered, whether or not it is its capability's default. Start-up starts only the default
/// provider, so without this a merely selectable provider serves nothing until something asks it twice.
/// </summary>
/// <remarks>
/// The host calls it once per provider instance, in the background, at start-up and when a provider is replaced.
/// It must not start a backend, open a port, take a lease or block its caller; a failure is logged and ignored.
/// </remarks>
public interface IProviderWarmup
{
    Task WarmAsync(CancellationToken ct = default);
}
