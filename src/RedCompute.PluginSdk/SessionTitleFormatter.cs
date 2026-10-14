using System.Text.RegularExpressions;

namespace RedCompute.PluginSdk;

/// <summary>Shared formatting for provider-owned first-turn session titles.</summary>
public static class SessionTitleFormatter
{
    public static string BuildPrompt(string opening)
    {
        var excerpt = opening.Length > 800 ? opening[..800] : opening;
        return "Write a specific title of at most six words for a conversation that opens with " +
               "the message below. Capture the subject rather than merely repeating a greeting. " +
               "Reply with the title only: no quotes, no trailing punctuation, no preamble.\n\n" +
               excerpt;
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

    public static string? DeriveFallback(string? message)
    {
        var text = StripContext(message);
        if (string.IsNullOrEmpty(text)) return null;

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
}
