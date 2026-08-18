using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionStreamEventContractTests
{
    [Fact]
    public void TranscriptPipelineUsesCanonicalSessionStreamEventName()
    {
        Assert.Equal("session.stream", SessionTranscriptPipeline.StreamEventType);
    }
}
