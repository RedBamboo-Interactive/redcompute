using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

public sealed record VoiceSessionSnapshot(
    string Id,
    string Provider,
    string Model,
    string Voice,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? ClosedAt,
    string DiscussionId,
    string AiSessionId,
    int DelegationCount,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record VoiceSessionAdmission(
    VoiceSessionSnapshot Session,
    string AnswerSdp);

public sealed record VoiceSessionStart(
    string OfferSdp,
    string Model,
    string Voice,
    string Instructions,
    string DiscussionId,
    string AiSessionId,
    JobProvenance Provenance,
    IReadOnlyList<VoiceHistoryMessage>? InitialHistory = null);

public sealed class VoiceSessionCoordinator
{
    private sealed class SessionState
    {
        public required string Id { get; init; }
        public required IVoiceSessionProvider Provider { get; init; }
        public required string ProviderName { get; init; }
        public required string Model { get; init; }
        public required string Voice { get; init; }
        public required string DiscussionId { get; init; }
        public required string AiSessionId { get; init; }
        public required JobProvenance Provenance { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset LastActivityAt { get; set; }
        public string ProviderSessionId { get; set; } = "";
        public string Status { get; set; } = "connecting";
        public DateTimeOffset? ConnectedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int DelegationCount { get; set; }
        public long UtteranceOrdinal { get; set; }
        public string? OpenRole { get; set; }
        public string? OpenDelegationId { get; set; }
        public Queue<string> PendingSpokenDelegations { get; } = new();
        public string OpenText { get; set; } = "";
        public long? OpenStartMs { get; set; }
        public long? OpenEndMs { get; set; }
        public HashSet<string> SeenProviderEvents { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SeenUpdates { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TaskCompletionSource<bool>? CloseCompletion { get; set; }
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly HttpClient _callbacks;
    private readonly Action<string, Guid?> _log;
    private readonly Uri _callbackUri;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _maxLifetime;
    private readonly TimeSpan _watchdogInterval;
    private readonly Func<DateTimeOffset> _utcNow;

    public VoiceSessionCoordinator(HttpClient callbacks, Action<string, Guid?> log,
        string redLeafUrl, TimeSpan? idleTimeout = null, TimeSpan? maxLifetime = null,
        TimeSpan? watchdogInterval = null, Func<DateTimeOffset>? utcNow = null)
    {
        _callbacks = callbacks;
        _log = log;
        _callbackUri = new Uri(new Uri(redLeafUrl.TrimEnd('/') + "/"),
            "api/apps/nova/callbacks/voice-session");
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(1);
        _maxLifetime = maxLifetime ?? TimeSpan.FromMinutes(15);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(5);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (_idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (_maxLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxLifetime));
        if (_watchdogInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(watchdogInterval));
    }

    public async Task<VoiceSessionAdmission> CreateAsync(
        IVoiceSessionProvider provider,
        VoiceSessionStart request,
        CancellationToken ct)
    {
        PurgeClosedSessions();
        request.Provenance.ValidateForNewJob();
        var id = $"vs_{Guid.NewGuid():N}";
        var now = _utcNow();
        var state = new SessionState
        {
            Id = id,
            Provider = provider,
            ProviderName = provider.VoiceProviderId,
            Model = request.Model,
            Voice = request.Voice,
            DiscussionId = request.DiscussionId,
            AiSessionId = request.AiSessionId,
            Provenance = request.Provenance,
            CreatedAt = now,
            LastActivityAt = now,
        };
        if (!_sessions.TryAdd(id, state)) throw new InvalidOperationException("Voice session identity collision");

        try
        {
            var created = await provider.CreateVoiceSessionAsync(new(
                id, request.OfferSdp, request.Model, request.Voice,
                request.Instructions, request.Provenance, request.InitialHistory),
                evt => HandleProviderEventAsync(state, evt), ct);
            state.ProviderSessionId = created.ProviderSessionId;
            _ = WatchSessionLifetimeAsync(state);
            return new(Snapshot(state), created.AnswerSdp);
        }
        catch
        {
            state.Status = "error";
            state.ClosedAt = DateTimeOffset.UtcNow;
            throw;
        }
    }

    public VoiceSessionSnapshot? Get(string id, string? userId, bool trustedInfrastructure = false)
        => _sessions.TryGetValue(id, out var state) && CanAccess(state, userId, trustedInfrastructure)
            ? Snapshot(state) : null;

    public async Task<bool> UpdateDelegationAsync(string id, string delegationId,
        VoiceDelegationUpdate update, string? userId, bool trustedInfrastructure,
        CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var state) || !CanAccess(state, userId, trustedInfrastructure))
            return false;
        await state.Gate.WaitAsync(ct);
        try
        {
            if (!state.SeenUpdates.Add(update.EventId)) return true;
            var marksSpokenDelivery = update.Kind is "commentary" or "failed";
            try
            {
                await state.Provider.SendDelegationUpdateAsync(
                    state.ProviderSessionId, delegationId, update, ct);
                if (marksSpokenDelivery) state.PendingSpokenDelegations.Enqueue(delegationId);
                state.LastActivityAt = _utcNow();
            }
            catch
            {
                state.SeenUpdates.Remove(update.EventId);
                throw;
            }
            return true;
        }
        finally { state.Gate.Release(); }
    }

    public async Task<bool> CloseAsync(string id, string? userId, bool trustedInfrastructure,
        CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var state) || !CanAccess(state, userId, trustedInfrastructure))
            return false;
        string? providerSessionId = null;
        Task<bool>? existingClose = null;
        TaskCompletionSource<bool>? ownedClose = null;
        await state.Gate.WaitAsync(ct);
        try
        {
            if (state.Status == "closed") return true;
            if (state.Status == "closing")
            {
                existingClose = state.CloseCompletion?.Task;
            }
            else
            {
                state.Status = "closing";
                ownedClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.CloseCompletion = ownedClose;
                await FlushUtteranceAsync(state, reason: "close", delegationId: null, ct);
                providerSessionId = state.ProviderSessionId;
            }
        }
        finally { state.Gate.Release(); }

        if (existingClose is not null)
            return await existingClose.WaitAsync(ct);

        try
        {
            if (!string.IsNullOrWhiteSpace(providerSessionId))
                await state.Provider.CloseVoiceSessionAsync(providerSessionId, ct);

            await state.Gate.WaitAsync(ct);
            try
            {
                state.Status = "closed";
                state.ClosedAt ??= DateTimeOffset.UtcNow;
            }
            finally { state.Gate.Release(); }
            ownedClose!.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            ownedClose!.TrySetException(ex);
            throw;
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        foreach (var state in _sessions.Values)
        {
            try { await CloseAsync(state.Id, null, trustedInfrastructure: true, ct); }
            catch { }
        }
    }

    private async Task HandleProviderEventAsync(SessionState state, VoiceProviderEvent evt)
    {
        await state.Gate.WaitAsync();
        try
        {
            if (evt.EventId is { Length: > 0 } && !state.SeenProviderEvents.Add(evt.EventId)) return;
            if (evt.Type is "session.started" or "session.input_transcript.delta"
                or "session.output_transcript.delta" or "session.delegation.created")
                state.LastActivityAt = _utcNow();
            switch (evt.Type)
            {
                case "session.started":
                    state.Status = "connected";
                    state.ConnectedAt ??= DateTimeOffset.UtcNow;
                    await NotifyAsync(state, "voice.session.started", evt.EventId ?? $"started:{state.Id}",
                        new { }, CancellationToken.None);
                    break;
                case "session.input_transcript.delta":
                    await AppendTranscriptAsync(state, "user", evt, CancellationToken.None);
                    break;
                case "session.output_transcript.delta":
                    await AppendTranscriptAsync(state, "assistant", evt, CancellationToken.None);
                    break;
                case "session.delegation.created":
                    if (string.IsNullOrWhiteSpace(evt.DelegationId)) break;
                    state.DelegationCount++;
                    await FlushUtteranceAsync(state, "delegation", evt.DelegationId, CancellationToken.None);
                    await NotifyAsync(state, "voice.delegation.created",
                        evt.EventId ?? $"delegation:{evt.DelegationId}", new
                        {
                            delegationId = evt.DelegationId,
                            offsetMs = evt.OffsetMs,
                        }, CancellationToken.None);
                    break;
                case "session.closed":
                    await FlushUtteranceAsync(state, "provider_close", null, CancellationToken.None);
                    state.Status = "closed";
                    state.ClosedAt ??= DateTimeOffset.UtcNow;
                    await NotifyAsync(state, "voice.session.closed", evt.EventId ?? $"closed:{state.Id}",
                        new { }, CancellationToken.None);
                    state.CloseCompletion?.TrySetResult(true);
                    break;
                case "error":
                    state.Status = "degraded";
                    state.ErrorCode = evt.ErrorCode ?? "provider_error";
                    state.ErrorMessage = evt.ErrorMessage ?? "Voice provider error";
                    await NotifyAsync(state, "voice.session.error", evt.EventId ?? $"error:{Guid.NewGuid():N}",
                        new { errorCode = state.ErrorCode, errorMessage = state.ErrorMessage }, CancellationToken.None);
                    break;
            }
        }
        finally { state.Gate.Release(); }
    }

    private async Task AppendTranscriptAsync(SessionState state, string role,
        VoiceProviderEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.Delta)) return;
        if (state.OpenRole is not null && !string.Equals(state.OpenRole, role, StringComparison.Ordinal))
            await FlushUtteranceAsync(state, "speaker_change", null, ct);
        if (state.OpenRole is null)
        {
            state.OpenDelegationId = role == "assistant" && state.PendingSpokenDelegations.Count > 0
                ? state.PendingSpokenDelegations.Dequeue() : null;
        }
        state.OpenRole = role;
        state.OpenText += evt.Delta;
        state.OpenStartMs = state.OpenStartMs is null ? evt.StartMs
            : evt.StartMs is null ? state.OpenStartMs : Math.Min(state.OpenStartMs.Value, evt.StartMs.Value);
        state.OpenEndMs = state.OpenEndMs is null ? evt.EndMs
            : evt.EndMs is null ? state.OpenEndMs : Math.Max(state.OpenEndMs.Value, evt.EndMs.Value);
    }

    private async Task FlushUtteranceAsync(SessionState state, string reason,
        string? delegationId, CancellationToken ct)
    {
        var content = state.OpenText;
        var role = state.OpenRole;
        var startMs = state.OpenStartMs;
        var endMs = state.OpenEndMs;
        var spokenDelegationId = state.OpenDelegationId;
        state.OpenRole = null;
        state.OpenDelegationId = null;
        state.OpenText = "";
        state.OpenStartMs = null;
        state.OpenEndMs = null;
        if (string.IsNullOrWhiteSpace(content) || role is null) return;

        var ordinal = ++state.UtteranceOrdinal;
        var correlatedDelegationId = role == "assistant" ? spokenDelegationId : delegationId;
        var messageUid = correlatedDelegationId is not null && role == "user"
            ? $"voice:{state.Id}:{correlatedDelegationId}:user"
            : $"voice:{state.Id}:{ordinal}:{role}";
        await NotifyAsync(state, "voice.utterance.finalized",
            $"utterance:{state.Id}:{ordinal}", new
            {
                role,
                content,
                messageUid,
                startMs,
                endMs,
                reason,
                delegationId = correlatedDelegationId,
            }, ct);
    }

    private async Task NotifyAsync(SessionState state, string type, string eventId,
        object data, CancellationToken ct)
    {
        var body = new
        {
            type,
            eventId,
            voiceSessionId = state.Id,
            providerSessionId = state.ProviderSessionId,
            discussionId = state.DiscussionId,
            aiSessionId = state.AiSessionId,
            provider = state.ProviderName,
            data,
        };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await _callbacks.PostAsJsonAsync(_callbackUri, body, ct);
                if (response.IsSuccessStatusCode) return;
                _log($"[Voice] Nova callback {type} failed with HTTP {(int)response.StatusCode} (attempt {attempt})", null);
            }
            catch (Exception ex) when (attempt < 3)
            {
                _log($"[Voice] Nova callback {type} failed (attempt {attempt}): {ex.Message}", null);
            }
            if (attempt < 3) await Task.Delay(250 * attempt, ct);
        }
        _log($"[Voice] Nova callback {type} exhausted retries for {state.Id}", null);
    }

    private static bool CanAccess(SessionState state, string? userId, bool trustedInfrastructure)
        => trustedInfrastructure
            || (!string.IsNullOrWhiteSpace(state.Provenance.OnBehalfOf.Id)
                && string.Equals(state.Provenance.OnBehalfOf.Id, userId, StringComparison.Ordinal));

    private void PurgeClosedSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(6);
        foreach (var entry in _sessions)
            if (entry.Value.ClosedAt is { } closedAt && closedAt < cutoff)
                _sessions.TryRemove(entry.Key, out _);
    }

    private async Task WatchSessionLifetimeAsync(SessionState state)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_watchdogInterval);
                if (await ExpireIfInactiveAsync(state, _utcNow())) return;
            }
        }
        catch (Exception ex)
        {
            _log($"[Voice] Session watchdog failed for {state.Id}: {ex.Message}", null);
            try { await CloseAsync(state.Id, null, trustedInfrastructure: true, CancellationToken.None); }
            catch { }
        }
    }

    internal async Task<bool> ExpireIfInactiveAsync(string id, DateTimeOffset now)
        => _sessions.TryGetValue(id, out var state) && await ExpireIfInactiveAsync(state, now);

    private async Task<bool> ExpireIfInactiveAsync(SessionState state, DateTimeOffset now)
    {
        string? reason = null;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Status is "closed" or "closing" or "error") return true;
            if (now - state.CreatedAt >= _maxLifetime) reason = "max_lifetime";
            else if (now - state.LastActivityAt >= _idleTimeout) reason = "idle_timeout";
            if (reason is null) return false;
            state.ErrorCode = reason;
            state.ErrorMessage = reason == "idle_timeout"
                ? "Voice session closed after one minute without activity"
                : "Voice session reached its fifteen-minute safety limit";
        }
        finally { state.Gate.Release(); }

        _log($"[Voice] Closing {state.Id} due to {reason}", null);
        try
        {
            await CloseAsync(state.Id, null, trustedInfrastructure: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log($"[Voice] Safety close failed for {state.Id}: {ex.Message}", null);
        }
        return true;
    }

    private static VoiceSessionSnapshot Snapshot(SessionState state) => new(
        state.Id, state.ProviderName, state.Model, state.Voice, state.Status,
        state.CreatedAt, state.ConnectedAt, state.ClosedAt,
        state.DiscussionId, state.AiSessionId, state.DelegationCount,
        state.ErrorCode, state.ErrorMessage);
}
