using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.NodeLink.Forward;

namespace RedCompute.Node.Runtimes;

/// <summary>What a route does with the runtime's answer on its way to the server.</summary>
public enum ResponseRule
{
    /// <summary>Streamed back as it is.</summary>
    Pass,

    /// <summary>
    /// <c>GET /queue</c> (review item 5): rewritten on the node to <c>{queue_running, queue_pending}</c> with each item
    /// <c>[number, id]</c> for a prompt the server submitted here and <c>[number, "other"]</c> for any other. The owner's
    /// own graphs, extra data and outputs, and their prompt ids, never leave the node.
    /// </summary>
    QueueRedacted,
}

/// <summary>The node's rewrites of a runtime's answer (<see cref="ResponseRule"/>).</summary>
internal static class RuntimeAnswers
{
    /// <summary>The most of a <c>/queue</c> answer the node reads to redact it.</summary>
    public const long MaxQueueBytes = 32 * RuntimeRoutes.MiB;

    /// <summary>What a prompt the server did not submit is in a redacted queue.</summary>
    public const string Other = "other";

    private static readonly string[] Queues = ["queue_running", "queue_pending"];

    /// <summary>
    /// ComfyUI's <c>/queue</c> answer (<c>{"queue_running": [[number, prompt_id, prompt, extra_data, outputs], …],
    /// "queue_pending": […]}</c>) with only each item's number, and its id when <paramref name="submitted"/> says the server
    /// submitted it. Every other member, and every other element of an item, is dropped. A queue that is not an array is
    /// left out, as a missing one is. Throws <c>upstream_failed</c> for an answer that is not a JSON object.
    /// </summary>
    public static byte[] RedactQueue(ReadOnlySpan<byte> answer, Func<string, bool> submitted)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(answer.ToArray());
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is not JsonObject queue)
            throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, "The runtime's /queue answer is not a JSON object.");
        var redacted = new JsonObject();
        foreach (var name in Queues)
        {
            if (queue[name] is not JsonArray items) continue;
            var kept = new JsonArray();
            foreach (var item in items)
            {
                var number = item is JsonArray { Count: > 0 } entry && entry[0] is JsonValue value
                             && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var n)
                    ? n
                    : 0;
                var id = item is JsonArray { Count: > 1 } withId && withId[1] is JsonValue idValue
                         && idValue.GetValueKind() == JsonValueKind.String && idValue.GetValue<string>() is { } promptId
                         && RuntimeRoutes.IsId(promptId) && submitted(promptId)
                    ? promptId
                    : Other;
                kept.Add(new JsonArray(number, id));
            }
            redacted[name] = kept;
        }
        return JsonSerializer.SerializeToUtf8Bytes(redacted);
    }

    /// <summary>A whole answer, at most <paramref name="cap"/> bytes, or <c>upstream_failed</c> past it.</summary>
    public static async Task<byte[]> ReadCappedAsync(Stream body, long cap, string route, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
                throw new ForwardTargetException(ForwardErrorCodes.UpstreamFailed, $"The runtime's {route} answer is over {cap / RuntimeRoutes.MiB} MiB.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
