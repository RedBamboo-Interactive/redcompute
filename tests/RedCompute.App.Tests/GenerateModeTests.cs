using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class GenerateModeTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"mode\":\"oneshot\"}", true)]
    [InlineData("{\"mode\":\"ONESHOT\"}", true)]
    [InlineData("{\"mode\":\"session\"}", false)]
    [InlineData("{\"mode\":null}", false)]
    [InlineData("{\"mode\":42}", false)]
    public void GenerateEndpoint_AcceptsOnlyStatelessMode(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, UnifiedSessionEndpoints.IsStatelessGenerateMode(document.RootElement));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"confidential\":false}", false)]
    [InlineData("{\"confidential\":true}", true)]
    [InlineData("{\"confidential\":\"true\"}", false)]
    [InlineData("{\"confidential\":null}", false)]
    public void GenerateEndpoint_ConfidentialityIsExplicitAndTighteningOnly(
        string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected,
            UnifiedSessionEndpoints.IsConfidentialGenerate(document.RootElement));
    }
}
