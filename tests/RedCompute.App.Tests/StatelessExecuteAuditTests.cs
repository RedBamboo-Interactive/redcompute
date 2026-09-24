using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class StatelessExecuteAuditTests
{
    [Fact]
    public void Exception_failure_has_a_normalized_auditable_result_envelope()
    {
        var json = UnifiedSessionEndpoints.ExecuteFailureResultJson(
            "ollama/gemma4-heretic-fast", "The operation was canceled.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("streamOutput").ValueKind);
        Assert.Equal("ollama/gemma4-heretic-fast", root.GetProperty("model").GetString());
        Assert.Equal("The operation was canceled.", root.GetProperty("error").GetString());
    }
}
