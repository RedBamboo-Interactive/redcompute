using System.Reflection;
using System.Text.Json;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionInputQueueEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "input-queue-transcript-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Transcript_text_uses_the_human_display_without_changing_provider_input()
    {
        var input = new[] { SessionInputPart.TextPart("<private>model context</private>") };

        Assert.Equal("<private>model context</private>", SessionInputFormatting.UserText(input));
        Assert.Equal("What I said", SessionInputFormatting.TranscriptText(input, "What I said"));
        Assert.Equal("", SessionInputFormatting.TranscriptText(input, ""));
        Assert.Equal("<private>model context</private>", SessionInputFormatting.TranscriptText(input, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BatchIdentityReachesProviderBeforeDeliveryAcknowledgement(bool confidential, bool richInput)
    {
        Directory.CreateDirectory(_root);
        var config = new RedComputeConfig();
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        var queue = new SessionInputQueueStore(config, attachments);
        var provider = DispatchProxy.Create<ITestProvider, HeldProvider>();
        var held = (HeldProvider)(object)provider;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        var service = new SessionInputQueueService(queue, attachments, registry, null!, new WebSocketBroadcaster(),
            (_, _) => { }, _ => confidential);
        var attachmentIds = new List<string>();
        if (richInput)
            attachmentIds.Add((await attachments.UploadBytesAsync("notes"u8.ToArray(), "notes.txt", "text/plain", "owner-a")).Id);
        var first = Submission("<private>one</private>", "first", attachmentIds,
            richInput ? "{\"context\":\"captured\"}" : null, "one");
        var second = Submission("two", "second", [], null);
        var item1 = (await queue.EnqueueAsync(first)).Item;
        var item2 = (await queue.EnqueueAsync(second)).Item;
        var admission = service.AdmitAsync(first);
        try
        {
            var payload = await held.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(admission.IsCompleted);
            Assert.Equal("first", payload.Uid);
            Assert.Equal("<private>one</private>\ntwo", payload.ModelContent);
            Assert.Equal("one\ntwo", payload.DisplayContent);
            using var envelope = JsonDocument.Parse(payload.Json!);
            Assert.Equal(["first", "second"], envelope.RootElement.GetProperty("inputMessageUids")
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(richInput ? 1 : 0, envelope.RootElement.GetProperty("attachments").GetArrayLength());
            if (richInput)
                Assert.Equal("captured", envelope.RootElement.GetProperty("metadata").GetProperty("context").GetString());
            Assert.Equal(SessionInputQueueState.Delivering, (await queue.GetAsync("session-a", item2.Id, "owner-a"))!.State);
        }
        finally
        {
            held.Release.TrySetResult(SessionInputDeliveryResult.Accepted());
            await admission;
        }
        Assert.Equal("first", (await queue.GetAsync("session-a", item1.Id, "owner-a"))!.DeliveredMessageUid);
        Assert.Equal("first", (await queue.GetAsync("session-a", item2.Id, "owner-a"))!.DeliveredMessageUid);
    }

    private (SessionInputQueueStore Queue, SessionInputQueueService Service, ScriptedProvider Provider) ScriptedQueue()
    {
        Directory.CreateDirectory(_root);
        var config = new RedComputeConfig();
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        var queue = new SessionInputQueueStore(config, attachments);
        var proxy = DispatchProxy.Create<ITestProvider, ScriptedProvider>();
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = proxy }, "test");
        var service = new SessionInputQueueService(queue, attachments, registry, null!, new WebSocketBroadcaster(),
            (_, _) => { }, _ => false);
        return (queue, service, (ScriptedProvider)(object)proxy);
    }

    private static async Task FailHeadAsync(SessionInputQueueStore queue)
        => await queue.FailAsync(await queue.ClaimBatchAsync("session-a", "test-lease", TimeSpan.FromMinutes(1)),
            "provider_rejected", "Provider 'test' rejected the input", retryable: false);

    [Fact]
    public async Task A_refused_input_the_provider_already_recorded_is_delivered_and_blocks_nothing()
    {
        var (queue, service, provider) = ScriptedQueue();
        provider.RecordBeforeResult = true;
        provider.Result = SessionInputDeliveryResult.Rejected("provider_rejected", "Provider 'test' rejected the input");
        var item = (await queue.EnqueueAsync(Submission("one", "first", [], null))).Item;

        await service.ProcessSessionAsync("session-a", CancellationToken.None);

        var settled = await queue.GetAsync("session-a", item.Id, "owner-a");
        Assert.Equal(SessionInputQueueState.Delivered, settled!.State);
        Assert.Equal("first", settled.DeliveredMessageUid);
        Assert.Equal("empty", (await service.GetSummaryAsync("session-a", "owner-a")).State);
    }

    [Fact]
    public async Task A_refused_input_missing_from_the_transcript_stays_a_failed_head_and_keeps_fifo()
    {
        var (queue, service, provider) = ScriptedQueue();
        provider.Result = SessionInputDeliveryResult.Rejected("provider_rejected", "Provider 'test' rejected the input");
        var first = (await queue.EnqueueAsync(Submission("one", "first", [], null))).Item;

        await service.ProcessSessionAsync("session-a", CancellationToken.None);
        var second = (await queue.EnqueueAsync(Submission("two", "second", [], null))).Item;
        await service.ProcessSessionAsync("session-a", CancellationToken.None);

        Assert.Equal(SessionInputQueueState.Failed, (await queue.GetAsync("session-a", first.Id, "owner-a"))!.State);
        Assert.Equal(SessionInputQueueState.Pending, (await queue.GetAsync("session-a", second.Id, "owner-a"))!.State);
        var summary = await service.GetSummaryAsync("session-a", "owner-a");
        Assert.Equal("failed", summary.State);
        Assert.Equal("failed_head", summary.BlockedReason);
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task An_old_failed_head_the_transcript_holds_settles_and_releases_the_queue_behind_it()
    {
        var (queue, service, provider) = ScriptedQueue();
        var failed = (await queue.EnqueueAsync(Submission("one", "first", [], null))).Item;
        await FailHeadAsync(queue);
        var next = (await queue.EnqueueAsync(Submission("two", "second", [], null))).Item;
        provider.Recorded.Add("first");

        await service.ProcessSessionAsync("session-a", CancellationToken.None);

        Assert.Equal(SessionInputQueueState.Delivered, (await queue.GetAsync("session-a", failed.Id, "owner-a"))!.State);
        Assert.Equal(SessionInputQueueState.Delivered, (await queue.GetAsync("session-a", next.Id, "owner-a"))!.State);
        Assert.Equal(["second"], provider.Sent);
    }

    [Fact]
    public async Task A_failed_batch_settles_whole_with_the_transcript_identity_of_its_head()
    {
        var (queue, service, provider) = ScriptedQueue();
        var first = (await queue.EnqueueAsync(Submission("one", "first", [], null))).Item;
        var second = (await queue.EnqueueAsync(Submission("two", "second", [], null))).Item;
        await FailHeadAsync(queue);
        provider.Recorded.Add("first");

        await service.ProcessSessionAsync("session-a", CancellationToken.None);

        Assert.Equal("first", (await queue.GetAsync("session-a", first.Id, "owner-a"))!.DeliveredMessageUid);
        Assert.Equal("first", (await queue.GetAsync("session-a", second.Id, "owner-a"))!.DeliveredMessageUid);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task A_retryable_refusal_keeps_its_retry_even_when_recorded()
    {
        var (queue, service, provider) = ScriptedQueue();
        provider.RecordBeforeResult = true;
        provider.Result = SessionInputDeliveryResult.Unavailable("session_restarting", "lost its process", retryable: true);
        var item = (await queue.EnqueueAsync(Submission("one", "first", [], null))).Item;

        await service.ProcessSessionAsync("session-a", CancellationToken.None);

        var pending = await queue.GetAsync("session-a", item.Id, "owner-a");
        Assert.Equal(SessionInputQueueState.Pending, pending!.State);
        Assert.Equal("session_restarting", pending.ErrorCode);
    }

    private static SessionInputQueueSubmission Submission(string text, string uid, IReadOnlyList<string> attachments,
        string? metadata, string? displayContent = null)
        => new("session-a", "owner-a", [new QueuedSessionInputPart("text", text),
            .. attachments.Select(id => new QueuedSessionInputPart("attachment", id))], displayContent ?? text, metadata,
            new JobProvenance(JobProvenance.CurrentSchemaVersion,
                new JobOrigin("redcompute", new JobAppReference("app", "test", null, "Test"), new JobEntrypoint("http", "/test", "POST")),
                new JobActor("user", "Owner", Id: "owner-a"), new JobBeneficiary("user", "owner-a", "Owner"),
                [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            SessionInputDeliveryPolicy.AfterCurrent, uid, attachments, "client-" + uid);

    public interface ITestProvider : ISessionProvider, IBackendProvider { }
    public class HeldProvider : DispatchProxy
    {
        public readonly TaskCompletionSource<(string? Json, string? Uid, string? DisplayContent, string? ModelContent)> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<SessionInputDeliveryResult> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly UnifiedSessionInfo _info = new() { Id = "session-a", Provider = "test", ProjectName = "Test", ProjectPath = ".", UserId = "owner-a", Status = SessionStatus.Idle };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionProvider.GetSession)) return (_info, new List<UnifiedMessageRecord>());
            if (method.Name == nameof(ISessionProvider.TrySendInputAsync))
            {
                _info.Status = SessionStatus.Active;
                var input = (IReadOnlyList<SessionInputPart>)args![1]!;
                Received.TrySetResult(((string?)args[2], (string?)args[3], (string?)args[4],
                    SessionInputFormatting.UserText(input)));
                return Release.Task;
            }
            throw new NotSupportedException(method.Name);
        }
    }

    public class ScriptedProvider : DispatchProxy
    {
        public readonly List<string> Recorded = [];
        public readonly List<string> Sent = [];
        public bool RecordBeforeResult;
        public int Attempts;
        public SessionInputDeliveryResult Result = SessionInputDeliveryResult.Accepted();
        private readonly UnifiedSessionInfo _info = new() { Id = "session-a", Provider = "test", ProjectName = "Test", ProjectPath = ".", UserId = "owner-a", Status = SessionStatus.Idle };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionProvider.GetSession))
                return (_info, Recorded.Select(uid => new UnifiedMessageRecord
                {
                    SessionId = "session-a", Role = "user", EventType = "text", MessageUid = uid,
                }).ToList());
            if (method.Name == nameof(ISessionProvider.TrySendInputAsync))
            {
                Attempts++;
                var uid = (string)args![3]!;
                if (RecordBeforeResult) Recorded.Add(uid);
                if (Result.Status == SessionInputDeliveryStatus.Accepted) Sent.Add(uid);
                return Task.FromResult(Result);
            }
            throw new NotSupportedException(method.Name);
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
