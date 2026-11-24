using System.Text.Json;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public class PingCommitContractTests
{
    private const string Sha = "a6badeb01d7ab948930e09fd06c8651c3062f502";

    [Theory]
    [InlineData("0.2.0+" + Sha, Sha)]
    [InlineData("1.0.0-beta.1+" + Sha + ".dirty", Sha)]
    [InlineData("0.2.0+A6BADEB01D7AB948930E09FD06C8651C3062F502", Sha)]
    [InlineData("0.2.0", null)]
    [InlineData("0.2.0+abc123", null)]
    [InlineData("0.2.0+" + Sha + "0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Commit_is_the_40_hex_after_plus_or_null(string? informational, string? expected)
        => Assert.Equal(expected, PingInfo.ParseCommit(informational));

    [Fact]
    public void Ping_body_keeps_ok_service_version_and_adds_commit()
    {
        var json = JsonSerializer.SerializeToElement(
            new PingInfo(true, "RedCompute", RedComputeServiceDescriptor.AppVersion, Sha),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("RedCompute", json.GetProperty("service").GetString());
        Assert.Equal(RedComputeServiceDescriptor.AppVersion, json.GetProperty("version").GetString());
        Assert.Equal(Sha, json.GetProperty("commit").GetString());
    }

    [Fact]
    public void Ping_lists_features_only_when_there_are_some()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.False(JsonSerializer.SerializeToElement(PingInfo.Create("RedCompute", "0.2.0", null), web).TryGetProperty("features", out _));

        var json = JsonSerializer.SerializeToElement(PingInfo.Create("RedCompute", "0.2.0", null) with { Features = ["placement.v1"] }, web);
        Assert.Equal(["placement.v1"], json.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
    }

    [Fact]
    public void Ping_without_a_commit_serializes_commit_as_null()
    {
        var json = JsonSerializer.SerializeToElement(PingInfo.Create("RedCompute", "0.2.0", null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("commit").ValueKind);
    }
}
