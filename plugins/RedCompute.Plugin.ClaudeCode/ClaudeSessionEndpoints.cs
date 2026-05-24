using Microsoft.AspNetCore.Builder;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public static class ClaudeSessionEndpoints
{
    public static void Map(WebApplication app, ClaudeSessionService claude, IJobTracker jobTracker, Action<string, Guid?> log)
    {
    }
}
