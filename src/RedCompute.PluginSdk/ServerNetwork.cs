using RedCompute.PluginSdk.Hosting;

namespace RedCompute.PluginSdk;

/// <summary>
/// The server's own network, for a provider whose host is a remote node. What a job names on the server (an input
/// asset in the kernel, a URL) is fetched here, on the server, and only its bytes go to the node: the node never sees
/// the reference, and nothing it answers can redirect the fetch. Given only to remote providers
/// (<c>RedComputeHost.CreateRemoteProvider</c>); node zero's providers get none and fetch through their own host, as
/// before.
/// </summary>
public sealed class ServerNetwork(IHostNetwork network)
{
    /// <summary>
    /// A client on the server for absolute URLs, every response body capped at <paramref name="maxResponseBytes"/>
    /// (<see cref="ServiceResponseTooLargeException"/> past it).
    /// </summary>
    public HttpClient CreateClient(TimeSpan timeout, Func<long> maxResponseBytes)
        => network.CreateServiceClient("http://127.0.0.1/", timeout, maxResponseBytes);
}
