using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services.SessionTitles;

/// <summary>
/// The one implementation of "Update title": the prompt built from a session's visible
/// conversation, the fast-tier instruction and the cleaning of what comes back. Moved from Nova
/// (<c>DiscussionTitleGeneration</c>) unchanged, so Nova's discussions and Code's sessions title
/// the same way; only the assistant's label is a parameter (Nova passes "Nova").
/// </summary>
public static partial class SessionTitleGeneration
{
    public const string QualityTier = "fast";
    public const int MaxTokens = 48;
    public const int TranscriptTail = 500;
    public const string DefaultAssistantLabel = "Assistant";

    public const string SystemPrompt =
        "Write a concise, specific title for this conversation in at most six words. Output only the title, with no label, quotes, or ending punctuation.";

    private const int LeadingMessages = 4;
    private const int TrailingMessages = 16;
    private const int MaxMessageCharacters = 600;
    public const int MaxTitleCharacters = 70;
    private const string NoReplyMarker = "<nova-no-reply/>";

    /// <summary>
    /// The user message for the fast model, or null when the transcript has no visible
    /// conversation yet. Visible means settled user and assistant text: no thinking, status, tool
    /// records, commentary or no-reply markers; injected context is stripped from user text.
    /// </summary>
    public static string? BuildPrompt(string? currentTitle, IReadOnlyList<UnifiedMessageRecord> messages,
        string? assistantLabel = null)
    {
        var visible = Collapse(messages)
            .Where(message => message.EventType == "text"
                && message.Role is "user" or "assistant"
                && !(message.Role == "assistant"
                    && string.Equals(message.Phase, "commentary", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(message.Content)
                && !IsNoReplyMarker(message.Content))
            .ToList();
        if (visible.Count == 0) return null;

        IEnumerable<Collapsed> selected = visible.Count <= LeadingMessages + TrailingMessages
            ? visible
            : visible.Take(LeadingMessages).Concat(visible.TakeLast(TrailingMessages));

        var label = string.IsNullOrWhiteSpace(assistantLabel) ? DefaultAssistantLabel : assistantLabel.Trim();
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(currentTitle))
            prompt.AppendLine($"Current title: {Normalize(currentTitle, MaxTitleCharacters)}");
        prompt.AppendLine("Conversation:");
        foreach (var message in selected)
        {
            prompt.Append(message.Role == "user" ? "User" : label).Append(": ")
                .AppendLine(Normalize(message.Content!, MaxMessageCharacters));
        }
        return prompt.ToString().Trim();
    }

    /// <summary>The model's last line without label, quotes or ending punctuation; null unless 1..70 characters.</summary>
    public static string? CleanGeneratedTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var line = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;

        line = TitlePrefix().Replace(line, "").Trim();
        line = line.Trim(' ', '\t', '"', '\'', '`', '*', '_');
        line = line.TrimEnd('.', ',', ':', ';');
        return line.Length is > 0 and <= MaxTitleCharacters ? line : null;
    }

    /// <summary>Text runs merged as the chat shows them: one entry per role, phase and message uid run.</summary>
    private static List<Collapsed> Collapse(IReadOnlyList<UnifiedMessageRecord> raw)
    {
        var result = new List<Collapsed>();
        foreach (var message in raw
            .Select((message, index) => (message, index))
            .OrderBy(item => item.message.Timestamp)
            .ThenBy(item => item.index)
            .Select(item => item.message))
        {
            if (message.EventType is "thinking" or "status") continue;

            var last = result.Count > 0 ? result[^1] : null;
            if (message.EventType == "text"
                && last is { EventType: "text" }
                && last.Role == message.Role
                && last.Phase == message.Phase
                && last.MessageUid == message.MessageUid)
            {
                last.Content = (last.Content ?? "") + (message.Content ?? "");
                continue;
            }

            var content = message.Content;
            if (message.EventType == "text" && message.Role == "user")
                content = StripInjectedTags(content ?? "");

            result.Add(new Collapsed
            {
                Role = message.Role,
                EventType = message.EventType,
                Content = content,
                Phase = message.Phase,
                MessageUid = message.MessageUid,
            });
        }
        return result;
    }

    internal static string StripInjectedTags(string content)
    {
        content = UnwrapApplicationEnvelope(content) ?? "";
        content = NovaContextTag().Replace(content, "");
        content = NovaPriorTag().Replace(content, "");
        content = ScratchSpaceTag().Replace(content, "");
        content = VoiceCausalContextTag().Replace(content, "");
        content = VoiceCurrentUtteranceTag().Replace(content, match => match.Groups["content"].Value);
        return content.TrimStart();
    }

    /// <summary>The participant's message inside an external application's conversation envelope.</summary>
    private static string? UnwrapApplicationEnvelope(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)
            || !content.StartsWith("Application conversation envelope JSON", StringComparison.Ordinal))
            return content;

        const string startTag = "<external-input-json>";
        const string endTag = "</external-input-json>";
        var start = content.IndexOf(startTag, StringComparison.Ordinal);
        var end = content.IndexOf(endTag, StringComparison.Ordinal);
        if (start < 0 || end <= start + startTag.Length) return content;
        try
        {
            using var envelope = JsonDocument.Parse(content[(start + startTag.Length)..end]);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("requestId", out var requestId)
                || requestId.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("applicationId", out var applicationId)
                || applicationId.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("message", out var participantMessage)
                || participantMessage.ValueKind != JsonValueKind.String)
                return content;
            return participantMessage.GetString();
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static bool IsNoReplyMarker(string? text)
        => text is not null && string.Equals(text.Trim(), NoReplyMarker, StringComparison.Ordinal);

    private static string Normalize(string value, int maxCharacters)
    {
        var normalized = Whitespace().Replace(value, " ").Trim();
        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..(maxCharacters - 1)].TrimEnd() + "…";
    }

    private sealed class Collapsed
    {
        public string Role { get; init; } = "";
        public string EventType { get; init; } = "";
        public string? Content { get; set; }
        public string? Phase { get; init; }
        public string? MessageUid { get; init; }
    }

    [GeneratedRegex(@"^(?:conversation\s+)?title\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<nova-context[^>]*>[\s\S]*?</nova-context>\s*")]
    private static partial Regex NovaContextTag();

    [GeneratedRegex(@"<nova-prior-message[s]?[^>]*>[\s\S]*?</nova-prior-message[s]?>\s*")]
    private static partial Regex NovaPriorTag();

    [GeneratedRegex(@"<scratch-space[^>]*>[\s\S]*?</scratch-space>\s*")]
    private static partial Regex ScratchSpaceTag();

    [GeneratedRegex(@"<voice-causal-context>[\s\S]*?</voice-causal-context>\s*")]
    private static partial Regex VoiceCausalContextTag();

    [GeneratedRegex(@"<voice-current-utterance>\s*(?<content>[\s\S]*?)\s*</voice-current-utterance>\s*")]
    private static partial Regex VoiceCurrentUtteranceTag();
}
