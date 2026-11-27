using System.Text.Json;

namespace RedCompute.PluginSdk;

public sealed record OwnerDraftAsset(string AssetId, string Description);

public sealed record OwnerDraftArguments(
    Guid BindingId, long BindingGeneration, string Content, string? DeliveryContent, OwnerDraftAsset[]? OutputAssets);

public sealed record NativeOwnerDraftCall(
    string ConnectionId, string ThreadId, string TurnId, string CallId, OwnerDraftArguments Draft);

public sealed record OwnerDraftResult(string Status, string? MessageUid = null);

/// <summary>A single queue delivery may confer draft-only authority on its native root turn.</summary>
public sealed class OwnerDraftDelivery(
    string sessionId, string messageUid,
    Func<NativeOwnerDraftCall, Func<bool>, CancellationToken, Task<OwnerDraftResult>> admit)
{
    private static readonly AsyncLocal<OwnerDraftDelivery?> Current = new();
    private readonly TaskCompletionSource<bool> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static OwnerDraftDelivery? For(string sessionId, string? messageUid)
        => Current.Value is { } current && current.SessionId == sessionId && current.MessageUid == messageUid
            ? current : null;

    private string SessionId { get; } = sessionId;
    private string MessageUid { get; } = messageUid;

    public static IDisposable Push(OwnerDraftDelivery delivery)
    {
        var previous = Current.Value;
        Current.Value = delivery;
        return new Restore(previous);
    }

    public void Settle(bool delivered) => _delivered.TrySetResult(delivered);

    public async Task<OwnerDraftResult> InvokeAsync(
        NativeOwnerDraftCall call, Func<bool> isCurrent, CancellationToken ct)
    {
        if (!await _delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct) || !isCurrent())
            return new("owner_input_not_delivered_or_retired");
        return await admit(call, isCurrent, ct);
    }

    private sealed class Restore(OwnerDraftDelivery? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
