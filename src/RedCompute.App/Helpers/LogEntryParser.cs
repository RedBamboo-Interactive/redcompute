using System.Text.RegularExpressions;
using RedCompute.Core.Logging;

namespace RedCompute.App.Helpers;

public static partial class LogEntryParser
{
    [GeneratedRegex(@"^\[([A-Za-z_ ]+)\]")]
    private static partial Regex TagRegex();

    private record TagInfo(string Color, string Category);

    private static readonly Dictionary<string, TagInfo> TagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App"] = new("#D4AA4F", "system"),
        ["Settings"] = new("#D4AA4F", "system"),
        ["SYSTEM"] = new("#D4AA4F", "system"),

        ["JOB"] = new("#72767D", "compute"),
        ["COMPUTE"] = new("#72767D", "compute"),

        ["PROVIDER"] = new("#26A69A", "provider"),
        ["HEALTH"] = new("#26A69A", "provider"),
        ["Relay"] = new("#26A69A", "provider"),

        ["TTS"] = new("#26A69A", "audio"),

        ["ImageGen"] = new("#72767D", "image"),
        ["COMFYUI"] = new("#72767D", "image"),

        ["MusicGen"] = new("#72767D", "music"),

        ["API"] = new("#72767D", "api"),

        ["CONFIG"] = new("#72767D", "config"),

        ["ERROR"] = new("#E55B5B", "error"),
        ["EXCEPTION"] = new("#E55B5B", "error"),

        ["DEBUG"] = new("#72767D", "debug"),
    };

    public static LogEntry Parse(string rawMessage)
    {
        var tag = "";
        var message = rawMessage;
        var tagColor = "#72767D";
        var tagCategory = "debug";
        var isError = false;

        var match = TagRegex().Match(rawMessage);
        if (match.Success)
        {
            tag = match.Groups[1].Value.Trim();
            message = rawMessage[(match.Length)..].TrimStart();

            if (TagMap.TryGetValue(tag, out var info))
            {
                tagColor = info.Color;
                tagCategory = info.Category;
            }

            isError = tagCategory == "error";
        }

        var firstLine = message.IndexOf('\n') is >= 0 and var idx
            ? message[..idx].TrimEnd('\r')
            : message;

        return new LogEntry
        {
            Timestamp = DateTime.Now,
            Tag = tag,
            TagCategory = tagCategory,
            Message = firstLine,
            FullMessage = rawMessage,
            TagColor = tagColor,
            IsMultiline = message.Contains('\n'),
            IsError = isError,
        };
    }

    public static IReadOnlyDictionary<string, (string Color, string Category)> GetTagDefinitions()
    {
        return TagMap.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Color, kvp.Value.Category));
    }
}
