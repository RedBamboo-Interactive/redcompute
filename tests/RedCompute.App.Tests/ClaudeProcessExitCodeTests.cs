using Xunit;
using RedCompute.Plugin.ClaudeCode;

namespace RedCompute.App.Tests;

public class ClaudeProcessExitCodeTests
{
    [Fact]
    public void An_exit_code_is_reported_as_its_number()
    {
        Assert.Equal("0", ClaudeSessionService.DescribeExitCode(() => 0));
        Assert.Equal("-1073741510", ClaudeSessionService.DescribeExitCode(() => -1073741510));
    }

    [Fact]
    public void A_process_that_can_no_longer_report_its_code_is_unknown_instead_of_throwing()
    {
        // Process.ExitCode throws this after Dispose, or before the exit is reaped; the Exited
        // handler that reads it must not end RedCompute.
        Assert.Equal("unknown", ClaudeSessionService.DescribeExitCode(
            () => throw new InvalidOperationException("Process must exit before requested information can be determined.")));
    }
}
