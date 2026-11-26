using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.SessionBindings;

internal sealed record BindingProviderOption(string Id, string Name, BindingTarget? Target);

/// <summary>Resolves existing session commands against the committed native binding, with a stable logical id.</summary>
internal sealed class SessionBindingCommands(CapabilityRegistry registry, SessionBindingTranscriptRoutes routes)
{
    internal IEnumerable<(string Instance, ISessionProvider Provider, IBackendProvider Backend)> Providers()
        => (registry.Get("ai-session")?.Providers ?? [])
            .Where(entry => entry.Value is ISessionProvider)
            .Select(entry => (entry.Key, (ISessionProvider)entry.Value, entry.Value));

    internal ISessionProvider? GetNative(BindingTarget target)
        => Providers().FirstOrDefault(entry => entry.Instance == target.ProviderInstanceId
            && entry.Provider.ProviderId == target.ProviderId).Provider;

    internal string? InstanceFor(ISessionProvider provider)
        => Providers().FirstOrDefault(entry => ReferenceEquals(entry.Provider, Unwrap(provider))).Instance;

    internal (ISessionProvider? Provider, UnifiedSessionInfo? Info) Find(string logicalId)
    {
        var binding = routes.Binding(logicalId);
        if (binding is not null)
        {
            var native = GetNative(binding.Active.Native.Target);
            if (native is null) return (null, null);
            if (binding.Active.Generation == 0 && binding.Active.Native.RuntimeSessionId == logicalId)
                return (native, native.GetSessionInfo(logicalId));
            var facade = new BoundProvider(native, binding);
            return (facade, facade.GetSessionInfo(logicalId));
        }
        // A physical preparation is not a user-addressable session id.
        if (logicalId.StartsWith("binding-", StringComparison.Ordinal)) return (null, null);
        foreach (var provider in registry.FindProviders<ISessionProvider>())
            if (provider.GetSessionInfo(logicalId) is { } info
                && routes.Resolve(info.Provider, info.Id)?.SessionId == logicalId) return (provider, info);
        return (null, null);
    }

    internal UnifiedSessionInfo? ProjectEvent(UnifiedSessionInfo physical)
    {
        var address = routes.Resolve(physical.Provider, physical.Id);
        return address is null ? null
            : address.SessionId == physical.Id && address.StorageProvider == physical.Provider ? physical
            : physical.AsLogicalSession(address.SessionId, address.StorageProvider);
    }

    internal object? ProjectEvent(ISessionProvider? provider, string type, object data)
    {
        if (data is UnifiedSessionInfo session) return ProjectEvent(session);
        if (type != "session.ended" || provider is null) return data;
        var ended = JsonSerializer.SerializeToNode(data) as JsonObject;
        if (ended?["id"]?.GetValue<string>() is not { } id) return data;
        var route = routes.Resolve(provider.ProviderId, id);
        if (route is null) return null;
        if (route.SessionId == id) return data;
        ended["id"] = route.SessionId;
        return ended;
    }

    internal SessionBindingSnapshot? Binding(string logicalId) => routes.Binding(logicalId);

    internal IReadOnlyList<BindingProviderOption> Options(ISessionProvider owner, UnifiedSessionInfo info, INodeRegistry? nodes)
    {
        var localNode = nodes?.LocalNodeId ?? "";
        if ((nodes?.Resolve(info.NodeId) ?? "") != localNode || !NativeSessionBindingPreparer.Supports(Unwrap(owner))
            || SessionExecutionProfile.IsConversationOnly(info.ExecutionProfile) || string.IsNullOrWhiteSpace(info.ProviderSessionId))
            return [];

        var config = registry.Get("ai-session")?.Config.Providers;
        var snapshot = routes.Binding(info.Id);
        var choices = new List<BindingProviderOption>();
        foreach (var group in Providers().Where(entry => NativeSessionBindingPreparer.Supports(entry.Provider)
                     && (config?.GetValueOrDefault(entry.Instance)?.RuntimeBinding ?? "shared") == "shared")
                 .GroupBy(entry => entry.Provider.ProviderId))
        {
            var instances = group.DistinctBy(entry => entry.Provider, ReferenceEqualityComparer.Instance).ToArray();
            if (group.Key == owner.ProviderId)
            {
                choices.Add(new(owner.ProviderId, owner.ProviderDisplayName, null));
                continue;
            }

            var previous = snapshot?.Threads.LastOrDefault(thread => thread.Native.Target.ProviderId == group.Key);
            var candidate = previous is null ? (instances.Length == 1 ? instances[0] : default)
                : group.FirstOrDefault(entry => entry.Instance == previous.Native.Target.ProviderInstanceId);
            if (candidate.Provider is null || NativeSessionBindingPreparer.ChooseAccess(candidate.Provider, info) is null)
                continue;

            var saved = previous is null ? null : candidate.Provider.GetSessionInfo(previous.Native.RuntimeSessionId);
            if (previous is not null && saved is null) continue;
            var catalogue = candidate.Provider.GetAvailableModels();
            var configured = config?.GetValueOrDefault(candidate.Instance)?.Model;
            var model = saved?.Model ?? catalogue.FirstOrDefault(item => item.Id == configured)?.Id
                ?? (candidate.Provider.ProviderId == "claude-code" ? catalogue.FirstOrDefault(item => item.Id == "sonnet")?.Id
                    : catalogue.FirstOrDefault()?.Id);
            if (model is null) continue;
            choices.Add(new(candidate.Provider.ProviderId, candidate.Provider.ProviderDisplayName,
                new(candidate.Provider.ProviderId, candidate.Instance, localNode, model, saved?.Effort)));
        }
        return choices;
    }

    internal bool IsBound(string logicalId) => routes.Binding(logicalId) is { Active.Generation: > 0 };

    internal LogicalTranscriptAddress? RouteEvent(string provider, string runtimeId) => routes.Resolve(provider, runtimeId);

    internal static ISessionProvider Unwrap(ISessionProvider provider)
        => provider is BoundProvider bound ? bound.Native : provider;

    private sealed class BoundProvider(ISessionProvider native, SessionBindingSnapshot binding)
        : ISessionProvider, ISessionInferenceSettings, IImageAttachmentSupportProvider
    {
        public ISessionProvider Native => native;
        private string RuntimeId => binding.Active.Native.RuntimeSessionId;
        private string StorageProvider => binding.Threads[0].Native.Target.ProviderId;
        private ISessionInferenceSettings Settings => (ISessionInferenceSettings)native;
        private UnifiedSessionInfo? Project(UnifiedSessionInfo? info) => info?.AsLogicalSession(binding.LogicalId, StorageProvider);

        public string ProviderId => native.ProviderId;
        public string ProviderDisplayName => native.ProviderDisplayName;
        // Provider-native rollback cannot remove messages from other bindings in the same logical suffix.
        public SessionCapabilities Capabilities => native.Capabilities & ~SessionCapabilities.SupportsRollback;
        public IReadOnlyList<string> SupportedAccessLevels => native.SupportedAccessLevels;
        public string? LastStartError => native.LastStartError;
        public string? LastStartErrorCode => native.LastStartErrorCode;

        public UnifiedSessionInfo? GetSessionInfo(string sessionId) => Project(native.GetSessionInfo(RuntimeId));
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
        {
            var (info, history) = native.GetSession(RuntimeId);
            return (Project(info), history);
        }
        public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
            => limit > 0 && GetSessionInfo(binding.LogicalId) is { } info && (includeDismissed || !info.Dismissed) ? [info] : [];
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId)
            => GetSessionInfo(binding.LogicalId)?.JobId == jobId ? GetSession(binding.LogicalId) : (null, []);
        public Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
            => GetSessionInfo(binding.LogicalId) is { JobId: { } jobId } info && jobIds.Contains(jobId)
                ? new() { [jobId] = info.Status } : [];

        public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId)
            => Project(await native.ResumeSessionAsync(RuntimeId));
        public async Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId, JobProvenance provenance)
            => Project(await native.ResumeSessionAsync(RuntimeId, provenance));
        public Task StopSessionAsync(string sessionId) => native.StopSessionAsync(RuntimeId);
        public Task ForceKillAsync(string sessionId) => native.ForceKillAsync(RuntimeId);
        public Task ForceKillAsync(string sessionId, string reason) => native.ForceKillAsync(RuntimeId, reason);
        public void DismissSession(string sessionId) => native.DismissSession(RuntimeId);
        public bool RestoreSession(string sessionId) => native.RestoreSession(RuntimeId);
        public bool SetDeliveredAsOneMessage(string sessionId, bool oneMessage) => native.SetDeliveredAsOneMessage(RuntimeId, oneMessage);

        public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null, string? messageUid = null)
            => native.SendInputAsync(RuntimeId, input, attachmentsJson, messageUid);
        public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson,
            string? messageUid, string? displayContent)
            => native.SendInputAsync(RuntimeId, input, attachmentsJson, messageUid, displayContent);
        public Task<SessionInputDeliveryResult> TrySendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
            string? attachmentsJson = null, string? messageUid = null)
            => native.TrySendInputAsync(RuntimeId, input, attachmentsJson, messageUid);
        public Task<SessionInputDeliveryResult> TrySendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
            string? attachmentsJson, string? messageUid, string? displayContent)
            => native.TrySendInputAsync(RuntimeId, input, attachmentsJson, messageUid, displayContent);
        public Task<SessionInputDeliveryResult> TrySteerInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
            string? attachmentsJson = null, string? messageUid = null, string? displayContent = null)
            => native.TrySteerInputAsync(RuntimeId, input, attachmentsJson, messageUid, displayContent);
        public Task<bool> InjectMessageAsync(string sessionId, string role, string content, string? attachmentsJson = null, string? messageUid = null)
            => native.InjectMessageAsync(RuntimeId, role, content, attachmentsJson, messageUid);
        public bool SendAnswer(string sessionId, string answer) => native.SendAnswer(RuntimeId, answer);
        public QuestionAnswerResult SubmitQuestionAnswer(string sessionId, SessionQuestionAnswer answer)
            => native.SubmitQuestionAnswer(RuntimeId, answer);
        public IReadOnlyList<PendingSessionQuestion> GetPendingQuestions(string sessionId) => native.GetPendingQuestions(RuntimeId);
        public InterruptResult InterruptSession(string sessionId) => native.InterruptSession(RuntimeId);
        public Task<BackgroundTaskStopResult> TryStopBackgroundTaskAsync(string sessionId, string taskId)
            => native.TryStopBackgroundTaskAsync(RuntimeId, taskId);
        public IReadOnlyList<BackgroundTaskInfo> GetBackgroundTasks(string sessionId) => native.GetBackgroundTasks(RuntimeId);
        public bool SetPermissionMode(string sessionId, string mode) => native.SetPermissionMode(RuntimeId, mode);
        public SessionAccessState? GetAccessState(string sessionId) => native.GetAccessState(RuntimeId);
        public bool SetAccessState(string sessionId, SessionAccessState state) => native.SetAccessState(RuntimeId, state);
        public Task<SessionAccessApplyResult> ApplyAccessStateAsync(string sessionId, SessionAccessState state, CancellationToken ct = default)
            => native.ApplyAccessStateAsync(RuntimeId, state, ct);

        public bool RequiresIdleForInferenceChanges => Settings.RequiresIdleForInferenceChanges;
        public bool RequiresResidentForInferenceChanges => Settings.RequiresResidentForInferenceChanges;
        public Task<SessionInferenceOptions> GetInferenceOptionsAsync(string sessionId, CancellationToken ct)
            => Settings.GetInferenceOptionsAsync(RuntimeId, ct);
        public Task<bool> TrySetInferenceSettingsAsync(string sessionId, string? model, string? effort, CancellationToken ct)
            => Settings.TrySetInferenceSettingsAsync(RuntimeId, model, effort, ct);
        public ImageAttachmentSupport GetImageAttachmentSupport(string sessionId) => GetImageAttachmentSupport(sessionId, false);
        public ImageAttachmentSupport GetImageAttachmentSupport(string sessionId, bool activeTurn)
            => native is IImageAttachmentSupportProvider images ? images.GetImageAttachmentSupport(RuntimeId, activeTurn)
                : new(native.Capabilities.HasFlag(SessionCapabilities.ImageAttachments));

        // This facade is a command address for an existing logical session. Starts, one-shot
        // execution and event subscriptions still go to the registered physical provider.
        public Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model,
            string? userId, string? userName, string? userAvatarUrl, string? effort, string? endpointUrl, string? apiKey,
            int? thinkingBudget, string? qualityTier, string? providerEntity, Guid? repositoryId, JobProvenance provenance,
            string? scratchDirectory = null, bool confidential = false) => throw NotSessionCommand();
        public Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
            int timeout, CancellationToken ct, string? streamKey = null, Dictionary<string, string>? env = null,
            Dictionary<string, object?>? providerParams = null) => throw NotSessionCommand();
        public Task<SessionGenerateResult> GenerateAsync(string? model, string? system, string messagesJson,
            int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null) => throw NotSessionCommand();
        public event Action<string, UnifiedStreamEvent>? SessionStreamEvent
        {
            add => throw NotSessionCommand();
            remove => throw NotSessionCommand();
        }
        public List<ModelInfo> GetAvailableModels() => native.GetAvailableModels();
        public void CancelExecution(string key) => throw NotSessionCommand();
        public Task StopAllAsync() => throw NotSessionCommand();

        private static NotSupportedException NotSessionCommand() => new("Use the registered provider for operations outside this logical session.");
    }
}
