using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionScratchTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"redcompute-scratch-{Guid.NewGuid():N}");

    [Fact]
    public void ExistingAbsoluteDirectoryProducesTemporaryEnvironment()
    {
        Directory.CreateDirectory(_directory);

        Assert.True(SessionScratch.TryResolveDirectory(_directory, out var resolved));
        var environment = SessionScratch.Environment(resolved)!;
        Assert.Equal(resolved, environment["TEMP"]);
        Assert.Equal(resolved, environment["TMP"]);
        Assert.Equal(resolved, environment["REDLEAF_SCRATCH_DIR"]);
    }

    [Fact]
    public void RelativeOrMissingDirectoryIsRejected()
    {
        Assert.False(SessionScratch.TryResolveDirectory("relative-scratch", out _));
        Assert.False(SessionScratch.TryResolveDirectory(_directory, out _));
    }

    [Fact]
    public void ExecutionTokenIsAvailableWithoutAScratchDirectoryAndScopeRestores()
    {
        Assert.Null(SessionScratch.Environment(null));
        using (SessionScratch.PushExecutionToken("outer-token"))
        {
            Assert.Equal("outer-token", SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"]);
            using (SessionScratch.PushExecutionToken("inner-token"))
                Assert.Equal("inner-token", SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"]);
            Assert.Equal("outer-token", SessionScratch.Environment(null)!["REDLEAF_EXECUTION_TOKEN"]);
        }
        Assert.Null(SessionScratch.Environment(null));
    }

    [Fact]
    public void SuiteAccessFalseExplicitlyShadowsAnyInheritedExecutionToken()
    {
        using (SessionScratch.PushExecutionToken("must-not-escape"))
        {
            var environment = SessionScratch.Environment(null, suiteAccess: false)!;
            Assert.True(environment.ContainsKey("REDLEAF_EXECUTION_TOKEN"));
            Assert.Equal("", environment["REDLEAF_EXECUTION_TOKEN"]);
        }
    }

    [Fact]
    public void ConversationProfileNormalizesAndRestoresAcrossNestedScopes()
    {
        Assert.Equal(SessionExecutionProfile.Default, SessionExecutionProfile.Current);
        Assert.True(SessionExecutionProfile.TryNormalize("CONVERSATION-ONLY", out var normalized));
        Assert.Equal(SessionExecutionProfile.ConversationOnly, normalized);
        Assert.False(SessionExecutionProfile.TryNormalize("unknown", out _));

        using (SessionExecutionProfile.Push(SessionExecutionProfile.ConversationOnly))
        {
            Assert.Equal(SessionExecutionProfile.ConversationOnly, SessionExecutionProfile.Current);
            using (SessionExecutionProfile.Push(SessionExecutionProfile.Default))
                Assert.Equal(SessionExecutionProfile.Default, SessionExecutionProfile.Current);
            Assert.Equal(SessionExecutionProfile.ConversationOnly, SessionExecutionProfile.Current);
        }

        Assert.Equal(SessionExecutionProfile.Default, SessionExecutionProfile.Current);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
