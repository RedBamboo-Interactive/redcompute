using System.Text;
using System.Text.Json;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Translates codex app-server notifications into the unified stream events the suite renders.
///
/// The frontend is provider-agnostic: it switches on event type
/// (text | thinking | tool_use | tool_result | status | error) and picks a tool renderer by
/// <c>ToolName</c> *string*. So the whole job of this class is to speak the vocabulary those
/// renderers already know — <c>Bash</c>, <c>Edit</c>, <c>Write</c>, <c>Read</c>, <c>WebSearch</c>,
/// <c>Agent</c> — with the input keys they expect. Passing Codex's own item names through would be
/// correct and would render as anonymous JSON blobs.
///
/// Pairing rule: the UI matches a tool_use with the *first following* tool_result and stops at the
/// next tool_use. So a use and its result must be emitted adjacently, never batched.
/// </summary>
public static class CodexEventMapper
{
    /// <summary>
    /// Maps one notification. <paramref name="method"/> is the JSON-RPC method
    /// (e.g. "item/completed"); <paramref name="params"/> its params object.
    /// Returns zero or more events, in emission order.
    /// </summary>
    public static List<CodexStreamEvent> Map(
        string method,
        JsonElement @params,
        IDictionary<string, string>? messagePhases = null)
    {
        // Stateless exec emits the same vocabulary with dots ("item.completed") rather than
        // slashes. Normalise so one mapper serves both transports.
        var m = method.Replace('.', '/');

        string? completedAgentMessageId = null;
        if (messagePhases is not null
            && m is "item/started" or "item/completed"
            && TryAgentMessageIdentity(@params, out var itemId, out var phase)
            && itemId is { Length: > 0 })
        {
            if (phase is { Length: > 0 }) messagePhases[itemId] = phase;
            if (m == "item/completed") completedAgentMessageId = itemId;
        }

        var events = m switch
        {
            "item/started" => MapItem(@params, started: true),
            "item/completed" => MapItem(@params, started: false),

            // Deltas carry the id of the item they belong to, which is what lets the caller tell
            // that the matching item/completed has already been delivered piecewise.
            "item/agentMessage/delta" => Single("text", Text(@params), partial: true, Str(@params, "itemId")),
            "item/reasoning/textDelta" => Single("thinking", Text(@params), partial: true, Str(@params, "itemId")),
            "item/reasoning/summaryTextDelta" => Single("thinking", Text(@params), partial: true, Str(@params, "itemId")),

            // A reasoning item can hold several summary parts, each its own headline. They
            // arrive as plain deltas with no separator, and the client concatenates partials
            // into one block — so without this they render as one run-on line:
            // "Planning personal reflectionAnalyzing recurring patternsClarifying tone".
            // Index 0 opens the block and needs no leading break.
            "item/reasoning/summaryPartAdded" => Int(@params, "summaryIndex") is > 0
                ? Single("thinking", "\n\n", partial: true, Str(@params, "itemId"))
                : [],
            "item/plan/delta" => Single("text", Text(@params), partial: true, Str(@params, "itemId")),

            "item/commandExecution/outputDelta" => Single("tool_result", Text(@params), partial: true, Str(@params, "itemId")),

            "turn/completed" => [],
            "turn/started" => [],

            _ => [],
        };

        if (messagePhases is not null)
        {
            foreach (var evt in events)
                if (evt.Phase is null
                    && evt.MessageId is { Length: > 0 } messageId
                    && messagePhases.TryGetValue(messageId, out var knownPhase))
                    evt.Phase = knownPhase;

            if (completedAgentMessageId is not null)
                messagePhases.Remove(completedAgentMessageId);
        }

        return events;
    }

    /// <summary>
    /// The two transports disagree on casing: <c>codex exec --json</c> emits snake_case item types
    /// (<c>agent_message</c>, <c>command_execution</c>) while the app-server emits camelCase
    /// (<c>agentMessage</c>, <c>commandExecution</c>). Same vocabulary, different shell — normalise
    /// so one set of cases serves both. A switch that only knows one form silently matches nothing.
    /// </summary>
    public static string? NormaliseItemType(string? type)
    {
        if (string.IsNullOrEmpty(type) || !type.Contains('_')) return type;

        var parts = type.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return type;

        var sb = new StringBuilder(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i][1..]);
        return sb.ToString();
    }

    private static List<CodexStreamEvent> MapItem(JsonElement @params, bool started)
    {
        if (!@params.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return [];

        var itemType = NormaliseItemType(Str(item, "type"));
        var id = Str(item, "id");

        return itemType switch
        {
            "agentMessage" => Single(
                "text", Str(item, "text"), partial: started, messageId: id, phase: MessagePhase(item)),
            "plan" => started ? [] : Single("text", Str(item, "text"), partial: false, messageId: id),
            "reasoning" => Single("thinking", Reasoning(item), partial: started, messageId: id),

            "commandExecution" => Command(item, started, id),
            "fileChange" => FileChange(item, started, id),
            "webSearch" => WebSearch(item, started, id),
            "mcpToolCall" => McpToolCall(item, started, id),
            "dynamicToolCall" => DynamicToolCall(item, started, id),
            "subAgentActivity" => SubAgent(item, started, id),
            "collabAgentToolCall" => CollabAgent(item, started, id),
            "imageView" => started
                ? Tool("Read", new { file_path = Str(item, "path") }, id)
                : [],
            "contextCompaction" => started ? [] : Single("status", "context compacted", partial: false, id),

            // userMessage is echoed back by the server; we already recorded it when it was sent.
            "userMessage" or "hookPrompt" => [],
            _ => [],
        };
    }

    // ---- item mappers -----------------------------------------------------------------------

    /// <summary>
    /// commandExecution → Bash. The UI's Bash renderer reads {command, description, timeout},
    /// so cwd rides along as the description, which is the field it displays as a subtitle.
    /// </summary>
    private static List<CodexStreamEvent> Command(JsonElement item, bool started, string? id)
    {
        // item/started already carries command and cwd, so the card can appear immediately and
        // fill in later. Emitting the tool_use again on completion would produce two uses for one
        // command, and the UI pairs a use with the *first following* result — so the first card
        // would render permanently empty.
        if (started)
            return Tool("Bash", new { command = DisplayCommand(item), description = Str(item, "cwd") }, id);

        var events = new List<CodexStreamEvent>();
        var output = Str(item, "aggregatedOutput");
        var exitCode = Int(item, "exitCode");
        var status = Str(item, "status");

        // A failed command with no output still needs to say so, or the card renders empty.
        var result = output;
        if (string.IsNullOrEmpty(result))
            result = status is "failed" or "declined"
                ? $"({status}{(exitCode is not null ? $", exit {exitCode}" : "")})"
                : exitCode is not null and not 0 ? $"(exit {exitCode})" : "";

        events.Add(new CodexStreamEvent
        {
            Type = "tool_result", ToolResult = result, Content = result, MessageId = id,
        });
        return events;
    }

    /// <summary>
    /// fileChange carries a list of {path, kind, diff} where diff is a unified diff. The UI's Edit
    /// renderer wants old_string/new_string, so the diff is split into its - and + sides; the raw
    /// patch is kept too, so nothing is lost if the renderer prefers it.
    /// </summary>
    private static List<CodexStreamEvent> FileChange(JsonElement item, bool started, string? id)
    {
        // The full diff is present on item/started, so the card is emitted there. On completion we
        // only need to say something if it did *not* work — a successful edit card is complete on
        // its own, and a redundant tool_use would break use→result pairing.
        if (!started)
        {
            var status = Str(item, "status");
            return status is "failed" or "declined"
                ? [new CodexStreamEvent { Type = "tool_result", ToolResult = $"({status})", Content = $"({status})", MessageId = id }]
                : [];
        }

        if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return [];

        var events = new List<CodexStreamEvent>();
        foreach (var change in changes.EnumerateArray())
        {
            var path = Str(change, "path");
            var diff = Str(change, "diff") ?? "";
            var kind = change.TryGetProperty("kind", out var k) ? Str(k, "type") : null;

            var (removed, added) = SplitUnifiedDiff(diff);

            var toolName = kind switch
            {
                "add" => "Write",
                "delete" => "Delete",
                _ => "Edit",
            };

            object input = kind switch
            {
                "add" => new { file_path = path, content = added, diff },
                "delete" => new { file_path = path, diff },
                _ => new { file_path = path, old_string = removed, new_string = added, diff },
            };

            events.AddRange(Tool(toolName, input, id));
        }
        return events;
    }

    private static List<CodexStreamEvent> WebSearch(JsonElement item, bool started, string? id)
    {
        if (started) return Tool("WebSearch", new { query = Str(item, "query") }, id);

        var results = item.TryGetProperty("results", out var r) && r.ValueKind != JsonValueKind.Null
            ? r.ToString()
            : null;
        return [new CodexStreamEvent { Type = "tool_result", ToolResult = results, Content = results, MessageId = id }];
    }

    private static List<CodexStreamEvent> McpToolCall(JsonElement item, bool started, string? id)
    {
        if (started)
            return Tool(Str(item, "tool") ?? "mcp",
                item.TryGetProperty("arguments", out var a) ? (object?)a.Clone() : null, id);

        var error = item.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null ? e.ToString() : null;
        var result = error ?? (item.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.ToString() : null);
        return [new CodexStreamEvent { Type = "tool_result", ToolResult = result, Content = result, MessageId = id }];
    }

    private static List<CodexStreamEvent> DynamicToolCall(JsonElement item, bool started, string? id)
    {
        if (started)
            return Tool(Str(item, "tool") ?? "tool",
                item.TryGetProperty("arguments", out var a) ? (object?)a.Clone() : null, id);

        var result = item.TryGetProperty("contentItems", out var c) && c.ValueKind != JsonValueKind.Null
            ? c.ToString()
            : null;
        return [new CodexStreamEvent { Type = "tool_result", ToolResult = result, Content = result, MessageId = id }];
    }

    private static List<CodexStreamEvent> SubAgent(JsonElement item, bool started, string? id) =>
        started
            ? Tool("Agent", new
            {
                description = Str(item, "kind"),
                subagent_type = Str(item, "agentPath"),
                agent_thread_id = Str(item, "agentThreadId"),
            }, id)
            : [];

    private static List<CodexStreamEvent> CollabAgent(JsonElement item, bool started, string? id) =>
        started
            ? Tool("Agent", new
            {
                description = Str(item, "tool"),
                prompt = Str(item, "prompt"),
                model = Str(item, "model"),
                effort = Str(item, "reasoningEffort"),
            }, id)
            : [];

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Codex wraps every shell call in the platform shell, so the literal `command` is an
    /// unreadable double-escaped invocation:
    /// <c>"C:\WINDOWS\...\powershell.exe" -Command "Get-Content -LiteralPath .\calc.py"</c>.
    /// It also ships its own parse of what that actually does in <c>commandActions</c>, which is
    /// what a person wants on the card. Prefer the parse; fall back to the raw string when Codex
    /// could not parse it (in which case the wrapper is genuinely the best available answer).
    /// </summary>
    private static string? DisplayCommand(JsonElement item)
    {
        if (item.TryGetProperty("commandActions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            var parts = actions.EnumerateArray()
                .Select(a => Str(a, "command"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            if (parts.Count > 0) return string.Join(" | ", parts);
        }
        return Str(item, "command");
    }

    /// <summary>
    /// Reasoning items carry both a short `summary` and the fuller `content`, each an array of
    /// strings. Prefer content; fall back to the summary when the model only produced one.
    /// </summary>
    private static string? Reasoning(JsonElement item)
    {
        var content = JoinStrings(item, "content");
        return !string.IsNullOrWhiteSpace(content) ? content : JoinStrings(item, "summary");
    }

    private static string? JoinStrings(JsonElement item, string prop)
    {
        if (!item.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var parts = arr.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "text"))
            .Where(s => !string.IsNullOrEmpty(s));
        // Blank line between parts, not a single newline: these render as markdown, which
        // folds a lone newline into the previous line and runs the headlines together.
        var joined = string.Join("\n\n", parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// Splits a unified diff into the before and after text. Hunk headers are dropped; context
    /// lines belong to both sides. Best-effort by design — the raw diff is always kept alongside.
    /// </summary>
    public static (string Removed, string Added) SplitUnifiedDiff(string diff)
    {
        if (string.IsNullOrEmpty(diff)) return ("", "");

        var removed = new StringBuilder();
        var added = new StringBuilder();

        foreach (var line in diff.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("@@") || l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("diff "))
                continue;

            // Always '\n', never AppendLine: Environment.NewLine would rewrite every file's line
            // endings to CRLF on Windows, so the reconstructed text would stop matching the file
            // it came from.
            if (l.StartsWith('+')) added.Append(l[1..]).Append('\n');
            else if (l.StartsWith('-')) removed.Append(l[1..]).Append('\n');
            else
            {
                var ctx = l.StartsWith(' ') ? l[1..] : l;
                removed.Append(ctx).Append('\n');
                added.Append(ctx).Append('\n');
            }
        }

        return (removed.ToString().TrimEnd('\n'), added.ToString().TrimEnd('\n'));
    }

    private static List<CodexStreamEvent> Tool(string name, object? input, string? id) =>
    [
        new CodexStreamEvent { Type = "tool_use", ToolName = name, ToolInput = input, MessageId = id },
    ];

    private static List<CodexStreamEvent> Single(
        string type, string? content, bool partial, string? messageId, string? phase = null) =>
        string.IsNullOrEmpty(content)
            ? []
            : [new CodexStreamEvent
            {
                Type = type,
                Content = content,
                IsPartial = partial,
                MessageId = messageId,
                Phase = phase,
            }];

    private static bool TryAgentMessageIdentity(
        JsonElement @params,
        out string? itemId,
        out string? phase)
    {
        itemId = null;
        phase = null;
        if (!@params.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return false;
        if (NormaliseItemType(Str(item, "type")) != "agentMessage") return false;

        itemId = Str(item, "id");
        phase = MessagePhase(item);
        return true;
    }

    private static string? MessagePhase(JsonElement item)
    {
        var phase = Str(item, "phase");
        return phase is "commentary" or "final_answer" ? phase : null;
    }

    /// <summary>Delta notifications carry their payload as `delta`, or occasionally `text`.</summary>
    private static string? Text(JsonElement p) => Str(p, "delta") ?? Str(p, "text");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
