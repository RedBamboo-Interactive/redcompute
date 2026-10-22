using System.Net;
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
using RedCompute.Core.Jobs;
using RedCompute.Plugin.OpenAILive;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// Unsolicited backend updates: the voice model hears what the backend Agent said on its
// own (watcher wakes, automations) without ever talking over the user, and the speech
// those updates cause is delivery of an existing canonical message, not a new turn.
public sealed class VoiceBackendUpdateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Speak_update_is_sent_once_per_update_id()
    {
        var (coordinator, provider, _, id) = await StartAsync();

        Assert.Equal(VoiceBackendUpdateOutcome.Sent, await Update(coordinator, id, "msg-1", "speak", "My turn's done."));
        Assert.Equal(VoiceBackendUpdateOutcome.Duplicate, await Update(coordinator, id, "msg-1", "speak", "My turn's done."));

        var sent = Assert.Single(provider.BackendUpdates);
        Assert.Equal("speak", sent.Mode);
        Assert.Equal("msg-1", sent.UpdateId);
        Assert.Equal("My turn's done.", sent.Content);
    }

    [Fact]
    public async Task Unknown_session_and_other_users_are_not_found()
    {
        var (coordinator, provider, _, id) = await StartAsync();

        Assert.Equal(VoiceBackendUpdateOutcome.NotFound, await Update(coordinator, "vs_missing", "msg-1", "speak", "Hi"));
        Assert.Equal(VoiceBackendUpdateOutcome.NotFound, await coordinator.UpdateBackendAsync(
            id, "msg-1", "speak", "Hi", "intruder", false, CancellationToken.None));
        Assert.Empty(provider.BackendUpdates);
    }

    [Fact]
    public async Task Provider_without_backend_updates_reports_unsupported()
    {
        var coordinator = NewCoordinator(new RecordingHandler(), () => T0);
        var created = await coordinator.CreateAsync(new LegacyVoiceProvider(), Start(), CancellationToken.None);

        Assert.Equal(VoiceBackendUpdateOutcome.Unsupported,
            await Update(coordinator, created.Session.Id, "msg-1", "speak", "Hi"));
    }

    [Fact]
    public async Task Closed_session_rejects_updates()
    {
        var (coordinator, provider, _, id) = await StartAsync();
        await coordinator.CloseAsync(id, "user-1", false, CancellationToken.None);

        Assert.Equal(VoiceBackendUpdateOutcome.Closed, await Update(coordinator, id, "msg-1", "speak", "Hi"));
        Assert.Empty(provider.BackendUpdates);
    }

    [Theory]
    [InlineData("session.input_transcript.delta")]
    [InlineData("session.output_transcript.delta")]
    public async Task Speech_is_queued_while_anyone_is_speaking_and_delivered_after(string speaking)
    {
        var now = T0;
        var (coordinator, provider, _, id) = await StartAsync(() => now);

        await provider.Emit(new(speaking, "delta-1", "So what I was thinking", 0, 900));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-1", "speak", "My turn's done."));
        Assert.Empty(provider.BackendUpdates);

        now = now.AddSeconds(3);
        Assert.Equal(1, await coordinator.DeliverQueuedBackendUpdatesAsync(id));
        var sent = Assert.Single(provider.BackendUpdates);
        Assert.Equal("speak", sent.Mode);
    }

    [Fact]
    public async Task Queued_speech_is_delivered_automatically_once_the_conversation_is_quiet()
    {
        var coordinator = NewCoordinator(new RecordingHandler(), () => DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(150));
        var provider = new FakeVoiceProvider();
        var id = (await coordinator.CreateAsync(provider, Start(), CancellationToken.None)).Session.Id;

        await provider.Emit(new("session.input_transcript.delta", "in-1", "Hang on", 0, 300));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-1", "speak", "Done."));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (provider.BackendUpdates.Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal("speak", Assert.Single(provider.BackendUpdates).Mode);
    }

    [Fact]
    public async Task Only_the_newest_queued_speech_is_spoken_older_ones_become_context()
    {
        var now = T0;
        var (coordinator, provider, _, id) = await StartAsync(() => now);

        await provider.Emit(new("session.input_transcript.delta", "in-1", "Wait", 0, 300));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-1", "speak", "First."));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-2", "context", "Note."));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-3", "speak", "Latest."));

        now = now.AddSeconds(3);
        await coordinator.DeliverQueuedBackendUpdatesAsync(id);

        Assert.Equal([("msg-1", "context"), ("msg-2", "context"), ("msg-3", "speak")],
            provider.BackendUpdates.Select(update => (update.UpdateId, update.Mode)).ToList());
    }

    [Fact]
    public async Task Context_is_appended_immediately_even_while_the_user_speaks()
    {
        var (coordinator, provider, _, id) = await StartAsync();

        await provider.Emit(new("session.input_transcript.delta", "in-1", "Hm", 0, 300));
        Assert.Equal(VoiceBackendUpdateOutcome.Sent, await Update(coordinator, id, "msg-1", "context", "Round 3."));
        Assert.Equal("context", Assert.Single(provider.BackendUpdates).Mode);
    }

    [Fact]
    public async Task A_spoken_update_holds_the_next_one_until_its_speech_has_had_room()
    {
        var now = T0;
        var (coordinator, provider, _, id) = await StartAsync(() => now);

        Assert.Equal(VoiceBackendUpdateOutcome.Sent, await Update(coordinator, id, "msg-1", "speak", "One."));
        Assert.Equal(VoiceBackendUpdateOutcome.Queued, await Update(coordinator, id, "msg-2", "speak", "Two."));
        Assert.Single(provider.BackendUpdates);
        await coordinator.DeliverQueuedBackendUpdatesAsync(id);
    }

    [Fact]
    public async Task Speech_caused_by_an_update_is_delivery_and_earlier_casual_speech_stays_its_own_turn()
    {
        var now = T0;
        var (coordinator, provider, callbacks, id) = await StartAsync(() => now);

        await provider.Emit(new("session.output_transcript.delta", "out-1", "Sure, sounds fun.", 0, 400));
        now = now.AddSeconds(3);
        Assert.Equal(VoiceBackendUpdateOutcome.Sent, await Update(coordinator, id, "msg-1", "speak", "Boss is down."));
        await provider.Emit(new("session.output_transcript.delta", "out-2", "We got the boss!", 500, 900));
        await provider.Emit(new("session.input_transcript.delta", "in-1", "Nice", 1000, 1200));

        var assistant = callbacks.Bodies
            .Where(body => body.GetProperty("type").GetString() == "voice.utterance.finalized")
            .Select(body => body.GetProperty("data"))
            .Where(data => data.GetProperty("role").GetString() == "assistant")
            .ToList();
        Assert.Equal(2, assistant.Count);
        Assert.Equal("Sure, sounds fun.", assistant[0].GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, assistant[0].GetProperty("updateId").ValueKind);
        Assert.Equal("We got the boss!", assistant[1].GetProperty("content").GetString());
        Assert.Equal("msg-1", assistant[1].GetProperty("updateId").GetString());
    }

    [Fact]
    public async Task User_speech_ends_the_spoken_update_window()
    {
        var now = T0;
        var (coordinator, provider, callbacks, id) = await StartAsync(() => now);

        await Update(coordinator, id, "msg-1", "speak", "Boss is down.");
        await provider.Emit(new("session.output_transcript.delta", "out-1", "Boss down!", 0, 300));
        await provider.Emit(new("session.input_transcript.delta", "in-1", "How are you?", 400, 800));
        await provider.Emit(new("session.output_transcript.delta", "out-2", "Great, thanks.", 900, 1200));
        await provider.Emit(new("session.input_transcript.delta", "in-2", "Cool", 1300, 1500));

        var replies = callbacks.Bodies
            .Select(body => body.GetProperty("data"))
            .Where(data => data.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .ToList();
        Assert.Equal("msg-1", replies[0].GetProperty("updateId").GetString());
        Assert.Equal(JsonValueKind.Null, replies[1].GetProperty("updateId").ValueKind);
    }

    [Fact]
    public async Task Rejected_or_failed_updates_can_be_retried_with_the_same_id()
    {
        var (coordinator, provider, callbacks, id) = await StartAsync();

        provider.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Update(coordinator, id, "msg-1", "speak", "Hi"));
        Assert.Equal(VoiceBackendUpdateOutcome.Sent, await Update(coordinator, id, "msg-1", "speak", "Hi"));

        var eventId = provider.BackendUpdates.Single().EventId;
        await provider.Emit(new("error", "provider-error", ErrorCode: "invalid_request_error",
            ErrorMessage: "Rejected", ClientEventId: eventId));
        await provider.Emit(new("session.output_transcript.delta", "out-1", "A casual line.", 0, 300));
        await provider.Emit(new("session.input_transcript.delta", "in-1", "Ok", 400, 500));

        var assistant = callbacks.Bodies.Select(body => body.GetProperty("data"))
            .Single(data => data.TryGetProperty("role", out var role) && role.GetString() == "assistant");
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("updateId").ValueKind);
        // A rejected update was never delivered, so the same id is accepted again.
        Assert.NotEqual(VoiceBackendUpdateOutcome.Duplicate, await Update(coordinator, id, "msg-1", "speak", "Hi"));
        await coordinator.DeliverQueuedBackendUpdatesAsync(id);
    }

    [Fact]
    public void OpenAI_live_speak_is_session_wide_commentary()
    {
        var json = JsonSerializer.Serialize(OpenAILiveProvider.BuildBackendUpdateCommand(
            new VoiceBackendUpdate("upd_1", "msg-1", "speak", "Boss is down.")));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("session.commentary.append", root.GetProperty("type").GetString());
        Assert.Equal("upd_1", root.GetProperty("event_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("delegation_id").ValueKind);
        Assert.Equal("Boss is down.", root.GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAI_live_context_is_unspoken_session_wide_thinking()
    {
        var json = JsonSerializer.Serialize(OpenAILiveProvider.BuildBackendUpdateCommand(
            new VoiceBackendUpdate("upd_2", "msg-2", "context", "Floor 12, 54 HP.")));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("session.thinking.append", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("delegation_id", out var delegation));
        Assert.Equal(JsonValueKind.Null, delegation.ValueKind);
        Assert.Equal("Floor 12, 54 HP.", root.GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAI_live_supports_backend_updates_and_rejects_unknown_modes()
    {
        IVoiceSessionProvider provider = new OpenAILiveProvider(new RedCompute.Core.Configuration.ProviderConfig
        {
            Type = "OpenAILive",
        }, "voice-session", _ => { });

        Assert.True(provider.SupportsBackendUpdates);
        Assert.Throws<ArgumentException>(() => OpenAILiveProvider.BuildBackendUpdateCommand(
            new VoiceBackendUpdate("upd_3", "msg-3", "shout", "x")));
    }

    [Theory]
    [InlineData("vs_missing", """{"updateId":"msg-1","mode":"speak","content":"Hi"}""", 404)]
    [InlineData(null, """{"updateId":"msg-1","mode":"shout","content":"Hi"}""", 400)]
    [InlineData(null, """{"updateId":"msg-1","mode":"speak","content":""}""", 400)]
    [InlineData(null, """{"mode":"speak","content":"Hi"}""", 400)]
    [InlineData(null, """{"updateId":"msg-1","mode":"speak","content":"Hi"}""", 200)]
    public async Task Endpoint_validates_and_maps_outcomes(string? sessionId, string body, int expected)
    {
        var (coordinator, provider, _, id) = await StartAsync();

        var status = await InvokeEndpointAsync(coordinator, sessionId ?? id, body, "user-1");

        Assert.Equal(expected, status);
        Assert.Equal(expected == 200 ? 1 : 0, provider.BackendUpdates.Count);
    }

    [Fact]
    public async Task Endpoint_reports_queued_duplicate_and_unsupported()
    {
        var now = T0;
        var (coordinator, provider, _, id) = await StartAsync(() => now);
        await provider.Emit(new("session.input_transcript.delta", "in-1", "Talking", 0, 300));
        const string body = """{"updateId":"msg-1","mode":"speak","content":"Hi"}""";

        Assert.Equal(202, await InvokeEndpointAsync(coordinator, id, body, "user-1"));
        Assert.Equal(200, await InvokeEndpointAsync(coordinator, id, body, "user-1"));
        Assert.Equal(404, await InvokeEndpointAsync(coordinator, id, body, "intruder"));
        Assert.Equal(1, await coordinator.DeliverQueuedBackendUpdatesAsync(id));

        var legacy = await coordinator.CreateAsync(new LegacyVoiceProvider(), Start(), CancellationToken.None);
        Assert.Equal(501, await InvokeEndpointAsync(coordinator, legacy.Session.Id, body, "user-1"));
    }

    [Fact]
    public async Task Discovery_describes_the_updates_route_next_to_its_siblings()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        VoiceSessionEndpoints.Map(endpoints, new CapabilityRegistry(),
            NewCoordinator(new RecordingHandler(), () => T0));

        var routes = endpoints.GetEndpoints().Where(endpoint => endpoint.Path.StartsWith("/voice-session/")).ToList();
        var update = routes.Single(endpoint => endpoint.Method == "POST"
            && endpoint.Path == "/voice-session/sessions/{id}/updates");
        Assert.Contains(routes, endpoint => endpoint.Path == "/voice-session/sessions/{id}/delegations/{delegationId}/update");
        Assert.Contains("idempotent", update.Description, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(update.RequestBody);
        Assert.Contains("\"speak\"", JsonSerializer.Serialize(update.RequestBody));
    }

    private static async Task<int> InvokeEndpointAsync(VoiceSessionCoordinator coordinator,
        string sessionId, string body, string user)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        VoiceSessionEndpoints.Map(new EndpointRegistry(app), new CapabilityRegistry(), coordinator);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/voice-session/sessions/{id}/updates");
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "signed")),
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.RouteValues["id"] = sessionId;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        return context.Response.StatusCode;
    }

    private static Task<VoiceBackendUpdateOutcome> Update(VoiceSessionCoordinator coordinator,
        string id, string updateId, string mode, string content)
        => coordinator.UpdateBackendAsync(id, updateId, mode, content, "user-1", false, CancellationToken.None);

    private static async Task<(VoiceSessionCoordinator, FakeVoiceProvider, RecordingHandler, string)> StartAsync(
        Func<DateTimeOffset>? utcNow = null)
    {
        var callbacks = new RecordingHandler();
        var coordinator = NewCoordinator(callbacks, utcNow ?? (() => T0));
        var provider = new FakeVoiceProvider();
        var created = await coordinator.CreateAsync(provider, Start(), CancellationToken.None);
        return (coordinator, provider, callbacks, created.Session.Id);
    }

    private static VoiceSessionCoordinator NewCoordinator(RecordingHandler handler,
        Func<DateTimeOffset> utcNow, TimeSpan? speechQuietPeriod = null)
        => new(new HttpClient(handler), (_, _) => { }, "http://127.0.0.1:18804",
            watchdogInterval: TimeSpan.FromHours(1), utcNow: utcNow, speechQuietPeriod: speechQuietPeriod);

    private static VoiceSessionStart Start() => new(
        "offer", "gpt-live-1", "marin", "Be Nova.", "discussion-1", "ai-session-1",
        new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redleaf", new JobAppReference("app", "nova", null, "Nova"),
                new JobEntrypoint("http", "/voice", "POST")),
            new JobActor("agent", "Nova", EntityId: "agent-1"),
            new JobBeneficiary("user", "user-1", "Laurent"),
            [new JobContextReference("discussion", "discussion-1")],
            new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow));

    /// <summary>Implements only the original contract: proves the SDK change is source-compatible.</summary>
    private sealed class LegacyVoiceProvider : IVoiceSessionProvider
    {
        public string VoiceProviderId => "legacy-live";
        public string DefaultModel => "legacy-model";
        public string DefaultVoice => "legacy-voice";
        public Task<VoiceSessionCreateResult> CreateVoiceSessionAsync(VoiceSessionCreateRequest request,
            Func<VoiceProviderEvent, Task> onEvent, CancellationToken ct)
            => Task.FromResult(new VoiceSessionCreateResult("legacy-1", "answer"));
        public Task SendDelegationUpdateAsync(string providerSessionId, string delegationId,
            VoiceDelegationUpdate update, CancellationToken ct) => Task.CompletedTask;
        public Task CloseVoiceSessionAsync(string providerSessionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeVoiceProvider : IVoiceSessionProvider
    {
        private Func<VoiceProviderEvent, Task>? _events;
        public string VoiceProviderId => "fake-live";
        public string DefaultModel => "fake-model";
        public string DefaultVoice => "fake-voice";
        public bool SupportsBackendUpdates => true;
        public bool FailNext { get; set; }
        public List<VoiceBackendUpdate> BackendUpdates { get; } = [];

        public Task<VoiceSessionCreateResult> CreateVoiceSessionAsync(VoiceSessionCreateRequest request,
            Func<VoiceProviderEvent, Task> onEvent, CancellationToken ct)
        {
            _events = onEvent;
            return Task.FromResult(new VoiceSessionCreateResult("provider-1", "answer"));
        }

        public Task SendDelegationUpdateAsync(string providerSessionId, string delegationId,
            VoiceDelegationUpdate update, CancellationToken ct) => Task.CompletedTask;

        public Task SendBackendUpdateAsync(string providerSessionId, VoiceBackendUpdate update,
            CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated provider failure");
            }
            lock (BackendUpdates) BackendUpdates.Add(update);
            return Task.CompletedTask;
        }

        public Task CloseVoiceSessionAsync(string providerSessionId, CancellationToken ct) => Task.CompletedTask;

        public Task Emit(VoiceProviderEvent evt) => _events!(evt);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies) Bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
