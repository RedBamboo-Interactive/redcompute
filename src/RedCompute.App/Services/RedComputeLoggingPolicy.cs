using AppHostLogLevel = RedBamboo.AppHost.Logging.LogLevel;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace RedCompute.App.Services;

internal static class RedComputeLoggingPolicy
{
    internal static MicrosoftLogLevel ParseMinimum(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => MicrosoftLogLevel.Trace,
        "debug" => MicrosoftLogLevel.Debug,
        "information" or "info" => MicrosoftLogLevel.Information,
        "warning" or "warn" => MicrosoftLogLevel.Warning,
        "error" => MicrosoftLogLevel.Error,
        "critical" or "fatal" => MicrosoftLogLevel.Critical,
        "none" => MicrosoftLogLevel.None,
        _ => MicrosoftLogLevel.Information,
    };

    internal static bool IsEnabled(
        string? configuredMinimum,
        string? category,
        MicrosoftLogLevel level)
    {
        var minimum = ParseMinimum(configuredMinimum);
        if (category?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true
            && minimum < MicrosoftLogLevel.Warning)
            minimum = MicrosoftLogLevel.Warning;
        return level >= minimum && level != MicrosoftLogLevel.None;
    }

    internal static bool IsEnabled(string? configuredMinimum, AppHostLogLevel level)
    {
        var microsoftLevel = level switch
        {
            AppHostLogLevel.Debug => MicrosoftLogLevel.Debug,
            AppHostLogLevel.Info => MicrosoftLogLevel.Information,
            AppHostLogLevel.Warn => MicrosoftLogLevel.Warning,
            AppHostLogLevel.Error => MicrosoftLogLevel.Error,
            AppHostLogLevel.Critical => MicrosoftLogLevel.Critical,
            _ => MicrosoftLogLevel.Information,
        };
        return IsEnabled(configuredMinimum, category: null, microsoftLevel);
    }
}
