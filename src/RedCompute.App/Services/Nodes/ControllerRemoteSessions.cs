using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink.Forward;
using RedCompute.NodeLink;

namespace RedCompute.App.Services.Nodes;

internal sealed record RemoteSessionPlacement(string NodeId, string RuntimeId, string Workspace, string Revision,
    string ProviderId, string ProviderInstanceId, string? Model, string? Effort);

internal sealed record RemoteSessionPlacementPreview(string NodeId, string RuntimeId, string Workspace, string Revision,
    string ProviderId, string ProviderInstanceId, string AccessLevel, string MaxAccessLevel);

internal sealed record ControllerRemoteSession(SessionRuntimeScope Scope, RemoteSessionPlacement Placement,
    RemoteSessionPlacementPreview Preview, RemoteSessionLifecycleThread Thread);

internal sealed record RemoteSessionInputStatus(string State, RemoteInputReceipt? Receipt, SessionTurnSummary? Turn,
    string? ErrorCode, string? PayloadDigest);

internal sealed record ControllerRemoteInputAdvance(string ItemId, string State, bool TranscriptChanged, string? ErrorCode = null);

internal sealed record RemoteReplayProgress(long Through, bool Changed, bool CaughtUp);

internal sealed class ControllerRemoteSessionException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal sealed class ControllerRemoteSessions(SessionInputQueueStore queue,
    Func<ClaimsPrincipal, SessionRuntimeScope, SessionRuntimeOperation, string, byte[], CancellationToken,
        Task<(HttpStatusCode Status, byte[] Body)>> send)
{
    private const string LogicalIdPrefix = "remote-";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static bool IsLogicalId(string id) => id.Length == LogicalIdPrefix.Length + 64
        && id.StartsWith(LogicalIdPrefix, StringComparison.Ordinal)
        && id.AsSpan(LogicalIdPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal Task<RemoteSessionPlacementPreview> ValidateAsync(ClaimsPrincipal caller, string key,
        RemoteSessionPlacement placement, CancellationToken ct)
        => InspectAsync(caller, GetScope(caller, key, placement), key, placement, ct);

    internal async Task<ControllerRemoteSession> StartAsync(ClaimsPrincipal caller, string key,
        RemoteSessionPlacement placement, CancellationToken ct, Func<RemoteSessionPlacementPreview, Task>? authorize = null)
    {
        var scope = GetScope(caller, key, placement);
        var preview = await InspectAsync(caller, scope, key, placement, ct);
        if (authorize is not null) await authorize(preview);
        await queue.ReserveControllerRemoteStartAsync(scope, placement, preview, ct);
        if (await queue.ReadBindingSnapshotAsync(scope.LogicalSessionId, ct) is { } existing)
            return MatchStart(existing, scope, placement, preview);

        var request = new RemoteSessionLifecycleRequest(placement.Revision, placement.ProviderInstanceId, placement.Model,
            placement.Effort, Fence: Fence(preview));
        var thread = await ApplyAsync<RemoteSessionLifecycleThread>(caller, scope, SessionRuntimeOperation.Start, key,
            request, HttpStatusCode.Created, ct);
        if (!Identifier(thread.RuntimeSessionId) || !Identifier(thread.NativeThreadId)
            || thread.Revision != placement.Revision || thread.ProviderInstanceId != placement.ProviderInstanceId)
            throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");

        // A lost reply is retried against the same durable reservation and the same destination command.
        var started = new ControllerRemoteSession(scope, placement, preview, thread);
        var committed = await queue.CommitControllerRemoteStartAsync(started, CancellationToken.None);
        if (MatchStart(committed, scope, placement, preview) != started)
            throw new ControllerRemoteSessionException(409, "remote_start_conflict");
        return started;
    }

    internal async Task<ControllerRemoteSession> ResumeAsync(ClaimsPrincipal caller, string logicalId, string key,
        CancellationToken ct)
    {
        if (!Identifier(key))
            throw new ControllerRemoteSessionException(422, "invalid_remote_request_key");
        var (_, session) = await ReadOwnedAsync(caller, logicalId, ct);

        var thread = session.Thread;
        var resumed = await ApplyAsync<RemoteSessionLifecycleThread>(caller, session.Scope, SessionRuntimeOperation.Resume,
            key, ThreadRequest(thread) with { Fence = Fence(session.Preview) }, HttpStatusCode.OK, ct);
        if (resumed != thread)
            throw new ControllerRemoteSessionException(502, "remote_thread_mismatch");
        return session;
    }

    internal async Task<RemoteInputReceipt> SendInputAsync(ClaimsPrincipal caller, string logicalId, string inputUid,
        string text, CancellationToken ct)
    {
        if (!Identifier(inputUid) || string.IsNullOrWhiteSpace(text) || text.Length > SessionRuntimeGrant.MaxPayloadBytes)
            throw new ControllerRemoteSessionException(422, "invalid_remote_input");
        var (_, session) = await ReadOwnedAsync(caller, logicalId, ct);
        var receipt = await ApplyAsync<RemoteInputReceipt>(caller, session.Scope, SessionRuntimeOperation.SendInput,
            inputUid, InputRequest(session, inputUid, text),
            HttpStatusCode.Accepted, ct);
        if (!MatchesInput(receipt, inputUid))
            throw new ControllerRemoteSessionException(502, "remote_input_receipt_mismatch");
        return receipt;
    }

    internal async Task<RemoteSessionInputStatus> InspectInputAsync(ClaimsPrincipal caller, string logicalId, string inputUid,
        CancellationToken ct)
    {
        if (!Identifier(inputUid))
            throw new ControllerRemoteSessionException(422, "invalid_remote_input");
        var (_, session) = await ReadOwnedAsync(caller, logicalId, ct);
        var status = await ApplyAsync<RemoteSessionInputStatus>(caller, session.Scope, SessionRuntimeOperation.Inspect,
            inputUid, ThreadRequest(session.Thread) with { InputUid = inputUid }, HttpStatusCode.OK, ct);
        var valid = status.State switch
        {
            "absent" => status.Receipt is null && status.Turn is null && status.ErrorCode is null && status.PayloadDigest is null,
            "pending" or "delivering" or "failed" or "delivered" or "cancelled" or "unknown" =>
                status.Receipt is { } receipt && MatchesInput(receipt, inputUid)
                && status.PayloadDigest is { Length: 64 } && status.PayloadDigest.All(char.IsAsciiHexDigitUpper)
                && (status.Turn is null || status.Turn.TurnId == receipt.TurnId),
            _ => false,
        };
        return valid ? status : throw new ControllerRemoteSessionException(502, "remote_input_receipt_mismatch");
    }

    internal async Task<ControllerRemoteInputAdvance?> AdvanceInputAsync(ClaimsPrincipal caller, ControllerRemoteSession binding,
        string expectedItemId, SessionTranscriptPipeline transcript, CancellationToken ct)
    {
        var scope = binding.Scope;
        if (NodeAccess.Subject(caller) != scope.OwnerId)
            throw new ControllerRemoteSessionException(404, "session_not_found");
        var item = await queue.ClaimControllerRemoteInputAsync(binding, expectedItemId, ct);
        if (item is null) return null;

        var text = item.Input[0].Value;
        var status = await InspectInputAsync(caller, scope.LogicalSessionId, item.MessageUid, ct);
        if (status.PayloadDigest is { } digest && digest != Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(InputRequest(binding, item.MessageUid, text), Json))))
            throw new ControllerRemoteSessionException(409, "idempotency_conflict");
        if (status.State == "absent" || status.State == "pending"
            && status.ErrorCode?.StartsWith("recovery_", StringComparison.Ordinal) == true)
        {
            // When the destination is parked, replay renews authority on its exact accepted
            // input. The guest compares the input ID and full payload digest before dispatch.
            if (!await queue.MarkControllerRemoteDispatchIssuedAsync(binding, item.Id, ct))
                return null;
            var receipt = await SendInputAsync(caller, scope.LogicalSessionId, item.MessageUid, text, ct);
            if (status.Receipt is { } inspected && receipt != inspected)
                throw new ControllerRemoteSessionException(502, "remote_input_receipt_mismatch");
        }

        var replay = await PollTranscriptAsync(caller, scope.LogicalSessionId, transcript, ct);
        if (status.State == "delivered" && status.Turn is { } turn && replay.CaughtUp
            && await queue.CompleteControllerRemoteInputAsync(item, turn, CancellationToken.None))
            return new(item.Id, "delivered", replay.Changed);

        // A parked or uncertain native turn owns the queue head. A later input cannot cross it.
        return status.State switch
        {
            "unknown" or "failed" or "cancelled" => new(item.Id, status.State, replay.Changed, status.ErrorCode),
            _ => new(item.Id, "delivering", replay.Changed),
        };
    }

    internal async Task<RemoteReplayProgress> PollTranscriptAsync(ClaimsPrincipal caller, string logicalId,
        SessionTranscriptPipeline transcript, CancellationToken ct)
    {
        var (snapshot, session) = await ReadOwnedAsync(caller, logicalId, ct);
        var scope = session.Scope;
        var thread = session.Thread;
        var binding = new RemoteTranscriptBinding(scope.NodeId, scope.RuntimeId, scope.Generation);
        var storageProvider = snapshot.Threads[0].Native.Target.ProviderId;
        var after = transcript.RemoteReplayPosition(storageProvider, logicalId, binding);
        var request = ThreadRequest(thread);
        var page = await ApplyAsync<RemoteSessionEventPage>(caller, scope, SessionRuntimeOperation.ReadEvents,
            FormattableString.Invariant($"events:{after}"), request with { AfterSeq = after, Limit = 128 },
            HttpStatusCode.OK, ct);

        var through = transcript.ImportRemoteReplay(storageProvider, logicalId, session.Placement.ProviderId,
            thread.RuntimeSessionId, binding, page);
        if (through > page.AcknowledgedThrough)
        {
            var ack = await ApplyAsync<ReplayAcknowledgement>(caller, scope, SessionRuntimeOperation.AcknowledgeEvents,
                FormattableString.Invariant($"ack:{through}"), request with { ThroughSeq = through }, HttpStatusCode.OK, ct);
            if (ack.AcknowledgedThrough < through
                || ack.AcknowledgedThrough > transcript.RemoteReplayPosition(storageProvider, logicalId, binding))
                throw new ControllerRemoteSessionException(502, "remote_transcript_receipt_mismatch");
        }
        return new(through, through > after, page.Events.Count == 0 && page.LastSequence == through);
    }

    private async Task<(SessionBindingSnapshot Snapshot, ControllerRemoteSession Session)> ReadOwnedAsync(
        ClaimsPrincipal caller, string logicalId, CancellationToken ct)
    {
        var owner = NodeAccess.Subject(caller) ?? throw new ControllerRemoteSessionException(401, "signed_owner_required");
        var binding = await queue.ReadBindingSnapshotAsync(logicalId, ct);
        if (binding?.Active.Native.Remote is not { } session || session.Scope.OwnerId != owner)
            throw new ControllerRemoteSessionException(404, "session_not_found");
        return (binding, session);
    }

    private static ControllerRemoteSession MatchStart(SessionBindingSnapshot snapshot, SessionRuntimeScope scope,
        RemoteSessionPlacement placement, RemoteSessionPlacementPreview preview)
    {
        if (snapshot.Threads[0].Native.Remote is not { } remote || remote.Scope != scope
            || remote.Placement != placement || remote.Preview != preview)
            throw new ControllerRemoteSessionException(409, "remote_start_conflict");
        return remote;
    }

    private async Task<RemoteSessionPlacementPreview> InspectAsync(ClaimsPrincipal caller, SessionRuntimeScope scope,
        string key, RemoteSessionPlacement placement, CancellationToken ct)
    {
        var preview = await ApplyAsync<RemoteSessionPlacementPreview>(caller, scope, SessionRuntimeOperation.Inspect, key,
            new(placement.Revision, placement.ProviderInstanceId), HttpStatusCode.OK, ct);
        if (preview.NodeId != placement.NodeId || preview.RuntimeId != placement.RuntimeId
            || preview.Workspace != placement.Workspace || preview.Revision != placement.Revision
            || preview.ProviderId != placement.ProviderId || preview.ProviderInstanceId != placement.ProviderInstanceId
            || !SessionAccessLevels.IsKnown(preview.AccessLevel) || !SessionAccessLevels.IsKnown(preview.MaxAccessLevel)
            || SessionAccessLevels.Rank(preview.AccessLevel) > SessionAccessLevels.Rank(preview.MaxAccessLevel))
            throw new ControllerRemoteSessionException(422, "remote_placement_mismatch");
        return preview;
    }

    private async Task<T> ApplyAsync<T>(ClaimsPrincipal caller, SessionRuntimeScope scope, SessionRuntimeOperation operation,
        string key, RemoteSessionLifecycleRequest request, HttpStatusCode expected, CancellationToken ct) where T : class
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (payload.Length > SessionRuntimeGrant.MaxPayloadBytes)
            throw new ControllerRemoteSessionException(422, "remote_request_too_large");
        var (status, body) = await send(caller, scope, operation, key, payload, ct);
        if (status != expected)
        {
            if (operation == SessionRuntimeOperation.ReadEvents && status == HttpStatusCode.Conflict
                && Read<RemoteSessionEventPage>(body) is { Gap: true })
                throw new ControllerRemoteSessionException(409, "remote_transcript_gap");
            if (Read<RuntimeError>(body) is { Code: { Length: > 0 } code } && (int)status is >= 400 and <= 599)
                throw new ControllerRemoteSessionException((int)status, code);
            throw new ControllerRemoteSessionException(502, "invalid_remote_runtime_reply");
        }
        return Read<T>(body) ?? throw new ControllerRemoteSessionException(502, "invalid_remote_runtime_reply");
    }

    private static T? Read<T>(byte[] bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    private static SessionRuntimeScope GetScope(ClaimsPrincipal caller, string key, RemoteSessionPlacement placement)
    {
        var owner = NodeAccess.Subject(caller) ?? throw new ControllerRemoteSessionException(401, "signed_owner_required");
        if (!Identifier(key) || !Identifier(placement.NodeId) || !ForwardOpen.IsRuntimeId(placement.RuntimeId)
            || !Identifier(placement.Workspace) || !Identifier(placement.Revision)
            || !Identifier(placement.ProviderId) || !Identifier(placement.ProviderInstanceId)
            || placement.Model is not null && !Identifier(placement.Model)
            || placement.Effort is not null && !Identifier(placement.Effort))
            throw new ControllerRemoteSessionException(422, "invalid_remote_placement");

        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { owner, key }, Json));
        return new(placement.NodeId, placement.RuntimeId, LogicalIdPrefix + Convert.ToHexString(digest).ToLowerInvariant(), 0,
            owner, placement.Workspace);
    }

    private static RemoteSessionLifecycleRequest ThreadRequest(RemoteSessionLifecycleThread thread)
        => new(thread.Revision, thread.ProviderInstanceId, RuntimeSessionId: thread.RuntimeSessionId,
            NativeThreadId: thread.NativeThreadId);

    private static RemoteSessionLifecycleRequest InputRequest(ControllerRemoteSession session, string uid, string text)
        => ThreadRequest(session.Thread) with { InputUid = uid, Text = text, Fence = Fence(session.Preview) };

    private static bool MatchesInput(RemoteInputReceipt receipt, string inputUid)
        => receipt.InputUid == inputUid && Identifier(receipt.QueueItemId) && Identifier(receipt.TurnId);

    private static RemoteSessionStartFence Fence(RemoteSessionPlacementPreview preview)
        => new(preview.ProviderId, preview.AccessLevel, preview.MaxAccessLevel);

    internal static bool Identifier(string? value) =>
        value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);

    private sealed record ReplayAcknowledgement(long AcknowledgedThrough);
    private sealed record RuntimeError(string Code);
}
