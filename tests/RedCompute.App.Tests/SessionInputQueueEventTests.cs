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
        var first = Submission("one", "first", attachmentIds, richInput ? "{\"context\":\"captured\"}" : null);
        var second = Submission("two", "second", [], null);
        var item1 = (await queue.EnqueueAsync(first)).Item;
        var item2 = (await queue.EnqueueAsync(second)).Item;
        var admission = service.AdmitAsync(first);
        try
        {
            var payload = await held.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(admission.IsCompleted);
            Assert.Equal("first", payload.Uid);
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

    private static SessionInputQueueSubmission Submission(string text, string uid, IReadOnlyList<string> attachments, string? metadata)
        => new("session-a", "owner-a", [new QueuedSessionInputPart("text", text),
            .. attachments.Select(id => new QueuedSessionInputPart("attachment", id))], text, metadata,
            new JobProvenance(JobProvenance.CurrentSchemaVersion,
                new JobOrigin("redcompute", new JobAppReference("app", "test", null, "Test"), new JobEntrypoint("http", "/test", "POST")),
                new JobActor("user", "Owner", Id: "owner-a"), new JobBeneficiary("user", "owner-a", "Owner"),
                [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow),
            SessionInputDeliveryPolicy.AfterCurrent, uid, attachments, "client-" + uid);

    public interface ITestProvider : ISessionProvider, IBackendProvider { }
    public class HeldProvider : DispatchProxy
    {
        public readonly TaskCompletionSource<(string? Json, string? Uid)> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<SessionInputDeliveryResult> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly UnifiedSessionInfo _info = new() { Id = "session-a", Provider = "test", ProjectName = "Test", ProjectPath = ".", UserId = "owner-a", Status = SessionStatus.Idle };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionProvider.GetSession)) return (_info, new List<UnifiedMessageRecord>());
            if (method.Name == nameof(ISessionProvider.TrySendInputAsync))
            {
                _info.Status = SessionStatus.Active;
                Received.TrySetResult(((string?)args![2], (string?)args[3]));
                return Release.Task;
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
