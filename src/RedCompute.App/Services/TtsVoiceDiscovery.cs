using System.IO;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public static class TtsVoiceDiscovery
{
    public static readonly string[] BuiltInSpeakers = ["Serena", "Aiden", "Ryan", "Vivian"];

    public static bool IsBuiltIn(string name)
        => BuiltInSpeakers.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static List<string> DiscoverCustomVoices(string? voicesBasePath)
    {
        if (string.IsNullOrEmpty(voicesBasePath) || !Directory.Exists(voicesBasePath))
            return [];

        var voices = new List<string>();
        foreach (var dir in Directory.GetDirectories(voicesBasePath))
        {
            var checkpoint = Path.Combine(dir, "model", "checkpoint", "model.safetensors");
            if (File.Exists(checkpoint))
                voices.Add(Path.GetFileName(dir));
        }
        voices.Sort(StringComparer.OrdinalIgnoreCase);
        return voices;
    }

    public static string? GetVoicesBasePath(CapabilityRegistry registry)
        => GetTtsProviderConfig(registry)?.VoicesBasePath;

    public static string? GetBaseModel(CapabilityRegistry registry)
        => GetTtsProviderConfig(registry)?.Model;

    private static ProviderConfig? GetTtsProviderConfig(CapabilityRegistry registry)
    {
        var entry = registry.Get("tts");
        if (entry?.Config.ActiveProvider == null) return null;
        return entry.Config.Providers.GetValueOrDefault(entry.Config.ActiveProvider);
    }

    public static List<string> AllVoices(CapabilityRegistry registry)
    {
        var custom = DiscoverCustomVoices(GetVoicesBasePath(registry));
        custom.AddRange(BuiltInSpeakers);
        return custom;
    }

    public static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var drive = char.ToLower(windowsPath[0]);
            var rest = windowsPath[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }
        return windowsPath.Replace('\\', '/');
    }
}
