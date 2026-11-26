using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class CodexBindingHandoffTests
{
    [Fact]
    public void Only_the_exact_native_developer_receipt_can_prevent_reinjecting_a_lost_acknowledgement()
    {
        const string marker = "RedCompute handoff operation q_exact\n";
        var tail = """
            first line left incomplete
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"RedCompute handoff operation q_exact\nquoted"}]}}
            {"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"RedCompute handoff operation q_old\nsome context"}]}}
            """;
        Assert.False(CodexInteractiveService.HasNativeHandoffReceipt(tail, marker));
        tail += "\n" + """
            {"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"RedCompute handoff operation q_exact\nbounded history data"}]}}
            """;
        Assert.True(CodexInteractiveService.HasNativeHandoffReceipt(tail, marker));
    }
}
