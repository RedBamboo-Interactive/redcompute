using System.Runtime.CompilerServices;

namespace RedCompute.App.Tests;

/// <summary>
/// Awaits an event a test is waiting for, without a wall clock of its own. A machine too busy to run the code under test is
/// slow, not wrong, so the wait is as long as it takes; only a backstop ends it, so that a real hang still fails, and the failure
/// names the test and the step it was stuck on.
/// </summary>
internal static class Step
{
    /// <summary>Far beyond any starved run, far short of the CI job's limit.</summary>
    public static readonly TimeSpan Backstop = TimeSpan.FromMinutes(3);

    public static async Task Await(Task task, string step, [CallerMemberName] string test = "")
    {
        try
        {
            await task.WaitAsync(Backstop);
        }
        catch (TimeoutException)
        {
            throw Stuck(test, step);
        }
    }

    public static async Task<T> Await<T>(Task<T> task, string step, [CallerMemberName] string test = "")
    {
        try
        {
            return await task.WaitAsync(Backstop);
        }
        catch (TimeoutException)
        {
            throw Stuck(test, step);
        }
    }

    private static TimeoutException Stuck(string test, string step)
        => new($"{test}: still waiting for {step} after {(int)Backstop.TotalSeconds} s, so it is hung, not slow.");
}
