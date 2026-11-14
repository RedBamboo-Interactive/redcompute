using System.Text.RegularExpressions;
using RedCompute.Core.Sessions;

namespace RedCompute.PluginSdk;

/// <summary>Shared formatting for provider-owned first-turn session titles.</summary>
public static class SessionTitleFormatter
{
    private const int MaxAttachmentNameCharacters = 80;

    /// <summary>
    /// The attachments of an opening input as the title model may know them: kind, name and media
    /// type. The title model never receives the bytes, so it must at least be told something was
    /// attached, or a screenshot sent with a short question reads as an empty message.
    /// </summary>
    public static IReadOnlyList<TitleAttachment> DescribeAttachments(IReadOnlyList<SessionInputPart>? input)
    {
        if (input is null || input.Count == 0) return [];
        var result = new List<TitleAttachment>();
        foreach (var part in input)
        {
            if (part.Attachment is { } attachment)
            {
                var kind = CleanLabel(attachment.Kind, 20).ToLowerInvariant();
                result.Add(new TitleAttachment(
                    kind.Length > 0 ? kind : "file",
                    CleanLabel(attachment.Name, MaxAttachmentNameCharacters),
                    CleanLabel(attachment.MediaType, 60)));
            }
            else if (part.LegacyImage is { } image)
            {
                result.Add(new TitleAttachment("image", "", CleanLabel(image.MediaType, 60)));
            }
        }
        return result;
    }

    public static string BuildPrompt(string opening, IReadOnlyList<TitleAttachment>? attachments = null)
    {
        var excerpt = opening.Length > 800 ? opening[..800] : opening;
        if (attachments is not { Count: > 0 })
            return "Write a specific title of at most six words for a conversation that opens with " +
                   "the message below. Capture the subject rather than merely repeating a greeting. " +
                   "Reply with the title only: no quotes, no trailing punctuation, no preamble.\n\n" +
                   excerpt;

        var attached = string.Join("; ", attachments.Select(Describe));
        return "Write a specific title of at most six words for a conversation that opens with " +
               "the message below. Capture the subject rather than merely repeating a greeting. " +
               $"The message came with attachments you cannot see: {attached}. " +
               "Title it from its text together with the fact of what was attached. Never describe " +
               "the message as empty, unclear, incomplete or missing content. " +
               "Reply with the title only: no quotes, no trailing punctuation, no preamble.\n\n" +
               (string.IsNullOrWhiteSpace(excerpt) ? "(no text, only the attachments)" : excerpt);
    }

    public static string? CleanGenerated(string raw)
    {
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0);
        if (line is null) return null;

        line = line.Trim('"', '\'', '`', '*', ' ').TrimEnd('.', '!', ':', ';', ',').Trim();
        return line.Length is 0 or > 70 ? null : line;
    }

    public static string StripContext(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";

        var text = Regex.Replace(
            message, @"<nova-context\b[^>]*>.*?</nova-context>", " ",
            RegexOptions.Singleline);

        var lastTag = text.LastIndexOf("<nova-context", StringComparison.OrdinalIgnoreCase);
        if (lastTag >= 0)
        {
            var close = text.IndexOf('>', lastTag);
            text = close >= 0 ? text[(close + 1)..] : text[..lastTag];
        }

        text = Regex.Replace(text, @"<[^>]+>", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string? DeriveFallback(string? message, IReadOnlyList<TitleAttachment>? attachments = null)
    {
        var text = StripContext(message);
        if (string.IsNullOrEmpty(text)) return AttachmentFallback(attachments);

        for (var i = 12; i < text.Length && i <= 60; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;
            return text[..i].Trim();
        }

        if (text.Length <= 60) return text;

        var cut = text.LastIndexOf(' ', 60);
        return (cut > 20 ? text[..cut] : text[..60]).Trim() + "...";
    }

    /// <summary>The immediate title of an attachment-only opening: "Shared image", "Shared file report.pdf".</summary>
    private static string? AttachmentFallback(IReadOnlyList<TitleAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 }) return null;
        if (attachments.Count > 1)
            return attachments.All(a => a.Kind == attachments[0].Kind)
                ? $"Shared {attachments.Count} {attachments[0].Kind}s"
                : $"Shared {attachments.Count} attachments";

        var only = attachments[0];
        var label = only.Kind == "image" || string.IsNullOrEmpty(only.Name)
            ? $"Shared {only.Kind}"
            : $"Shared {only.Kind} {only.Name}";
        return label.Length <= 60 ? label : label[..57].TrimEnd() + "...";
    }

    private static string Describe(TitleAttachment attachment)
    {
        var description = $"{("aeiou".Contains(attachment.Kind[0]) ? "an" : "a")} {attachment.Kind}";
        if (attachment.Name.Length > 0) description += $" named \"{attachment.Name}\"";
        if (attachment.MediaType.Length > 0) description += $" ({attachment.MediaType})";
        return description;
    }

    /// <summary>One bounded line without quotes or markup: the value is spliced into a prompt sentence.</summary>
    private static string CleanLabel(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = Regex.Replace(value, @"[\p{C}""<>`]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length <= maxCharacters ? cleaned : cleaned[..maxCharacters].TrimEnd();
    }
}

/// <summary>What a title model is told about one attachment of the opening message.</summary>
public sealed record TitleAttachment(string Kind, string Name, string MediaType);
