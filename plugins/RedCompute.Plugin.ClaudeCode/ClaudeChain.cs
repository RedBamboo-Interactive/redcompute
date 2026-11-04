using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.ClaudeCode;

/// <summary>
/// A rollback waiting to take effect. A CLI launched with these flags truncates only in memory: until
/// a new chain entry lands on the fork point, a plain <c>--resume</c> brings every dropped turn back
/// (checked against 2.1.318). So the target stays durable until the chain on disk excludes the drops.
/// </summary>
internal sealed record ClaudeForkTarget(
    string RollbackId,
    /// <summary>The kept turn's last chain entry. Null when the first message was dropped: nothing is kept.</summary>
    string? ForkAtUuid,
    /// <summary>
    /// Set only when exactly one prompt is dropped. <c>--resume-drops-turn</c> refuses a range holding
    /// any other prompt, so a multi-turn rollback passes <c>--resume-session-at</c> alone, after
    /// <see cref="ClaudeChain.ResolveRollback"/> has made the same check against the canonical turns.
    /// </summary>
    string? DropsTurnUuid,
    IReadOnlyList<string> DroppedPromptUuids)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static ClaudeForkTarget? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ClaudeForkTarget>(json); }
        catch (JsonException) { return null; }
    }
}

internal sealed record ClaudeChainEntry(
    string Uuid,
    string? ParentUuid,
    string Type,
    string? Subtype,
    bool IsMeta,
    bool IsSidechain,
    bool IsCompactSummary,
    bool HasToolResult,
    string? Text)
{
    /// <summary>A person's prompt, as opposed to tool results, reminders and refusal companions.</summary>
    public bool IsPrompt => Type == "user" && !IsMeta && !IsSidechain && !IsCompactSummary && !HasToolResult;
}

internal sealed record ClaudeRollbackPlan(
    string? ForkAtUuid,
    string? DropsTurnUuid,
    IReadOnlyList<string> DroppedPromptUuids);

/// <summary>
/// The conversation as the CLI will load it: the parent chain from the transcript's last entry.
/// Entries a fork left behind stay in the file, so they are absent from the chain but not the file;
/// a grep cannot tell them apart.
/// </summary>
internal sealed class ClaudeChain
{
    private readonly Dictionary<string, ClaudeChainEntry> _entries;

    public IReadOnlyList<ClaudeChainEntry> Live { get; }

    private ClaudeChain(Dictionary<string, ClaudeChainEntry> entries, IReadOnlyList<ClaudeChainEntry> live)
    {
        _entries = entries;
        Live = live;
    }

    public static ClaudeChain Load(string transcriptPath)
    {
        var entries = new Dictionary<string, ClaudeChainEntry>(StringComparer.Ordinal);
        ClaudeChainEntry? last = null;
        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            // Bookkeeping lines (request blobs among them) have no chain position and can be huge.
            if (!line.Contains("\"parentUuid\"", StringComparison.Ordinal)) continue;
            if (ParseEntry(line) is not { } entry) continue;
            entries[entry.Uuid] = entry;
            if (!entry.IsSidechain) last = entry;
        }

        var live = new List<ClaudeChainEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = last; current is not null && seen.Add(current.Uuid);
             current = current.ParentUuid is { } parent ? entries.GetValueOrDefault(parent) : null)
            live.Add(current);
        live.Reverse();
        return new ClaudeChain(entries, live);
    }

    private static ClaudeChainEntry? ParseEntry(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var uuid = String(root, "uuid");
            var type = String(root, "type");
            if (uuid is null || type is null) return null;

            string? text = null;
            var hasToolResult = false;
            if (type == "user" && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString();
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var part in content.EnumerateArray())
                    {
                        var partType = part.ValueKind == JsonValueKind.Object ? String(part, "type") : null;
                        if (partType == "tool_result") hasToolResult = true;
                        else if (partType == "text" && String(part, "text") is { } partText) parts.Add(partText);
                    }
                    text = string.Join("\n", parts);
                }
            }

            return new ClaudeChainEntry(uuid, String(root, "parentUuid"), type, String(root, "subtype"),
                True(root, "isMeta"), True(root, "isSidechain"), True(root, "isCompactSummary"),
                hasToolResult, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool True(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// True when the chain already runs through the fork and past every dropped prompt: a later
    /// entry landed on the fork point, so the target has done its work.
    /// </summary>
    public bool HasConsumed(ClaudeForkTarget target)
    {
        var onChain = Live.Select(entry => entry.Uuid).ToHashSet(StringComparer.Ordinal);
        return !target.DroppedPromptUuids.Any(onChain.Contains)
            && (target.ForkAtUuid is null || onChain.Contains(target.ForkAtUuid));
    }

    /// <summary>
    /// Finds where to fork so the target and everything after it leave the conversation.
    /// </summary>
    /// <param name="promptUuids">Prompt uuid per canonical message uid, for turns sent since it was recorded.</param>
    public (ClaudeRollbackPlan? Plan, SessionRollbackResult? Refusal) ResolveRollback(
        SessionRollbackRequest request, IReadOnlyDictionary<string, string> promptUuids)
    {
        var prompts = Live.Where(entry => entry.IsPrompt).ToList();
        var targetIndex = FindPrompt(prompts, request, promptUuids);
        if (targetIndex < 0)
        {
            // A compaction summary replaces what came before it, so the chain stops there.
            var compacted = Live.Any(entry => entry.IsCompactSummary || entry.Subtype == "compact_boundary");
            return compacted
                ? (null, SessionRollbackResult.Refused(SessionRollbackRefusals.TargetBeforeCompaction,
                    "This message is before a compaction: the summary already absorbed it, so it cannot be removed"))
                : (null, SessionRollbackResult.Refused(SessionRollbackRefusals.TargetNotMapped,
                    "Could not find this message in the provider's conversation"));
        }

        var dropped = prompts.Skip(targetIndex).Select(prompt => prompt.Uuid).ToList();
        // The same check --resume-drops-turn makes, which it cannot make across several turns:
        // nothing the caller still sees as kept may fall in the discarded range.
        var kept = request.KeptUserTurns
            .Select(turn => promptUuids.GetValueOrDefault(turn.MessageUid))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (dropped.Any(kept.Contains))
            return (null, SessionRollbackResult.Refused(SessionRollbackRefusals.TargetNotMapped,
                "The provider's conversation does not match the transcript at this message"));

        return (new ClaudeRollbackPlan(
            prompts[targetIndex].ParentUuid,
            dropped.Count == 1 ? dropped[0] : null,
            dropped), null);
    }

    private static int FindPrompt(
        List<ClaudeChainEntry> prompts,
        SessionRollbackRequest request,
        IReadOnlyDictionary<string, string> promptUuids)
    {
        if (promptUuids.TryGetValue(request.TargetMessageUid, out var recorded))
            return prompts.FindIndex(prompt => prompt.Uuid == recorded);

        // Older turns were written with no uuid of ours. Walk both lists from the newest and require
        // every pair to agree, so one unmatched prompt refuses instead of forking at the wrong place.
        var canonical = request.KeptUserTurns.Concat(request.DroppedUserTurns).ToList();
        var chainIndex = prompts.Count - 1;
        for (var i = canonical.Count - 1; i >= 0 && chainIndex >= 0; i--, chainIndex--)
        {
            var turn = canonical[i];
            var known = promptUuids.GetValueOrDefault(turn.MessageUid);
            var matches = known is not null
                ? prompts[chainIndex].Uuid == known
                : TextMatches(prompts[chainIndex].Text, turn.Content);
            if (!matches) return -1;
            if (turn.MessageUid == request.TargetMessageUid) return chainIndex;
        }
        return -1;
    }

    /// <summary>
    /// The transcript keeps what the person wrote; the provider may have received it after an envelope
    /// of context. So the prompt must be that text, whitespace normalized, with nothing before it but a
    /// closed envelope. Finding it anywhere is not enough: an envelope quotes the conversation, usually
    /// the previous turn, so a walk one prompt out of step would agree with itself all the way down.
    /// </summary>
    internal static bool TextMatches(string? promptText, string? canonicalText)
    {
        var prompt = Normalize(promptText);
        var canonical = Normalize(canonicalText);
        if (canonical.Length == 0 || !prompt.EndsWith(canonical, StringComparison.Ordinal)) return false;
        var before = prompt[..^canonical.Length].TrimEnd();
        return before.Length == 0 || before[^1] == '>';
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
