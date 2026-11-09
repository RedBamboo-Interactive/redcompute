using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// The access-level contract's resolution and clamping rules (Agent Permission Modes, 2026-11-09),
// implemented once in SessionAccessResolver. Every combination is checked against the rules as the
// contract states them, plus the table of cases the contract names.
public sealed class SessionAccessResolverTests
{
    private const string Plan = SessionAccessLevels.Plan, Ask = SessionAccessLevels.Ask,
        Auto = SessionAccessLevels.Auto, Full = SessionAccessLevels.Full;
    private static readonly string[] Claude = [Plan, Ask, Auto, Full];
    private static readonly string[] AskAndFull = [Ask, Full];
    private static readonly string?[] LevelsOrNone = [null, Plan, Ask, Auto, Full];

    private static SessionAccessPolicy Policy(string? defaultLevel = null, bool full = false,
        Dictionary<string, string>? agents = null, int textVersion = 1)
        => new()
        {
            DefaultLevel = defaultLevel,
            Agents = agents ?? [],
            FullAccess = new FullAccessAcknowledgement(full, textVersion),
        };

    private static int R(string? level) => SessionAccessLevels.Rank(level);

    public static IEnumerable<object?[]> StartCombinations()
    {
        foreach (var requested in LevelsOrNone)
        foreach (var person in new[] { true, false })
        foreach (var full in new[] { true, false })
        foreach (var configured in new[] { Plan, Ask, Auto, Full })
        foreach (var supported in new[] { Claude, AskAndFull })
        foreach (var cap in LevelsOrNone)
            yield return [requested, person, full, configured, supported, cap];
    }

    [Fact]
    public void Every_start_combination_obeys_the_contract()
    {
        var checkedCount = 0;
        foreach (var row in StartCombinations())
        {
            var (requested, person, full, configured, supported, cap) =
                ((string?)row[0], (bool)row[1]!, (bool)row[2]!, (string)row[3]!, (string[])row[4]!, (string?)row[5]);
            var policy = Policy(agents: new() { ["agent-1"] = configured }, defaultLevel: Plan, full: full);
            var result = SessionAccessResolver.ResolveStart(requested, "agent-1", person, policy, supported, cap);
            var context = $"requested={requested} person={person} full={full} configured={configured} supported={string.Join('/', supported)} cap={cap}";
            checkedCount++;

            if (cap is not null && !supported.Any(level => R(level) <= R(cap)))
            {
                Assert.True(result.Level is null, context);
                Assert.Equal(SessionAccessResolver.CapBelowSupported, result.Error);
                continue;
            }
            Assert.True(result.Level is not null, context);
            var level = result.Level!;
            Assert.True(supported.Contains(level), $"unsupported result {level}: {context}");
            Assert.True(cap is null || R(level) <= R(cap), $"above cap {level}: {context}");
            Assert.True(level != Full || full, $"full while disabled: {context}");
            // Nothing is ever raised above what was asked for, except an unsupported plan or auto
            // becoming ask, which the contract names.
            var asked = requested ?? configured;
            Assert.True(R(level) <= Math.Max(R(asked), R(Ask)), $"raised above {asked}: {context}");
            if (!person)
                Assert.True(R(level) <= Math.Max(R(configured), R(Ask)), $"non-person above its default {configured}: {context}");

            // The expected value, step by step as the contract states it.
            var expected = asked;
            if (!person && R(expected) > R(configured)) expected = configured;
            if (expected == Full && !full) expected = Auto;
            if (!supported.Contains(expected)) expected = Ask;
            if (cap is not null && R(expected) > R(cap))
                expected = supported.Where(candidate => R(candidate) <= R(cap)).MaxBy(R)!;
            Assert.True(expected == level, $"expected {expected}, got {level}: {context}");
        }
        Assert.Equal(5 * 2 * 2 * 4 * 2 * 5, checkedCount);
    }

    [Theory]
    // person-present: gets what it asks for when the policy allows it
    [InlineData(Full, true, true, Ask, null, Full)]
    [InlineData(Full, true, false, Ask, null, Auto)]          // full needs Full enabled
    [InlineData(Auto, true, false, Ask, null, Auto)]
    [InlineData(Plan, true, false, Ask, null, Plan)]
    // an Agent or automation is clamped to the configured default
    [InlineData(Full, false, true, Ask, null, Ask)]
    [InlineData(Full, false, true, Auto, null, Auto)]
    [InlineData(Full, false, true, Full, null, Full)]
    [InlineData(Plan, false, true, Auto, null, Plan)]         // lowering is always open
    [InlineData(null, false, true, Auto, null, Auto)]         // nothing asked: the configured default
    [InlineData(null, false, false, Full, null, Auto)]        // default full, Full disabled
    // caps clamp last
    [InlineData(Full, true, true, Ask, Auto, Auto)]
    [InlineData(null, false, true, Full, Auto, Auto)]         // Discord: unattended, capped at auto
    [InlineData(Auto, true, true, Ask, Plan, Plan)]
    public void Claude_start_resolves_as_the_contract_says(string? requested, bool person, bool full,
        string configured, string? cap, string expected)
    {
        var policy = Policy(defaultLevel: configured, full: full);
        Assert.Equal(expected, SessionAccessResolver.ResolveStart(requested, null, person, policy, Claude, cap).Level);
    }

    [Theory]
    [InlineData(Plan, Ask)]   // unsupported levels resolve to ask
    [InlineData(Auto, Ask)]
    [InlineData(Ask, Ask)]
    [InlineData(Full, Full)]
    public void Codex_and_OpenCode_resolve_unsupported_levels_to_ask(string requested, string expected)
        => Assert.Equal(expected, SessionAccessResolver.ResolveStart(requested, null, true, Policy(full: true), AskAndFull, null).Level);

    [Fact]
    public void A_cap_below_every_supported_level_refuses_rather_than_exceeding_it()
    {
        var result = SessionAccessResolver.ResolveStart(null, null, true, Policy(), AskAndFull, Plan);

        Assert.Null(result.Level);
        Assert.Equal(SessionAccessResolver.CapBelowSupported, result.Error);
    }

    [Fact]
    public void Unknown_levels_are_refused()
    {
        Assert.Equal(SessionAccessResolver.InvalidAccessLevel,
            SessionAccessResolver.ResolveStart("default", null, true, Policy(), Claude, null).Error);
        Assert.Equal(SessionAccessResolver.InvalidAccessLevel,
            SessionAccessResolver.ResolveChange(new(Ask), "bypassPermissions", null, true, Policy(), Claude).Error);
    }

    [Fact]
    public void Agent_entry_wins_over_the_global_default_and_unknown_entries_are_ignored()
    {
        var policy = Policy(defaultLevel: Ask, agents: new() { ["nova"] = Auto, ["broken"] = "everything" });

        Assert.Equal(Auto, policy.DefaultFor("nova"));
        Assert.Equal(Ask, policy.DefaultFor("broken"));
        Assert.Equal(Ask, policy.DefaultFor("someone-else"));
        Assert.Equal(Ask, policy.DefaultFor(null));
        Assert.Equal(Ask, SessionAccessPolicy.NoRecord.DefaultFor("nova"));
        Assert.Equal(Ask, SessionAccessPolicy.FailClosed.DefaultFor("nova"));
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]    // RedLeaf's enabled is already effective: no text-version check here
    [InlineData(true, 0, true)]
    [InlineData(false, 1, false)]  // accepted on an older text: RedLeaf reports enabled false
    public void Full_is_redleafs_effective_enabled_value_as_is(bool enabled, int textVersion, bool counts)
        => Assert.Equal(counts, Policy(full: enabled, textVersion: textVersion).FullAccessEnabled);

    [Fact]
    public void No_record_and_fail_closed_mean_ask_with_full_disabled()
    {
        Assert.False(SessionAccessPolicy.NoRecord.FullAccessEnabled);
        Assert.False(SessionAccessPolicy.FailClosed.FullAccessEnabled);
        Assert.False(SessionAccessPolicy.FailClosed.Available);
        Assert.Equal(Ask, SessionAccessResolver.ResolveStart(null, "nova", false, SessionAccessPolicy.FailClosed, Claude, null).Level);
        Assert.Equal(Auto, SessionAccessResolver.ResolveStart(Full, "nova", true, SessionAccessPolicy.FailClosed, Claude, null).Level);
    }

    public static IEnumerable<object?[]> ChangeCombinations()
    {
        foreach (var current in LevelsOrNone)
        foreach (var requested in LevelsOrNone)
        foreach (var person in new[] { true, false })
        foreach (var full in new[] { true, false })
        foreach (var cap in LevelsOrNone)
        foreach (var supported in new[] { Claude, AskAndFull })
            yield return [current, requested, person, full, cap, supported];
    }

    [Fact]
    public void Every_change_combination_obeys_the_contract()
    {
        var checkedCount = 0;
        foreach (var row in ChangeCombinations())
        {
            var (current, requested, person, full, cap, supported) =
                ((string?)row[0], (string?)row[1], (bool)row[2]!, (bool)row[3]!, (string?)row[4], (string[])row[5]!);
            if (cap is not null && current is not null && R(current) > R(cap)) continue; // a session never sits above its cap
            var state = new SessionAccessState(current, cap, AccessLevelBeforePlan: current == Plan ? Auto : null);
            var change = SessionAccessResolver.ResolveChange(state, requested, null, person, Policy(full: full), supported);
            var context = $"current={current} requested={requested} person={person} full={full} cap={cap} supported={string.Join('/', supported)}";
            checkedCount++;

            var from = current ?? Ask; // a legacy state is judged as ask, never as a stored bypass
            if (requested is null && from != Plan)
            {
                Assert.False(change.IsChange, context);
                continue;
            }
            var target = requested ?? state.AccessLevelBeforePlan!;
            target = supported.Contains(target) ? target : Ask;
            if (R(target) > R(from))
            {
                var error = !person ? SessionAccessResolver.RaiseRequiresPerson
                    : target == Full && !full ? SessionAccessResolver.FullAccessNotEnabled
                    : cap is not null && R(target) > R(cap) ? SessionAccessResolver.AboveSessionCap
                    : null;
                Assert.True(error == change.Error, $"expected {error ?? "allowed"}, got {change.Error ?? "allowed"}: {context}");
                if (error is not null)
                {
                    Assert.Null(change.State);
                    continue;
                }
            }
            else
                Assert.True(change.Error is null, $"lowering refused ({change.Error}): {context}");

            Assert.Equal(target, change.State!.AccessLevel);
            Assert.Equal(cap, change.State.MaxAccessLevel);
            if (target == Plan)
                Assert.Equal(from == Plan ? state.AccessLevelBeforePlan : from, change.State.AccessLevelBeforePlan);
            else
                Assert.Null(change.State.AccessLevelBeforePlan);
        }
        Assert.Equal(760, checkedCount); // 1000 combinations, less those holding a level above their cap
    }

    [Fact]
    public void Entering_plan_remembers_the_level_and_leaving_without_one_restores_it()
    {
        var running = new SessionAccessState(Auto);

        var planned = SessionAccessResolver.ResolveChange(running, Plan, null, personPresent: false, Policy(), Claude);
        Assert.Equal(new SessionAccessState(Plan, AccessLevelBeforePlan: Auto), planned.State);

        var back = SessionAccessResolver.ResolveChange(planned.State!, null, null, personPresent: true, Policy(), Claude);
        Assert.Equal(new SessionAccessState(Auto), back.State);

        // Leaving plan is a raise like any other: an Agent cannot do it alone.
        var agent = SessionAccessResolver.ResolveChange(planned.State!, null, null, personPresent: false, Policy(), Claude);
        Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, agent.Error);
    }

    [Fact]
    public void Leaving_plan_with_nothing_remembered_uses_the_configured_default()
    {
        var change = SessionAccessResolver.ResolveChange(new SessionAccessState(Plan), null, "nova", true,
            Policy(agents: new() { ["nova"] = Auto }), Claude);

        Assert.Equal(Auto, change.State!.AccessLevel);
    }

    [Theory]
    [InlineData(Full, null, Ask)]        // full demoted to its resolved default
    [InlineData(Full, Auto, Ask)]
    [InlineData(Plan, null, Plan)]       // plan stays; a remembered full is demoted too
    [InlineData(Auto, null, Auto)]
    public void Revoking_full_demotes_full_to_the_resolved_default(string level, string? cap, string expected)
    {
        var state = new SessionAccessState(level, cap, AccessLevelBeforePlan: level == Plan ? Full : null);
        var demoted = SessionAccessResolver.DemoteIfFullRevoked(state, null, Policy(defaultLevel: Ask), Claude);

        if (level is Full or Plan)
        {
            Assert.NotNull(demoted);
            Assert.Equal(expected, demoted!.AccessLevel);
            if (level == Plan) Assert.Equal(Ask, demoted.AccessLevelBeforePlan);
        }
        else Assert.Null(demoted);
        Assert.Null(SessionAccessResolver.DemoteIfFullRevoked(state, null, Policy(full: true), Claude));
    }

    [Theory]
    [InlineData("bypassPermissions", Full)]
    [InlineData("acceptEdits", Ask)]
    [InlineData("default", Ask)]
    [InlineData("manual", Ask)]
    [InlineData("dontAsk", Ask)]
    [InlineData("plan", Plan)]
    [InlineData("auto", Auto)]
    [InlineData("full", Full)]
    [InlineData("ask", Ask)]
    public void Legacy_permission_modes_map_to_levels(string mode, string level)
    {
        Assert.True(SessionAccessLevels.TryFromPermissionMode(mode, out var mapped));
        Assert.Equal(level, mapped);
    }

    [Theory]
    [InlineData("everything")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_permission_modes_do_not_map(string? mode)
        => Assert.False(SessionAccessLevels.TryFromPermissionMode(mode, out _));

    [Fact]
    public void A_legacy_session_with_a_stored_bypass_resolves_fresh_from_policy()
    {
        // Claude rows from before access levels carry no level; their stored permission mode was the
        // hardcoded bypassPermissions. Neither is a level: the session resolves to its default.
        var record = new RedCompute.Plugin.ClaudeCode.ClaudeSessionRecord
        {
            Id = "legacy", ProjectName = "p", ProjectPath = "p", Status = "Stopped",
        };
        var state = RedCompute.Plugin.ClaudeCode.ClaudeSessionService.AccessOf(record);

        Assert.Null(state.AccessLevel);
        Assert.Equal("acceptEdits", RedCompute.Plugin.ClaudeCode.ClaudeSessionService.PermissionModeFor(state.AccessLevel));
        Assert.Equal(Auto, SessionAccessResolver.ResolveDefault("nova",
            Policy(agents: new() { ["nova"] = Auto }), Claude, null).Level);
    }

    // ---- policy client: cache at most 30 s, fail closed ----

    [Fact]
    public async Task Policy_is_read_and_cached_for_at_most_thirty_seconds()
    {
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var reads = 0;
        var client = Client(_ =>
        {
            reads++;
            return Json(new
            {
                version = 3, defaultLevel = "auto", agents = new Dictionary<string, string> { ["nova"] = "full" },
                fullAccess = new { enabled = true, textVersion = 1, acknowledgedAt = "2026-11-09T10:00:00Z", acknowledgedBy = "laurent" },
            });
        }, () => now);

        var first = await client.GetAsync();
        now += TimeSpan.FromSeconds(29);
        await client.GetAsync();
        Assert.Equal(1, reads);
        now += TimeSpan.FromSeconds(2);
        await client.GetAsync();
        Assert.Equal(2, reads);
        await client.GetAsync(refresh: true);
        Assert.Equal(3, reads);

        Assert.True(first.Available);
        Assert.Equal(3, first.Version);
        Assert.Equal(Auto, first.DefaultLevel);
        Assert.Equal(Full, first.DefaultFor("nova"));
        Assert.True(first.FullAccessEnabled);
        Assert.Equal("laurent", first.FullAccess!.AcknowledgedBy);
    }

    [Fact]
    public async Task Policy_fetch_failure_fails_closed_to_ask_with_full_disabled()
    {
        var client = Client(_ => throw new HttpRequestException("connection refused"));

        var policy = await client.GetAsync();

        Assert.False(policy.Available);
        Assert.False(policy.FullAccessEnabled);
        Assert.Equal(Ask, SessionAccessResolver.ResolveStart(null, "nova", false, policy, Claude, null).Level);
        Assert.Equal(Auto, SessionAccessResolver.ResolveStart(Full, "nova", true, policy, Claude, null).Level);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, true)]   // no record: the defaults, read successfully
    public async Task Error_statuses_read_as_the_defaults(HttpStatusCode status, bool available)
    {
        var policy = await Client(_ => new HttpResponseMessage(status)).GetAsync();

        Assert.Equal(available, policy.Available);
        Assert.False(policy.FullAccessEnabled);
        Assert.Equal(Ask, policy.DefaultFor("nova"));
    }

    [Fact]
    public async Task An_unreadable_body_fails_closed_and_a_wrapped_policy_is_read()
    {
        Assert.False((await Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }).GetAsync()).Available);
        var wrapped = await Client(_ => Json(new { policy = new { defaultLevel = "plan", fullAccess = new { enabled = true, textVersion = 1 } } })).GetAsync();
        Assert.Equal(Plan, wrapped.DefaultFor(null));
        Assert.True(wrapped.FullAccessEnabled);
    }

    [Fact]
    public async Task A_failure_is_retried_soon_rather_than_cached_for_thirty_seconds()
    {
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        var fail = true;
        var client = Client(_ => fail ? throw new HttpRequestException("down") : Json(new { defaultLevel = "auto" }), () => now);

        Assert.False((await client.GetAsync()).Available);
        fail = false;
        now += TimeSpan.FromSeconds(6);
        Assert.Equal(Auto, (await client.GetAsync()).DefaultFor(null));
    }

    private static RedLeafSessionAccessPolicyClient Client(Func<HttpRequestMessage, HttpResponseMessage> send,
        Func<DateTimeOffset>? now = null)
        => new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("http://redleaf/") }, (_, _) => { }, now);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/agent-permissions/policy", request.RequestUri!.AbsolutePath);
            return Task.FromResult(send(request));
        }
    }
}
