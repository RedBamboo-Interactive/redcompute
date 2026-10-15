using System.Text.Json;
using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public class ClaudeResultEventTests
{
    [Fact]
    public void Api_error_result_with_success_subtype_is_not_a_successful_turn()
    {
        using var doc = JsonDocument.Parse("""
        {
          "type": "result",
          "subtype": "success",
          "terminal_reason": "api_error",
          "is_error": true,
          "api_error_status": 401,
          "result": "Failed to authenticate. API Error: 401 OAuth access token has expired. Re-authenticate to continue."
        }
        """);

        var error = ClaudeSessionService.ReadTerminalResultError(doc.RootElement);

        Assert.Equal(
            "Failed to authenticate. API Error: 401 OAuth access token has expired. Re-authenticate to continue.",
            error);
    }

    [Fact]
    public void Ordinary_success_result_remains_successful()
    {
        using var doc = JsonDocument.Parse("""
        { "type": "result", "subtype": "success", "is_error": false, "terminal_reason": "end_turn" }
        """);

        Assert.Null(ClaudeSessionService.ReadTerminalResultError(doc.RootElement));
    }

    [Fact]
    public void Legacy_error_subtype_keeps_its_error_message()
    {
        using var doc = JsonDocument.Parse("""
        { "type": "result", "subtype": "error", "error": "provider unavailable" }
        """);

        Assert.Equal(
            "provider unavailable",
            ClaudeSessionService.ReadTerminalResultError(doc.RootElement));
    }
}
