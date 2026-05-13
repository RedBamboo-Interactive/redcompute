using System.Text.Json;
using RedCompute.Core.Configuration;

namespace RedCompute.PluginSdk;

public static class ProviderHelpers
{
    public static T? GetParam<T>(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var val) || val == null) return default;
        if (val is T t) return t;
        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool?) || typeof(T) == typeof(bool))
            {
                if (je.ValueKind == JsonValueKind.True) return (T)(object)true;
                if (je.ValueKind == JsonValueKind.False) return (T)(object)false;
            }
            if (typeof(T) == typeof(long?) || typeof(T) == typeof(long))
                return je.TryGetInt64(out var l) ? (T)(object)l : default;
            if (typeof(T) == typeof(int?) || typeof(T) == typeof(int))
                return je.TryGetInt32(out var i) ? (T)(object)i : default;
            if (typeof(T) == typeof(double?) || typeof(T) == typeof(double))
                return je.TryGetDouble(out var d) ? (T)(object)d : default;
        }
        try { return (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)); }
        catch { return default; }
    }

    public static string GetExtra(ProviderConfig config, string key, string defaultValue)
    {
        if (config.Extra != null && config.Extra.TryGetValue(key, out var val) && val != null)
            return val.ToString()!;
        return defaultValue;
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
