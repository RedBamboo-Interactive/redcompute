using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeTranscriptPathsTests
{
    [Theory]
    [InlineData("S:/Nova/8698f6ec/wt-spike/repo.worktrees/feature-x", "S--Nova-8698f6ec-wt-spike-repo-worktrees-feature-x")]
    [InlineData("S:\\Nova\\wt-spike\\repo.worktrees\\feature-x", "S--Nova-wt-spike-repo-worktrees-feature-x")]
    [InlineData("T:/Projects/Leaf Body", "T--Projects-Leaf-Body")]
    [InlineData("T:/Projects/Game Atlas", "T--Projects-Game-Atlas")]
    [InlineData("C:/work/my_project", "C--work-my-project")]
    [InlineData("C:\\Users\\laure\\AppData\\Local\\x.y_z w", "C--Users-laure-AppData-Local-x-y-z-w")]
    public void ProjectSlug_replaces_every_non_alphanumeric_character(string path, string expected)
        => Assert.Equal(expected, ClaudeTranscriptPaths.ProjectSlug(path));

    [Fact]
    public void FindTranscript_uses_the_computed_slug_folder()
    {
        var root = Directory.CreateTempSubdirectory("rc-claude-projects").FullName;
        try
        {
            var folder = Path.Combine(root, "T--Projects-Leaf-Body");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "abc.jsonl");
            File.WriteAllText(file, "{}");
            Assert.Equal(file, ClaudeTranscriptPaths.FindTranscript("T:/Projects/Leaf Body", "abc", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindTranscript_scans_other_folders_when_the_slug_folder_is_missing()
    {
        var root = Directory.CreateTempSubdirectory("rc-claude-projects").FullName;
        try
        {
            var folder = Path.Combine(root, "truncated-and-hashed-slug");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "abc.jsonl");
            File.WriteAllText(file, "{}");
            Assert.Equal(file, ClaudeTranscriptPaths.FindTranscript("T:/very/long/path", "abc", root));
            Assert.Null(ClaudeTranscriptPaths.FindTranscript("T:/very/long/path", "missing", root));
        }
        finally { Directory.Delete(root, true); }
    }
}
