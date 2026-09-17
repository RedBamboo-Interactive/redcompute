using System.Text.Json;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexTurnCompletionTests
{
    [Fact]
    public void CompletedTurnUsesFinalItemAsAuthoritativeFallback()
    {
        using var payload = JsonDocument.Parse(
            """
            {"turn":{"status":"completed","error":null,"items":[
              {"type":"agentMessage","text":"Done.","phase":"final_answer"}
            ]}}
            """);

        Assert.Null(CodexInteractiveService.TurnCompletionError(
            payload.RootElement, hasFinalOutput: false, interruptRequested: false));
        var recovered = Assert.IsType<CodexStreamEvent>(
            CodexInteractiveService.CompletionFinalOutput(payload.RootElement));
        Assert.Equal("Done.", recovered.Content);
        Assert.Equal("final_answer", recovered.Phase);
    }

    [Fact]
    public void OutputlessCompletedTurnBecomesExplicitError()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"completed","error":null,"items":[]}}""");

        Assert.Equal(
            "Codex turn completed without final output",
            CodexInteractiveService.TurnCompletionError(
                payload.RootElement, hasFinalOutput: false, interruptRequested: false));
    }

    [Fact]
    public void FailedTurnPreservesProviderError()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"failed","error":{"message":"No output in stream"},"items":[]}}""");

        Assert.Equal(
            "No output in stream",
            CodexInteractiveService.TurnCompletionError(
                payload.RootElement, hasFinalOutput: false, interruptRequested: false));
    }

    [Fact]
    public void InterruptedTurnMayEndWithoutFinalOutput()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"failed","error":{"message":"Interrupted"},"items":[]}}""");

        Assert.Null(CodexInteractiveService.TurnCompletionError(
            payload.RootElement, hasFinalOutput: false, interruptRequested: true));
    }

    [Fact]
    public void CommentaryDoesNotSatisfyFinalOutputInvariant()
    {
        using var agentMessage = JsonDocument.Parse(
            """{"item":{"type":"agentMessage"}}""");
        Assert.False(CodexInteractiveService.IsFinalOutput(
            "item/completed", agentMessage.RootElement, new CodexStreamEvent
        {
            Type = "text",
            Content = "Working on it",
            Phase = "commentary",
        }));
        Assert.True(CodexInteractiveService.IsFinalOutput(
            "item/completed", agentMessage.RootElement, new CodexStreamEvent
        {
            Type = "text",
            Content = "Finished",
            Phase = "final_answer",
        }));
    }

    [Fact]
    public void PlanTextDoesNotSatisfyFinalOutputInvariant()
    {
        using var plan = JsonDocument.Parse(
            """{"item":{"type":"plan"}}""");

        Assert.False(CodexInteractiveService.IsFinalOutput(
            "item/completed", plan.RootElement, new CodexStreamEvent
            {
                Type = "text",
                Content = "1. Investigate",
            }));
    }
}
