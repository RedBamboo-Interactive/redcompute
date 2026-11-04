using System.Net.WebSockets;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
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

// The orchestration of a rollback with the provider faked: the route, the idle gate, the ledger that
// makes it restartable, and the epoch that makes every client reset. The Claude and Codex halves are
// replayed in ClaudeRollbackTests and CodexRollbackTests.
public sealed class SessionRollbackTests
{
    [Fact]
    public async Task Rollback_then_next_message_continues_above_the_rotation_in_the_new_epoch()
    {
        using var f = new Fixture();
        var before = f.Sequences.Next("test", "session").Epoch;

        var outcome = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        Assert.True(outcome.RolledBack);
        Assert.False(outcome.AlreadyRolledBack);
        Assert.Equal("u2", outcome.MessageUid);
        Assert.Equal(["u2", "a2", "u3", "a3"], outcome.RemovedMessageUids);
        Assert.Equal("second", outcome.RestoredText);
        Assert.NotEqual(before, outcome.Epoch);
        // A product uses this to keep what was written after the rollback when a repeat arrives late.
        Assert.NotNull(outcome.RolledBackAt);

        // The provider is told exactly what stays and what goes.
        var request = Assert.Single(f.Runtime.Requests);
        Assert.Equal("u2", request.TargetMessageUid);
        Assert.Equal(["u1"], request.KeptUserTurns.Select(turn => turn.MessageUid));
        Assert.Equal(["u2", "u3"], request.DroppedUserTurns.Select(turn => turn.MessageUid));

        // Records are marked from the target's record on. Nothing is deleted.
        Assert.Equal([(f.EntityId, 12L, outcome.RollbackId!)], f.Transcript.Marks);

        // The next message's records follow the rotation in one ordered line.
        var next = f.Sequences.Next("test", "session");
        Assert.Equal(outcome.Epoch, next.Epoch);
        Assert.True(next.Sequence > outcome.ThroughSequence);
    }

    [Fact]
    public async Task Rollback_tells_live_clients_through_the_ordered_stream()
    {
        using var f = new Fixture();
        var socket = f.AttachClient();

        var outcome = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        var frame = Assert.Single(socket.Frames);
        using var json = JsonDocument.Parse(frame);
        Assert.Equal("session.stream", json.RootElement.GetProperty("type").GetString());
        var evt = json.RootElement.GetProperty("data").GetProperty("event");
        Assert.Equal("transcript_reset", evt.GetProperty("type").GetString());
        Assert.Equal("u2", evt.GetProperty("messageUid").GetString());
        Assert.Equal(outcome.Epoch, evt.GetProperty("epoch").GetString());
        Assert.Equal(outcome.ThroughSequence, evt.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task Confidential_session_reset_stays_content_free()
    {
        using var f = new Fixture { Confidential = true };
        var socket = f.AttachClient();

        await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        var frame = Assert.Single(socket.Frames);
        using var json = JsonDocument.Parse(frame);
        Assert.Equal("ai-session.changed", json.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("u2", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("second", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_of_a_refused_turn_removes_the_error_row_with_it()
    {
        using var f = new Fixture();
        f.Transcript.History =
        [
            Fixture.Record(10, "u1", "user", "first", 1),
            Fixture.Record(11, "a1", "assistant", "kept", 2),
            Fixture.Record(12, "u2", "user", "the refused prompt", 3),
            new()
            {
                Id = 13, SessionId = "session", Role = "assistant", EventType = "error", MessageUid = "a2",
                Content = "API Error: safeguards flagged this message", Sequence = 4, Epoch = "e",
            },
        ];

        var outcome = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        Assert.True(outcome.RolledBack);
        Assert.Equal(["u2", "a2"], outcome.RemovedMessageUids);
        Assert.Equal("the refused prompt", outcome.RestoredText);
    }

    [Fact]
    public async Task Duplicate_request_returns_the_same_result_and_changes_nothing()
    {
        using var f = new Fixture();
        var service = f.Service();

        var first = await service.RollbackAsync(f.Provider, "session", "u2", default);
        f.Runtime.Info.Status = SessionStatus.Active; // Even no longer idle: it asks for what already happened.
        var second = await service.RollbackAsync(f.Provider, "session", "u2", default);
        var third = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        Assert.True(second.RolledBack);
        Assert.True(second.AlreadyRolledBack);
        Assert.Equal(first.RollbackId, second.RollbackId);
        Assert.Equal(first.Epoch, second.Epoch);
        Assert.Equal(first.ThroughSequence, second.ThroughSequence);
        Assert.Equal(first.RemovedMessageUids, second.RemovedMessageUids);
        Assert.Equal(first.RolledBackAt, second.RolledBackAt);
        Assert.Equal(first.RollbackId, third.RollbackId);
        Assert.Single(f.Runtime.Requests);
        Assert.Single(f.Transcript.Marks);
    }

    [Fact]
    public async Task Restart_between_rollback_and_next_message_changes_nothing_a_client_can_see()
    {
        using var f = new Fixture();
        var outcome = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        var reopened = new TranscriptSequenceStore(f.SequencesPath);
        var view = reopened.CurrentView("test", "session");

        Assert.NotNull(view);
        Assert.Equal(outcome.Epoch, view.Epoch);
        Assert.Equal(outcome.ThroughSequence, view.RotationSequence);
        Assert.Equal(outcome.Epoch, reopened.Next("test", "session").Epoch);
        Assert.False(f.Service().HasIncomplete("session"));
        Assert.Single(f.Runtime.Requests);
    }

    [Theory]
    [InlineData(SessionRollbackState.Prepared, 1, 1)]
    [InlineData(SessionRollbackState.ProviderApplied, 0, 1)]
    [InlineData(SessionRollbackState.Marked, 0, 0)]
    public async Task Recovery_finishes_a_rollback_a_restart_interrupted(string state, int providerCalls, int marks)
    {
        using var f = new Fixture();
        f.Ledger.Insert(f.Row(state));
        var service = f.Service();
        Assert.True(service.HasIncomplete("session"));

        await service.RecoverAsync(default);

        Assert.False(service.HasIncomplete("session"));
        Assert.Equal(providerCalls, f.Runtime.Requests.Count);
        Assert.Equal(marks, f.Transcript.Marks.Count);
        var view = f.Sequences.CurrentView("test", "session");
        Assert.NotNull(view);
        Assert.Equal(TranscriptOrdering.RotatedEpoch("test", "session", "rollback-1"), view.Epoch);
        Assert.Equal("rollback-1", f.Ledger.Find("session", "u2")!.RollbackId);
    }

    [Fact]
    public async Task A_mark_failure_holds_input_and_a_retry_finishes_the_same_rollback()
    {
        using var f = new Fixture();
        f.Transcript.FailMarks = 1;
        var service = f.Service();

        var failed = await service.RollbackAsync(f.Provider, "session", "u2", default);

        Assert.False(failed.RolledBack);
        Assert.Equal(503, failed.StatusCode);
        Assert.Equal(SessionRollbackRefusals.Incomplete, failed.Error);
        Assert.True(service.HasIncomplete("session"));
        Assert.Null(f.Sequences.CurrentView("test", "session"));

        // Even a blocker that appeared since cannot strand the half that already happened.
        f.Runtime.Info.Status = SessionStatus.Active;
        var retried = await service.RollbackAsync(f.Provider, "session", "u2", default);

        Assert.True(retried.RolledBack);
        Assert.False(service.HasIncomplete("session"));
        Assert.Single(f.Runtime.Requests);
        Assert.Single(f.Transcript.Marks);
    }

    [Fact]
    public async Task Startup_recovery_makes_one_pass_and_does_not_wait_for_what_cannot_finish()
    {
        using var f = new Fixture();
        f.Transcript.FailMarks = 1;
        f.Ledger.Insert(f.Row(SessionRollbackState.ProviderApplied));
        var service = f.Service();

        await service.RecoverAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.HasIncomplete("session"));
        Assert.Empty(f.Transcript.Marks);
        Assert.Equal(0, f.Transcript.FailMarks);
    }

    [Fact]
    public async Task A_resume_finishes_an_unfinished_rollback_before_the_session_comes_back()
    {
        using var f = new Fixture();
        f.Ledger.Insert(f.Row(SessionRollbackState.Prepared));
        var rollbacks = f.Service();
        var queue = f.Queue();
        queue.SettleRollback = rollbacks.SettleAsync;
        var unfinishedAtResume = true;

        await queue.ExplicitResumeAsync("session", () =>
        {
            unfinishedAtResume = rollbacks.HasIncomplete("session");
            return Task.FromResult(true);
        }, default);

        Assert.False(unfinishedAtResume);
        Assert.Single(f.Runtime.Requests);
    }

    [Fact]
    public async Task Input_is_not_delivered_while_a_rollback_is_unfinished_and_the_queue_finishes_it()
    {
        using var f = new Fixture();
        // One failure for the request, one for the queue's first visit.
        f.Transcript.FailMarks = 2;
        var rollbacks = f.Service();
        var queue = f.Queue();
        queue.SettleRollback = rollbacks.SettleAsync;
        await rollbacks.RollbackAsync(f.Provider, "session", "u2", default);

        await f.Store.EnqueueAsync(f.Submission("after"), default);
        await queue.ProcessSessionAsync("session", default);

        Assert.Equal(0, f.Runtime.Sends);
        Assert.True(rollbacks.HasIncomplete("session"));

        // No repeat of the request: the sweep coming back for the waiting input is enough.
        await queue.ProcessSessionAsync("session", default);

        Assert.False(rollbacks.HasIncomplete("session"));
        Assert.Equal(1, f.Runtime.Sends);
        Assert.Single(f.Runtime.Requests);
        Assert.Single(f.Transcript.Marks);
    }

    [Theory]
    [InlineData(SessionRollbackBlockers.TurnActive)]
    [InlineData(SessionRollbackBlockers.QuestionParked)]
    [InlineData(SessionRollbackBlockers.BackgroundTasksRunning)]
    [InlineData(SessionRollbackBlockers.InputQueued)]
    [InlineData(SessionRollbackBlockers.SessionStopped)]
    public async Task Each_reason_it_is_not_idle_gets_a_structured_409_and_touches_nothing(string reason)
    {
        using var f = new Fixture();
        await f.Block(reason);

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(SessionRollbackRefusals.NotIdle, body.GetProperty("error").GetString());
        Assert.Equal([reason], body.GetProperty("reasons").EnumerateArray().Select(item => item.GetString()!));
        // Clients show this sentence as it is, so it says it in words.
        Assert.DoesNotContain("_", body.GetProperty("message").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task Every_reason_is_reported_together()
    {
        using var f = new Fixture();
        await f.Block(SessionRollbackBlockers.TurnActive);
        await f.Block(SessionRollbackBlockers.QuestionParked);
        await f.Block(SessionRollbackBlockers.BackgroundTasksRunning);
        await f.Block(SessionRollbackBlockers.InputQueued);

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(
            [
                SessionRollbackBlockers.TurnActive, SessionRollbackBlockers.QuestionParked,
                SessionRollbackBlockers.BackgroundTasksRunning, SessionRollbackBlockers.InputQueued,
            ],
            body.GetProperty("reasons").EnumerateArray().Select(item => item.GetString()!));
    }

    [Fact]
    public async Task A_parked_session_is_idle_and_a_finished_background_task_does_not_block()
    {
        using var f = new Fixture();
        f.Runtime.Info.Status = SessionStatus.Stopped;
        f.Runtime.Info.StopReason = SessionStopReasons.IdleTimeout;
        f.Runtime.BackgroundTasks =
        [
            new BackgroundTaskInfo { TaskId = "t", Kind = "shell", Status = BackgroundTaskStatuses.Completed },
        ];

        var (status, _) = await f.PostAsync("u2");

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Capability_false_is_refused_before_anything_is_read()
    {
        using var f = new Fixture();
        f.Runtime.Capability = false;

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(SessionRollbackRefusals.Unsupported, body.GetProperty("error").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task A_provider_with_the_capability_can_still_refuse_one_session()
    {
        using var f = new Fixture();
        f.Runtime.SupportsThisSession = false;

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(SessionRollbackRefusals.Unsupported, body.GetProperty("error").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task A_kernel_that_cannot_mark_refuses_before_the_provider_changes()
    {
        using var f = new Fixture();
        f.Transcript.CanMark = false;

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(SessionRollbackRefusals.Unsupported, body.GetProperty("error").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task Unknown_target_is_404_and_an_assistant_target_is_refused()
    {
        using var f = new Fixture();

        var missing = await f.PostAsync("nobody");
        var assistant = await f.PostAsync("a1");

        Assert.Equal(404, missing.Status);
        Assert.Equal(SessionRollbackRefusals.TargetNotFound, missing.Body.GetProperty("error").GetString());
        Assert.Equal(409, assistant.Status);
        Assert.Equal(SessionRollbackRefusals.TargetNotUserMessage, assistant.Body.GetProperty("error").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task A_compaction_after_the_target_refuses_and_one_before_it_does_not()
    {
        using var f = new Fixture();
        f.Transcript.History =
        [
            Fixture.Record(10, "u1", "user", "first", 1),
            new()
            {
                Id = 11, SessionId = "session", Role = "tool", EventType = "tool_use", MessageUid = "c1",
                ToolName = "ContextCompaction", Sequence = 2, Epoch = "e",
            },
            Fixture.Record(12, "u2", "user", "second", 3),
            Fixture.Record(13, "a2", "assistant", "reply", 4),
        ];

        var before = await f.PostAsync("u1");
        Assert.Equal(409, before.Status);
        Assert.Equal(SessionRollbackRefusals.TargetBeforeCompaction, before.Body.GetProperty("error").GetString());
        f.AssertUntouched();

        var after = await f.PostAsync("u2");
        Assert.Equal(200, after.Status);
    }

    [Fact]
    public async Task A_provider_refusal_is_final_and_leaves_nothing_pending()
    {
        using var f = new Fixture();
        f.Runtime.Result = SessionRollbackResult.Refused(SessionRollbackRefusals.ProviderRejected, "Resume rejected by --resume-drops-turn: nope");
        var service = f.Service();

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(409, status);
        Assert.Equal(SessionRollbackRefusals.ProviderRejected, body.GetProperty("error").GetString());
        Assert.Contains("Resume rejected", body.GetProperty("message").GetString());
        Assert.Empty(f.Transcript.Marks);
        Assert.Null(f.Sequences.CurrentView("test", "session"));
        Assert.False(service.HasIncomplete("session"));
        Assert.Null(f.Ledger.Find("session", "u2"));
    }

    [Fact]
    public async Task An_unavailable_provider_is_503_and_can_be_asked_again()
    {
        using var f = new Fixture();
        f.Runtime.Result = SessionRollbackResult.Unavailable("no answer");

        var first = await f.PostAsync("u2");
        f.Runtime.Result = SessionRollbackResult.Applied();
        var second = await f.PostAsync("u2");

        Assert.Equal(503, first.Status);
        Assert.Equal(SessionRollbackRefusals.ProviderUnavailable, first.Body.GetProperty("error").GetString());
        Assert.Equal(200, second.Status);
    }

    [Fact]
    public async Task A_second_rollback_rotates_again_and_keeps_removing_the_first_range()
    {
        using var f = new Fixture();
        for (var i = 0; i < 6; i++) f.Sequences.Next("test", "session");
        var first = await f.Service().RollbackAsync(f.Provider, "session", "u3", default);
        var kept = f.Transcript.History.Take(4).ToList();
        f.Transcript.History = kept;

        var second = await f.Service().RollbackAsync(f.Provider, "session", "u2", default);

        Assert.NotEqual(first.Epoch, second.Epoch);
        Assert.True(second.ThroughSequence > first.ThroughSequence);
        var view = f.Sequences.CurrentView("test", "session")!;
        Assert.Equal(second.Epoch, view.Epoch);
        Assert.True(view.IsRemoved(5)); // The first rollback's message.
        Assert.True(view.IsRemoved(3)); // The second's.
        Assert.False(view.IsRemoved(2));
    }

    /// <summary>
    /// The identities as the kernel builds them: <c>/api/auth/execution-token</c> for the browser token,
    /// <c>ComputeGateway</c> for a hop through a kernel service, <c>WithPageCause</c> for a page's own
    /// mutation, <c>SessionExecutionToken.Child</c> for a session's own process.
    /// </summary>
    public static TheoryData<string> People => new()
    {
        "signed login", "code browser", "code browser with page cause", "nova through the kernel",
        "nova through the kernel with cause", "loopback browser", "loopback nova through the kernel",
    };

    public static TheoryData<string> NotPeople => new()
    {
        "agent", "agent through the kernel", "agent with a browser context", "automation",
        "session process of a browser token", "session process of an agent", "direct client session process",
        "hop of a hop", "hop with another step", "hop through a bare other step", "root with another step", "no browser",
        "one step that is not a browser", "browser with no route", "system browser token for a named subject",
        "kernel proxy without a token", "browser for someone else", "local subject claiming a user",
        "infrastructure service token", "service subject", "system subject", "loopback without a token",
    };

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_person_can_rewind_however_their_request_arrives(string caller)
    {
        using var f = new Fixture();

        var answer = await f.PostAsync("u2", identify: context => Identify(context, caller));

        Assert.Equal(200, answer.Status);
        Assert.True(answer.Body.GetProperty("rolledBack").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Anything_else_with_read_access_is_refused_and_changes_nothing(string caller)
    {
        using var f = new Fixture();
        f.Runtime.Info.UserId = null; // Anyone can read it, so only the person check can refuse.

        var answer = await f.PostAsync("u2", identify: context => Identify(context, caller));

        Assert.Equal(403, answer.Status);
        Assert.Equal("rollback_requires_person", answer.Body.GetProperty("error").GetString());
        f.AssertUntouched();
    }

    [Fact]
    public async Task Who_is_asking_is_not_said_to_someone_who_cannot_see_the_session()
    {
        using var f = new Fixture();

        var answer = await f.PostAsync("u2", identify: context => Identify(context, "agent", subject: "intruder"));

        Assert.Equal(403, answer.Status);
        Assert.Equal("forbidden", answer.Body.GetProperty("error").GetString());
    }

    private static void Identify(DefaultHttpContext context, string caller, string subject = "owner")
    {
        ExecutionContextReference Browser(string app) => new("browser", Route: $"/apps/{app}");
        var hop = new ExecutionContextReference("redcompute-call");
        var cause = new ExecutionContextReference(JobCauses.ContextKind, JobCauses.Beneficiary);
        ExecutionIdentity App(string app, ExecutionBeneficiaryIdentity beneficiary, string? parent, params ExecutionContextReference[] steps) =>
            new(ExecutionIdentity.CurrentSchemaVersion, Guid.NewGuid().ToString(),
                new ExecutionAppIdentity(app, app, "plugin-" + app),
                new ExecutionActorIdentity("app", app, app, "plugin-" + app),
                beneficiary, steps, parent);
        ExecutionIdentity Actor(string kind, ExecutionBeneficiaryIdentity beneficiary, string? parent, params ExecutionContextReference[] steps) =>
            App("nova", beneficiary, parent, steps) with { Actor = new ExecutionActorIdentity(kind, "worker", "Worker", "worker-entity") };
        var person = new ExecutionBeneficiaryIdentity("user", subject, "Owner");
        var local = new ExecutionBeneficiaryIdentity("system", Reason: "Browser work requested through the loopback LocalDefault identity");
        var session = new[]
        {
            new ExecutionContextReference("ai-session", "session"),
            new ExecutionContextReference("ai-session-provider", "fake", Name: "Fake"),
            new ExecutionContextReference(JobCauses.ContextKind, JobCauses.Agent),
        };

        void Execution(ExecutionIdentity identity, string sub)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", sub), new Claim(ExecutionIdentityClaims.TokenUseClaim, ExecutionIdentityClaims.TokenUse)], "signed"));
            context.Items[ExecutionIdentityClaims.HttpContextItemKey] = identity;
        }

        void Plain(string authenticationType, params Claim[] claims) =>
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));

        switch (caller)
        {
            case "signed login": Plain("signed", new Claim("sub", subject)); break;
            case "code browser": Execution(App("codered", person, null, Browser("codered")), subject); break;
            case "code browser with page cause": Execution(App("codered", person, null, Browser("codered"), cause), subject); break;
            case "nova through the kernel": Execution(App("nova", person, "browser-token", Browser("nova"), hop), subject); break;
            case "nova through the kernel with cause": Execution(App("nova", person, "browser-token", Browser("nova"), hop, cause), subject); break;
            case "loopback browser": Execution(App("codered", local, null, Browser("codered")), "local-user"); break;
            case "loopback nova through the kernel": Execution(App("nova", local, "browser-token", Browser("nova"), hop), "local-user"); break;

            case "agent": Execution(Actor("agent", person, null, new ExecutionContextReference("discussion", "d1")), subject); break;
            case "agent through the kernel": Execution(Actor("agent", person, "agent-token", new ExecutionContextReference("discussion", "d1"), hop), subject); break;
            case "agent with a browser context": Execution(Actor("agent", person, null, Browser("nova")), subject); break;
            case "automation": Execution(Actor("automation", person, null, Browser("nova")), subject); break;
            case "session process of a browser token": Execution(App("codered", person, "browser-token", [Browser("codered"), .. session]), subject); break;
            case "session process of an agent": Execution(Actor("agent", person, "agent-token", session), subject); break;
            case "direct client session process": Execution(App("direct-redcompute-api", person, null, session), subject); break;
            case "hop of a hop": Execution(App("nova", person, "derived-token", Browser("nova"), hop, hop), subject); break;
            case "hop with another step": Execution(App("nova", person, "browser-token", Browser("nova"), new ExecutionContextReference("flow", "f1")), subject); break;
            case "hop through a bare other step": Execution(App("nova", person, "browser-token", Browser("nova"), new ExecutionContextReference("worker")), subject); break;
            case "one step that is not a browser": Execution(App("codered", person, null, new ExecutionContextReference("discussion", "d1")), subject); break;
            case "browser with no route": Execution(App("codered", person, null, new ExecutionContextReference("browser")), subject); break;
            case "system browser token for a named subject": Execution(App("codered", local, null, Browser("codered")), subject); break;
            case "root with another step": Execution(App("nova", person, null, Browser("nova"), new ExecutionContextReference("flow", "f1")), subject); break;
            case "no browser": Execution(App("nova", person, null), subject); break;
            case "kernel proxy without a token": Execution(App("redleaf", local, null, new ExecutionContextReference("redcompute-route", Route: "/ai-session"), new ExecutionContextReference("origin-entrypoint", Route: "/ai-session")), "system"); break;
            case "browser for someone else": Execution(App("codered", new ExecutionBeneficiaryIdentity("user", "someone-else"), null, Browser("codered")), subject); break;
            case "local subject claiming a user": Execution(App("codered", new ExecutionBeneficiaryIdentity("user", "local-user"), null, Browser("codered")), "local-user"); break;
            case "infrastructure service token": Plain("signed", new Claim("sub", "service:redleaf"), new Claim("client_id", "redleaf"), new Claim("compute_provenance", "true")); break;
            case "service subject": Plain("signed", new Claim("sub", "service:nova"), new Claim("name", "Nova")); break;
            case "system subject": Plain("signed", new Claim("sub", "system")); break;
            case "loopback without a token": Plain(ConfidentialResourcePolicy.LocalDefaultAuthenticationType, new Claim("sub", "local-user")); break;
            default: throw new ArgumentOutOfRangeException(nameof(caller), caller, null);
        }
    }

    [Fact]
    public async Task Route_rejects_a_missing_target_and_hides_other_peoples_sessions()
    {
        using var f = new Fixture();

        var empty = await f.PostAsync(null);
        var intruder = await f.PostAsync("u2", user: "intruder");
        var unknown = await f.PostAsync("u2", sessionId: "missing");

        Assert.Equal(400, empty.Status);
        Assert.Equal(403, intruder.Status);
        Assert.Equal(404, unknown.Status);
        f.AssertUntouched();
    }

    [Fact]
    public async Task Discover_describes_the_route_with_files_never_changing()
    {
        using var f = new Fixture();
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);

        SessionRollbackEndpoints.Map(endpoints, f.Registry, new NoJobs(), f.Service());

        var route = endpoints.GetEndpoints().Single(endpoint => endpoint.Path == "/ai-session/sessions/{id}/rollback");
        Assert.Equal("POST", route.Method);
        Assert.Contains("files are never changed", route.Description, StringComparison.OrdinalIgnoreCase);
        foreach (var refusal in new[]
                 {
                     SessionRollbackBlockers.TurnActive, SessionRollbackBlockers.QuestionParked,
                     SessionRollbackBlockers.BackgroundTasksRunning, SessionRollbackBlockers.InputQueued,
                     SessionRollbackBlockers.SessionStopped, SessionRollbackRefusals.TargetBeforeCompaction,
                 })
            Assert.Contains(refusal, route.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_projects_kept_records_into_the_current_epoch_and_hides_removed_ones()
    {
        using var f = new Fixture();
        var reader = new RedLeafSessionReader(new HttpClient(), null!) { Sequences = f.Sequences };
        for (var i = 0; i < 4; i++) f.Sequences.Next("test", "session");
        var old = TranscriptOrdering.Epoch("test", "session");
        var stamp = f.Sequences.Rotate("test", "session", "r1", 3);
        List<UnifiedMessageRecord> durable =
        [
            new() { Id = 1, SessionId = "session", Role = "user", EventType = "text", Sequence = 1, Epoch = old },
            new() { Id = 2, SessionId = "session", Role = "assistant", EventType = "text", Sequence = 2, Epoch = old },
            // A batched write of the removed turn reached RedLeaf after it was marked.
            new() { Id = 3, SessionId = "session", Role = "assistant", EventType = "text", Sequence = 4, Epoch = old },
            new() { Id = 4, SessionId = "session", Role = "user", EventType = "text", Sequence = stamp.Sequence + 1, Epoch = stamp.Epoch },
        ];

        var visible = reader.ProjectEpoch("test", "session", durable);
        var cursor = reader.Cursor("test", "session", visible);

        Assert.Equal([1L, 2L, 4L], visible.Select(record => record.Id));
        Assert.All(visible, record => Assert.Equal(stamp.Epoch, record.Epoch));
        Assert.Equal(stamp.Epoch, cursor.Epoch);
        Assert.Equal(stamp.Sequence + 1, cursor.ThroughSequence);
        Assert.Equal(stamp.Epoch, reader.CurrentEpoch("test", "session"));
    }

    [Fact]
    public void A_session_that_never_rolled_back_keeps_its_derived_epoch()
    {
        using var f = new Fixture();
        var reader = new RedLeafSessionReader(new HttpClient(), null!) { Sequences = f.Sequences };
        var record = new UnifiedMessageRecord
        {
            Id = 1, SessionId = "session", Role = "user", EventType = "text", Sequence = 1,
            Epoch = TranscriptOrdering.Epoch("test", "session"),
        };

        var visible = reader.ProjectEpoch("test", "session", [record]);

        Assert.Equal(TranscriptOrdering.Epoch("test", "session"), reader.CurrentEpoch("test", "session"));
        Assert.Equal(TranscriptOrdering.Epoch("test", "session"), Assert.Single(visible).Epoch);
    }

    [Fact]
    public void Rotation_is_once_per_rollback_and_survives_reopening()
    {
        using var f = new Fixture();
        f.Sequences.Next("test", "session");
        f.Sequences.Next("test", "session");

        var first = f.Sequences.Rotate("test", "session", "r1", 2);
        var repeat = new TranscriptSequenceStore(f.SequencesPath).Rotate("test", "session", "r1", 2);
        var next = f.Sequences.Next("test", "session");

        Assert.Equal(first, repeat);
        Assert.Equal(3, first.Sequence);
        Assert.Equal(first.Epoch, next.Epoch);
        Assert.Equal(4, next.Sequence);
    }

    private sealed class NoJobs : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
        public JobRecord? GetJob(Guid jobId) => null;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string EntityIdValue = "11111111-1111-1111-1111-111111111111";
        internal readonly string EntityId = EntityIdValue;
        private readonly string _root = Path.Combine(
            Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
            "redcompute-rollback", Guid.NewGuid().ToString("N"));
        private readonly RedComputeConfig _config = new();
        private readonly InputAttachmentStore _attachments;
        private readonly WebSocketBroadcaster _broadcaster = new();
        private readonly RedLeafStreamClient _client;
        internal readonly SessionInputQueueStore Store;
        internal readonly CapabilityRegistry Registry = new();
        internal readonly RollbackProvider Runtime;
        internal readonly ITestProvider Provider;
        internal readonly TranscriptSequenceStore Sequences;
        internal readonly string SequencesPath;
        internal readonly SessionRollbackLedger Ledger;
        internal readonly FakeTranscript Transcript = new();
        internal bool Confidential { get; init; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _attachments = new(_config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
            Store = new(_config, _attachments);
            Provider = DispatchProxy.Create<ITestProvider, RollbackProvider>();
            Runtime = (RollbackProvider)(object)Provider;
            Registry.Register("ai-session",
                new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
                new CapabilityConfig(),
                new Dictionary<string, IBackendProvider> { ["test"] = Provider }, "test");
            SequencesPath = Path.Combine(_root, "cursor.db");
            Sequences = new TranscriptSequenceStore(SequencesPath);
            Ledger = new SessionRollbackLedger(Path.Combine(_root, "ledger.db"));
            _client = new RedLeafStreamClient("http://127.0.0.1:9", "RedComputeTests",
                new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            Transcript.History =
            [
                Record(10, "u1", "user", "first", 1),
                Record(11, "a1", "assistant", "first reply", 2),
                Record(12, "u2", "user", "second", 3),
                Record(13, "a2", "assistant", "second reply", 4),
                Record(14, "u3", "user", "third", 5),
                Record(15, "a3", "assistant", "third reply", 6),
            ];
            Transcript.EntityId = EntityIdValue;
        }

        internal static UnifiedMessageRecord Record(long id, string uid, string role, string content, long sequence) => new()
        {
            Id = id, SessionId = "session", Role = role, EventType = "text", MessageUid = uid, Content = content,
            Sequence = sequence, Epoch = "e", Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(id),
        };

        internal SessionInputQueueService Queue() => new(
            Store, _attachments, Registry, null!, _broadcaster, (_, _) => { }, deliveryPaused: () => false);

        internal SessionRollbackService Service() => new(
            Registry, Queue(),
            new SessionTranscriptPipeline(_client, _broadcaster, (_, _) => { }, (_, _) => Confidential, Sequences),
            Transcript, Ledger, (_, _) => { });

        internal RecordingWebSocket AttachClient()
        {
            var socket = new RecordingWebSocket();
            typeof(WebSocketBroadcaster).GetMethod("AddClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_broadcaster, ["test", socket]);
            return socket;
        }

        internal async Task Block(string reason)
        {
            switch (reason)
            {
                case SessionRollbackBlockers.TurnActive:
                    Runtime.Info.Status = SessionStatus.Active;
                    break;
                case SessionRollbackBlockers.QuestionParked:
                    using (var empty = JsonDocument.Parse("[]"))
                    {
                        Runtime.Questions =
                        [
                            new PendingSessionQuestion
                            {
                                RequestId = "q", ToolName = "AskUserQuestion", Questions = empty.RootElement.Clone(),
                                Input = empty.RootElement.Clone(), AskedAt = DateTimeOffset.UtcNow,
                            },
                        ];
                    }
                    break;
                case SessionRollbackBlockers.BackgroundTasksRunning:
                    Runtime.BackgroundTasks =
                    [
                        new BackgroundTaskInfo { TaskId = "t", Kind = "shell", Status = BackgroundTaskStatuses.Running },
                    ];
                    break;
                case SessionRollbackBlockers.InputQueued:
                    await Store.EnqueueAsync(Submission("waiting"), default);
                    break;
                case SessionRollbackBlockers.SessionStopped:
                    Runtime.Info.Status = SessionStatus.Stopped;
                    Runtime.Info.StopReason = "user_stopped";
                    break;
            }
        }

        internal void AssertUntouched()
        {
            Assert.Empty(Runtime.Requests);
            Assert.Empty(Transcript.Marks);
            Assert.Null(Sequences.CurrentView("test", "session"));
            Assert.Empty(Ledger.ListIncomplete());
        }

        internal SessionRollbackRow Row(string state) => new()
        {
            SessionId = "session", MessageUid = "u2", RollbackId = "rollback-1", Provider = "test",
            TranscriptProvider = "test", EntityId = EntityId, FromRecordId = 12, FromSequence = 3,
            FromCreatedAt = DateTimeOffset.UnixEpoch, RestoredText = "second",
            KeptTurns = [new("u1", "first")], DroppedTurns = [new("u2", "second")],
            RemovedMessageUids = ["u2", "a2"], State = state,
        };

        internal SessionInputQueueSubmission Submission(string text) => new(
            "session", "owner", [new("text", text)], text, null,
            new(JobProvenance.CurrentSchemaVersion,
                new("redcompute", new("app", "nova", null, "Nova"), new("http", "/accepted", "POST")),
                new("user", "Owner", Id: "owner"), new("user", "owner", "Owner"),
                [], new(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            SessionInputDeliveryPolicy.AfterCurrent, "m_" + Guid.NewGuid().ToString("N"), [], Guid.NewGuid().ToString("N"));

        internal async Task<(int Status, JsonElement Body)> PostAsync(
            string? messageUid, string user = "owner", string sessionId = "session", Action<DefaultHttpContext>? identify = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            await using var app = builder.Build();
            SessionRollbackEndpoints.Map(new EndpointRegistry(app), Registry, new NoJobs(), Service());
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(item => item.RoutePattern.RawText == "/ai-session/sessions/{id}/rollback");
            var context = new DefaultHttpContext
            {
                RequestServices = app.Services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "signed")),
            };
            identify?.Invoke(context);
            context.SetEndpoint(endpoint);
            context.Request.Method = "POST";
            context.Request.RouteValues["id"] = sessionId;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                messageUid is null ? "{}" : JsonSerializer.Serialize(new { messageUid })));
            var output = new MemoryStream();
            context.Response.Body = output;

            await endpoint.RequestDelegate!(context);

            output.Position = 0;
            using var document = output.Length == 0 ? JsonDocument.Parse("{}") : await JsonDocument.ParseAsync(output);
            return (context.Response.StatusCode, document.RootElement.Clone());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _ = _client.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    internal sealed class RecordingWebSocket : WebSocket
    {
        public List<string> Frames { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (Frames) Frames.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeTranscript : ISessionRollbackTranscript
    {
        internal string? EntityId;
        internal List<UnifiedMessageRecord> History = [];
        internal List<(string EntityId, long From, string RollbackId)> Marks = [];
        internal bool CanMark = true;
        internal int FailMarks;

        public Task<(string? EntityId, List<UnifiedMessageRecord> History)> ReadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult((EntityId, History.ToList()));

        public Task<bool> CanMarkAsync(string entityId, CancellationToken ct) => Task.FromResult(CanMark);

        public Task MarkAsync(string entityId, long fromRecordId, string rollbackId, CancellationToken ct)
        {
            if (FailMarks > 0)
            {
                FailMarks--;
                throw new HttpRequestException("RedLeaf is restarting");
            }
            Marks.Add((entityId, fromRecordId, rollbackId));
            return Task.CompletedTask;
        }
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class RollbackProvider : DispatchProxy
    {
        internal UnifiedSessionInfo Info = new()
        {
            Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".",
            UserId = "owner", Status = SessionStatus.Idle,
        };
        internal bool Capability = true;
        internal bool SupportsThisSession = true;
        internal SessionRollbackResult Result = SessionRollbackResult.Applied();
        internal List<SessionRollbackRequest> Requests = [];
        internal IReadOnlyList<PendingSessionQuestion> Questions = [];
        internal IReadOnlyList<BackgroundTaskInfo> BackgroundTasks = [];
        internal int Sends;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Capabilities":
                    return SessionCapabilities.SendMessage
                        | (Capability ? SessionCapabilities.SupportsRollback : SessionCapabilities.None);
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case nameof(ISessionProvider.GetSession):
                    return ((string)args![0]! == Info.Id ? Info : null, new List<UnifiedMessageRecord>());
                case nameof(ISessionProvider.SupportsRollback): return SupportsThisSession;
                case nameof(ISessionProvider.GetPendingQuestions): return Questions;
                case nameof(ISessionProvider.GetBackgroundTasks): return BackgroundTasks;
                case nameof(ISessionProvider.RollbackAsync):
                    if (Result.Status == SessionRollbackStatus.Applied) Requests.Add((SessionRollbackRequest)args![0]!);
                    return Task.FromResult(Result);
                case nameof(ISessionProvider.TrySendInputAsync):
                    Sends++;
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
