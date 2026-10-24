using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// 2026-10-24: twice an urgent "stop touching that repo" continuation sat in the input queue behind a
// long turn. interrupt-current now interrupts that turn and is delivered next, ahead of older queued
// input; the turn it ended reports interrupted (and by whom), never completed; and every prompt one
// turn carried, steered ones included, names the same turn.
public sealed class SessionInterruptTurnTests
{
    [Fact]
    public async Task Interrupt_overtakes_the_queue_and_the_interrupted_turn_reports_interrupted_from_its_own_record()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");                                  // p1's long turn runs
        var older = await f.AdmitAsync("older", SessionInputDeliveryPolicy.AfterCurrent);
        var stop = await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        Assert.Equal(SessionInterruptResults.Interrupted, stop.InterruptResult);
        Assert.Equal(1, f.Runtime.Interrupts);
        Assert.Equal(stop.Item.Id, (await f.Service.GetSummaryAsync("session", "owner")).HeadItemId);

        // The provider aborts p1's turn; the queue delivers "stop" before the callback observer runs.
        f.EndTurn(SessionTurnOutcomes.Interrupted);
        await f.Service.ProcessSessionAsync("session", default);
        Assert.Equal(["p1", "stop"], f.Runtime.Delivered);
        Assert.Equal(SessionStatus.Active, f.Runtime.Info.Status);

        await f.ObserveAsync();
        var p1 = Assert.Single(f.Posts);
        Assert.Equal("p1", p1.GetProperty("promptMessageUid").GetString());
        Assert.Equal("Idle", p1.GetProperty("status").GetString());
        Assert.Equal("p1", p1.GetProperty("turnId").GetString());
        Assert.Equal(SessionTurnOutcomes.Interrupted, p1.GetProperty("turnOutcome").GetString());
        Assert.Equal(SessionInterruptSources.InterruptCurrent, p1.GetProperty("interruptedBy").GetProperty("source").GetString());
        Assert.Equal("stop", p1.GetProperty("interruptedBy").GetProperty("messageUid").GetString());

        // "stop" reports its own turn, completed; the overtaken input then runs, still queued in order.
        f.EndTurn(SessionTurnOutcomes.Completed);
        await f.ObserveAsync();
        var stopped = f.Posts.Single(p => p.GetProperty("promptMessageUid").GetString() == "stop");
        Assert.Equal("stop", stopped.GetProperty("turnId").GetString());
        Assert.Equal(SessionTurnOutcomes.Completed, stopped.GetProperty("turnOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("interruptedBy").ValueKind);
        await f.Service.ProcessSessionAsync("session", default);
        Assert.Equal(["p1", "stop", "older"], f.Runtime.Delivered);
        Assert.Equal(SessionInputQueueState.Delivered, (await f.Store.GetAsync("session", older.Item.Id, "owner"))!.State);
    }

    [Fact]
    public async Task Overtaken_inputs_keep_their_order_and_later_interrupts_ride_the_overtaking_batch()
    {
        using var f = new Fixture { InitialStatus = SessionStatus.Stopped };
        await f.Store.EnqueueAsync(f.Submission("a", SessionInputDeliveryPolicy.AfterCurrent));
        await f.Store.EnqueueAsync(f.Submission("i1", SessionInputDeliveryPolicy.InterruptCurrent));
        await f.Store.EnqueueAsync(f.Submission("b", SessionInputDeliveryPolicy.AfterCurrent));
        await f.Store.EnqueueAsync(f.Submission("i2", SessionInputDeliveryPolicy.InterruptCurrent));

        var first = await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1));
        Assert.Equal(["i1", "i2"], first.Select(item => item.MessageUid));
        await f.Store.MarkDeliveredAsync(first, "i1");
        var second = await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1));
        Assert.Equal(["a", "b"], second.Select(item => item.MessageUid));
    }

    [Fact]
    public async Task Nothing_overtakes_an_input_in_flight_or_failed()
    {
        using var f = new Fixture { InitialStatus = SessionStatus.Stopped };
        await f.Store.EnqueueAsync(f.Submission("a", SessionInputDeliveryPolicy.AfterCurrent));
        var failed = await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1));
        await f.Store.FailAsync(failed, "delivery_failed", "boom", retryable: false);
        await f.Store.EnqueueAsync(f.Submission("i", SessionInputDeliveryPolicy.InterruptCurrent));

        Assert.Equal("a", (await f.Store.GetHeadAsync("session"))!.MessageUid);
        Assert.Empty(await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Delivery_order_follows_the_overtaking_not_the_admission_sequence()
    {
        using var f = new Fixture { InitialStatus = SessionStatus.Stopped };
        await f.Store.EnqueueAsync(f.Submission("a", SessionInputDeliveryPolicy.AfterCurrent));
        await f.Store.EnqueueAsync(f.Submission("i", SessionInputDeliveryPolicy.InterruptCurrent));
        var interrupt = await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1));
        await f.Store.MarkDeliveredAsync(interrupt, "i");
        var afterInterrupt = await f.Store.LatestDeliverySequenceAsync("session");

        var older = await f.Store.ClaimBatchAsync("session", "test", TimeSpan.FromMinutes(1));
        await f.Store.MarkDeliveredAsync(older, "a");

        // "a" has the lower sequence but was delivered last: it is the current delivery, and the
        // delivery position moved, so its turn is never mistaken for one nobody prompted.
        Assert.Equal("a", await f.Store.CurrentDeliveryUidAsync("session"));
        Assert.True(await f.Store.LatestDeliverySequenceAsync("session") > afterInterrupt);
    }

    [Fact]
    public async Task Admission_reports_what_the_interrupt_did_and_a_replay_never_interrupts_again()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        var submission = f.Submission("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        var first = await f.Service.AdmitAsync(submission);
        var replay = await f.Service.AdmitAsync(submission);

        Assert.Equal(SessionInterruptResults.Interrupted, first.InterruptResult);
        Assert.True(replay.Existing);
        Assert.Equal(SessionInterruptResults.Interrupted, replay.InterruptResult);
        Assert.Equal(1, f.Runtime.Interrupts);
        Assert.Equal(SessionInterruptResults.Interrupted, replay.Item.InterruptResult);
    }

    [Fact]
    public async Task Interrupt_with_no_running_turn_reports_not_active_and_is_simply_delivered()
    {
        using var f = new Fixture();

        var admitted = await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        Assert.Equal(SessionInterruptResults.NotActive, admitted.InterruptResult);
        Assert.Equal(0, f.Runtime.Interrupts);
        Assert.Equal(["stop"], f.Runtime.Delivered);
    }

    [Fact]
    public async Task Interrupt_reports_unsupported_for_a_provider_without_interrupts()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        f.Runtime.SupportsInterrupt = false;

        var admitted = await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        Assert.Equal(SessionInterruptResults.Unsupported, admitted.InterruptResult);
        Assert.Equal(0, f.Runtime.Interrupts);
    }

    [Fact]
    public async Task Interrupt_the_provider_refuses_is_not_attributed_to_the_turn()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        f.Runtime.InterruptReturns = InterruptResult.Error;

        var admitted = await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);
        f.EndTurn(SessionTurnOutcomes.Completed);

        Assert.Equal(SessionInterruptResults.Error, admitted.InterruptResult);
        var turn = f.Service.LastTurn("session")!;
        Assert.Equal(SessionTurnOutcomes.Completed, turn.Outcome);
        Assert.Null(turn.InterruptSource);
    }

    [Fact]
    public async Task Interrupt_that_loses_the_race_to_a_finishing_turn_is_a_completion()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        f.EndTurn(SessionTurnOutcomes.Completed);

        var turn = f.Service.LastTurn("session")!;
        Assert.Equal(SessionTurnOutcomes.Completed, turn.Outcome);
        Assert.Null(turn.InterruptedByMessageUid);
    }

    [Fact]
    public async Task Turn_ended_without_a_provider_outcome_after_an_interrupt_is_interrupted()
    {
        // A provider that replaced its process (forced interrupt) or reports no outcome at all.
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        f.Runtime.Info.LastTurnOutcome = SessionTurnOutcomes.Completed; // stale: an earlier turn's
        f.Runtime.Info.LastTurnEndedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);

        f.EndTurn(outcome: null);

        var turn = f.Service.LastTurn("session")!;
        Assert.Equal("p1", turn.TurnId);
        Assert.Equal(SessionTurnOutcomes.Interrupted, turn.Outcome);
        Assert.Equal("stop", turn.InterruptedByMessageUid);
    }

    [Fact]
    public async Task Send_now_interrupts_for_the_input_that_runs_next()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        await f.AdmitAsync("next", SessionInputDeliveryPolicy.AfterCurrent);

        var sent = await f.Service.SendNowAsync("session", "owner");
        f.EndTurn(SessionTurnOutcomes.Interrupted);

        Assert.True(sent.Requested);
        Assert.Equal(SessionInterruptResults.Interrupted, sent.InterruptResult);
        var turn = f.Service.LastTurn("session")!;
        Assert.Equal(SessionInterruptSources.SendNow, turn.InterruptSource);
        Assert.Equal("next", turn.InterruptedByMessageUid);
    }

    [Fact]
    public async Task Plain_interrupt_is_attributed_to_no_input()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");

        var (provider, result) = f.Service.InterruptRunningTurn("session", SessionInterruptSources.Interrupt, null);
        f.EndTurn(SessionTurnOutcomes.Interrupted);

        Assert.Equal(InterruptResult.Interrupted, provider);
        Assert.Equal(SessionInterruptResults.Interrupted, result);
        var turn = f.Service.LastTurn("session")!;
        Assert.Equal(SessionInterruptSources.Interrupt, turn.InterruptSource);
        Assert.Null(turn.InterruptedByMessageUid);
    }

    [Fact]
    public async Task Steer_to_an_idle_session_reports_that_it_ran_as_the_next_turn()
    {
        using var f = new Fixture();

        var admitted = await f.AdmitAsync("also", SessionInputDeliveryPolicy.SteerCurrent);

        Assert.Equal(0, f.Runtime.Steers);
        Assert.Equal(["also"], f.Runtime.Delivered);
        var item = admitted.Item;
        Assert.Equal(SessionInputQueueState.Delivered, item.State);
        Assert.Equal(SessionInputDeliveryPolicy.SteerCurrent, item.Requested);
        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, item.DeliveryPolicy);
        Assert.Equal(SessionInputDeliveryReasons.NoActiveTurn, item.DeliveryReason);
    }

    [Fact]
    public async Task Steered_prompt_and_the_turn_it_joined_report_one_shared_turn()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        var steer = await f.AdmitAsync("also", SessionInputDeliveryPolicy.SteerCurrent);
        Assert.Equal(1, f.Runtime.Steers);
        Assert.Equal("p1", steer.Item.TurnId);

        f.EndTurn(SessionTurnOutcomes.Completed);
        await f.ObserveAsync();

        Assert.Equal(2, f.Posts.Count);
        Assert.Equal(["also", "p1"], f.Posts.Select(p => p.GetProperty("deliveredMessageUid").GetString()).Order());
        Assert.All(f.Posts, p =>
        {
            Assert.Equal("p1", p.GetProperty("turnId").GetString());
            Assert.Equal(SessionTurnOutcomes.Completed, p.GetProperty("turnOutcome").GetString());
        });
    }

    [Fact]
    public async Task Demoted_steer_replayed_under_its_key_is_the_same_admission()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        f.Runtime.SupportsSteering = false;
        var submission = f.Submission("also", SessionInputDeliveryPolicy.SteerCurrent);

        var first = await f.Service.AdmitAsync(submission);
        var replay = await f.Service.AdmitAsync(submission);

        Assert.Equal(SessionInputDeliveryPolicy.AfterCurrent, first.Item.DeliveryPolicy);
        Assert.Equal(SessionInputDeliveryReasons.SteerUnavailable, first.Item.DeliveryReason);
        Assert.True(replay.Existing);
        Assert.Equal(first.Item.Id, replay.Item.Id);
    }

    [Fact]
    public void Turn_the_provider_started_itself_is_recorded_and_can_be_interrupted()
    {
        using var f = new Fixture();
        f.Active();
        f.Service.InterruptRunningTurn("session", SessionInterruptSources.Interrupt, null);
        f.EndTurn(SessionTurnOutcomes.Interrupted);

        var turn = f.Service.LastTurn("session")!;
        Assert.StartsWith("unprompted:", turn.TurnId);
        Assert.Equal(SessionTurnOutcomes.Interrupted, turn.Outcome);
    }

    [Fact]
    public async Task Maintenance_drain_lets_the_turn_finish_but_a_plain_interrupt_still_stops_it()
    {
        using var f = new Fixture();
        await f.DeliverAsync("p1");
        f.Paused = true;

        var admitted = await f.AdmitAsync("stop", SessionInputDeliveryPolicy.InterruptCurrent);
        Assert.Equal(SessionInterruptResults.Maintenance, admitted.InterruptResult);
        Assert.Equal(0, f.Runtime.Interrupts);

        var (_, plain) = f.Service.InterruptRunningTurn("session", SessionInterruptSources.Interrupt, null);
        Assert.Equal(SessionInterruptResults.Interrupted, plain);
        Assert.Equal(1, f.Runtime.Interrupts);
    }

    [Fact]
    public async Task Unprompted_turn_report_says_it_was_interrupted()
    {
        using var f = new Fixture();
        await f.Callbacks.SubscribeSessionAsync("session", "http://localhost/complete", "owner", "http://localhost/question");
        await f.DeliverAsync("p1");
        f.EndTurn(SessionTurnOutcomes.Completed);
        await f.ObserveAsync();
        Assert.Single(f.Posts);

        f.Active();                                                  // the provider wakes on its own
        await f.ObserveAsync();
        f.Service.InterruptRunningTurn("session", SessionInterruptSources.Interrupt, null);
        f.EndTurn(SessionTurnOutcomes.Interrupted);
        await f.ObserveAsync();

        var report = f.Posts.Single(p => p.TryGetProperty("trigger", out var trigger) && trigger.GetString() == "unprompted");
        Assert.StartsWith("unprompted:", report.GetProperty("turnId").GetString());
        Assert.Equal(SessionTurnOutcomes.Interrupted, report.GetProperty("turnOutcome").GetString());
        Assert.Equal(SessionInterruptSources.Interrupt, report.GetProperty("interruptedBy").GetProperty("source").GetString());
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
            "redcompute-interrupt-turn-tests", Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly List<JsonElement> _posts = [];
        private SessionInputQueueService? _service;
        internal readonly InputAttachmentStore Attachments;
        internal readonly SessionInputQueueStore Store;
        internal readonly RuntimeProvider Runtime;
        internal readonly CapabilityRegistry Registry = new();
        internal readonly SessionPromptCallbacks Callbacks;
        internal SessionStatus InitialStatus { init => Runtime.Info.Status = value; }
        internal List<JsonElement> Posts { get { lock (_posts) return [.. _posts]; } }
        internal IJobTracker? Jobs;
        internal string Root => _root;
        internal bool Paused;
        internal SessionInputQueueService Service => _service ??= new(
            Store, Attachments, Registry, Jobs!, new WebSocketBroadcaster(), (_, _) => { }, deliveryPaused: () => Paused);

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var config = new RedComputeConfig();
            Attachments = new(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
            Store = new(config, Attachments);
            var provider = DispatchProxy.Create<ITestProvider, RuntimeProvider>();
            Runtime = (RuntimeProvider)(object)provider;
            Runtime.Fixture = this;
            Registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
            _http = new HttpClient(new Handler(async request =>
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
                lock (_posts) _posts.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            Callbacks = new(Attachments.DatabasePath, Store, _ => Runtime.Info, _http);
        }

        internal SessionInputQueueSubmission Submission(string uid, SessionInputDeliveryPolicy policy) => new(
            "session", "owner", [new("text", uid)], uid, null,
            new(JobProvenance.CurrentSchemaVersion,
                new("redcompute", new("app", "nova", null, "Nova"), new("http", "/accepted", "POST")),
                new("user", "Owner", Id: "owner"), new("user", "owner", "Owner"), [], new(),
                JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            policy, uid, [], "key-" + uid);

        /// <summary>Admits an input as the message endpoint does, with a completion registration.</summary>
        internal async Task<SessionInputAdmissionResult> AdmitAsync(string uid, SessionInputDeliveryPolicy policy)
        {
            await Callbacks.RegisterAsync("session", uid, "http://localhost/complete", "owner", uid);
            return await Service.AdmitAsync(Submission(uid, policy));
        }

        /// <summary>Admits and delivers a prompt to the idle session, leaving its turn running.</summary>
        internal async Task DeliverAsync(string uid)
        {
            await AdmitAsync(uid, SessionInputDeliveryPolicy.AfterCurrent);
            Assert.Equal(SessionStatus.Active, Runtime.Info.Status);
            await ObserveAsync();
        }

        /// <summary>The provider runs a turn of its own.</summary>
        internal void Active()
        {
            Runtime.Info.Status = SessionStatus.Active;
            Service.OnProviderEvent("session.updated", Runtime.Info);
        }

        /// <summary>The provider ends its turn and publishes Idle, as Claude Code and Codex do.</summary>
        internal void EndTurn(string? outcome)
        {
            Runtime.Info.Status = SessionStatus.Idle;
            if (outcome is not null)
            {
                Runtime.Info.LastTurnOutcome = outcome;
                Runtime.Info.LastTurnEndedAt = DateTimeOffset.UtcNow;
            }
            Service.OnProviderEvent("session.updated", Runtime.Info);
        }

        internal async Task ObserveAsync()
        {
            await Callbacks.ObserveAsync("session", observedActive: Runtime.Info.Status == SessionStatus.Active);
            await Callbacks.DrainAsync();
        }

        public void Dispose()
        {
            _http.Dispose();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class RuntimeProvider : DispatchProxy
    {
        internal Fixture? Fixture;
        internal UnifiedSessionInfo Info = new()
        {
            Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".",
            UserId = "owner", Status = SessionStatus.Idle,
        };
        internal bool SupportsSteering = true;
        internal bool SupportsInterrupt = true;
        internal InterruptResult InterruptReturns = InterruptResult.Interrupted;
        internal int Interrupts;
        internal int Steers;
        internal List<string> Delivered = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Capabilities":
                    return SessionCapabilities.SendMessage
                        | (SupportsSteering ? SessionCapabilities.SupportsSteering : SessionCapabilities.None)
                        | (SupportsInterrupt ? SessionCapabilities.Interrupt : SessionCapabilities.None);
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case nameof(ISessionProvider.GetSession):
                    Info.SupportsSteering = SupportsSteering;
                    return (Info, new List<UnifiedMessageRecord>());
                case nameof(ISessionProvider.InterruptSession):
                    Interrupts++;
                    return Info.Status == SessionStatus.Active ? InterruptReturns : InterruptResult.NotActive;
                case nameof(ISessionProvider.TrySteerInputAsync):
                    Steers++;
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                case nameof(ISessionProvider.TrySendInputAsync):
                    Delivered.Add(SessionInputFormatting.UserText((IReadOnlyList<SessionInputPart>)args![1]!));
                    Info.Status = SessionStatus.Active;
                    Fixture?.Service.OnProviderEvent("session.updated", Info);
                    return Task.FromResult(SessionInputDeliveryResult.Accepted());
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}

// The message admission reply carries what the queue decided, through the mapped endpoint.
[Collection("Unified session endpoint mapping")]
public sealed class SessionInterruptAdmissionEndpointTests
{
    [Fact]
    public async Task Interrupt_current_reply_says_the_running_turn_was_interrupted_and_the_input_is_next()
    {
        using var f = new SessionInterruptTurnTests.Fixture();
        await using var api = await Api.CreateAsync(f);
        await f.DeliverAsync("p1");

        var (status, reply) = await api.PostMessageAsync(new { content = "stop", messageUid = "stop", delivery = "interrupt-current" });

        Assert.Equal(202, status);
        Assert.Equal(SessionInterruptResults.Interrupted, reply.GetProperty("interruptResult").GetString());
        Assert.True(reply.GetProperty("turnInterrupted").GetBoolean());
        var item = reply.GetProperty("item");
        Assert.Equal("interrupt-current", item.GetProperty("requestedDelivery").GetString());
        Assert.Equal("interrupt-current", item.GetProperty("delivery").GetString());
        Assert.Equal(SessionInterruptResults.Interrupted, item.GetProperty("interruptResult").GetString());
        Assert.Equal(item.GetProperty("id").GetString(), reply.GetProperty("queue").GetProperty("headItemId").GetString());
    }

    [Fact]
    public async Task Steer_reply_on_an_idle_session_says_it_ran_as_the_next_turn()
    {
        using var f = new SessionInterruptTurnTests.Fixture();
        await using var api = await Api.CreateAsync(f);

        var (status, reply) = await api.PostMessageAsync(new { content = "also", messageUid = "also", delivery = "steer-current" });

        Assert.Equal(200, status);
        Assert.False(reply.GetProperty("turnInterrupted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("interruptResult").ValueKind);
        var item = reply.GetProperty("item");
        Assert.Equal("steer-current", item.GetProperty("requestedDelivery").GetString());
        Assert.Equal("after-current", item.GetProperty("delivery").GetString());
        Assert.Equal(SessionInputDeliveryReasons.NoActiveTurn, item.GetProperty("deliveryReason").GetString());
        Assert.Equal("also", item.GetProperty("turnId").GetString());
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RouteEndpoint _message;
        private readonly ClaimsPrincipal _user;

        private Api(WebApplication app, RouteEndpoint message, ClaimsPrincipal user) { _app = app; _message = message; _user = user; }

        internal static async Task<Api> CreateAsync(SessionInterruptTurnTests.Fixture f)
        {
            var dbPath = Path.Combine(f.Root, "jobs.db");
            RedComputeDbContext Db() => new(dbPath);
            using (var db = Db()) db.Database.EnsureCreated();
            var jobs = new JobTrackingService(Db);
            f.Jobs = jobs;
            var options = new JwtOptions { SigningKey = "interrupt-admission-endpoint-secret-1234567890123456" };
            var jwt = new JwtService(options);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(options).AddSingleton<IExecutionTokenIssuer>(new ExecutionTokenIssuer(jwt, options));
            var app = builder.Build();
            var user = jwt.ValidateToken(jwt.GenerateAccessToken("owner", "owner@test", "Owner", ["operator"]))!;
            var ctx = new DefaultHttpContext { RequestServices = app.Services, User = user };
            ctx.Request.Method = "POST";
            var provenance = await ProvenanceCapture.ResolveAsync(ctx, "/ai-session/sessions/{id}/message");
            f.Runtime.Info.JobId = jobs.CreateJob(new JobSubmission("ai-session", "test", "{}", provenance)).Id;
            var registry = new EndpointRegistry(app);
            UnifiedSessionEndpoints.Map(registry, f.Registry, jobs, (_, _) => { }, new RedComputeConfig(),
                attachmentStore: f.Attachments, inputQueue: f.Service);
            var message = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == "/ai-session/sessions/{id}/message");
            return new Api(app, message, user);
        }

        internal async Task<(int Status, JsonElement Reply)> PostMessageAsync(object body)
        {
            var ctx = new DefaultHttpContext { RequestServices = _app.Services, User = _user };
            ctx.SetEndpoint(_message); ctx.Request.RouteValues["id"] = "session";
            ctx.Request.Method = "POST"; ctx.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            ctx.Request.Body = new MemoryStream(bytes); ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
            ctx.Response.Body = new MemoryStream();
            await _message.RequestDelegate!(ctx);
            ctx.Response.Body.Position = 0;
            return (ctx.Response.StatusCode, await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Response.Body));
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private sealed class BodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    }
}
