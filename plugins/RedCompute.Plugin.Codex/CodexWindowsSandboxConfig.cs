using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Codex on Windows enforces its workspace-write sandbox only once the Windows sandbox is enabled
/// (<c>[windows] sandbox</c>). Without it there is no platform sandbox, so on-request approval asks for
/// every command, even a read inside the workspace. RedCompute therefore launches the app-server with
/// <c>-c windows.sandbox="unelevated"</c> (no admin rights), unless the user's own config.toml already
/// chooses a mode. The user's config.toml is only ever read.
/// </summary>
internal static class CodexWindowsSandboxConfig
{
    /// <summary>The <c>-c</c> override, as two arguments.</summary>
    internal static readonly string[] Override = ["-c", "windows.sandbox=\"unelevated\""];

    /// <summary>The app-server arguments on <paramref name="host"/>: the override first on Windows unless the user set the key.</summary>
    internal static IReadOnlyList<string> AppServerArguments(
        IExecutionHost host, IReadOnlyDictionary<string, string>? envOverlay = null)
        => !host.Platform.IsWindows || UserSetsSandbox(host, envOverlay)
            ? ["app-server"]
            : [.. Override, "app-server"];

    /// <summary>Test seam: replaces reading the user's config.toml (null result: no file).</summary>
    internal static Func<IExecutionHost, IReadOnlyDictionary<string, string>?, string?>? ConfigTextOverride { get; set; }

    private static bool UserSetsSandbox(IExecutionHost host, IReadOnlyDictionary<string, string>? envOverlay)
    {
        try
        {
            if (ConfigTextOverride is { } over)
                return over(host, envOverlay) is { } text && SetsWindowsSandbox(text);
            var home = envOverlay is not null && envOverlay.TryGetValue("CODEX_HOME", out var overlaid)
                ? overlaid
                : host.Platform.GetEnvironmentVariable("CODEX_HOME");
            string path;
            if (!string.IsNullOrWhiteSpace(home))
                path = host.PathRules.Combine(home, "config.toml");
            else
            {
                var profile = host.Platform.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(profile)) return false;
                path = host.PathRules.Combine(profile, ".codex", "config.toml");
            }
            var hostPath = host.Path(path);
            return host.Files.FileExists(hostPath) && SetsWindowsSandbox(host.Files.ReadAllText(hostPath));
        }
        catch
        {
            // Unreadable config: add the override (it only takes effect when Codex cannot read the user's choice either).
            return false;
        }
    }

    /// <summary>
    /// True when the TOML text sets <c>windows.sandbox</c>: a <c>sandbox</c> key in a <c>[windows]</c> table,
    /// a dotted <c>windows.sandbox</c> key at the root, or <c>windows = { sandbox = ... }</c>. Comments, quoted
    /// keys and multi-line strings are honoured, so mentions in values or comments do not count.
    /// </summary>
    internal static bool SetsWindowsSandbox(string toml)
    {
        string[] table = [];
        string? multiline = null; // closing delimiter while inside a multi-line string
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (multiline is not null)
            {
                if (line.Contains(multiline, StringComparison.Ordinal)) multiline = null;
                continue;
            }
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;

            if (t[0] == '[')
            {
                var array = t.StartsWith("[[", StringComparison.Ordinal);
                var keys = ParseKey(t[(array ? 2 : 1)..], out _, stopAt: ']');
                // An array of tables is never [windows]; a malformed header matches nothing.
                table = array || keys is null ? ["?"] : keys;
                continue;
            }

            var key = ParseKey(t, out var rest, stopAt: '=');
            if (key is null) continue;
            string[] path = [.. table, .. key];
            if (path.Length >= 2 && path[0] == "windows" && path[1] == "sandbox") return true;
            if (path is ["windows"] && InlineTableSetsSandbox(rest)) return true;

            var v = rest.TrimStart();
            if ((v.StartsWith("\"\"\"", StringComparison.Ordinal) || v.StartsWith("'''", StringComparison.Ordinal))
                && v.IndexOf(v[..3], 3, StringComparison.Ordinal) < 0)
                multiline = v[..3];
        }
        return false;
    }

    private static bool InlineTableSetsSandbox(string rest)
    {
        var v = rest.TrimStart();
        if (!v.StartsWith('{')) return false;
        var depth = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c is '"' or '\'') { i = SkipString(v, i); continue; }
            if (c == '#') break;
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (depth == 1 && (char.IsLetter(c) || c == '_'))
            {
                var start = i;
                while (i + 1 < v.Length && (char.IsLetterOrDigit(v[i + 1]) || v[i + 1] is '_' or '-')) i++;
                if (v[start..(i + 1)] == "sandbox" && v[(i + 1)..].TrimStart().StartsWith('=')) return true;
            }
        }
        return false;
    }

    private static int SkipString(string s, int i)
    {
        var quote = s[i];
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] == '\\' && quote == '"') { j++; continue; }
            if (s[j] == quote) return j;
        }
        return s.Length;
    }

    /// <summary>A dotted TOML key (bare or quoted parts) ended by <paramref name="stopAt"/>; null when malformed.</summary>
    private static string[]? ParseKey(string s, out string rest, char stopAt)
    {
        var parts = new List<string>();
        var i = 0;
        rest = "";
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) return null;
            if (s[i] is '"' or '\'')
            {
                var end = SkipString(s, i);
                if (end >= s.Length) return null;
                parts.Add(s[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-')) i++;
                if (i == start) return null;
                parts.Add(s[start..i]);
            }
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i < s.Length && s[i] == '.') { i++; continue; }
            if (i < s.Length && s[i] == stopAt) { rest = s[(i + 1)..]; return [.. parts]; }
            return null;
        }
    }
}
