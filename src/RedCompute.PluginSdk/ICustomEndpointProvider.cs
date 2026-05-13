using Microsoft.AspNetCore.Builder;

namespace RedCompute.PluginSdk;

public interface ICustomEndpointProvider
{
    void MapCustomEndpoints(WebApplication app);
}
