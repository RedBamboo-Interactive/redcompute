using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>Worktree acceptance in <see cref="RepositoryReferenceValidator"/>, against real temp git repositories.</summary>
public sealed class RepositoryWorktreeValidatorTests
{
    private static readonly Guid RepositoryId = Guid.Parse("1910ac53-d68a-4ccc-883d-0541c0091d9b");

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    /// <summary>A real repo with one commit and a linked worktree at &lt;repo&gt;.worktrees/feature-x.</summary>
    private sealed class RepoFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"rc-wt-{Guid.NewGuid():N}");
        public string Repo => Path.Combine(Root, "repo");
        public string Worktree => Path.Combine(Root, "repo.worktrees", "feature-x");

        public RepoFixture()
        {
            Directory.CreateDirectory(Repo);
            Git(Repo, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(Repo, "a.txt"), "a");
            Git(Repo, "add", ".");
            Git(Repo, "commit", "-q", "-m", "init");
            Git(Repo, "worktree", "add", "-q", "-b", "feature/x", Worktree);
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                    f.Attributes = FileAttributes.Normal;
                Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }

    private static Task<RepositoryValidationResult> Validate(string repoPath, string projectPath, string? branchWork = null)
        => new RepositoryReferenceValidator(new HttpClient(new EntityHandler(repoPath, branchWork))
        {
            BaseAddress = new Uri("http://redleaf/"),
        }).ValidateAsync(RepositoryId.ToString("D"), projectPath);

    [Fact]
    public async Task Exact_match_does_not_need_git()
    {
        // Not a git repository: an exact match must succeed without ever asking git.
        var dir = Directory.CreateTempSubdirectory("rc-nogit").FullName;
        try
        {
            var result = await Validate(dir, dir);
            Assert.True(result.Ok, result.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Accepts_linked_worktree_of_the_repository()
    {
        using var fx = new RepoFixture();
        var result = await Validate(fx.Repo, fx.Worktree);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.GetFullPath(fx.Worktree), result.Repository!.Path);
    }

    [Fact]
    public async Task Accepts_worktree_given_with_forward_slashes_and_trailing_separator()
    {
        using var fx = new RepoFixture();
        var result = await Validate(fx.Repo.Replace('\\', '/'), fx.Worktree.Replace('\\', '/') + "/");

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Accepts_worktree_when_repository_path_is_reached_through_a_junction_alias()
    {
        // Git prints the physical form for both sides, so an alias form of local_path still matches.
        if (!OperatingSystem.IsWindows()) return;
        using var fx = new RepoFixture();
        var alias = Path.Combine(Path.GetTempPath(), $"rc-alias-{Guid.NewGuid():N}");
        using (var mk = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{alias}\" \"{fx.Root}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            mk.WaitForExit();
        try
        {
            Assert.True(Directory.Exists(alias), "junction was not created");
            var result = await Validate(Path.Combine(alias, "repo"), fx.Worktree);
            Assert.True(result.Ok, result.Error);
        }
        finally { try { Directory.Delete(alias); } catch { } }
    }

    [Fact]
    public async Task Rejects_prunable_worktree_whose_folder_is_gone()
    {
        using var fx = new RepoFixture();
        Directory.Delete(fx.Worktree, recursive: true);
        Assert.Contains("prunable", Git(fx.Repo, "worktree", "list", "--porcelain"));

        var result = await Validate(fx.Repo, fx.Worktree);

        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        Assert.Contains("Worktree folder is missing", result.Error);
    }

    [Theory]
    [InlineData("main-only")]
    [InlineData("MAIN-ONLY")]
    public async Task Rejects_worktree_of_a_main_only_repository(string branchWork)
    {
        using var fx = new RepoFixture();
        var result = await Validate(fx.Repo, fx.Worktree, branchWork);

        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal("Repository is main-only; sessions must use its checkout", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("allowed")]
    public async Task Absent_or_allowed_branch_work_permits_worktrees(string? branchWork)
    {
        using var fx = new RepoFixture();
        var result = await Validate(fx.Repo, fx.Worktree, branchWork);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Main_only_repository_still_accepts_its_own_checkout()
    {
        using var fx = new RepoFixture();
        var result = await Validate(fx.Repo, fx.Repo, "main-only");

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Rejects_worktree_of_another_repository()
    {
        using var fx = new RepoFixture();
        using var other = new RepoFixture();
        var result = await Validate(fx.Repo, other.Worktree);

        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        Assert.Contains("different git common dir", result.Error);
    }

    [Fact]
    public async Task Rejects_subfolders_of_the_worktree_and_of_the_checkout()
    {
        using var fx = new RepoFixture();
        var inWorktree = Path.Combine(fx.Worktree, "sub");
        var inRepo = Path.Combine(fx.Repo, "sub");
        Directory.CreateDirectory(inWorktree);
        Directory.CreateDirectory(inRepo);

        var a = await Validate(fx.Repo, inWorktree);
        var b = await Validate(fx.Repo, inRepo);

        Assert.False(a.Ok);
        Assert.Contains("subfolder", a.Error);
        Assert.False(b.Ok);
        Assert.Contains("not a linked git worktree", b.Error);
    }

    [Fact]
    public async Task Rejects_a_plain_folder_next_to_the_worktrees()
    {
        using var fx = new RepoFixture();
        var lookalike = Path.Combine(fx.Root, "repo.worktrees", "not-a-worktree");
        Directory.CreateDirectory(lookalike);

        var result = await Validate(fx.Repo, lookalike);

        Assert.False(result.Ok);
        Assert.Equal(422, result.StatusCode);
        Assert.Contains("is not a git worktree", result.Error);
    }

    private sealed class EntityHandler(string repositoryPath, string? branchWork) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = new
            {
                id = RepositoryId,
                typeSlug = "repository",
                name = "RedCompute",
                data = JsonSerializer.Serialize(new { status = "active", local_path = repositoryPath, branch_work = branchWork }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
