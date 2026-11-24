using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services;
using RedCompute.App.Services.Placement;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Stage B of placement by reference (M1): with the authority on, a start that carries <c>placement</c> runs on
/// what it resolves to, legacy path fields beside it are only compared, and a start with no placement is what
/// it always was. The same fixtures and goldens as stage A: for each creation path the placement-only body
/// must produce the capture its legacy body does. Two differences are deliberate and named where they are
/// handled: Code's browser names its folders with forward slashes and the resolved folder is written with the
/// node's own separator (the same folder), and the Sentinel's scratch folder arrives with the scratch
/// variables its placement implies (design 2.1, scratchAs cwd).
/// </summary>
public sealed partial class PlacementEquivalenceTests
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private PlacementAuthority Authority(bool mounted = true, Func<CancellationToken, Task<bool>>? probe = null)
    {
        var places = new FixturePlaces(_root, mounted);
        var repositories = new RepositoryPlacementResolver(new HttpClient(new RepositoryEntity(Path.Combine(_root, "songes")))
        {
            BaseAddress = new Uri("http://redleaf/"),
        });
        return new PlacementAuthority(new PlacementResolver(null, places, places, repositories), probe, (_, _) => { });
    }

    /// <summary>The fixture as a stage-C caller sends it: its non-path members and its placement alone.</summary>
    private static string PlacementOnly(string name)
    {
        var body = JsonNode.Parse(Fixtures[name].Body)!.AsObject();
        foreach (var key in new[] { "projectPath", "repositoryId", "scratchDir", "workingDir", "addDirs" }) body.Remove(key);
        if (body["env"] is JsonObject env)
        {
            foreach (var variable in PlacementShadow.ScratchVariables) env.Remove(variable);
            if (env.Count == 0) body.Remove("env");
        }
        body["placement"] = JsonNode.Parse(StageAPlacements[name]);
        return body.ToJsonString(Relaxed);
    }

    /// <summary>
    /// The capture with the two deliberate differences put back to what the legacy body recorded, after
    /// asserting them (see the class summary).
    /// </summary>
    private static JsonNode Settled(string name, JsonNode capture)
    {
        var provider = capture["provider"]!.AsObject();
        if (name.StartsWith("code-session-", StringComparison.Ordinal))
            provider["projectPath"] = provider["projectPath"]!.GetValue<string>().Replace('\\', '/');
        if (name == "sentinel-review")
        {
            var environment = Assert.IsType<JsonObject>(provider["environment"]);
            Assert.Equal(PlacementShadow.ScratchVariables.Order(), environment.Select(pair => pair.Key).Where(k => k != "REDLEAF_EXECUTION_TOKEN").Order());
            provider["environment"] = null;
            provider["providerParams"]!.AsObject()["addDirs"] = new JsonArray();
        }
        return capture;
    }

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(StageACases))]
    public async Task A_placement_only_start_runs_where_its_legacy_body_ran(string name)
        => PlacementGoldens.Verify(name, Settled(name,
            await CaptureAsync(Fixtures[name], PlacementOnly(name), authority: Authority(name != "nova-discussion-disposable-workspace"))));

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(StageACases))]
    public async Task A_start_with_both_runs_on_the_placement_and_agrees_with_its_legacy_fields(string name)
        => PlacementGoldens.Verify(name, Settled(name,
            await CaptureAsync(Fixtures[name], WithPlacement(Fixtures[name], StageAPlacements[name]),
                authority: Authority(name != "nova-discussion-disposable-workspace"))));

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(Cases))]
    public async Task A_legacy_start_is_unchanged_while_placement_is_authoritative(string name)
        => PlacementGoldens.Verify(name, await CaptureAsync(Fixtures[name], Fixtures[name].Body, authority: Authority()));

    // ── the deterministic body Code's browser sends (codered 093374e, add3170) ───────────────────

    /// <summary>
    /// What <c>startSession</c> in codered's use-claude.ts posts (legacy members, then placement last): a checkout,
    /// a worktree with its branch, a node the person picked with the checkout on it, and no legacy path at all.
    /// Code sends <c>qualityTier: "standard"</c>, which picks the model and needs the suite's quality catalog
    /// (503 without it); it never touches a path, so a fixed <c>model</c> stands in for it here.
    /// </summary>
    public static TheoryData<string, string, string> CodeBrowserStarts() => new()
    {
        { "checkout", """{"projectPath":"<root/>/songes","repositoryId":"7e1e0000-0000-4000-8000-000000000001","model":"claude-sonnet","placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}}""", "songes" },
        { "worktree", """{"projectPath":"<root/>/songes.worktrees/laurent-x","repositoryId":"7e1e0000-0000-4000-8000-000000000001","model":"claude-sonnet","provider":"claude-code","placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"laurent/x"}}}""", "songes.worktrees\\laurent-x" },
        { "run-on", """{"projectPath":"<root/>/songes","repositoryId":"7e1e0000-0000-4000-8000-000000000001","model":"claude-sonnet","placement":{"nodeId":"local","repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","checkoutId":"7e1e0000-0000-4000-8000-0000000000c1"}}}""", "songes" },
        { "placement-only", """{"model":"claude-sonnet","placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}}""", "songes" },
    };

    [WindowsTheory("Paths are Windows paths")]
    [MemberData(nameof(CodeBrowserStarts))]
    public async Task Code_browser_start_runs_in_the_resolved_checkout_or_worktree(string kind, string body, string folder)
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), body: body, authority: Authority());

        Assert.True(result.Status == 200, $"{kind}: {result.Status} {result.Response}");
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(Path.Combine(_root, folder), call["projectPath"]!.GetValue<string>());
        Assert.Equal(RepositoryId, call["repositoryId"]!.GetValue<string>());
        Assert.Null(call["scratchDirectory"]);
        Assert.Null(call["environment"]);
        using var response = JsonDocument.Parse(result.Response);
        Assert.Equal(Path.Combine(_root, folder), response.RootElement.GetProperty("placementResolved").GetProperty("cwd").GetString());
    }

    // ── a Queue job from the dashboard (placement.nodeId only) ───────────────────────────────────

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [InlineData("""{"prompt":"Queue job from the dashboard","timeout":600}""")]
    [InlineData("""{"prompt":"Queue job from the dashboard","timeout":600,"placement":{"nodeId":"local"}}""")]
    [InlineData("""{"prompt":"Queue job from the dashboard","timeout":600,"placement":{"nodeId":null}}""")]
    public async Task A_queue_job_runs_as_it_did_with_or_without_its_node(string body)
    {
        var legacy = new PlacementCase("/ai-session/execute", """{"prompt":"Queue job from the dashboard","timeout":600}""");
        PlacementGoldens.Verify("dashboard-queue-job", await CaptureAsync(legacy, body, authority: Authority()));
    }

    // ── conflicts ────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, string, string> Conflicts() => new()
    {
        // a session whose projectPath is the checkout but whose placement is an Agent's workspace
        { "nova-delegate-repository", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}""", "", "cwd" },
        // a session whose scratchDir is not the scratch the placement allocates
        { "nova-discussion-mounted-workspace", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"target"}}""", "\"scratchDir\":\"<root>\\\\scratch\"|\"scratchDir\":\"<root>\\\\other\"", "scratch" },
        // a session that names a different repository
        { "nova-delegate-repository", """{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""", "\"repositoryId\":\"7e1e0000-0000-4000-8000-000000000001\"|\"repositoryId\":\"7e1e0000-0000-4000-8000-0000000000ff\"", "repositoryId" },
        // a one-shot whose workingDir is not the workspace
        { "nova-automation-session", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"attempt-1"},"scratchAs":"add-dir"}""", "\"workingDir\":\"<root>\\\\workspace\"|\"workingDir\":\"<root>\\\\scratch\"", "cwd" },
        // a one-shot whose scratch variables point elsewhere
        { "nova-automation-session", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"attempt-1"},"scratchAs":"add-dir"}""", "\"TEMP\":\"<root>\\\\scratch\"|\"TEMP\":\"<root>\\\\other\"", "env" },
        // a one-shot whose addDirs lack the scratch folder the placement adds
        { "nova-automation-session", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"attempt-1"},"scratchAs":"add-dir"}""", "\"addDirs\":[\"<root>\\\\scratch\"]|\"addDirs\":[\"<root>\\\\other\"]", "addDirs" },
    };

    [WindowsTheory("Paths are Windows paths")]
    [MemberData(nameof(Conflicts))]
    public async Task Legacy_fields_that_disagree_with_the_placement_are_a_409_and_start_nothing(string name, string placement,
        string change, string field)
    {
        var fixture = Fixtures[name];
        var legacy = fixture with { Body = change.Length == 0 ? fixture.Body : fixture.Body.Replace(change.Split('|')[0], change.Split('|')[1]) };
        Assert.True(legacy.Body != fixture.Body || change.Length == 0, "the fixture no longer contains " + change);
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(legacy, jobs => recorder.Jobs = jobs, recorder.Providers(),
            body: WithPlacement(legacy, placement), authority: Authority());

        Assert.Equal(409, result.Status);
        using var response = JsonDocument.Parse(result.Response);
        Assert.Equal("placement_conflict", response.RootElement.GetProperty("error").GetString());
        Assert.Equal(field, Assert.Single(response.RootElement.GetProperty("fields").EnumerateObject()).Name);
        Assert.DoesNotContain(_root, result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Calls);
        Assert.Null(result.JobInput);
    }

    // ── the error table, through the endpoint ───────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-0000000000ff"}}""", 404, "repository_not_found")]
    [InlineData("""{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000002"}}""", 409, "repository_inactive")]
    [InlineData("""{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"no/such"}}""", 409, "worktree_not_found")]
    [InlineData("""{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","checkoutId":"7e1e0000-0000-4000-8000-0000000000ee"}}""", 422, "validation_failed")]
    [InlineData("""{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"cwd","workspace":null}""", 200, null)]
    public async Task A_placement_that_cannot_be_resolved_answers_the_error_table_and_starts_nothing(string placement, int status,
        string? code)
    {
        var recorder = new RecordingProvider();
        var fixture = new PlacementCase("/ai-session/sessions", """{"qualityTier":null}""");
        var result = await InvokeAsync(fixture, jobs => recorder.Jobs = jobs, recorder.Providers(),
            body: """{"placement":""" + placement + "}", authority: Authority());

        if (code is null)
        {
            Assert.Equal(status, result.Status);
            return;
        }
        Assert.Equal(status, result.Status);
        using var response = JsonDocument.Parse(result.Response);
        Assert.Equal(code, response.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(_root, result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task A_session_without_a_folder_from_either_side_is_still_the_projectPath_error()
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), body: """{"placement":{"nodeId":"local"}}""", authority: Authority());

        Assert.Equal(422, result.Status);
        Assert.Contains("projectPath is required", result.Response);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task An_unreachable_kernel_is_a_503_not_a_guess()
    {
        var places = new FixturePlaces(_root, mounted: true) { Fail = true };
        var authority = new PlacementAuthority(new PlacementResolver(null, places, places, null), null, (_, _) => { });
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), body: """{"projectPath":"x","placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}}""",
            authority: authority);

        Assert.Equal(503, result.Status);
        Assert.Contains("placement_resolution_unavailable", result.Response);
        Assert.Empty(recorder.Calls);
    }

    // ── stage A stays while the kernel does not resolve placements ─────────────────────────────

    [WindowsFact("Placement goldens are recorded on Windows (path bytes, separators included)")]
    public async Task While_the_kernel_does_not_answer_the_legacy_fields_decide_and_placement_v1_is_not_advertised()
    {
        var authority = Authority(probe: _ => Task.FromResult(false));
        await authority.RefreshAsync();
        Assert.False(authority.Active);
        Assert.Empty(authority.Features);

        var name = "nova-delegate-branch-worktree";
        PlacementGoldens.Verify(name, await CaptureAsync(Fixtures[name], WithPlacement(Fixtures[name], StageAPlacements[name]),
            authority: authority));
    }

    [Fact]
    public async Task The_authority_follows_the_kernel_probe_and_a_probe_that_throws_is_no()
    {
        var answer = true;
        var authority = Authority(probe: _ => answer ? Task.FromResult(true) : throw new HttpRequestException("down"));
        Assert.False(authority.Active);

        await authority.RefreshAsync();
        Assert.True(authority.Active);
        Assert.Equal([PlacementAuthority.Feature], authority.Features);

        answer = false;
        await authority.RefreshAsync();
        Assert.False(authority.Active);
        Assert.Empty(authority.Features);
    }

    // ── references: parsed, validated, resolved read-only, exposed, never mounted ─────────────

    private const string Worktree = """{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"laurent/x"}""";
    private const string References = $$$"""[{"repository":{{{Worktree}}}},{"workspace":{"agentId":"{{{AgentId}}}"}}]""";

    [WindowsFact("Paths are Windows paths")]
    public async Task References_resolve_to_read_only_folders_in_the_start_response_and_start_nothing_new()
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), authority: Authority(), body: $$$"""
                {"placement":{"repository":{"repositoryId":"{{{RepositoryId}}}"},"references":{{{References}}}}}
                """);

        Assert.True(result.Status == 200, result.Response);
        // Nothing new is mounted: the session sees only its own folder.
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(Path.Combine(_root, "songes"), call["projectPath"]!.GetValue<string>());
        Assert.Null(call["environment"]);
        using var response = JsonDocument.Parse(result.Response);
        var references = response.RootElement.GetProperty("placementResolved").GetProperty("references").EnumerateArray().ToList();
        Assert.Equal(2, references.Count);
        Assert.Equal(("repository", RepositoryId, "laurent/x", Path.Combine(_root, "songes.worktrees", "laurent-x"), "read"),
            (references[0].GetProperty("kind").GetString(), references[0].GetProperty("id").GetString(),
                references[0].GetProperty("branch").GetString(), references[0].GetProperty("path").GetString(),
                references[0].GetProperty("access").GetString()));
        Assert.Equal(("workspace", AgentId, Path.Combine(_root, "workspace"), "read"),
            (references[1].GetProperty("kind").GetString(), references[1].GetProperty("id").GetString(),
                references[1].GetProperty("path").GetString(), references[1].GetProperty("access").GetString()));
    }

    [Theory]
    [InlineData("7e1e0000-0000-4000-8000-0000000000ff")]   // unknown
    [InlineData("7e1e0000-0000-4000-8000-000000000002")]   // inactive: unreadable
    public async Task An_unknown_or_unreadable_reference_is_one_and_the_same_404(string repositoryId)
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), authority: Authority(), body: $$$"""
                {"projectPath":"x","placement":{"references":[{"repository":{"repositoryId":"{{{repositoryId}}}"}}]}}
                """);

        Assert.Equal(404, result.Status);
        using var response = JsonDocument.Parse(result.Response);
        Assert.Equal("reference_not_found", response.RootElement.GetProperty("error").GetString());
        Assert.Equal("not found", response.RootElement.GetProperty("fields").GetProperty("placement.references[0]").GetString());
        // Same text whichever it was: the id is not even echoed.
        Assert.DoesNotContain(repositoryId, result.Response);
        Assert.DoesNotContain(_root, result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public void A_stored_placement_keeps_its_references_as_ids_only()
    {
        using var document = JsonDocument.Parse($$$"""{"placement":{"repository":{"repositoryId":"{{{RepositoryId}}}"},"references":{{{References}}}}}""");
        Assert.True(PlacementParser.TryParse(document.RootElement, PlacementRoute.Session, false, out var placement, out _));
        Assert.Equal($$$"""{"nodeId":null,"repository":{"repositoryId":"{{{RepositoryId}}}"},"references":{{{References}}}}""",
            placement!.ToJson().ToJsonString());
    }
}
