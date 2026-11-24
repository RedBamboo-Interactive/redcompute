using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Placement resolution on node zero (design 2.2), not yet wired to any launch: the kernel's workspace
/// and scratch (K2), repository checkouts and branches with real git, and the mapping onto today's
/// launch inputs.
/// </summary>
public sealed class PlacementResolverTests : IDisposable
{
    private static readonly Guid RepositoryId = Guid.Parse("7e1e0000-0000-4000-8000-000000000001");
    private static readonly Guid AgentId = Guid.Parse("7e1e0000-0000-4000-8000-0000000000a1");
    private const string NodeZero = "node_01JCZ0000000000000000PLAC";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-placement-resolve", Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "songes");
    private string Worktree => Path.Combine(_root, "songes.worktrees", "laurent-x");

    public PlacementResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, "inner", "repo"));
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "inner", "repo", "a.txt"), "a");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "init");
        Git(Repo, "worktree", "add", "-q", "-b", "laurent/x", Worktree);
    }

    // ── K2: the kernel's workspace and scratch ───────────────────────────────────────────────────

    [Fact]
    public async Task A_mounted_workspace_comes_back_with_its_path_and_the_request_names_only_the_agent()
    {
        var kernel = new Kernel((_, _) => (HttpStatusCode.OK, """{"workspace":{"kind":"mounted","path":"L:\\Workspaces\\Nova"}}"""));

        var result = await new KernelPlacementClient(kernel.Client()).ResolveAsync(new WorkspacePlacement(AgentId));

        Assert.True(result.Ok);
        Assert.Equal(new WorkspaceResolution("mounted", "L:\\Workspaces\\Nova"), result.Value);
        var (method, path, body) = Assert.Single(kernel.Requests);
        Assert.Equal(("POST", "/api/compute/placement/resolve"), (method, path));
        Assert.Equal($$$"""{"workspace":{"agentId":"{{{AgentId}}}"}}""", body);
    }

    [Fact]
    public async Task An_agent_without_a_workspace_is_none()
    {
        var kernel = new Kernel((_, _) => (HttpStatusCode.OK, """{"workspace":{"kind":"none"}}"""));

        var result = await new KernelPlacementClient(kernel.Client()).ResolveAsync(new WorkspacePlacement(AgentId));

        Assert.Equal(new WorkspaceResolution("none", null), result.Value);
    }

    [Fact]
    public async Task A_prepared_scratch_keeps_the_kernel_environment()
    {
        var kernel = new Kernel((_, _) => (HttpStatusCode.OK, """
            {"scratch":{"path":"S:\\Nova\\d1","environment":{"REDLEAF_SCRATCH_ROOT":"S:\\Nova\\d1","REDLEAF_SCRATCH_DIR":"S:\\Nova\\d1","TEMP":"S:\\Nova\\d1","TMP":"S:\\Nova\\d1","TMPDIR":"S:\\Nova\\d1"}}}
            """));

        var result = await new KernelPlacementClient(kernel.Client()).PrepareAsync(new ScratchPlacement("Nova", "d1/node run"));

        Assert.True(result.Ok);
        Assert.Equal("S:\\Nova\\d1", result.Value!.Path);
        Assert.Equal(5, result.Value.Environment.Count);
        Assert.Equal("""{"scratch":{"owner":"Nova","executionKey":"d1/node run"}}""", Assert.Single(kernel.Requests).Body);
    }

    [Theory]
    [InlineData("nova", JobProvenanceAssurance.Verified, "d1", true, false, true, true)]
    [InlineData("nova", JobProvenanceAssurance.Verified, "d2", true, false, true, false)]
    [InlineData("nova", JobProvenanceAssurance.Verified, "", true, false, true, false)]
    [InlineData("nova", JobProvenanceAssurance.Verified, "d1", true, true, false, false)]
    [InlineData("nova", JobProvenanceAssurance.Verified, "d1", false, false, false, false)]
    [InlineData("nova", JobProvenanceAssurance.Asserted, "d1", true, false, false, false)]
    [InlineData("codered", JobProvenanceAssurance.Verified, "d1", true, false, false, false)]
    public async Task A_scratch_write_grant_requires_the_allocation_for_the_verified_nova_discussion(
        string app, JobProvenanceAssurance assurance, string requestedKey,
        bool signed, bool ambiguous, bool callsKernel, bool allowed)
    {
        var ownerDirectory = Path.Combine(_root, "Nova");
        var allocated = Path.Combine(ownerDirectory, "d1");
        Directory.CreateDirectory(allocated);
        Directory.CreateDirectory(Path.Combine(ownerDirectory, "d2"));
        var environment = SessionScratch.Environment(allocated);
        var kernel = new Kernel((_, _) => (HttpStatusCode.OK, JsonSerializer.Serialize(
            new { scratch = new { path = allocated, environment } })));
        var context = new List<JobContextReference> { new("discussion", "d1") };
        if (signed) context.Add(new("execution", Guid.NewGuid().ToString("D")));
        if (ambiguous) context.Add(new("discussion", "d2"));
        var provenance = new JobProvenance(JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redcompute", new JobAppReference("plugin", app, null, app),
                new JobEntrypoint("http", "/ai-session/sessions")),
            new JobActor("agent", "Nova", AgentId.ToString()), new JobBeneficiary("user", "owner"),
            context, new JobTrace(), assurance, DateTimeOffset.UtcNow);
        var host = LocalExecutionHost.Shared;

        var result = await new KernelPlacementClient(kernel.Client())
            .ResolveWritableAsync(host, provenance, Path.Combine(ownerDirectory, requestedKey));

        Assert.Equal(allowed ? host.Files.GetFinalDirectoryPath(host.Path(allocated)) : null, result);
        if (allowed) Assert.NotNull(result);
        if (callsKernel)
            Assert.Equal("""{"scratch":{"owner":"Nova","executionKey":"d1"}}""", Assert.Single(kernel.Requests).Body);
        else
            Assert.Empty(kernel.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"agent_not_found","message":"No Agent"}""", 404, "agent_not_found")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"workspace_unavailable","message":"VFS down"}""", 503, "workspace_unavailable")]
    [InlineData(HttpStatusCode.NotFound, "", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.NotFound, """{"error":"not_found"}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"forbidden"}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, """{"workspace":{"kind":"mounted"}}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, """{"workspace":{"kind":"mounted","path":"Workspaces\\Nova"}}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, """{"workspace":{"kind":"elsewhere"}}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, "not json", 503, "placement_resolution_unavailable")]
    public async Task Workspace_failures_keep_the_kernel_code_or_are_unavailable(HttpStatusCode status, string body, int expected, string code)
    {
        var kernel = new Kernel((_, _) => (status, body));

        var result = await new KernelPlacementClient(kernel.Client()).ResolveAsync(new WorkspacePlacement(AgentId));

        Assert.False(result.Ok);
        Assert.Equal((expected, code), (result.Error!.Status, result.Error.Code));
    }

    /// <summary>The probe asks for an Agent that cannot exist: the route's own refusal says the route is there; nothing is allocated or mounted.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"agent_not_found","message":"No Agent"}""", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"workspace_unavailable","message":"VFS down"}""", true)]
    [InlineData(HttpStatusCode.OK, """{"workspace":{"kind":"none"}}""", true)]
    [InlineData(HttpStatusCode.NotFound, "", false)]
    [InlineData(HttpStatusCode.NotFound, """{"error":"not_found"}""", false)]
    [InlineData(HttpStatusCode.InternalServerError, "boom", false)]
    public async Task The_probe_says_whether_the_kernel_resolves_placements(HttpStatusCode status, string body, bool answers)
    {
        var kernel = new Kernel((_, _) => (status, body));

        Assert.Equal(answers, await new KernelPlacementClient(kernel.Client()).ProbeAsync());
        Assert.Equal("""{"workspace":{"agentId":"00000000-0000-0000-0000-000000000000"}}""", Assert.Single(kernel.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"error":"validation_failed","message":"owner"}""", 422, "validation_failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"scratch_unavailable","message":"S: not mapped"}""", 503, "scratch_unavailable")]
    [InlineData(HttpStatusCode.NotFound, "", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.NotFound, """{"error":"agent_not_found"}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, """{"scratch":{"path":"S:\\Nova\\d1","environment":{"TEMP":"S:\\Nova\\d1"}}}""", 503, "placement_resolution_unavailable")]
    [InlineData(HttpStatusCode.OK, """{"scratch":{"path":"S:\\Nova\\d1"}}""", 503, "placement_resolution_unavailable")]
    public async Task Scratch_failures_keep_the_kernel_code_or_are_unavailable(HttpStatusCode status, string body, int expected, string code)
    {
        var kernel = new Kernel((_, _) => (status, body));

        var result = await new KernelPlacementClient(kernel.Client()).PrepareAsync(new ScratchPlacement("Nova", "d1"));

        Assert.Equal((expected, code), (result.Error!.Status, result.Error.Code));
    }

    [Fact]
    public async Task An_unreachable_kernel_is_unavailable_without_its_address()
    {
        var kernel = new Kernel((_, _) => throw new HttpRequestException("connection refused (127.0.0.1:18804)"));

        var result = await new KernelPlacementClient(kernel.Client()).PrepareAsync(new ScratchPlacement("Nova", "d1"));

        Assert.Equal("placement_resolution_unavailable", result.Error!.Code);
        Assert.DoesNotContain("18804", result.Error.Message);
    }

    // ── repositories: checkout, branch, subPath ─────────────────────────────────────────────────

    private RepositoryPlacementResolver Repositories(string? branchWork = "allowed", string status = "active",
        params object[] checkouts)
        => new(new Kernel((path, _) =>
        {
            if (path == $"/api/entities/{RepositoryId:D}")
                return (HttpStatusCode.OK, Entity(RepositoryId, "repository", new { status, local_path = Repo, branch_work = branchWork }));
            if (path.StartsWith("/api/entities?", StringComparison.Ordinal))
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new { items = checkouts.Select(c => JsonNode.Parse(Entity(c))).ToArray(), total = checkouts.Length }));
            var named = checkouts.Select(c => JsonNode.Parse(Entity(c))!).FirstOrDefault(c => "/api/entities/" + c["id"]!.GetValue<string>() == path);
            return named is null ? (HttpStatusCode.NotFound, """{"error":"not_found"}""") : (HttpStatusCode.OK, named.ToJsonString());
        }).Client());

    private static string Entity(Guid id, string type, object data) => JsonSerializer.Serialize(new
    {
        id, typeSlug = type, name = "songes", data = JsonSerializer.Serialize(data),
    });

    private static string Entity(object checkout) => checkout is string text ? text : throw new ArgumentException();

    private string Checkout(Guid id, string? nodeId, string? path = null, Guid? repository = null, string status = "active")
        => Entity(id, "repository-checkout", new { repository = (repository ?? RepositoryId).ToString("D"), node_id = nodeId, path = path ?? Repo, status });

    private Task<Resolution<RepositoryResolution>> Resolve(RepositoryPlacementResolver resolver, string? branch = null,
        string? subPath = null, Guid? checkoutId = null, bool legacyNode = true)
        => resolver.ResolveAsync(new RepositoryPlacement(RepositoryId, checkoutId, branch, subPath), NodeZero, legacyNode,
            LocalExecutionHost.Shared);

    [Fact]
    public async Task Without_checkout_entities_the_local_path_is_the_legacy_node_checkout()
    {
        var result = await Resolve(Repositories());

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(new RepositoryResolution(RepositoryId, "songes", null, Repo, Repo, null), result.Value);
    }

    [Fact]
    public async Task Without_checkout_entities_another_node_has_no_checkout()
    {
        var result = await Resolve(Repositories(), legacyNode: false);

        Assert.Equal((409, "checkout_not_on_node"), (result.Error!.Status, result.Error.Code));
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("laurent/x", true)]
    public async Task A_branch_resolves_with_git_to_the_checkout_or_its_linked_worktree(string branch, bool worktree)
    {
        var result = await Resolve(Repositories(), branch);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(worktree ? Worktree : Repo, result.Value!.WorkingDirectory);
        Assert.Equal(Repo, result.Value.CheckoutPath);
        Assert.Equal(branch, result.Value.Branch);
    }

    [Fact]
    public async Task A_branch_without_a_worktree_is_409_and_nothing_is_created()
    {
        var result = await Resolve(Repositories(), "feature/none");

        Assert.Equal((409, "worktree_not_found"), (result.Error!.Status, result.Error.Code));
        Assert.False(Directory.Exists(Path.Combine(_root, "songes.worktrees", "feature-none")));
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_gone_does_not_count()
    {
        Directory.Delete(Worktree, recursive: true);

        var result = await Resolve(Repositories(), "laurent/x");

        Assert.Equal((409, "worktree_not_found"), (result.Error!.Status, result.Error.Code));
    }

    // ── git's budget: a slow start is waited for and retried; only a real stall is a timeout ─────────

    private static readonly string[] WorktreeList = ["worktree", "list", "--porcelain"];

    private static ProcessStartInfo Hung() => new("ping", "-n 60 127.0.0.1")
    {
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
    };

    /// <summary>The real git, started after <paramref name="seconds"/> seconds (a loaded machine's slow process start).</summary>
    private static ProcessStartInfo Slow(ProcessStartInfo git, int seconds)
    {
        var quoted = string.Join(' ', git.ArgumentList.Select(a => '"' + a + '"'));
        return new ProcessStartInfo("cmd", $"/c ping -n {seconds + 1} 127.0.0.1 >nul & git {quoted}")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = git.WorkingDirectory,
        };
    }

    [Fact]
    public void Git_gets_more_than_the_old_ten_seconds_and_a_second_attempt()
    {
        Assert.True(RedCompute.App.Services.HostGit.Attempts >= 2);
        // The test assembly raises the limit for loaded hosts; the production default is what ships.
        Assert.Equal(TimeSpan.FromSeconds(15), RedCompute.App.Services.HostGit.DefaultTimeout);
        Assert.True(RedCompute.App.Services.HostGit.DefaultTimeout > TimeSpan.FromSeconds(10));
    }

    /// <summary>A git that starts after 11 s, longer than the old 10 s limit, still resolves under the production 15 s attempt.</summary>
    [WindowsFact("Needs a process that is slow to start")]
    public async Task A_git_slower_than_the_old_limit_still_resolves()
    {
        var host = new LocalExecutionHost("slow-git")
        {
            StartProcess = (start, _, _) => Process.Start(Slow(start, 11)),
        };

        var lines = await RedCompute.App.Services.HostGit.RunAsync(host, Repo, WorktreeList, CancellationToken.None,
            TimeSpan.FromSeconds(15));

        Assert.Contains(lines!, line => line == "branch refs/heads/laurent/x");
    }

    [WindowsFact("Needs a process that outlives the limit")]
    public async Task A_stalled_git_start_is_retried_once_and_the_retry_answers()
    {
        var starts = 0;
        var host = new LocalExecutionHost("stalled-git")
        {
            StartProcess = (start, _, _) => Process.Start(Interlocked.Increment(ref starts) == 1 ? Hung() : start),
        };

        var lines = await RedCompute.App.Services.HostGit.RunAsync(host, Repo, WorktreeList, CancellationToken.None,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(2, starts);
        Assert.Contains(lines!, line => line == "branch refs/heads/laurent/x");
    }

    [WindowsFact("Needs a process that outlives the limit")]
    public async Task A_git_that_stalls_on_every_attempt_is_a_timeout_after_exactly_two()
    {
        var starts = 0;
        var host = new LocalExecutionHost("hung-git")
        {
            StartProcess = (_, _, _) => { Interlocked.Increment(ref starts); return Process.Start(Hung()); },
        };

        await Assert.ThrowsAsync<TimeoutException>(() => RedCompute.App.Services.HostGit.RunAsync(host, Repo, WorktreeList,
            CancellationToken.None, TimeSpan.FromMilliseconds(300)));
        Assert.Equal(2, starts);
    }

    /// <summary>
    /// Through the resolver: a branch whose first git start stalls resolves on the retry instead of answering 503,
    /// which since stage B would fail a start that worked on its legacy fields.
    /// </summary>
    [WindowsFact("Needs a process that outlives the limit")]
    public async Task A_branch_resolves_when_its_first_git_stalls()
    {
        var starts = 0;
        var host = new LocalExecutionHost("stalled-git")
        {
            // A start that is slow, not hung: within the (raised) test limit, so this is the resolver's own path.
            StartProcess = (start, _, _) => Process.Start(Interlocked.Increment(ref starts) == 1 ? Slow(start, 2) : start),
        };

        var result = await Repositories().ResolveAsync(new RepositoryPlacement(RepositoryId, null, "laurent/x", null), NodeZero,
            legacyNode: true, host);

        Assert.True(result.Ok, $"answered {result.Error?.Code}: {result.Error?.Message}");
        Assert.Equal(Worktree, result.Value!.WorkingDirectory);
    }

    [Fact]
    public async Task A_main_only_repository_refuses_a_worktree_but_keeps_its_checkout()
    {
        var resolver = Repositories(branchWork: "main-only");

        var worktree = await Resolve(resolver, "laurent/x");
        Assert.True(worktree.Error?.Code == "main_only", $"answered {worktree.Error?.Code}: {worktree.Error?.Message}");
        var checkout = await Resolve(resolver, "main");
        Assert.True(checkout.Ok, $"answered {checkout.Error?.Code}: {checkout.Error?.Message}");
    }

    [Theory]
    [InlineData("inner/repo")]
    [InlineData("inner\\repo")]
    public async Task A_sub_path_is_a_folder_inside_the_checkout(string subPath)
    {
        var result = await Resolve(Repositories(), subPath: subPath);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(Path.Combine(Repo, "inner", "repo"), result.Value!.WorkingDirectory);
    }

    [Theory]
    [InlineData("inner/missing", 409, "project_path_missing")]
    [InlineData("../songes.worktrees", 422, "validation_failed")]
    public async Task A_sub_path_must_exist_inside_the_checkout(string subPath, int status, string code)
    {
        var result = await Resolve(Repositories(), subPath: subPath);

        Assert.Equal((status, code), (result.Error!.Status, result.Error.Code));
    }

    [Fact]
    public async Task The_one_checkout_entity_on_this_node_is_used()
    {
        var checkout = Guid.NewGuid();
        var resolver = Repositories("allowed", "active",
            Checkout(Guid.NewGuid(), "node_01JCZ000000000000000OTHER", "D:\\elsewhere"),
            Checkout(checkout, NodeZero));

        var result = await Resolve(resolver);

        Assert.Equal(checkout, result.Value!.CheckoutId);
        Assert.Equal(Repo, result.Value.WorkingDirectory);
    }

    [Fact]
    public async Task A_checkout_without_a_node_is_on_the_legacy_node()
    {
        var checkout = Guid.NewGuid();
        var resolver = Repositories("allowed", "active", Checkout(checkout, null));

        Assert.Equal(checkout, (await Resolve(resolver)).Value!.CheckoutId);
        Assert.Equal("checkout_not_on_node", (await Resolve(resolver, legacyNode: false)).Error!.Code);
    }

    [Fact]
    public async Task Several_checkouts_on_one_node_need_a_checkout_id()
    {
        var chosen = Guid.NewGuid();
        var resolver = Repositories("allowed", "active", Checkout(Guid.NewGuid(), NodeZero), Checkout(chosen, NodeZero));

        Assert.Equal((409, "checkout_ambiguous"), ((await Resolve(resolver)).Error!.Status, (await Resolve(resolver)).Error!.Code));
        Assert.Equal(chosen, (await Resolve(resolver, checkoutId: chosen)).Value!.CheckoutId);
    }

    [Fact]
    public async Task A_named_checkout_must_belong_to_the_repository_and_be_on_this_node()
    {
        var foreign = Guid.NewGuid();
        var remote = Guid.NewGuid();
        var resolver = Repositories("allowed", "active",
            Checkout(foreign, NodeZero, repository: Guid.NewGuid()),
            Checkout(remote, "node_01JCZ000000000000000OTHER"));

        Assert.Equal((422, "validation_failed"), Code(await Resolve(resolver, checkoutId: foreign)));
        Assert.Equal((409, "checkout_not_on_node"), Code(await Resolve(resolver, checkoutId: remote)));
        Assert.Equal((422, "validation_failed"), Code(await Resolve(resolver, checkoutId: Guid.NewGuid())));
    }

    [Theory]
    [InlineData("archived", 409, "repository_inactive")]
    public async Task An_inactive_repository_is_refused(string status, int code, string error)
        => Assert.Equal((code, error), Code(await Resolve(Repositories(status: status))));

    [Fact]
    public async Task An_unknown_repository_is_404()
    {
        var resolver = new RepositoryPlacementResolver(new Kernel((_, _) => (HttpStatusCode.NotFound, "")).Client());

        Assert.Equal((404, "repository_not_found"), Code(await Resolve(resolver)));
    }

    [Fact]
    public async Task A_missing_checkout_folder_is_409()
    {
        DeleteTree(Repo);

        Assert.Equal((409, "project_path_missing"), Code(await Resolve(Repositories())));
    }

    [Theory]
    [InlineData(@"D:\Scratch\RedCompute\p\songes", @"S:\RedCompute\p\songes", @"D:\Scratch\RedCompute\p\songes.worktrees\x", @"S:\RedCompute\p\songes.worktrees\x")]
    [InlineData(@"T:\Projects\songes", @"T:\Projects\songes", @"T:\Projects\songes.worktrees\x", @"T:\Projects\songes.worktrees\x")]
    [InlineData(@"D:\Scratch\songes", @"S:\songes", @"E:\elsewhere\x", @"E:\elsewhere\x")]
    [InlineData(@"D:\a\songes", @"S:\b\other", @"D:\a\songes.worktrees\x", @"D:\a\songes.worktrees\x")]
    public void A_linked_worktree_is_named_in_the_checkout_form(string gitCheckout, string checkout, string gitWorktree, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, RepositoryPlacementResolver.InCheckoutForm(LocalExecutionHost.Shared, gitCheckout, checkout, gitWorktree));
    }

    [Fact]
    public void Porcelain_records_keep_branch_and_prunable()
    {
        var worktrees = RepositoryPlacementResolver.ParseWorktrees([
            "worktree T:/Projects/songes", "HEAD 1111", "branch refs/heads/main", "",
            "worktree T:/Projects/songes.worktrees/laurent-x", "HEAD 2222", "branch refs/heads/laurent/x", "",
            "worktree T:/Projects/songes.worktrees/gone", "HEAD 3333", "branch refs/heads/gone", "prunable gitdir file points to non-existent location", "",
            "worktree T:/Projects/songes.worktrees/detached", "HEAD 4444", "detached",
        ]);

        Assert.Equal([
            new RepositoryPlacementResolver.Worktree("T:/Projects/songes", "refs/heads/main", false),
            new RepositoryPlacementResolver.Worktree("T:/Projects/songes.worktrees/laurent-x", "refs/heads/laurent/x", false),
            new RepositoryPlacementResolver.Worktree("T:/Projects/songes.worktrees/gone", "refs/heads/gone", true),
            new RepositoryPlacementResolver.Worktree("T:/Projects/songes.worktrees/detached", null, false),
        ], worktrees);
    }

    // ── mapping onto today's launch inputs ──────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> ScratchEnvironment = new Dictionary<string, string>
    {
        ["REDLEAF_SCRATCH_ROOT"] = "S:\\nova\\d1", ["REDLEAF_SCRATCH_DIR"] = "S:\\nova\\d1",
        ["TEMP"] = "S:\\nova\\d1", ["TMP"] = "S:\\nova\\d1", ["TMPDIR"] = "S:\\nova\\d1",
    };

    private sealed class Fixed(WorkspaceResolution? workspace = null) : IWorkspaceResolver, IHostScratch
    {
        public List<object> Calls { get; } = [];

        public Task<Resolution<WorkspaceResolution>> ResolveAsync(WorkspacePlacement value, CancellationToken ct = default)
        {
            Calls.Add(value);
            return Task.FromResult(Resolution<WorkspaceResolution>.Of(workspace ?? new("mounted", "L:\\Workspaces\\Nova")));
        }

        public Task<Resolution<ScratchAllocation>> PrepareAsync(ScratchPlacement value, CancellationToken ct = default)
        {
            Calls.Add(value);
            return Task.FromResult(Resolution<ScratchAllocation>.Of(new("S:\\nova\\d1", ScratchEnvironment)));
        }
    }

    private static NodeRegistry Nodes() => new(
        new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);

    private static PlacementRequest Placement(string json, PlacementRoute route)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(PlacementParser.TryParseValue(document.RootElement, route, false, out var placement, out var error), error?.Message);
        return placement!;
    }

    [Fact]
    public async Task A_workspace_with_scratch_maps_to_project_path_and_scratch_dir()
    {
        var kernel = new Fixed();
        var result = await new PlacementResolver(Nodes(), kernel, kernel, null).ResolveAsync(Placement(
            $$$"""{"workspace":{"agentId":"{{{AgentId}}}"},"scratch":{"owner":"nova","executionKey":"d1"}}""", PlacementRoute.Session),
            PlacementRoute.Session, null);

        var resolved = result.Value!;
        Assert.Equal(NodeZero, resolved.NodeId);
        Assert.Equal("L:\\Workspaces\\Nova", resolved.WorkingDirectory);
        Assert.Equal("S:\\nova\\d1", resolved.ScratchDirectory);
        Assert.Same(ScratchEnvironment, resolved.ScratchEnvironment);
        Assert.Null(resolved.AddDirs);
        Assert.Null(resolved.RepositoryId);
    }

    [Fact]
    public async Task An_agent_without_a_workspace_works_in_its_scratch_folder()
    {
        var kernel = new Fixed(new WorkspaceResolution("none", null));
        var resolver = new PlacementResolver(Nodes(), kernel, kernel, null);

        var withScratch = await resolver.ResolveAsync(Placement(
            $$$"""{"workspace":{"agentId":"{{{AgentId}}}"},"scratch":{"owner":"nova","executionKey":"d1"}}""", PlacementRoute.Session),
            PlacementRoute.Session, null);
        var without = await resolver.ResolveAsync(Placement(
            $$$"""{"workspace":{"agentId":"{{{AgentId}}}"}}""", PlacementRoute.Session), PlacementRoute.Session, null);

        Assert.Equal("S:\\nova\\d1", withScratch.Value!.WorkingDirectory);
        Assert.Equal((422, "validation_failed"), (without.Error!.Status, without.Error.Code));
    }

    [Theory]
    [InlineData("env", null, null)]
    [InlineData("add-dir", null, "S:\\nova\\d1")]
    [InlineData("cwd", "S:\\nova\\d1", null)]
    public async Task Scratch_use_maps_onto_the_execute_inputs(string scratchAs, string? workingDirectory, string? addDir)
    {
        var kernel = new Fixed();
        var json = $$$"""{"nodeId":"local","scratch":{"owner":"nova","executionKey":"d1"},"scratchAs":"{{{scratchAs}}}"}""";

        var resolved = (await new PlacementResolver(Nodes(), kernel, kernel, null)
            .ResolveAsync(Placement(json, PlacementRoute.Execute), PlacementRoute.Execute, null)).Value!;

        Assert.Equal(workingDirectory, resolved.WorkingDirectory);
        Assert.Equal(addDir is null ? null : [addDir], resolved.AddDirs);
        Assert.Equal("S:\\nova\\d1", resolved.ScratchDirectory);
        Assert.Same(ScratchEnvironment, resolved.ScratchEnvironment);
    }

    [Fact]
    public async Task A_repository_maps_to_its_working_directory_and_id()
    {
        var resolved = (await new PlacementResolver(Nodes(), null, null, Repositories())
            .ResolveAsync(new PlacementRequest(null, null, new RepositoryPlacement(RepositoryId, null, "laurent/x", null), null),
                PlacementRoute.Session, null)).Value!;

        Assert.Equal(Worktree, resolved.WorkingDirectory);
        Assert.Equal(RepositoryId, resolved.RepositoryId);
        Assert.Null(resolved.ScratchDirectory);
    }

    [Fact]
    public async Task A_missing_resolver_or_an_unknown_node_refuses_before_asking_anyone()
    {
        var kernel = new Fixed();

        var unknown = await new PlacementResolver(Nodes(), kernel, kernel, null)
            .ResolveAsync(new PlacementRequest("node_01JCZ000000000000000OTHER", new WorkspacePlacement(AgentId), null, null), PlacementRoute.Session, null);
        var noKernel = await new PlacementResolver(Nodes(), null, null, null)
            .ResolveAsync(new PlacementRequest(null, new WorkspacePlacement(AgentId), null, null), PlacementRoute.Session, null);

        Assert.Equal("node_not_found", unknown.Error!.Code);
        Assert.Empty(kernel.Calls);
        Assert.Equal((503, "placement_resolution_unavailable"), (noKernel.Error!.Status, noKernel.Error.Code));
    }

    // ── scratch validation follows the host's path rules (defect 7.6) ───────────────────────────

    [Fact]
    public void Scratch_directories_are_qualified_and_checked_by_the_host_not_by_this_server()
    {
        var host = PosixHost.Create("/srv/scratch/nova");

        Assert.True(SessionScratch.TryResolveDirectory(host, "/srv/scratch/nova", out var directory));
        Assert.Equal("/srv/scratch/nova", directory);
        Assert.False(SessionScratch.TryResolveDirectory(host, "C:\\scratch", out _));
        Assert.False(SessionScratch.TryResolveDirectory(host, "/srv/scratch/missing", out _));
    }

    /// <summary>A host with POSIX path rules and one existing folder, whatever this server runs on.</summary>
    public class PosixHost : DispatchProxy
    {
        private string _existing = "";

        public static IExecutionHost Create(string existing)
        {
            var host = DispatchProxy.Create<IExecutionHost, PosixHost>();
            ((PosixHost)(object)host)._existing = existing;
            return host;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_NodeId" => "posix-node",
            nameof(IExecutionHost.Path) => new HostPath("posix-node", (string)args![0]!),
            "get_PathRules" => new PosixRules(),
            "get_Files" => PosixFiles.Create(_existing),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    private sealed class PosixRules : IHostPathRules
    {
        public char DirectorySeparator => '/';
        public StringComparison Comparison => StringComparison.Ordinal;
        public bool IsPathFullyQualified(string path) => path.StartsWith('/');
        public string GetFullPath(string path) => path;
        public string Combine(params string[] parts) => string.Join('/', parts);
    }

    public class PosixFiles : DispatchProxy
    {
        private string _existing = "";

        public static IHostFileSystem Create(string existing)
        {
            var files = DispatchProxy.Create<IHostFileSystem, PosixFiles>();
            ((PosixFiles)(object)files)._existing = existing;
            return files;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IHostFileSystem.DirectoryExists) => ((HostPath)args![0]!).Value == _existing,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static (int, string) Code<T>(Resolution<T> result) where T : class => (result.Error!.Status, result.Error.Code);

    /// <summary>RedLeaf answering by path (and query), recording every request.</summary>
    private sealed class Kernel(Func<string, string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Requests { get; } = [];

        public HttpClient Client() => new(this) { BaseAddress = new Uri("http://redleaf/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add((request.Method.Method, path, body));
            var (status, text) = answer(path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
    }

    private static void DeleteTree(string directory)
    {
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(directory, recursive: true);
    }

    public void Dispose()
    {
        try { DeleteTree(_root); }
        catch { }
    }
}
