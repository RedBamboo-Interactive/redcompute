using Microsoft.Extensions.Logging;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RedComputeLoggingPolicyTests
{
    [Theory]
    [InlineData("Info", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    [InlineData("unexpected", LogLevel.Information)]
    public void Parses_configured_minimum(string value, LogLevel expected)
        => Assert.Equal(expected, RedComputeLoggingPolicy.ParseMinimum(value));

    [Fact]
    public void Suppresses_successful_aspnet_request_lifecycle_but_keeps_warnings()
    {
        Assert.False(RedComputeLoggingPolicy.IsEnabled(
            "Info", "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information));
        Assert.True(RedComputeLoggingPolicy.IsEnabled(
            "Info", "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
        Assert.True(RedComputeLoggingPolicy.IsEnabled(
            "Info", "RedCompute.App.Services.Worker", LogLevel.Information));
    }

    [Fact]
    public void Configured_error_floor_is_not_weakened_for_aspnet_categories()
    {
        Assert.False(RedComputeLoggingPolicy.IsEnabled(
            "Error", "Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning));
        Assert.True(RedComputeLoggingPolicy.IsEnabled(
            "Error", "Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Error));
    }
}
