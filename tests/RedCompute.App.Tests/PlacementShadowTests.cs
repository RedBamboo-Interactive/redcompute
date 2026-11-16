using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services.Placement;
using RedCompute.PluginSdk.Hosting;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Stage A, Layer 3 (design 6 and 8.1): RedCompute resolves a start's placement off the request path,
/// compares it field by field with the legacy fields that were used, and counts per caller app, route
/// and placement shape, through restarts and without a path anywhere in what it keeps or serves. The
/// endpoint side (every caller's stage-A body) is in <see cref="PlacementEquivalenceTests"/>.
/// </summary>
public sealed class PlacementShadowTests : IDisposable
{
    private const string Workspace = @"L:\Workspaces\Nova";
    private const string Scratch = @"S:\nova\d1";
    private static readonly Guid RepositoryId = Guid.Parse("7e1e0000-0000-4000-8000-000000000001");
    private static readonly Guid AgentId = Guid.Parse("7e1e0000-0000-4000-8000-0000000000a1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-placement-shadow", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public PlacementShadowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── one field ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a\b", ShadowOutcome.Exact)]
    [InlineData("C:/a/b", @"C:\a\b", ShadowOutcome.Equivalent)]
    [InlineData(@"c:\A\b", @"C:\a\b", ShadowOutcome.Equivalent)]
    [InlineData(@"C:\a\b\", @"C:\a\b", ShadowOutcome.Equivalent)]
    [InlineData(@"C:\", @"C:\", ShadowOutcome.Exact)]
    [InlineData(@"C:\", "C:/", ShadowOutcome.Equivalent)]
    [InlineData(@"C:\a", @"C:\b", ShadowOutcome.Mismatch)]
    [InlineData(@"C:\a", @"C:\a\b", ShadowOutcome.Mismatch)]
    [InlineData(null, @"C:\a", ShadowOutcome.Mismatch)]
    [InlineData(@"C:\a", null, ShadowOutcome.Mismatch)]
    public void A_path_on_a_windows_node_compares_by_its_rules(string? used, string? placed, string expected)
        => Assert.Equal(expected, PlacementShadow.ComparePaths(used, placed, new WindowsRules()));

    [Theory]
    [InlineData("/srv/a", "/srv/a", ShadowOutcome.Exact)]
    [InlineData("/srv/a/", "/srv/a", ShadowOutcome.Equivalent)]
    [InlineData("/srv/A", "/srv/a", ShadowOutcome.Mismatch)]
    [InlineData(@"/srv\a", "/srv/a", ShadowOutcome.Mismatch)]
    public void A_path_on_a_posix_node_is_case_and_backslash_sensitive(string used, string placed, string expected)
        => Assert.Equal(expected, PlacementShadow.ComparePaths(used, placed, new PosixRules()));

    // ── the fields of each route ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_session_compares_cwd_repository_and_scratch()
    {
        var resolved = Resolved(@"T:\songes", Scratch, RepositoryId);

        var fields = PlacementShadow.Compare(resolved,
            new(@"T:\songes", Scratch, RepositoryId.ToString("D"), ["ignored"], Env("X:\\other")), PlacementRoute.Session, new WindowsRules());

        Assert.Equal(new Dictionary<string, string>
        {
            ["cwd"] = ShadowOutcome.Exact, ["repositoryId"] = ShadowOutcome.Exact, ["scratch"] = ShadowOutcome.Exact,
        }, fields);
    }

    [Fact]
    public void A_field_neither_side_names_is_left_out()
    {
        var fields = PlacementShadow.Compare(Resolved(Workspace, null, null), new(Workspace, null, null, null, null),
            PlacementRoute.Session, new WindowsRules());

        Assert.Equal(new[] { "cwd" }, fields.Keys);
    }

    [Theory]
    [InlineData("7E1E0000-0000-4000-8000-000000000001", ShadowOutcome.Equivalent)]
    [InlineData("7e1e0000-0000-4000-8000-000000000002", ShadowOutcome.Mismatch)]
    [InlineData(null, ShadowOutcome.Mismatch)]
    public void A_session_repository_id_compares_as_an_id(string? used, string expected)
    {
        var fields = PlacementShadow.Compare(Resolved(@"T:\songes", null, RepositoryId), new(@"T:\songes", null, used, null, null),
            PlacementRoute.Session, new WindowsRules());

        Assert.Equal(expected, fields["repositoryId"]);
    }

    [Fact]
    public void An_execute_compares_cwd_add_dirs_and_the_scratch_keys_of_env_never_a_repository()
    {
        var resolved = Resolved(@"T:\songes", Scratch, RepositoryId, addDirs: [Scratch], env: Env(Scratch));
        var env = new Dictionary<string, string>(Env(Scratch)) { ["OPENCODE_CONFIG_DIR"] = @"C:\elsewhere" };

        var fields = PlacementShadow.Compare(resolved, new(@"T:\songes", null, null, [Scratch], env),
            PlacementRoute.Execute, new WindowsRules());

        Assert.Equal(new Dictionary<string, string>
        {
            ["addDirs"] = ShadowOutcome.Exact, ["cwd"] = ShadowOutcome.Exact, ["env"] = ShadowOutcome.Exact,
        }, fields);
    }

    [Theory]
    [InlineData(new string[0], null, ShadowOutcome.Equivalent)]
    [InlineData(new string[0], new string[0], ShadowOutcome.Exact)]
    [InlineData(new[] { @"S:\nova\d1\" }, new[] { @"S:\nova\d1" }, ShadowOutcome.Equivalent)]
    [InlineData(new[] { @"S:\nova\d1" }, null, ShadowOutcome.Mismatch)]
    [InlineData(new[] { @"S:\nova\d1", @"S:\x" }, new[] { @"S:\nova\d1" }, ShadowOutcome.Mismatch)]
    [InlineData(new[] { @"S:\x", @"S:\nova\d1" }, new[] { @"S:\nova\d1", @"S:\x" }, ShadowOutcome.Mismatch)]
    public void Add_dirs_compare_in_order_and_an_empty_list_is_none(string[] used, string[]? placed, string expected)
    {
        var fields = PlacementShadow.Compare(Resolved(null, null, null, addDirs: placed), new(null, null, null, used, null),
            PlacementRoute.Execute, new WindowsRules());

        Assert.Equal(expected, fields["addDirs"]);
    }

    [Fact]
    public void Env_takes_the_worst_scratch_key_and_a_missing_side_is_a_mismatch()
    {
        var oneOff = new Dictionary<string, string>(Env(Scratch)) { ["TMP"] = @"C:\Temp" };
        var slashed = new Dictionary<string, string>(Env(Scratch)) { ["TEMP"] = "S:/nova/d1" };
        var lower = Env(Scratch).ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value);

        Assert.Equal(ShadowOutcome.Mismatch, EnvOutcome(oneOff, Env(Scratch)));
        Assert.Equal(ShadowOutcome.Equivalent, EnvOutcome(slashed, Env(Scratch)));
        Assert.Equal(ShadowOutcome.Exact, EnvOutcome(lower, Env(Scratch)));
        Assert.Equal(ShadowOutcome.Mismatch, EnvOutcome(null, Env(Scratch)));
        Assert.Equal(ShadowOutcome.Mismatch, EnvOutcome(Env(Scratch), null));
        Assert.Null(EnvOutcome(new Dictionary<string, string> { ["OPENCODE_CONFIG_DIR"] = "C:\\x" }, null));
    }

    private static string? EnvOutcome(IReadOnlyDictionary<string, string>? used, IReadOnlyDictionary<string, string>? placed)
        => PlacementShadow.Compare(Resolved(null, null, null, env: placed), new(null, null, null, null, used),
            PlacementRoute.Execute, new WindowsRules()).GetValueOrDefault("env");

    // ── counting ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_creation_path_is_counted_and_the_counts_survive_a_restart()
    {
        var store = Store();
        var fixedPlaces = new Fixed();
        var shadow = Shadow(fixedPlaces, store: store);
        var nova = Placement(workspace: true, scratch: true);

        shadow.Observe(nova, PlacementRoute.Session, "nova", new(Workspace, Scratch, null, null, null), new WindowsRules());
        shadow.Observe(nova, PlacementRoute.Session, "nova", new("L:/Workspaces/Nova", Scratch, null, null, null), new WindowsRules());
        shadow.Observe(nova, PlacementRoute.Session, "nova", new(@"L:\Workspaces\Other", Scratch, null, null, null), new WindowsRules());
        shadow.Observe(Placement(workspace: true, scratch: true, scratchAs: "add-dir"), PlacementRoute.Execute, "redleaf",
            new(Workspace, null, null, [Scratch], Env(Scratch)), new WindowsRules());
        await shadow.IdleAsync();

        var status = Shadow(fixedPlaces, store: store).Status();
        Assert.Equal(2, status.Paths.Count);
        var sessions = Assert.Single(status.Paths, p => p.App == "nova");
        Assert.Equal(("sessions", "workspace+scratch(env)", 3L, 3L), (sessions.Route, sessions.Shape, sessions.Observed, sessions.Compared));
        Assert.Equal((1L, 1L, 1L), (sessions.Fields["cwd"].Exact, sessions.Fields["cwd"].Equivalent, sessions.Fields["cwd"].Mismatch));
        Assert.Equal(3L, sessions.Fields["scratch"].Exact);
        Assert.NotNull(sessions.LastMismatchAt);
        var execute = Assert.Single(status.Paths, p => p.App == "redleaf");
        Assert.Equal(("execute", "workspace+scratch(add-dir)"), (execute.Route, execute.Shape));
        Assert.Equal(new[] { "addDirs", "cwd", "env" }, execute.Fields.Keys);
        Assert.All(execute.Fields.Values, counts => Assert.Equal(1L, counts.Exact));
        Assert.Null(execute.LastMismatchAt);
        Assert.Equal(new ShadowTotals(4, 4, 0, 0, 1, 7, 1, 1), status.Totals);
        Assert.NotNull(status.Since);

        // What is kept and what is served name no place.
        var served = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var text in new[] { File.ReadAllText(store.Path), served, string.Join("\n", _log) })
        {
            Assert.DoesNotContain("Workspaces", text);
            Assert.DoesNotContain("nova\\\\d1", text);
            Assert.DoesNotContain(@"nova\d1", text);
        }
        // Only the paths that differ are logged, by field and outcome.
        Assert.Equal(new[] { "[Placement] Shadow nova/sessions workspace+scratch(env): cwd equivalent, scratch exact",
            "[Placement] Shadow nova/sessions workspace+scratch(env): cwd mismatch, scratch exact" }, _log.Order());
    }

    [Fact]
    public async Task A_resolver_that_cannot_answer_is_unavailable_and_a_refused_reference_is_counted_apart()
    {
        var refusing = new RepositoryPlacementResolver(new HttpClient(new RedLeaf(HttpStatusCode.NotFound, """{"error":"not_found"}"""))
        {
            BaseAddress = new Uri("http://redleaf/"),
        });
        var shadow = new PlacementShadow(new PlacementResolver(null, null, null, refusing), null, (line, _) => { lock (_log) _log.Add(line); });
        var legacy = new LegacyPlacement(@"T:\songes", null, RepositoryId.ToString("D"), null, null);

        shadow.Observe(Placement(workspace: true), PlacementRoute.Session, "nova", legacy, new WindowsRules());
        shadow.Observe(Placement(repository: true), PlacementRoute.Session, "codered", legacy, new WindowsRules());
        await shadow.IdleAsync();

        var status = shadow.Status();
        var unavailable = Assert.Single(status.Paths, p => p.App == "nova");
        Assert.Equal((1L, 0L, 1L, 0L), (unavailable.Observed, unavailable.Compared, unavailable.Unavailable, unavailable.Refused));
        Assert.Equal(1L, unavailable.Codes["placement_resolution_unavailable"]);
        Assert.Empty(unavailable.Fields);
        var refused = Assert.Single(status.Paths, p => p.App == "codered");
        Assert.Equal((1L, 0L, 0L, 1L), (refused.Observed, refused.Compared, refused.Unavailable, refused.Refused));
        Assert.Equal("repository_not_found", Assert.Single(refused.Codes.Keys));
        Assert.Equal(1, status.Totals.PathsWithDisagreement);
        Assert.Contains("[Placement] Shadow codered/sessions repository: not resolved (repository_not_found)", _log);
    }

    [Fact]
    public async Task A_resolver_fault_is_counted_and_never_reaches_the_start()
    {
        var shadow = Shadow(new Fixed(fault: new InvalidOperationException("boom")));

        shadow.Observe(Placement(workspace: true), PlacementRoute.Session, "nova", new(Workspace, null, null, null, null), new WindowsRules());
        await shadow.IdleAsync();

        var path = Assert.Single(shadow.Status().Paths);
        Assert.Equal((1L, 1L), (path.Unavailable, path.Codes["placement_resolution_unavailable"]));
    }

    [Theory]
    [InlineData("nova", "nova")]
    [InlineData(" codered ", "codered")]
    [InlineData("leaf-capabilities:sentinel", "leaf-capabilities:sentinel")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData(@"C:\Users\someone", "unknown")]
    [InlineData("a b", "unknown")]
    public void The_app_is_a_short_plain_id(string? app, string expected)
        => Assert.Equal(expected, PlacementShadow.AppName(app));

    [Fact]
    public void Shapes_name_references_only()
    {
        Assert.Equal("node", PlacementShadow.Shape(new PlacementRequest("local", null, null, null)));
        Assert.Equal("workspace+scratch(env)", PlacementShadow.Shape(Placement(workspace: true, scratch: true)));
        Assert.Equal("repository+checkout+branch+subPath", PlacementShadow.Shape(new PlacementRequest(null, null,
            new RepositoryPlacement(RepositoryId, Guid.NewGuid(), "laurent/x", "inner"), null)));
        Assert.Equal("scratch(cwd)", PlacementShadow.Shape(Placement(scratch: true, scratchAs: "cwd")));
    }

    [Fact]
    public async Task Past_the_cap_new_paths_are_counted_under_other()
    {
        var shadow = Shadow(new Fixed());
        for (var i = 0; i < PlacementShadow.MaxPaths + 3; i++)
            shadow.Observe(Placement(workspace: true), PlacementRoute.Session, $"app{i}", new(Workspace, null, null, null, null), new WindowsRules());
        await shadow.IdleAsync();

        var status = shadow.Status();
        Assert.Equal(PlacementShadow.MaxPaths + 1, status.Paths.Count);
        Assert.Equal(3L, Assert.Single(status.Paths, p => p.App == "other").Observed);
        Assert.Equal(PlacementShadow.MaxPaths + 3L, status.Totals.Observed);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_and_counting_starts_over()
    {
        var store = Store();
        File.WriteAllText(store.Path, "{ not json");

        var status = Shadow(new Fixed(), store: store).Status();

        Assert.Empty(status.Paths);
        Assert.Null(status.Since);
        Assert.True(File.Exists(store.Path + ".unreadable"));
        Assert.Contains(_log, line => line.Contains("placement-shadow.json could not be read (JsonException)"));
    }

    [Fact]
    public async Task The_status_route_serves_the_counts()
    {
        var shadow = Shadow(new Fixed());
        shadow.Observe(Placement(workspace: true, scratch: true), PlacementRoute.Session, "nova", new(Workspace, Scratch, null, null, null),
            new WindowsRules());
        await shadow.IdleAsync();
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);
        PlacementEndpoints.Map(endpoints, shadow.Status);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == PlacementEndpoints.ShadowStatusPath);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await route.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(200, context.Response.StatusCode);
        var path = body.RootElement.GetProperty("paths")[0];
        Assert.Equal("nova", path.GetProperty("app").GetString());
        Assert.Equal("sessions", path.GetProperty("route").GetString());
        Assert.Equal(1, path.GetProperty("fields").GetProperty("cwd").GetProperty("exact").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("totals").GetProperty("compared").GetInt32());
        Assert.DoesNotContain("Workspaces", body.RootElement.GetRawText());
        var described = Assert.Single(endpoints.GetEndpoints(), e => e.Path == PlacementEndpoints.ShadowStatusPath);
        Assert.Equal("GET", described.Method);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private PlacementShadowStore Store() => new(Path.Combine(_root, PlacementShadowStore.FileName));

    private PlacementShadow Shadow(Fixed places, PlacementShadowStore? store = null)
        => new(new PlacementResolver(null, places, places, null), store, (line, _) => { lock (_log) _log.Add(line); });

    private static PlacementRequest Placement(bool workspace = false, bool repository = false, bool scratch = false,
        string scratchAs = "env")
        => new(null,
            workspace ? new WorkspacePlacement(AgentId) : null,
            repository ? new RepositoryPlacement(RepositoryId, null, null, null) : null,
            scratch ? new ScratchPlacement("nova", "d1") : null,
            scratchAs);

    private static ResolvedPlacement Resolved(string? cwd, string? scratch, Guid? repositoryId,
        IReadOnlyList<string>? addDirs = null, IReadOnlyDictionary<string, string>? env = null)
        => new(new PlacementRequest(null, null, null, null), "local", cwd, scratch, env, addDirs, repositoryId, null, null);

    private static IReadOnlyDictionary<string, string> Env(string directory)
        => PlacementShadow.ScratchVariables.ToDictionary(name => name, _ => directory);

    private sealed class Fixed(Exception? fault = null) : IWorkspaceResolver, IHostScratch
    {
        public Task<Resolution<WorkspaceResolution>> ResolveAsync(WorkspacePlacement value, CancellationToken ct = default)
            => fault is not null ? throw fault : Task.FromResult(Resolution<WorkspaceResolution>.Of(new("mounted", Workspace)));

        public Task<Resolution<ScratchAllocation>> PrepareAsync(ScratchPlacement value, CancellationToken ct = default)
            => Task.FromResult(Resolution<ScratchAllocation>.Of(new(Scratch, Env(Scratch))));
    }

    private sealed class RedLeaf(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class WindowsRules : IHostPathRules
    {
        public char DirectorySeparator => '\\';
        public StringComparison Comparison => StringComparison.OrdinalIgnoreCase;
        public bool IsPathFullyQualified(string path) => path.Length >= 3 && path[1] == ':' && path[2] is '\\' or '/';
        public string GetFullPath(string path) => path;
        public string Combine(params string[] parts) => string.Join('\\', parts);
    }

    private sealed class PosixRules : IHostPathRules
    {
        public char DirectorySeparator => '/';
        public StringComparison Comparison => StringComparison.Ordinal;
        public bool IsPathFullyQualified(string path) => path.StartsWith('/');
        public string GetFullPath(string path) => path;
        public string Combine(params string[] parts) => string.Join('/', parts);
    }
}
