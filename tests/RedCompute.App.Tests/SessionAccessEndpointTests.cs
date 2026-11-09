using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// The access-level routes' authorization matrix: lowering is open to anyone who may write to the
// session; raising, approving a tool call and leaving plan need a person. The callers are the
// identities the kernel really mints (SessionRollbackTests.Identify).
[Collection("Unified session endpoint mapping")]
public sealed class SessionAccessEndpointTests
{
    private const string Plan = SessionAccessLevels.Plan, Ask = SessionAccessLevels.Ask,
        Auto = SessionAccessLevels.Auto, Full = SessionAccessLevels.Full;
    private const string Policy = SessionAccessLevelSources.Policy, Explicit = SessionAccessLevelSources.Explicit,
        ParentSource = SessionAccessLevelSources.Parent;

    public static TheoryData<string> People => Callers(SessionRollbackTests.PeopleCallers);
    public static TheoryData<string> NotPeople => Callers(SessionRollbackTests.NotPeopleCallers);
    public static TheoryData<string> LocalDefault => Callers(SessionRollbackTests.LocalDefaultCallers);

    private static TheoryData<string> Callers(IEnumerable<string> callers)
    {
        var data = new TheoryData<string>();
        foreach (var caller in callers) data.Add(caller);
        return data;
    }

    // ---- access-level ----

    [Theory]
    [MemberData(nameof(People))]
    [MemberData(nameof(NotPeople))]
    public async Task Anyone_who_may_write_can_lower_a_session(string caller)
    {
        var f = new Fixture(Full) { Readable = true };

        var (status, body) = await f.PostAsync("access-level", new { level = Ask }, caller);

        Assert.Equal(200, status);
        Assert.Equal(Ask, body.GetProperty("accessLevel").GetString());
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
    }

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_person_can_raise_a_session(string caller)
    {
        var f = new Fixture(Ask) { Readable = true };

        var (status, body) = await f.PostAsync("access-level", new { level = Auto }, caller);

        Assert.Equal(200, status);
        Assert.Equal(Auto, body.GetProperty("accessLevel").GetString());
        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Nothing_but_a_person_can_raise_a_session(string caller)
    {
        var f = new Fixture(Ask, fullEnabled: true) { Readable = true };

        foreach (var level in new[] { Auto, Full })
        {
            var (status, body) = await f.PostAsync("access-level", new { level }, caller);
            Assert.Equal(403, status);
            Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, body.GetProperty("error").GetString());
        }
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task Full_needs_full_access_enabled_even_for_a_person()
    {
        var disabled = new Fixture(Ask);
        var enabled = new Fixture(Ask, fullEnabled: true);

        var refused = await disabled.PostAsync("access-level", new { level = Full }, "code browser");
        var allowed = await enabled.PostAsync("access-level", new { level = Full }, "code browser");

        Assert.Equal(403, refused.Status);
        Assert.Equal(SessionAccessResolver.FullAccessNotEnabled, refused.Body.GetProperty("error").GetString());
        Assert.Equal(Ask, disabled.Provider.State.AccessLevel);
        Assert.Equal(200, allowed.Status);
        Assert.Equal(Full, enabled.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_person_cannot_raise_above_the_session_cap()
    {
        var f = new Fixture(Ask, fullEnabled: true, cap: Auto);

        var above = await f.PostAsync("access-level", new { level = Full }, "code browser");
        var at = await f.PostAsync("access-level", new { level = Auto }, "code browser");

        Assert.Equal(403, above.Status);
        Assert.Equal(SessionAccessResolver.AboveSessionCap, above.Body.GetProperty("error").GetString());
        Assert.Equal(200, at.Status);
        Assert.Equal(new SessionAccessState(Auto, Auto, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Fact]
    public async Task Entering_plan_is_open_and_leaving_it_needs_a_person()
    {
        var f = new Fixture(Auto);

        var entered = await f.PostAsync("access-level", new { level = Plan }, "agent");
        Assert.Equal(200, entered.Status);
        Assert.Equal(new SessionAccessState(Plan, AccessLevelBeforePlan: Auto, AccessLevelSource: Explicit), f.Provider.State);

        var agentLeaves = await f.PostAsync("access-level", new { level = (string?)null }, "agent");
        Assert.Equal(403, agentLeaves.Status);
        Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, agentLeaves.Body.GetProperty("error").GetString());

        var personLeaves = await f.PostAsync("access-level", new { }, "code browser");
        Assert.Equal(200, personLeaves.Status);
        Assert.Equal(new SessionAccessState(Auto, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Fact]
    public async Task A_legacy_session_is_judged_at_its_resolved_level_never_its_stored_bypass()
    {
        // Rows from before access levels carry no level (their stored mode was bypassPermissions).
        // An Agent's "lower to auto" is a raise from the resolved ask, so it is refused.
        var f = new Fixture(level: null, fullEnabled: true);

        var (status, body) = await f.PostAsync("access-level", new { level = Auto }, "agent");

        Assert.Equal(403, status);
        Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, body.GetProperty("error").GetString());
        Assert.Null(f.Provider.State.AccessLevel);
    }

    [Theory]
    [InlineData("\"bypass\"")]
    [InlineData("\"default\"")]
    [InlineData("3")]
    public async Task Unknown_levels_are_422(string level)
    {
        var f = new Fixture(Ask);

        var (status, body) = await f.PostRawAsync("access-level", $"{{\"level\":{level}}}", "code browser");

        Assert.Equal(422, status);
        Assert.Equal(SessionAccessResolver.InvalidAccessLevel, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Someone_who_cannot_see_the_session_learns_nothing()
    {
        var f = new Fixture(Full);

        var (status, body) = await f.PostAsync("access-level", new { level = Ask }, "agent", subject: "intruder");

        Assert.Equal(403, status);
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
        Assert.Equal(Full, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_provider_without_levels_refuses_and_an_unknown_session_is_404()
    {
        var none = new Fixture(Ask) { Provider = { Supported = [] } };
        var refused = await none.PostAsync("access-level", new { level = Plan }, "code browser");
        var missing = await new Fixture(Ask).PostAsync("access-level", new { level = Plan }, "code browser", sessionId: "missing");

        Assert.Equal(422, refused.Status);
        Assert.Equal("access_levels_not_supported", refused.Body.GetProperty("error").GetString());
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task Codex_and_OpenCode_take_ask_for_an_unsupported_level()
    {
        var f = new Fixture(Full) { Provider = { Supported = [Ask, Full] } };

        var (status, body) = await f.PostAsync("access-level", new { level = Plan }, "agent");

        Assert.Equal(200, status);
        Assert.Equal(Ask, body.GetProperty("accessLevel").GetString());
    }

    // ---- LocalDefault: what an Agent with a shell can get on loopback ----

    [Theory]
    [MemberData(nameof(LocalDefault))]
    public async Task A_localdefault_identity_cannot_raise_approve_or_leave_plan(string caller)
    {
        // Unowned session, Full enabled, no cap: only the person rule can refuse.
        var raise = new Fixture(Ask, fullEnabled: true) { Readable = true };
        var raised = await raise.PostAsync("access-level", new { level = Full }, caller, subject: "local-user");
        var aliasPlan = new Fixture(Plan, fullEnabled: true) { Readable = true };
        aliasPlan.Provider.State = new SessionAccessState(Plan, AccessLevelBeforePlan: Full);
        var aliased = await aliasPlan.PostAsync("permission-mode", new { mode = "bypassPermissions" }, caller, subject: "local-user");

        var plan = new Fixture(Plan, fullEnabled: true) { Readable = true };
        plan.Provider.State = new SessionAccessState(Plan, AccessLevelBeforePlan: Full);
        var left = await plan.PostAsync("access-level", new { level = (string?)null }, caller, subject: "local-user");

        var approve = new Fixture(Ask) { Readable = true };
        approve.Provider.Pending.Add(Permission("perm-1"));
        var approved = await approve.PostQuestionAsync(new { requestId = "perm-1", decision = "allow_session" }, caller, subject: "local-user");

        Assert.Equal((403, SessionAccessResolver.RaiseRequiresPerson), (raised.Status, raised.Body.GetProperty("error").GetString()));
        Assert.Equal((403, SessionAccessResolver.RaiseRequiresPerson), (aliased.Status, aliased.Body.GetProperty("error").GetString()));
        Assert.Equal((403, SessionAccessResolver.RaiseRequiresPerson), (left.Status, left.Body.GetProperty("error").GetString()));
        Assert.Equal((403, "approval_requires_person"), (approved.Status, approved.Body.GetProperty("error").GetString()));
        Assert.Equal(Ask, raise.Provider.State.AccessLevel);
        Assert.Equal(Plan, plan.Provider.State.AccessLevel);
        Assert.Equal(0, raise.Provider.Sets + plan.Provider.Sets);
        Assert.Empty(approve.Provider.Answers);
        Assert.Single(approve.Provider.Pending);
    }

    [Theory]
    [MemberData(nameof(LocalDefault))]
    public async Task A_localdefault_identity_can_still_lower(string caller)
    {
        var f = new Fixture(Full) { Readable = true };

        var (status, _) = await f.PostAsync("access-level", new { level = Plan }, caller, subject: "local-user");

        Assert.Equal(200, status);
        Assert.Equal(Plan, f.Provider.State.AccessLevel);
    }

    // ---- permission-mode: the compat alias, same rules ----

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task The_permission_mode_alias_cannot_raise_for_an_agent(string caller)
    {
        var f = new Fixture(Plan, fullEnabled: true) { Readable = true };
        f.Provider.State = new SessionAccessState(Plan, AccessLevelBeforePlan: Auto);

        var (status, body) = await f.PostAsync("permission-mode", new { mode = "bypassPermissions" }, caller);

        Assert.Equal(403, status);
        Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, body.GetProperty("error").GetString());
        Assert.Equal(Plan, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_stale_tab_leaving_plan_with_bypass_returns_to_its_level_never_to_full()
    {
        // A bundle from before access levels flips plan off with bypassPermissions. A signed-in
        // person must not raise the session to Full (and pin it there) by toggling plan.
        var f = new Fixture(Plan, fullEnabled: true);
        f.Provider.State = new SessionAccessState(Plan, AccessLevelBeforePlan: Auto, AccessLevelSource: Policy);

        var (status, body) = await f.PostAsync("permission-mode", new { mode = "bypassPermissions" }, "code browser");

        Assert.Equal(200, status);
        Assert.Equal(Auto, body.GetProperty("accessLevel").GetString());
        Assert.Equal(Auto, f.Provider.State.AccessLevel);

        var notInPlan = await f.PostAsync("permission-mode", new { mode = "bypassPermissions" }, "code browser");
        Assert.Equal(200, notInPlan.Status);
        Assert.NotEqual(Full, f.Provider.State.AccessLevel);
    }

    [Theory]
    [InlineData("bypassPermissions", Ask)]   // never raises: leaves plan, else stays
    [InlineData("acceptEdits", Ask)]
    [InlineData("default", Ask)]
    [InlineData("plan", Plan)]
    [InlineData("auto", Auto)]
    [InlineData("full", Ask)]
    public async Task The_permission_mode_alias_maps_legacy_modes_for_a_person(string mode, string level)
    {
        var f = new Fixture(Ask, fullEnabled: true);

        var (status, body) = await f.PostAsync("permission-mode", new { mode }, "nova through the kernel");

        Assert.Equal(200, status);
        Assert.Equal(level, body.GetProperty("accessLevel").GetString());
        Assert.Equal(level, f.Provider.State.AccessLevel);
        Assert.Equal("native-" + level, body.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task The_permission_mode_alias_lowers_for_anyone_and_rejects_unknown_modes()
    {
        var f = new Fixture(Full);

        var lowered = await f.PostAsync("permission-mode", new { mode = "acceptEdits" }, "agent");
        var unknown = await f.PostAsync("permission-mode", new { mode = "yolo" }, "code browser");
        var missing = await f.PostAsync("permission-mode", new { }, "code browser");

        Assert.Equal(200, lowered.Status);
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
        Assert.Equal(422, unknown.Status);
        Assert.Equal(SessionAccessResolver.InvalidAccessLevel, unknown.Body.GetProperty("error").GetString());
        Assert.Equal(422, missing.Status);
    }

    // ---- a harness that will not switch ----

    private const string CliRefusal = "Claude Code said: \"Cannot set permission mode to bypassPermissions because the session "
        + "was not launched with --dangerously-skip-permissions\"";

    [Fact]
    public async Task A_refused_raise_keeps_the_level_and_says_why_in_plain_words()
    {
        var f = new Fixture(Ask, fullEnabled: true);
        f.Provider.Refusal = CliRefusal;

        var (status, body) = await f.PostAsync("access-level", new { level = Full }, "code browser");

        Assert.Equal(409, status);
        Assert.Equal(SessionAccessService.ProviderRefusedLevel, body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(Ask, body.GetProperty("accessLevel").GetString());
        Assert.Equal("Test didn't switch this session to Full, so it stays at Ask. " + CliRefusal,
            body.GetProperty("message").GetString());
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task The_permission_mode_alias_reports_a_refusal_the_same_way()
    {
        var f = new Fixture(Plan, fullEnabled: true);
        f.Provider.State = new SessionAccessState(Plan, AccessLevelBeforePlan: Full);
        f.Provider.Refusal = CliRefusal;

        var (status, body) = await f.PostAsync("permission-mode", new { mode = "bypassPermissions" }, "nova through the kernel");

        Assert.Equal(409, status);
        Assert.Equal(SessionAccessService.ProviderRefusedLevel, body.GetProperty("error").GetString());
        Assert.Contains(CliRefusal, body.GetProperty("message").GetString());
        Assert.Equal(Plan, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_confirmed_raise_persists()
    {
        var f = new Fixture(Ask, fullEnabled: true);

        var (status, body) = await f.PostAsync("access-level", new { level = Full }, "code browser");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(Full, f.Provider.State.AccessLevel);
        Assert.Equal(1, f.Provider.Sets);
    }

    [Fact]
    public async Task Policy_demotion_a_live_harness_refuses_stops_the_session_and_persists_the_lower_level()
    {
        var f = new Fixture(Full, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { FullAccess = new FullAccessAcknowledgement(false, 1) };
        f.Provider.Refusal = "Claude Code did not confirm the change within 10 seconds.";

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.Equal(["session-1"], body.GetProperty("demotedSessions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, f.Provider.Stops);
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
    }

    // ---- access-level limits: one-way tightening of maxAccessLevel and unattended ----

    [Theory]
    [MemberData(nameof(People))]
    [MemberData(nameof(NotPeople))]
    public async Task Anyone_who_may_write_can_set_and_lower_a_cap_which_lowers_the_level(string caller)
    {
        var f = new Fixture(Full, fullEnabled: true) { Readable = true };

        var set = await f.PostAsync("access-level", new { maxAccessLevel = Auto }, caller);

        Assert.Equal(200, set.Status);
        Assert.Equal(Auto, set.Body.GetProperty("accessLevel").GetString());
        Assert.Equal(Auto, set.Body.GetProperty("maxAccessLevel").GetString());
        Assert.False(set.Body.GetProperty("stopped").GetBoolean());
        Assert.Equal(new SessionAccessState(Auto, Auto, AccessLevelSource: Explicit), f.Provider.State);

        var lowered = await f.PostAsync("access-level", new { maxAccessLevel = Ask }, caller);

        Assert.Equal(200, lowered.Status);
        Assert.Equal(new SessionAccessState(Ask, Ask, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Nothing_but_a_person_can_raise_or_clear_a_cap(string caller)
    {
        var f = new Fixture(Ask, fullEnabled: true, cap: Ask) { Readable = true };

        var raise = await f.PostAsync("access-level", new { maxAccessLevel = Full }, caller);
        var clear = await f.PostAsync("access-level", new { maxAccessLevel = (string?)null }, caller);

        foreach (var (status, body) in new[] { raise, clear })
        {
            Assert.Equal(403, status);
            Assert.Equal(SessionAccessResolver.CapChangeRequiresPerson, body.GetProperty("error").GetString());
            Assert.Equal(Ask, body.GetProperty("maxAccessLevel").GetString());
        }
        Assert.Equal(new SessionAccessState(Ask, Ask, AccessLevelSource: Explicit), f.Provider.State);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_person_can_raise_and_clear_a_cap_without_raising_the_level(string caller)
    {
        var f = new Fixture(Ask, fullEnabled: true, cap: Ask) { Readable = true };

        var raised = await f.PostAsync("access-level", new { maxAccessLevel = Full }, caller);
        Assert.Equal(200, raised.Status);
        Assert.Equal(new SessionAccessState(Ask, Full, AccessLevelSource: Explicit), f.Provider.State);

        var cleared = await f.PostAsync("access-level", new { maxAccessLevel = (string?)null }, caller);
        Assert.Equal(200, cleared.Status);
        Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("maxAccessLevel").ValueKind);
        Assert.Equal(new SessionAccessState(Ask, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Anyone_can_mark_a_session_unattended_but_only_a_person_can_undo_it(string caller)
    {
        var f = new Fixture(Auto) { Readable = true };

        var on = await f.PostAsync("access-level", new { unattended = true }, caller);
        Assert.Equal(200, on.Status);
        Assert.True(on.Body.GetProperty("unattended").GetBoolean());
        Assert.True(f.Provider.State.Unattended);

        var again = await f.PostAsync("access-level", new { unattended = true }, caller);
        Assert.Equal(200, again.Status);

        var off = await f.PostAsync("access-level", new { unattended = false }, caller);
        Assert.Equal(403, off.Status);
        Assert.Equal(SessionAccessResolver.AttendedRequiresPerson, off.Body.GetProperty("error").GetString());
        Assert.True(f.Provider.State.Unattended);

        var person = await f.PostAsync("access-level", new { unattended = false }, "code browser");
        Assert.Equal(200, person.Status);
        Assert.False(f.Provider.State.Unattended);
        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Turning_a_session_unattended_denies_the_approvals_it_already_parked()
    {
        // A pre-upgrade Discord session parked on an approval nobody can see: the retrofit must
        // release it now instead of holding the next turn for thirty minutes. Questions stay.
        var f = new Fixture(Auto) { Readable = true };
        f.Provider.Pending.Add(Permission("perm-1"));
        f.Provider.Pending.Add(SessionQuestionCallbackTests.Question("q-1"));

        var (status, _) = await f.PostAsync("access-level", new { maxAccessLevel = Auto, unattended = true }, "agent");

        Assert.Equal(200, status);
        var answer = Assert.Single(f.Provider.Answers);
        Assert.Equal("perm-1", answer.RequestId);
        Assert.Equal(SessionPermissionDecisions.Deny, answer.Decision);
        Assert.StartsWith("Denied: this session runs at Auto with nobody to approve", answer.DeclineReason);
        Assert.Equal("q-1", Assert.Single(f.Provider.Pending).RequestId);

        // Already unattended: nothing parked is touched again.
        f.Provider.Pending.Add(Permission("perm-2"));
        var again = await f.PostAsync("access-level", new { unattended = true }, "agent");
        Assert.Equal(200, again.Status);
        Assert.Single(f.Provider.Answers);
    }

    [Fact]
    public async Task An_agent_token_retrofits_a_pre_upgrade_discord_session()
    {
        // A Discord session from before access levels follow-ups: policy-sourced, attended, uncapped,
        // running at the full the default gave it. Nova's own token caps it at auto and marks it
        // unattended in one call; the level drops, the source stays policy, nothing is stopped.
        var f = new Fixture(Full, fullEnabled: true, source: Policy);

        var (status, body) = await f.PostAsync("access-level", new { maxAccessLevel = Auto, unattended = true }, "agent");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(Auto, body.GetProperty("accessLevel").GetString());
        Assert.Equal(new SessionAccessState(Auto, Auto, true, AccessLevelSource: Policy), f.Provider.State);
        Assert.Equal(1, f.Provider.Sets);

        // Policy changes keep following the default, inside the cap.
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };
        await f.PostPolicyChangedAsync("infrastructure service token");
        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Limits_without_a_level_never_leave_plan()
    {
        var f = new Fixture(Plan);
        f.Provider.State = f.Provider.State with { AccessLevelBeforePlan = Full };

        var (status, body) = await f.PostAsync("access-level", new { maxAccessLevel = Auto }, "agent");

        Assert.Equal(200, status);
        Assert.Equal(Plan, body.GetProperty("accessLevel").GetString());
        Assert.Equal(new SessionAccessState(Plan, Auto, AccessLevelBeforePlan: Auto, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Fact]
    public async Task Limits_apply_first_and_a_level_in_the_same_body_is_judged_against_them()
    {
        var f = new Fixture(Ask, fullEnabled: true);

        var agent = await f.PostAsync("access-level", new { level = Full, maxAccessLevel = Auto }, "agent");
        Assert.Equal(403, agent.Status);
        Assert.Equal(SessionAccessResolver.RaiseRequiresPerson, agent.Body.GetProperty("error").GetString());
        Assert.Equal(Auto, agent.Body.GetProperty("maxAccessLevel").GetString());
        Assert.Equal(new SessionAccessState(Ask, Auto, AccessLevelSource: Explicit), f.Provider.State);

        var person = await f.PostAsync("access-level", new { level = Full, maxAccessLevel = Auto }, "code browser");
        Assert.Equal(403, person.Status);
        Assert.Equal(SessionAccessResolver.AboveSessionCap, person.Body.GetProperty("error").GetString());

        var within = await f.PostAsync("access-level", new { level = Auto, unattended = true }, "code browser");
        Assert.Equal(200, within.Status);
        Assert.Equal(new SessionAccessState(Auto, Auto, true, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Fact]
    public async Task A_cap_a_live_harness_will_not_switch_down_to_stops_the_session()
    {
        var f = new Fixture(Full, fullEnabled: true);
        f.Provider.Refusal = "Claude Code did not confirm the change within 10 seconds.";

        var (status, body) = await f.PostAsync("access-level", new { maxAccessLevel = Ask }, "agent");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("stopped").GetBoolean());
        Assert.Equal(1, f.Provider.Stops);
        Assert.Equal(new SessionAccessState(Ask, Ask, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Fact]
    public async Task A_cap_clamps_to_what_the_provider_supports()
    {
        var f = new Fixture(Full, fullEnabled: true);
        f.Provider.Supported = [Ask, Full];

        var plan = await f.PostAsync("access-level", new { maxAccessLevel = Plan }, "agent");
        Assert.Equal(422, plan.Status);
        Assert.Equal(SessionAccessResolver.CapBelowSupported, plan.Body.GetProperty("error").GetString());
        Assert.Equal(Full, f.Provider.State.AccessLevel);

        var auto = await f.PostAsync("access-level", new { maxAccessLevel = Auto }, "agent");
        Assert.Equal(200, auto.Status);
        Assert.Equal(new SessionAccessState(Ask, Auto, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Theory]
    [InlineData("""{"maxAccessLevel":"everything"}""", SessionAccessResolver.InvalidAccessLevel)]
    [InlineData("""{"maxAccessLevel":3}""", SessionAccessResolver.InvalidAccessLevel)]
    [InlineData("""{"unattended":"yes"}""", "validation_failed")]
    [InlineData("""{"unattended":null}""", "validation_failed")]
    public async Task Malformed_limits_are_422(string json, string error)
    {
        var f = new Fixture(Full, fullEnabled: true);

        var (status, body) = await f.PostRawAsync("access-level", json, "agent");

        Assert.Equal(422, status);
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task A_legacy_session_is_capped_at_its_resolved_level()
    {
        var f = new Fixture(null, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };

        var (status, _) = await f.PostAsync("access-level", new { maxAccessLevel = Auto, unattended = true }, "agent");

        Assert.Equal(200, status);
        Assert.Equal(new SessionAccessState(Auto, Auto, true), f.Provider.State);
        Assert.True(f.Provider.State.FollowsPolicy);
    }

    [Fact]
    public async Task Limits_on_a_session_the_caller_cannot_read_are_refused()
    {
        var f = new Fixture(Full, fullEnabled: true);

        var (status, _) = await f.PostAsync("access-level", new { maxAccessLevel = Ask }, "agent", subject: "someone-else");

        Assert.NotEqual(200, status);
        Assert.Equal(new SessionAccessState(Full, AccessLevelSource: Explicit), f.Provider.State);
    }

    // ---- access-policy/changed ----

    [Theory]
    [InlineData("agent")]
    [InlineData("agent through the kernel")]
    [InlineData("automation")]
    [InlineData("session process of an agent")]
    [InlineData("loopback without a token")]
    public async Task Policy_change_refuses_agents_automations_and_tokenless_loopback(string caller)
    {
        var f = new Fixture(Full);

        var (status, body) = await f.PostPolicyChangedAsync(caller);

        Assert.Equal(403, status);
        Assert.Equal("service_caller_required", body.GetProperty("error").GetString());
        Assert.Equal(Full, f.Provider.State.AccessLevel);
        Assert.Equal(0, f.Policy.Reads);
    }

    [Theory]
    [InlineData("infrastructure service token")]
    [InlineData("service subject")]
    [InlineData("system subject")]
    [InlineData("code browser")]
    public async Task Policy_change_refetches_and_demotes_live_full_sessions(string caller)
    {
        var f = new Fixture(Full, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { FullAccess = new FullAccessAcknowledgement(false, 1), DefaultLevel = Auto };

        var (status, body) = await f.PostPolicyChangedAsync(caller);

        Assert.Equal(200, status);
        Assert.False(body.GetProperty("fullAccessEnabled").GetBoolean());
        Assert.Equal(["session-1"], body.GetProperty("demotedSessions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Policy_change_with_full_still_enabled_changes_nothing()
    {
        var f = new Fixture(Full, fullEnabled: true);

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.Empty(body.GetProperty("demotedSessions").EnumerateArray());
        Assert.Equal(Full, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Policy_change_while_redleaf_is_unreachable_changes_nothing()
    {
        // An unreadable policy is not a revocation: demoting to the fail-closed default would pin
        // the owner's Full sessions at Ask until a person raised each one again.
        var f = new Fixture(Full, fullEnabled: true);
        f.Policy.Policy = SessionAccessPolicy.FailClosed;

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.False(body.GetProperty("policyAvailable").GetBoolean());
        Assert.Empty(body.GetProperty("demotedSessions").EnumerateArray());
        Assert.Equal(new SessionAccessState(Full, AccessLevelSource: Explicit), f.Provider.State);
    }

    [Theory]
    [InlineData(Explicit)]
    [InlineData(ParentSource)]
    [InlineData(Policy)]
    public async Task A_launch_while_redleaf_is_unreachable_keeps_the_level_it_holds(string source)
    {
        var f = new Fixture(Full, fullEnabled: true, source: source);
        f.Policy.Policy = SessionAccessPolicy.FailClosed;

        await f.Access.EnsureLaunchStateAsync(f.Provider.Proxy, "session-1");

        Assert.Equal(new SessionAccessState(Full, AccessLevelSource: source), f.Provider.State);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task A_launch_while_redleaf_is_unreachable_still_gives_a_levelless_session_ask()
    {
        var f = new Fixture(null);
        f.Policy.Policy = SessionAccessPolicy.FailClosed;

        await f.Access.EnsureLaunchStateAsync(f.Provider.Proxy, "session-1");

        Assert.Equal(Ask, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_policy_write_that_was_never_announced_is_applied_on_the_next_read()
    {
        // RedCompute was down during the write, or the notice failed: the next decision that reads
        // a new revision runs the same pass an announcement would.
        var f = new Fixture(Full, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { Revision = 4, DefaultLevel = Full };
        await f.Access.EnsureLaunchStateAsync(f.Provider.Proxy, "session-1");
        await WaitForAsync(() => f.Policy.Reads >= 2);
        Assert.Equal(Full, f.Provider.State.AccessLevel);

        f.Policy.Policy = f.Policy.Policy with { Revision = 5, DefaultLevel = Auto, FullAccess = new FullAccessAcknowledgement(false, 1) };
        await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, unattended: false, personPresent: false, agentEntityId: null);

        await WaitForAsync(() => f.Provider.State.AccessLevel == Auto);
        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }

    // ---- following the policy (accessLevelSource policy) ----

    [Theory]
    [InlineData(null)]     // persisted before sources existed: counts as policy
    [InlineData(Policy)]
    public async Task A_policy_session_follows_a_later_full_default_on_policy_change(string? source)
    {
        var f = new Fixture(Ask, source: source);
        f.Provider.Pending.Add(Permission("parked"));
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.Equal(["session-1"], body.GetProperty("followedSessions").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(body.GetProperty("demotedSessions").EnumerateArray());
        Assert.Equal(new SessionAccessState(Full, AccessLevelSource: source), f.Provider.State);
        // A parked approval still needs its answer (or expires); nothing answered it.
        Assert.Equal("parked", Assert.Single(f.Provider.Pending).RequestId);
        Assert.Empty(f.Provider.Answers);
    }

    [Fact]
    public async Task An_explicit_session_keeps_its_level_on_policy_change()
    {
        var f = new Fixture(Ask, source: Explicit);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.Empty(body.GetProperty("followedSessions").EnumerateArray());
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task A_policy_session_follows_only_up_to_its_cap_and_stays_unattended()
    {
        var f = new Fixture(Ask, cap: Auto, source: null);
        f.Provider.State = f.Provider.State with { Unattended = true };
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };

        await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(new SessionAccessState(Auto, Auto, Unattended: true), f.Provider.State);
    }

    [Fact]
    public async Task A_policy_default_of_full_without_full_enabled_gives_auto()
    {
        var f = new Fixture(Ask, source: null);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };

        await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(Auto, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_policy_session_follows_a_lower_default_and_one_whose_harness_refuses_is_stopped()
    {
        var f = new Fixture(Full, fullEnabled: true, source: null);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Ask };
        f.Provider.Refusal = "Claude Code did not confirm the change within 10 seconds.";

        var (_, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(["session-1"], body.GetProperty("followedSessions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, f.Provider.Stops);
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task A_refused_raise_from_the_policy_keeps_the_level_and_does_not_stop_the_session()
    {
        var f = new Fixture(Ask, source: null);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };
        f.Provider.Refusal = CliRefusal;

        var (status, body) = await f.PostPolicyChangedAsync("infrastructure service token");

        Assert.Equal(200, status);
        Assert.Empty(body.GetProperty("followedSessions").EnumerateArray());
        Assert.Equal(0, f.Provider.Stops);
        Assert.Equal(Ask, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Launch_state_moves_a_policy_session_to_the_current_default_and_keeps_an_explicit_one()
    {
        var followed = new Fixture(Ask, fullEnabled: true, source: null);
        followed.Policy.Policy = followed.Policy.Policy with { DefaultLevel = Full };
        await followed.Access.EnsureLaunchStateAsync(followed.Provider.Proxy, "session-1");
        Assert.Equal(Full, followed.Provider.State.AccessLevel);

        var capped = new Fixture(Ask, fullEnabled: true, cap: Auto, source: Policy);
        capped.Policy.Policy = capped.Policy.Policy with { DefaultLevel = Full };
        await capped.Access.EnsureLaunchStateAsync(capped.Provider.Proxy, "session-1");
        Assert.Equal(new SessionAccessState(Auto, Auto, AccessLevelSource: Policy), capped.Provider.State);

        var chosen = new Fixture(Ask, fullEnabled: true, source: Explicit);
        chosen.Policy.Policy = chosen.Policy.Policy with { DefaultLevel = Full };
        await chosen.Access.EnsureLaunchStateAsync(chosen.Provider.Proxy, "session-1");
        Assert.Equal(Ask, chosen.Provider.State.AccessLevel);
        Assert.Equal(0, chosen.Provider.Sets);
    }

    [Theory]
    [InlineData("access-level", """{"level":"ask"}""")]       // naming the level it already has pins it
    [InlineData("access-level", """{"level":"plan"}""")]
    [InlineData("permission-mode", """{"mode":"acceptEdits"}""")]
    public async Task A_change_through_the_routes_makes_the_level_explicit(string route, string json)
    {
        var f = new Fixture(Ask, fullEnabled: true, source: null);

        var (status, _) = await f.PostRawAsync(route, json, "code browser");
        Assert.Equal(200, status);
        Assert.Equal(Explicit, f.Provider.State.AccessLevelSource);

        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };
        await f.PostPolicyChangedAsync("infrastructure service token");
        Assert.NotEqual(Full, f.Provider.State.AccessLevel);
    }

    [Fact]
    public async Task Leaving_plan_without_being_in_it_leaves_the_source_alone()
    {
        var f = new Fixture(Ask, source: null);

        var (status, _) = await f.PostAsync("access-level", new { }, "code browser");

        Assert.Equal(200, status);
        Assert.Null(f.Provider.State.AccessLevelSource);
        Assert.Equal(0, f.Provider.Sets);
    }

    [Fact]
    public async Task A_start_with_a_level_is_explicit_and_one_without_follows_the_policy()
    {
        var f = new Fixture(Ask);

        var named = await f.Access.ResolveStartAsync(f.Provider.Proxy, Ask, null, false, personPresent: false, null);
        var defaulted = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, false, personPresent: false, null);

        Assert.Equal(Explicit, named.State!.AccessLevelSource);
        Assert.Equal(Policy, defaulted.State!.AccessLevelSource);
    }

    [Fact]
    public void Following_the_policy_keeps_plan_entered_from_another_level_and_moves_where_it_returns()
    {
        var policy = new SessionAccessPolicy { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };
        IReadOnlyCollection<string> all = SessionAccessLevels.All;

        var inPlan = SessionAccessResolver.FollowPolicy(new SessionAccessState(Plan, AccessLevelBeforePlan: Ask), null, policy, all);
        var planDefault = SessionAccessResolver.FollowPolicy(new SessionAccessState(Plan), null, policy, all);
        var chosen = SessionAccessResolver.FollowPolicy(new SessionAccessState(Ask, AccessLevelSource: Explicit), null, policy, all);
        var same = SessionAccessResolver.FollowPolicy(new SessionAccessState(Full), null, policy, all);
        var codex = SessionAccessResolver.FollowPolicy(new SessionAccessState(Ask), null, policy with { DefaultLevel = Auto }, [Ask, Full]);

        Assert.Equal(new SessionAccessState(Plan, AccessLevelBeforePlan: Full), inPlan);
        Assert.Equal(new SessionAccessState(Full), planDefault);
        Assert.Null(chosen);
        Assert.Null(same);
        Assert.Null(codex);   // auto is not Codex's, so its default resolves to ask: unchanged
    }

    // ---- inheriting from a delegating parent (accessLevelSource parent) ----

    [Fact]
    public async Task A_codex_child_of_an_auto_parent_resolves_ask_capped_at_auto()
    {
        var f = new Fixture(Ask) { Provider = { Supported = [Ask, Full] } };

        var start = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, false, personPresent: false, null,
            parent: new SessionAccessParent("claude-code", "parent", Auto));

        Assert.Equal(new SessionAccessState(Ask, Auto, AccessLevelSource: ParentSource), start.State);
    }

    [Fact]
    public async Task A_requested_level_or_cap_only_lowers_what_the_parent_gives()
    {
        var f = new Fixture(Ask, fullEnabled: true);
        var parent = new SessionAccessParent("claude-code", "parent", Full);

        var higher = await f.Access.ResolveStartAsync(f.Provider.Proxy, Full, null, false, false, null, parent: parent with { AccessLevel = Auto });
        var lowerLevel = await f.Access.ResolveStartAsync(f.Provider.Proxy, Ask, null, false, false, null, parent: parent);
        var lowerCap = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, Auto, true, false, null, parent: parent);

        Assert.Equal(new SessionAccessState(Auto, Auto, AccessLevelSource: ParentSource), higher.State);
        Assert.Equal(new SessionAccessState(Ask, Full, AccessLevelSource: ParentSource), lowerLevel.State);
        Assert.Equal(new SessionAccessState(Auto, Auto, Unattended: true, AccessLevelSource: ParentSource), lowerCap.State);
    }

    [Fact]
    public async Task A_worker_of_an_unattended_parent_is_unattended_too()
    {
        // A heartbeat or Discord session delegates: nobody watches the worker either, so its asks
        // must be denied at once rather than park in a Code session no one looks at.
        var f = new Fixture(Ask, fullEnabled: true);
        var parent = new SessionAccessParent("claude-code", "parent", Full, Unattended: true);

        var start = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, unattended: false, personPresent: false, null, parent: parent);

        Assert.Equal(new SessionAccessState(Full, Full, Unattended: true, AccessLevelSource: ParentSource), start.State);
        var attended = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, unattended: false, personPresent: false, null,
            parent: parent with { Unattended = false });
        Assert.False(attended.State!.Unattended);
    }

    [Fact]
    public async Task Delegating_from_a_session_in_plan_is_refused_with_why()
    {
        var f = new Fixture(Ask, fullEnabled: true);

        var start = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, false, false, null,
            parent: new SessionAccessParent("claude-code", "parent", SessionAccessLevels.Plan));

        Assert.Null(start.State);
        Assert.Equal(SessionAccessResolver.ParentInPlan, start.Error);
        Assert.Contains("plan mode", start.Message);
    }

    [Fact]
    public async Task A_oneshot_run_by_a_capped_session_stays_within_its_level()
    {
        // Laurent's policy is Full; a Discord session runs at Auto under its cap. Its one-shots
        // used to resolve from the policy alone and ran at Full.
        var f = new Fixture(Auto, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };

        var bySession = await f.Access.ResolveOneshotAsync(f.Provider.Proxy, null, personPresent: false, null,
            parent: new SessionAccessParent("claude-code", "discord", Auto, Unattended: true));
        var asked = await f.Access.ResolveOneshotAsync(f.Provider.Proxy, Full, personPresent: false, null,
            parent: new SessionAccessParent("claude-code", "discord", Auto, Unattended: true));
        var noParent = await f.Access.ResolveOneshotAsync(f.Provider.Proxy, null, personPresent: false, null);

        Assert.Equal(Auto, bySession.State!.AccessLevel);
        Assert.Equal(Auto, asked.State!.AccessLevel);
        Assert.Equal(Full, noParent.State!.AccessLevel);
    }

    [Theory]
    [MemberData(nameof(LocalDefault))]
    [InlineData("kernel proxy without a token")]
    [InlineData("system browser token for a named subject")]
    public void A_caller_with_no_identity_of_its_own_never_starts_above_ask(string caller)
    {
        var context = new DefaultHttpContext();
        SessionRollbackTests.Identify(context, caller);

        Assert.True(ComputeResourceAccess.IsAnonymous(context));
        Assert.Equal(Ask, ComputeResourceAccess.AnonymousCap(context, null));
        Assert.Equal(Ask, ComputeResourceAccess.AnonymousCap(context, Full));
        Assert.Equal(SessionAccessLevels.Plan, ComputeResourceAccess.AnonymousCap(context, SessionAccessLevels.Plan));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("automation")]
    [InlineData("code browser")]
    [InlineData("nova through the kernel")]
    [InlineData("session process of an agent")]
    [InlineData("infrastructure service token")]
    [InlineData("signed login")]
    public void A_caller_with_its_own_identity_keeps_the_cap_it_asked_for(string caller)
    {
        var context = new DefaultHttpContext();
        SessionRollbackTests.Identify(context, caller);

        Assert.False(ComputeResourceAccess.IsAnonymous(context));
        Assert.Null(ComputeResourceAccess.AnonymousCap(context, null));
    }

    [Fact]
    public async Task A_session_started_in_plan_returns_to_its_default_when_the_plan_is_approved()
    {
        var f = new Fixture(Ask, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { DefaultLevel = Full };

        var start = await f.Access.ResolveStartAsync(f.Provider.Proxy, SessionAccessLevels.Plan, null, false, personPresent: true, null);
        var capped = await f.Access.ResolveStartAsync(f.Provider.Proxy, SessionAccessLevels.Plan, Auto, false, personPresent: true, null);

        Assert.Equal(new SessionAccessState(SessionAccessLevels.Plan, AccessLevelBeforePlan: Full, AccessLevelSource: Explicit), start.State);
        Assert.Equal(Auto, capped.State!.AccessLevelBeforePlan);
    }

    [Fact]
    public void The_found_parent_carries_whether_it_is_unattended()
    {
        var f = new Fixture(Auto);
        f.Provider.State = f.Provider.State with { Unattended = true };

        Assert.Equal(new SessionAccessParent("test", "session-1", Auto, Unattended: true), f.Access.FindParent(["session-1"], _ => true));
    }

    [Fact]
    public void A_named_parent_that_cannot_be_established_reads_as_ask_never_the_policy_default()
    {
        var f = new Fixture(Auto);

        Assert.Equal(new SessionAccessParent("test", "session-1", Auto), f.Access.FindParent(["session-1"], _ => true));
        Assert.Null(f.Access.FindParent([], _ => true));
        Assert.Null(f.Access.FindParent([null, " "], _ => true));
        // Named but not established: never the policy default (Full on an owner's install), Ask.
        var unresolved = SessionAccessService.UnresolvedParentProvider;
        Assert.Equal(new SessionAccessParent(unresolved, "session-1", Ask), f.Access.FindParent(["session-1"], _ => false));
        Assert.Equal(new SessionAccessParent(unresolved, "session-1,session-2", Ask), f.Access.FindParent(["session-1", "session-2"], _ => true));
        Assert.Equal(new SessionAccessParent(unresolved, "missing", Ask), f.Access.FindParent(["missing"], _ => true));
        Assert.Equal(new SessionAccessParent("test", "session-1", Ask), new Fixture(level: null).Access.FindParent(["session-1"], _ => true));
    }

    [Fact]
    public async Task A_parent_sourced_session_ignores_policy_changes_except_a_revoked_full()
    {
        var kept = new Fixture(Ask, source: ParentSource);
        kept.Policy.Policy = kept.Policy.Policy with { DefaultLevel = Full, FullAccess = new FullAccessAcknowledgement(true, 1) };
        await kept.PostPolicyChangedAsync("infrastructure service token");
        await kept.Access.EnsureLaunchStateAsync(kept.Provider.Proxy, "session-1");
        Assert.Equal(Ask, kept.Provider.State.AccessLevel);
        Assert.Equal(0, kept.Provider.Sets);

        var revoked = new Fixture(Full, fullEnabled: true, cap: Full, source: ParentSource);
        revoked.Policy.Policy = revoked.Policy.Policy with { FullAccess = new FullAccessAcknowledgement(false, 1) };
        var (_, body) = await revoked.PostPolicyChangedAsync("infrastructure service token");
        Assert.Equal(["session-1"], body.GetProperty("demotedSessions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new SessionAccessState(Ask, Full, AccessLevelSource: ParentSource), revoked.Provider.State);
    }

    // ---- approving a parked permission ----

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Nothing_but_a_person_can_approve_a_tool_call(string caller)
    {
        var f = new Fixture(Ask) { Readable = true };
        f.Provider.Pending.Add(Permission("perm-1"));

        var (status, body) = await f.PostQuestionAsync(new { requestId = "perm-1", decision = "allow_once" }, caller);

        Assert.Equal(403, status);
        Assert.Equal("approval_requires_person", body.GetProperty("error").GetString());
        Assert.Empty(f.Provider.Answers);
    }

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_person_can_approve_a_tool_call(string caller)
    {
        var f = new Fixture(Ask) { Readable = true };
        f.Provider.Pending.Add(Permission("perm-1"));

        var (status, body) = await f.PostQuestionAsync(new { requestId = "perm-1", decision = "allow_session" }, caller);

        Assert.Equal(200, status);
        Assert.Equal("allowed_session", body.GetProperty("outcome").GetString());
        Assert.Equal("allow_session", body.GetProperty("decision").GetString());
        Assert.Equal("permission", body.GetProperty("kind").GetString());
        var answer = Assert.Single(f.Provider.Answers);
        Assert.Equal(("perm-1", "allow_session"), (answer.RequestId, answer.Decision));
    }

    [Fact]
    public async Task A_decision_with_nothing_parked_is_still_judged_as_an_approval()
    {
        var f = new Fixture(Ask);

        var agent = await f.PostQuestionAsync(new { requestId = "gone", decision = "allow_once" }, "agent");
        var person = await f.PostQuestionAsync(new { requestId = "gone", decision = "allow_once" }, "code browser");

        Assert.Equal(403, agent.Status);
        Assert.Equal("approval_requires_person", agent.Body.GetProperty("error").GetString());
        Assert.Equal(409, person.Status);
    }

    [Fact]
    public async Task Approval_answers_are_validated()
    {
        var f = new Fixture(Ask);
        f.Provider.Pending.Add(Permission("perm-1"));
        f.Provider.Pending.Add(SessionQuestionCallbackTests.Question("q-1"));

        var noDecision = await f.PostQuestionAsync(new { requestId = "perm-1", response = "yes" }, "code browser");
        var unknown = await f.PostQuestionAsync(new { requestId = "perm-1", decision = "maybe" }, "code browser");
        var notOffered = await f.PostQuestionAsync(new { requestId = "perm-1", decision = "deny" }, "code browser", invalid: true);
        var decisionForQuestion = await f.PostQuestionAsync(new { requestId = "q-1", decision = "deny" }, "code browser");
        var denied = await f.PostQuestionAsync(new { requestId = "perm-1", decision = "deny" }, "code browser");

        Assert.Equal(422, noDecision.Status);
        Assert.Equal(422, unknown.Status);
        Assert.Equal(422, notOffered.Status);
        Assert.Equal(422, decisionForQuestion.Status);
        Assert.Equal(200, denied.Status);
        Assert.Equal("denied", denied.Body.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_agent_still_answers_an_ask_user_question()
    {
        var f = new Fixture(Ask);
        f.Provider.Pending.Add(SessionQuestionCallbackTests.Question("q-1"));

        var (status, body) = await f.PostQuestionAsync(new { requestId = "q-1", answers = new[] { "Postgres" } }, "agent");

        Assert.Equal(200, status);
        Assert.Equal("question", body.GetProperty("kind").GetString());
    }

    // ---- the service: start resolution, launch state, overlay ----

    [Fact]
    public async Task Start_resolves_from_the_verified_agent_and_clamps_a_non_person()
    {
        var f = new Fixture(Ask, fullEnabled: true);
        f.Policy.Policy = f.Policy.Policy with { Agents = new Dictionary<string, string> { ["agent-nova"] = Auto } };
        var provenance = Provenance("agent", "agent-nova", JobProvenanceAssurance.Verified);
        var agent = SessionAccessService.AgentEntityId(provenance);

        var byAgent = await f.Access.ResolveStartAsync(f.Provider.Proxy, Full, Full, unattended: true, personPresent: false, agent);
        var byPerson = await f.Access.ResolveStartAsync(f.Provider.Proxy, Full, null, unattended: false, personPresent: true, agent);
        var defaulted = await f.Access.ResolveStartAsync(f.Provider.Proxy, null, null, false, false, agent);

        Assert.Equal("agent-nova", agent);
        Assert.Equal(new SessionAccessState(Auto, Full, Unattended: true, AccessLevelSource: Explicit), byAgent.State);
        Assert.Equal(new SessionAccessState(Full, AccessLevelSource: Explicit), byPerson.State);
        Assert.Equal(new SessionAccessState(Auto, AccessLevelSource: Policy), defaulted.State);
    }

    [Fact]
    public void Only_verified_provenance_names_an_agent()
    {
        Assert.Equal("agent-1", SessionAccessService.AgentEntityId(Provenance("agent", "agent-1", JobProvenanceAssurance.Verified)));
        Assert.Equal("agent-1", SessionAccessService.AgentEntityId(Provenance("agent", "agent-1", JobProvenanceAssurance.BackfilledExact)));
        Assert.Null(SessionAccessService.AgentEntityId(Provenance("agent", "agent-1", JobProvenanceAssurance.Asserted)));
        Assert.Null(SessionAccessService.AgentEntityId(Provenance("app", "nova", JobProvenanceAssurance.Verified)));
        Assert.Equal("agent-2", SessionAccessService.AgentEntityId(Provenance("app", "nova", JobProvenanceAssurance.Verified)
            with { Context = [new JobContextReference("agent", "a", "agent-2")] }));
        Assert.Null(SessionAccessService.AgentEntityId(null));
    }

    [Fact]
    public async Task Launch_state_resolves_a_legacy_session_and_demotes_revoked_full()
    {
        var legacy = new Fixture(level: null);
        legacy.Policy.Policy = legacy.Policy.Policy with { DefaultLevel = Auto };
        await legacy.Access.EnsureLaunchStateAsync(legacy.Provider.Proxy, "session-1");
        Assert.Equal(Auto, legacy.Provider.State.AccessLevel);

        var revoked = new Fixture(Full);
        await revoked.Access.EnsureLaunchStateAsync(revoked.Provider.Proxy, "session-1");
        Assert.Equal(Ask, revoked.Provider.State.AccessLevel);

        var kept = new Fixture(Full, fullEnabled: true);
        await kept.Access.EnsureLaunchStateAsync(kept.Provider.Proxy, "session-1");
        Assert.Equal(Full, kept.Provider.State.AccessLevel);
        Assert.Equal(0, kept.Provider.Sets);
    }

    [Fact]
    public void Session_reads_carry_the_provider_access_state()
    {
        var f = new Fixture(Plan, cap: Auto);
        f.Provider.State = f.Provider.State with { Unattended = true, AccessLevelBeforePlan = Ask };

        var read = SessionAccessService.Overlay(new UnifiedSessionInfo
        {
            Id = "session-1", Provider = "test", ProjectName = "p", ProjectPath = "p", Status = SessionStatus.Idle,
        }, f.Provider.Proxy);

        Assert.Equal(Plan, read.AccessLevel);
        Assert.Equal(Auto, read.MaxAccessLevel);
        Assert.True(read.Unattended);
        Assert.Equal(Ask, read.AccessLevelBeforePlan);
        Assert.Equal(Explicit, read.AccessLevelSource);
        Assert.Equal([Plan, Ask, Auto, Full], read.SupportedAccessLevels);
        f.Provider.State = f.Provider.State with { AccessLevelSource = null };
        Assert.Equal(Policy, SessionAccessService.Overlay(new UnifiedSessionInfo
        {
            Id = "session-1", Provider = "test", ProjectName = "p", ProjectPath = "p", Status = SessionStatus.Idle,
        }, f.Provider.Proxy).AccessLevelSource);
    }

    [Fact]
    public async Task Discovery_lists_the_access_routes_and_levels()
    {
        var f = new Fixture(Ask);
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);
        SessionAccessEndpoints.Map(endpoints, f.Registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), f.Access);

        var change = endpoints.GetEndpoints().Single(e => e.Method == "POST" && e.Path == "/ai-session/sessions/{id}/access-level");
        var alias = endpoints.GetEndpoints().Single(e => e.Method == "POST" && e.Path == "/ai-session/sessions/{id}/permission-mode");
        Assert.Contains(change.Parameters!, p => p.Name == "level");
        Assert.Contains("raise_requires_person", change.Description);
        Assert.Contains("access-level", alias.Description);
        Assert.Contains(endpoints.GetEndpoints(), e => e.Method == "POST" && e.Path == "/ai-session/access-policy/changed");
    }

    // ---- fixture ----

    internal static PendingSessionQuestion Permission(string requestId)
    {
        var input = JsonSerializer.SerializeToElement(new { command = "node -e \"console.log(6*7)\"" });
        using var empty = JsonDocument.Parse("[]");
        return new PendingSessionQuestion
        {
            RequestId = requestId, Kind = PendingSessionQuestion.PermissionKind, ToolName = "Bash", ToolUseId = "toolu_1",
            Questions = empty.RootElement.Clone(), Input = input, AskedAt = DateTimeOffset.UtcNow,
            Permission = new PendingPermissionRequest { ToolName = "Bash", Summary = "Run node", Input = input },
        };
    }

    private static JobProvenance Provenance(string actorKind, string actorEntity, JobProvenanceAssurance assurance) => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", "nova", null, "Nova"), new JobEntrypoint("http", "/ai-session/sessions", "POST")),
        new JobActor(actorKind, "Worker", actorEntity),
        new JobBeneficiary("user", "owner", "Owner"),
        [], new JobTrace(), assurance, DateTimeOffset.UtcNow);

    private sealed class Fixture
    {
        internal readonly AccessProvider Provider;
        internal readonly StaticSessionAccessPolicySource Policy;
        internal readonly CapabilityRegistry Registry = new();
        internal readonly SessionAccessService Access;

        /// <summary>When set, the session has no owner, so every caller may read and write it and only the person rule decides.</summary>
        internal bool Readable { set => Provider.Info.UserId = value ? null : "owner"; }

        /// <param name="source">Where the session's level came from; explicit unless a test says otherwise, null being a row from before sources.</param>
        internal Fixture(string? level, bool fullEnabled = false, string? cap = null, string? source = Explicit)
        {
            var proxy = DispatchProxy.Create<ITestProvider, AccessProvider>();
            Provider = (AccessProvider)(object)proxy;
            Provider.Proxy = proxy;
            Provider.State = new SessionAccessState(level, cap, AccessLevelSource: level is null ? null : source);
            Policy = new StaticSessionAccessPolicySource(new SessionAccessPolicy
            {
                FullAccess = new FullAccessAcknowledgement(fullEnabled, 1),
            });
            Registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = proxy }, "test");
            Access = new SessionAccessService(Policy, () => Registry.FindProviders<ISessionProvider>(), _ => null, (_, _) => { });
        }

        internal Task<(int Status, JsonElement Body)> PostAsync(string route, object body, string caller,
            string subject = "owner", string sessionId = "session-1")
            => PostRawAsync(route, JsonSerializer.Serialize(body), caller, subject, sessionId);

        internal async Task<(int Status, JsonElement Body)> PostRawAsync(string route, string json, string caller,
            string subject = "owner", string sessionId = "session-1")
        {
            await using var app = WebApplication.CreateSlimBuilder().Build();
            SessionAccessEndpoints.Map(new EndpointRegistry(app), Registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), Access);
            return await InvokeAsync(app, $"/ai-session/sessions/{{id}}/{route}", json, caller, subject, sessionId);
        }

        internal async Task<(int Status, JsonElement Body)> PostPolicyChangedAsync(string caller)
        {
            await using var app = WebApplication.CreateSlimBuilder().Build();
            SessionAccessEndpoints.Map(new EndpointRegistry(app), Registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), Access);
            return await InvokeAsync(app, "/ai-session/access-policy/changed", "{}", caller, "owner", null);
        }

        internal async Task<(int Status, JsonElement Body)> PostQuestionAsync(object body, string caller, bool invalid = false, string subject = "owner")
        {
            Provider.RejectDecision = invalid;
            using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
            await using var app = WebApplication.CreateSlimBuilder().Build();
            UnifiedSessionEndpoints.Map(new EndpointRegistry(app), Registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), (_, _) => { },
                f.Config, attachmentStore: f.Attachments, inputQueue: f.Service(), access: Access);
            return await InvokeAsync(app, "/ai-session/sessions/{id}/question", JsonSerializer.Serialize(body), caller, subject, "session-1");
        }

        private static async Task<(int Status, JsonElement Body)> InvokeAsync(WebApplication app, string pattern, string json,
            string caller, string subject, string? sessionId)
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(item => item.RoutePattern.RawText == pattern
                    && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            var context = new DefaultHttpContext
            {
                RequestServices = app.Services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "signed")),
            };
            SessionRollbackTests.Identify(context, caller, subject);
            context.SetEndpoint(endpoint);
            context.Request.Method = "POST";
            if (sessionId is not null) context.Request.RouteValues["id"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            var output = new MemoryStream();
            context.Response.Body = output;

            await endpoint.RequestDelegate!(context);

            output.Position = 0;
            using var document = output.Length == 0 ? JsonDocument.Parse("{}") : await JsonDocument.ParseAsync(output);
            return (context.Response.StatusCode, document.RootElement.Clone());
        }
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class AccessProvider : DispatchProxy
    {
        internal ITestProvider Proxy = null!;
        internal SessionAccessState State = new(SessionAccessLevels.Ask);
        internal IReadOnlyList<string> Supported = SessionAccessLevels.All;
        internal int Sets;
        // While set, a live switch is refused with this reason, as Claude refuses set_permission_mode.
        internal string? Refusal;
        internal int Stops;
        internal readonly List<PendingSessionQuestion> Pending = [];
        internal readonly List<SessionQuestionAnswer> Answers = [];
        internal bool RejectDecision;
        internal readonly UnifiedSessionInfo Info = new()
        {
            Id = "session-1", Provider = "test", ProjectName = "Nova", ProjectPath = "C:\\nova",
            Status = SessionStatus.Active, UserId = "owner",
        };

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case "get_Capabilities":
                    return SessionCapabilities.PersistentSessions | SessionCapabilities.SendMessage | SessionCapabilities.PermissionMode;
                case "get_Name": return "Test";
                case "get_CapabilitySlug": return "ai-session";
                case "get_HealthCheckInterval": return TimeSpan.FromSeconds(10);
                case "get_SupportedAccessLevels": return Supported;
                case nameof(ISessionProvider.GetAvailableModels): return new List<ModelInfo>();
                case nameof(ISessionProvider.GetSession):
                    if ((string)args![0]! != Info.Id) return ((UnifiedSessionInfo?)null, new List<UnifiedMessageRecord>());
                    Info.PermissionMode = "native-" + State.AccessLevel;
                    return (Info, new List<UnifiedMessageRecord>());
                case nameof(ISessionProvider.GetSessions): return new List<UnifiedSessionInfo> { Info };
                case nameof(ISessionProvider.GetAccessState):
                    return (string)args![0]! == Info.Id ? State : null;
                case nameof(ISessionProvider.SetAccessState):
                    Sets++;
                    State = (SessionAccessState)args![1]!;
                    return true;
                case nameof(ISessionProvider.ApplyAccessStateAsync):
                    if (Refusal is { } why) return Task.FromResult(SessionAccessApplyResult.Refused(why));
                    Sets++;
                    State = (SessionAccessState)args![1]!;
                    return Task.FromResult(SessionAccessApplyResult.Done);
                case nameof(ISessionProvider.StopSessionAsync):
                    // A stopped session has no harness to refuse: its level is just persisted.
                    Stops++;
                    Refusal = null;
                    return Task.CompletedTask;
                case nameof(ISessionProvider.GetBackgroundTasks): return (IReadOnlyList<BackgroundTaskInfo>)[];
                case nameof(ISessionProvider.GetPendingQuestions): return (IReadOnlyList<PendingSessionQuestion>)[.. Pending];
                case nameof(ISessionProvider.SubmitQuestionAnswer):
                {
                    var answer = (SessionQuestionAnswer)args![1]!;
                    var parked = Pending.FirstOrDefault(q => q.RequestId == answer.RequestId);
                    if (parked is null) return QuestionAnswerResult.RequestNotFound;
                    if (answer.Decision is null) return QuestionAnswerResult.Answered;
                    if (RejectDecision) return QuestionAnswerResult.InvalidDecision;
                    Answers.Add(answer);
                    Pending.Remove(parked);
                    return answer.Decision == SessionPermissionDecisions.Deny ? QuestionAnswerResult.Denied : QuestionAnswerResult.Allowed;
                }
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
