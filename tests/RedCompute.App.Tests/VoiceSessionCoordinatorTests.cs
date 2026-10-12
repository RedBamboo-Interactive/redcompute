using System.Net;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class VoiceSessionCoordinatorTests
{
    [Fact]
    public async Task Speaker_change_finalizes_one_stable_user_utterance()
    {
        var callbacks = new RecordingHandler();
        var provider = new FakeVoiceProvider();
        var coordinator = NewCoordinator(callbacks);

        var created = await coordinator.CreateAsync(provider,
            Start("offer"), CancellationToken.None);
        await provider.Emit(new("session.input_transcript.delta", "in-1", "Hello ", 10, 30));
        await provider.Emit(new("session.input_transcript.delta", "in-2", "Nova", 30, 50));
        await provider.Emit(new("session.output_transcript.delta", "out-1", "Hi", 60, 80));

        var utterance = callbacks.Bodies.Single(body => body.GetProperty("type").GetString() == "voice.utterance.finalized");
        var data = utterance.GetProperty("data");
        Assert.Equal("user", data.GetProperty("role").GetString());
        Assert.Equal("Hello Nova", data.GetProperty("content").GetString());
        Assert.Equal($"voice:{created.Session.Id}:1:user", data.GetProperty("messageUid").GetString());
        Assert.Equal(10, data.GetProperty("startMs").GetInt64());
        Assert.Equal(50, data.GetProperty("endMs").GetInt64());
    }

    [Fact]
    public async Task Delegation_finalizes_user_text_and_duplicate_provider_event_is_idempotent()
    {
        var callbacks = new RecordingHandler();
        var provider = new FakeVoiceProvider();
        var coordinator = NewCoordinator(callbacks);
        var created = await coordinator.CreateAsync(provider, Start("offer"), CancellationToken.None);

        await provider.Emit(new("session.input_transcript.delta", "in-1", "Turn on the lights", 10, 90));
        var delegated = new VoiceProviderEvent("session.delegation.created", "delegate-event",
            DelegationId: "item_123", OffsetMs: 100);
        await provider.Emit(delegated);
        await provider.Emit(delegated);

        var utterances = callbacks.Bodies.Where(body => body.GetProperty("type").GetString() == "voice.utterance.finalized").ToList();
        var delegations = callbacks.Bodies.Where(body => body.GetProperty("type").GetString() == "voice.delegation.created").ToList();
        Assert.Single(utterances);
        Assert.Single(delegations);
        Assert.Equal($"voice:{created.Session.Id}:item_123:user",
            utterances[0].GetProperty("data").GetProperty("messageUid").GetString());
        Assert.Equal("item_123", delegations[0].GetProperty("data").GetProperty("delegationId").GetString());
    }

    [Fact]
    public async Task Delegation_update_and_close_are_idempotent()
    {
        var provider = new FakeVoiceProvider();
        var coordinator = NewCoordinator(new RecordingHandler());
        var created = await coordinator.CreateAsync(provider, Start("offer"), CancellationToken.None);

        var update = new VoiceDelegationUpdate("result-1", "commentary", "Done.");
        Assert.True(await coordinator.UpdateDelegationAsync(created.Session.Id, "item_1", update,
            "user-1", false, CancellationToken.None));
        Assert.True(await coordinator.UpdateDelegationAsync(created.Session.Id, "item_1", update,
            "user-1", false, CancellationToken.None));
        Assert.Single(provider.Updates);

        Assert.True(await coordinator.CloseAsync(created.Session.Id, "user-1", false, CancellationToken.None));
        Assert.True(await coordinator.CloseAsync(created.Session.Id, "user-1", false, CancellationToken.None));
        Assert.Equal(1, provider.CloseCount);
        Assert.Equal("closed", coordinator.Get(created.Session.Id, "user-1")!.State);
        Assert.Null(coordinator.Get(created.Session.Id, "someone-else"));
    }

    [Fact]
    public async Task Spoken_delegation_result_is_correlated_as_delivery_not_a_second_answer()
    {
        var callbacks = new RecordingHandler();
        var provider = new FakeVoiceProvider();
        var coordinator = NewCoordinator(callbacks);
        var created = await coordinator.CreateAsync(provider, Start("offer"), CancellationToken.None);

        Assert.True(await coordinator.UpdateDelegationAsync(created.Session.Id, "item_1",
            new("result-1", "commentary", "Verified result"), "user-1", false, CancellationToken.None));
        await provider.Emit(new("session.output_transcript.delta", "out-1", "It's done.", 100, 200));
        await provider.Emit(new("session.input_transcript.delta", "in-1", "Thanks", 220, 300));

        var delivery = callbacks.Bodies.Single(body => body.GetProperty("type").GetString() == "voice.utterance.finalized");
        Assert.Equal("assistant", delivery.GetProperty("data").GetProperty("role").GetString());
        Assert.Equal("item_1", delivery.GetProperty("data").GetProperty("delegationId").GetString());
    }

    private static VoiceSessionCoordinator NewCoordinator(RecordingHandler handler)
        => new(new HttpClient(handler), (_, _) => { }, "http://127.0.0.1:18804");

    private static VoiceSessionStart Start(string offer) => new(
        offer, "gpt-live-1", "marin", "Be Nova.", "discussion-1", "ai-session-1",
        new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redleaf", new JobAppReference("app", "nova", null, "Nova"),
                new JobEntrypoint("http", "/voice", "POST")),
            new JobActor("agent", "Nova", EntityId: "agent-1"),
            new JobBeneficiary("user", "user-1", "Laurent"),
            [new JobContextReference("discussion", "discussion-1")],
            new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow));

    private sealed class FakeVoiceProvider : IVoiceSessionProvider
    {
        private Func<VoiceProviderEvent, Task>? _events;
        public string VoiceProviderId => "fake-live";
        public string DefaultModel => "fake-model";
        public string DefaultVoice => "fake-voice";
        public List<VoiceDelegationUpdate> Updates { get; } = [];
        public int CloseCount { get; private set; }

        public Task<VoiceSessionCreateResult> CreateVoiceSessionAsync(VoiceSessionCreateRequest request,
            Func<VoiceProviderEvent, Task> onEvent, CancellationToken ct)
        {
            _events = onEvent;
            return Task.FromResult(new VoiceSessionCreateResult("provider-1", "answer"));
        }

        public Task SendDelegationUpdateAsync(string providerSessionId, string delegationId,
            VoiceDelegationUpdate update, CancellationToken ct)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public Task CloseVoiceSessionAsync(string providerSessionId, CancellationToken ct)
        {
            CloseCount++;
            return Task.CompletedTask;
        }

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
