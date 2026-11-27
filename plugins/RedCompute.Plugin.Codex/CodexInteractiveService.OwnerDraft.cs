using System.Text.Json;
using System.Text.Json.Serialization;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public sealed partial class CodexInteractiveService
{
    internal bool EnableOwnerDraftTool { get; init; }
    private const string OwnerDraftToolName = "leaf_submit_outreach_draft";
    private static readonly JsonSerializerOptions DraftWire = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class OwnerDraftTurn(OwnerDraftDelivery delivery)
    {
        public OwnerDraftDelivery Delivery { get; } = delivery;
        public string? NativeTurnId;
        public CancellationTokenSource Retired { get; } = new();
    }

    private static void RetireOwnerDraft(ManagedSession session)
        => Interlocked.Exchange(ref session.OwnerDraft, null)?.Retired.Cancel();

    private static async Task HandleOwnerDraftAsync(
        ManagedSession session, CodexAppServerConnection conn, JsonElement rpcId, JsonElement parameters,
        Func<bool> connectionIsCurrent)
    {
        var turn = Volatile.Read(ref session.OwnerDraft);
        var nativeThread = Text(parameters, "threadId");
        var nativeTurn = Text(parameters, "turnId");
        var nativeCall = Text(parameters, "callId");
        OwnerDraftResult outcome;
        if (!string.Equals(Text(parameters, "tool"), OwnerDraftToolName, StringComparison.Ordinal)
            || (parameters.TryGetProperty("namespace", out var ns) && ns.ValueKind != JsonValueKind.Null)
            || nativeThread is null || nativeTurn is null || nativeCall is null
            || !parameters.TryGetProperty("arguments", out var args) || !TryReadDraft(args, out var draft))
        {
            outcome = new("invalid_owner_draft_call");
        }
        else if (turn is null || !connectionIsCurrent() || nativeThread != session.Info.ThreadId)
        {
            outcome = new("owner_turn_not_attributed");
        }
        else
        {
            bool IsCurrent() => connectionIsCurrent() && ReferenceEquals(Volatile.Read(ref session.OwnerDraft), turn)
                && session.Info.ThreadId == nativeThread && session.ActiveTurnId == nativeTurn
                && Volatile.Read(ref turn.NativeTurnId) == nativeTurn && !session.InterruptRequested;
            try
            {
                outcome = await turn.Delivery.InvokeAsync(
                    new NativeOwnerDraftCall(conn.OwnerDraftConnectionId, nativeThread, nativeTurn, nativeCall, draft!),
                    IsCurrent, turn.Retired.Token);
            }
            catch (OperationCanceledException) when (turn.Retired.IsCancellationRequested)
            {
                outcome = new("owner_turn_retired");
            }
            catch (TimeoutException)
            {
                outcome = new("owner_input_not_delivered_or_retired");
            }
            catch (Exception)
            {
                outcome = new("owner_draft_recipient_unavailable");
            }
        }
        await conn.RespondAsync(rpcId, new
        {
            success = outcome.Status is "held" or "clear",
            contentItems = new[] { new { type = "inputText", text = JsonSerializer.Serialize(outcome, DraftWire) } }
        });
    }

    private static string? Text(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 256 } text
            ? text : null;

    private static bool TryReadDraft(JsonElement args, out OwnerDraftArguments? draft)
    {
        draft = null;
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Any(p => p.Name is not ("bindingId" or "bindingGeneration" or "content"
                or "deliveryContent" or "outputAssets"))
            || args.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return false;
        try { draft = args.Deserialize<OwnerDraftArguments>(DraftWire); }
        catch (JsonException) { return false; }
        if (draft is not { Content.Length: > 0 and <= 16000 } || string.IsNullOrWhiteSpace(draft.Content)
            || draft.BindingId == Guid.Empty || draft.BindingGeneration <= 0 || draft.DeliveryContent is { Length: > 16000 }
            || draft.OutputAssets is { Length: > 10 }
            || draft.OutputAssets?.Any(asset => asset is not
                { AssetId.Length: > 0 and <= 120, Description.Length: <= 400 }) == true)
            return false;
        draft = draft with { OutputAssets = draft.OutputAssets ?? [] };
        return true;
    }

    private static readonly JsonElement OwnerDraftToolSpec = JsonSerializer.SerializeToElement(new
    {
        name = OwnerDraftToolName,
        description = "Submit one Discord outreach draft for private review. This tool never publishes or releases.",
        inputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","additionalProperties":false,"required":["bindingId","bindingGeneration","content"],
             "properties":{"bindingId":{"type":"string"},"bindingGeneration":{"type":"integer"},"content":{"type":"string"},
             "deliveryContent":{"type":"string"},"outputAssets":{"type":"array","items":{"type":"object",
             "additionalProperties":false,"required":["assetId","description"],"properties":{
             "assetId":{"type":"string"},"description":{"type":"string"}}}}}}
            """),
    });
}
