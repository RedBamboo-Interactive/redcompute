using Xunit;

namespace RedCompute.App.Tests;

public sealed class ScratchDirectoryTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "redcompute-scratch-dir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "input.db"), "x");
        return root;
    }

    [WindowsFact("a held file blocks removal and Restart Manager names the holder")]
    public void A_hold_that_ends_within_the_patience_is_waited_out()
    {
        var root = NewRoot();
        var held = new FileStream(Path.Combine(root, "input.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(300).ContinueWith(_ => held.Dispose());

        ScratchDirectory.Delete(root, TimeSpan.FromSeconds(30));

        Assert.False(Directory.Exists(root));
    }

    [WindowsFact("a held file blocks removal and Restart Manager names the holder")]
    public void A_hold_that_does_not_end_fails_and_names_the_file_and_the_holder()
    {
        var root = NewRoot();
        using var held = new FileStream(Path.Combine(root, "input.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var failure = Assert.Throws<IOException>(() => ScratchDirectory.Delete(root, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("Holders:", failure.Message);
        Assert.Contains("input.db", failure.Message);
        Assert.Contains($"pid {Environment.ProcessId}", failure.Message);
        held.Dispose();
        ScratchDirectory.Delete(root);
    }
}
