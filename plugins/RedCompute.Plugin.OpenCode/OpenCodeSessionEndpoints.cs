using Microsoft.AspNetCore.Builder;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public static class OpenCodeSessionEndpoints
{
    public static void Map(WebApplication app, OpenCodeSessionService opencode, IJobTracker jobTracker, Action<string, Guid?> log)
    {
    }
}
