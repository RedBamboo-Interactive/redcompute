using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services;

internal sealed record OwnerDraftOrigin(
    string DiscussionId, string SessionId, string OwnerId, string AgentId,
    string Type, bool Confidential, bool Active);

internal sealed record OwnerDraftAdmission(
    int Version, string Operation, string IntentId, string PayloadSha256, DateTimeOffset ExpiresAt,
    string DiscussionId, string SessionId, string OwnerId, string AgentId, string QueueItemId,
    string MessageUid, string ConnectionId, string ThreadId, string TurnId, string CallId,
    OwnerDraftArguments Draft);

/// <summary>Only an explicitly composed host can grant this capability to local Codex.</summary>
internal sealed class OwnerDraftBroker(
    SessionInputQueueStore store,
    Func<string, CancellationToken, Task<OwnerDraftOrigin?>> readOrigin,
    OwnerDraftClient client)
{
    private const string Accepted = "redcompute-owner-draft-input";
    private static readonly HashSet<string> SignedContext = new(StringComparer.Ordinal)
    {
        "discussion", "session", "origin-entrypoint", "execution", "parent-execution",
    };

    internal static JobProvenance ClassifyAdmission(JobProvenance provenance, string sessionId, string ownerId, bool authenticated)
    {
        // Drop a caller-supplied copy even when the rest of the request is ineligible.
        if (provenance.Context.Any(reference => reference.Kind == Accepted))
            return provenance with { Context = provenance.Context.Where(reference => reference.Kind != Accepted).ToArray() };
        if (authenticated && SourceDiscussion(provenance, sessionId, ownerId) is not null)
            return provenance with { Context = [.. provenance.Context, new JobContextReference(Accepted, sessionId)] };
        return provenance;
    }

    internal OwnerDraftDelivery? Prepare(UnifiedSessionInfo session, IReadOnlyList<SessionInputQueueItem> batch)
    {
        if (batch.Count != 1 || session.Provider != "codex" || session.Confidential || session.Unattended
            || session.DeliveredAsOneMessage || session.NativeRuntimeId is not null)
            return null;
        var item = batch[0];
        if (item.IsControl || item.SessionId != session.Id || item.TurnId != item.MessageUid
            || item.Requested == SessionInputDeliveryPolicy.SteerCurrent || session.UserId != item.OwnerUserId
            || item.Provenance.Context.Count(reference => reference.Kind == Accepted && reference.Id == session.Id) != 1)
            return null;
        var source = item.Provenance with
        {
            Context = item.Provenance.Context.Where(reference => reference.Kind != Accepted).ToArray()
        };
        var discussion = SourceDiscussion(source, session.Id, item.OwnerUserId);
        var agent = source.Actor.EntityId;
        if (discussion is null || agent is null || session.OwnerAgentId is { } sessionAgent && sessionAgent != agent)
            return null;

        var pending = new Dictionary<Guid, (string Digest, Task<OwnerDraftResult> Result)>();
        var calls = new Dictionary<string, (Guid Binding, string Digest)>(StringComparer.Ordinal);
        return new OwnerDraftDelivery(session.Id, item.MessageUid, SubmitAsync);

        Task<OwnerDraftResult> SubmitAsync(NativeOwnerDraftCall call, Func<bool> isCurrent, CancellationToken ct)
        {
            var digest = OwnerDraftClient.Digest(call.Draft);
            lock (pending)
            {
                if (!isCurrent() || ct.IsCancellationRequested)
                    return Task.FromResult(new OwnerDraftResult("owner_turn_retired"));
                var binding = call.Draft.BindingId;
                if (calls.TryGetValue(call.CallId, out var seen) && seen != (binding, digest))
                    return Task.FromResult(new OwnerDraftResult("owner_draft_conflict"));
                calls[call.CallId] = (binding, digest);
                if (pending.TryGetValue(binding, out var existing))
                {
                    if (existing.Digest != digest)
                        return Task.FromResult(new OwnerDraftResult("owner_draft_conflict"));
                    if (!existing.Result.IsCompleted || existing.Result.IsCompletedSuccessfully
                        && existing.Result.Result.Status != "owner_draft_recipient_unavailable")
                        return existing.Result;
                }
                var next = DeliverAsync(call, digest, isCurrent, ct);
                pending[binding] = (digest, next);
                return next;
            }
        }

        async Task<OwnerDraftResult> DeliverAsync(
            NativeOwnerDraftCall call, string digest, Func<bool> isCurrent, CancellationToken ct)
        {
            var delivered = await store.GetAsync(session.Id, item.Id, item.OwnerUserId, ct);
            if (delivered?.State != SessionInputQueueState.Delivered || delivered.MessageUid != item.MessageUid
                || delivered.DeliveredMessageUid != item.MessageUid || delivered.TurnId != item.MessageUid)
                return new("owner_input_not_delivered_or_retired");
            var origin = await readOrigin(discussion, ct);
            if (origin is null || !origin.Active || origin.Confidential || origin.Type is not ("chat" or "live")
                || origin.OwnerId != item.OwnerUserId || origin.AgentId != agent
                || origin.DiscussionId != discussion || origin.SessionId != session.Id)
                return new("owner_draft_origin_ineligible");
            if (!isCurrent() || ct.IsCancellationRequested)
                return new("owner_turn_retired");

            var intent = OwnerDraftClient.Digest(new { SessionId = session.Id, QueueItemId = item.Id, call.Draft.BindingId });
            return await client.AdmitAsync(new OwnerDraftAdmission(1, "discord.outreach.draft", intent, digest,
                DateTimeOffset.UtcNow.AddMinutes(1), discussion, session.Id, item.OwnerUserId, agent,
                item.Id, item.MessageUid, call.ConnectionId, call.ThreadId, call.TurnId, call.CallId, call.Draft), ct);
        }
    }

    private static string? SourceDiscussion(JobProvenance source, string sessionId, string ownerId)
    {
        if (source.Assurance != JobProvenanceAssurance.Verified || source.Cause != JobCauses.Beneficiary
            || source.Origin.App.Id != "nova" || source.Origin.Entrypoint.Route != "/ai-session/sessions/{id}/message"
            || source.Origin.Entrypoint.Method != "POST" || source.Actor.Kind != "agent"
            || !Guid.TryParse(source.Actor.EntityId, out _) || source.OnBehalfOf.Kind != "user"
            || !Guid.TryParse(ownerId, out _) || source.OnBehalfOf.Id != ownerId
            || source.Context.Any(reference => !SignedContext.Contains(reference.Kind)))
            return null;
        var discussion = UniqueReference(source, "discussion")?.Id;
        if (discussion is null || UniqueReference(source, "session")?.Id != sessionId
            || !Guid.TryParse(UniqueReference(source, "execution")?.Id, out _)
            || UniqueReference(source, "origin-entrypoint")?.Route != $"/api/apps/nova/discussions/{discussion}/message")
            return null;
        return discussion;
    }

    private static JobContextReference? UniqueReference(JobProvenance source, string kind)
    {
        var matches = source.Context.Where(reference => reference.Kind == kind).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

/// <summary>Fixed draft-only service route. The provider never receives service credentials.</summary>
internal sealed class OwnerDraftClient : IDisposable
{
    internal const string Route = "api/apps/leaf-discord/outreach/owner-turn-drafts";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<string> _credential;

    internal OwnerDraftClient(Uri kernel, JwtService jwt)
        : this(kernel, () => jwt.GenerateServiceAccessToken("redcompute", roles: ["service"])) { }

    private OwnerDraftClient(Uri kernel, Func<string> credential)
    {
        if (!kernel.IsLoopback || kernel.Scheme is not ("http" or "https") || kernel.AbsolutePath != "/"
            || kernel.UserInfo.Length != 0 || kernel.Query.Length != 0 || kernel.Fragment.Length != 0)
            throw new ArgumentException("Owner draft recipient must use the loopback kernel origin.", nameof(kernel));
        var serviceHandler = LocalExecutionHost.Shared.Network.CreateServiceHandler(kernel);
        if (serviceHandler is not HttpClientHandler handler)
        {
            serviceHandler.Dispose();
            throw new InvalidOperationException("The local kernel transport cannot be configured for a draft request.");
        }

        handler.AllowAutoRedirect = false;
        handler.UseProxy = false;
        _http = new HttpClient(handler)
        {
            BaseAddress = kernel,
            Timeout = TimeSpan.FromSeconds(10),
        };
        _credential = credential;
    }

    internal static string Digest<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Wire))).ToLowerInvariant();

    internal async Task<OwnerDraftResult> AdmitAsync(OwnerDraftAdmission admission, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(admission, options: Wire),
        };
        request.Headers.Authorization = new("Bearer", _credential());
        request.Headers.Add("Idempotency-Key", admission.IntentId);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if ((int)response.StatusCode == 409) return new("owner_draft_conflict");
            if (!response.IsSuccessStatusCode) return new("owner_draft_recipient_unavailable");
            var result = await response.Content.ReadFromJsonAsync<OwnerDraftResult>(Wire, ct);
            return result is { Status: "held", MessageUid: { Length: > 0 and <= 120 } }
                or { Status: "clear", MessageUid: null } ? result : new("owner_draft_recipient_unavailable");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new("owner_draft_recipient_unavailable");
        }
    }

    public void Dispose() => _http.Dispose();
}
