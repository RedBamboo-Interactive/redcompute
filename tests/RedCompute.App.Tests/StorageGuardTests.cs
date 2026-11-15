using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Data relocation guard (design 3.2): RedCompute's own data is located by
/// <c>RedCompute.Core.Storage.ComputeStorage</c> and nothing else. This scans the source of the host,
/// Core, the plugin SDK and every plugin for the per-user and machine data folders
/// (<c>SpecialFolder.LocalApplicationData</c>, <c>ApplicationData</c>, <c>CommonApplicationData</c>,
/// and the <c>LOCALAPPDATA</c>/<c>APPDATA</c>/<c>ProgramData</c> variables), comments and string
/// literals aside, so <c>using static</c> or an alias does not slip past.
/// <para>
/// Every remaining use must be listed below with its reason, and every listed use must still exist.
/// A new one is Leaf data until shown otherwise: route it through <c>ComputeStorage</c> (or
/// <c>IHostPlatform.ComputeDataRoot</c> for a file on the execution host).
/// </para>
/// </summary>
public sealed class StorageGuardTests
{
    /// <summary>The resolver itself: the only source that may name the data folders for RedCompute's data.</summary>
    private const string Resolver = "src/RedCompute.Core/Storage/ComputeStorage.cs";

    /// <summary>(file, folder, uses): locations RedCompute does not own and never relocates.</summary>
    internal static readonly (string File, string Folder, int Uses, string Why)[] Allowlist =
    [
        // ---- CLI installs and homes: owned by the CLIs and npm, not Leaf data -------------------------
        ("src/RedCompute.PluginSdk/CliVersionManagement.cs", "ApplicationData", 1,
            "%APPDATA%\\npm: the global npm prefix the managed CLIs install into"),
        ("plugins/RedCompute.Plugin.ClaudeCode/ClaudeSessionService.cs", "ApplicationData", 1,
            "%APPDATA%\\npm: locating the installed Claude Code CLI"),
        ("plugins/RedCompute.Plugin.Codex/CodexCliLocator.cs", "ApplicationData", 1,
            "%APPDATA%\\npm: locating the npm-installed Codex CLI"),
        ("plugins/RedCompute.Plugin.Codex/CodexCliLocator.cs", "LocalApplicationData", 1,
            "%LOCALAPPDATA%\\Programs\\OpenAI\\Codex: the official Codex installer's location"),
        ("plugins/RedCompute.Plugin.OpenCode/OpenCodeSessionService.cs", "ApplicationData", 1,
            "%APPDATA%\\npm: locating the npm-installed OpenCode CLI"),
        // Not listed because they never name a folder: LocalExecutionHost's pass-through
        // GetFolderPath(folder) and its one-shot temp directories (Path.GetTempPath, caller-scoped
        // and deleted by the caller), ~/.claude, ~/.codex and ~/.local/share/opencode (UserProfile,
        // XDG_DATA_HOME: the CLIs' own homes), and scratch, which the caller passes in.
    ];

    private static readonly Regex Forbidden = new(
        @"\b(?<folder>LocalApplicationData|CommonApplicationData|ApplicationData)\b"
        + @"|GetEnvironmentVariable\s*\(\s*""(?<variable>LOCALAPPDATA|APPDATA|ProgramData|PROGRAMDATA)""",
        RegexOptions.Compiled);

    internal sealed record Use(string File, int Line, string Folder);

    [Fact]
    public void Only_the_resolver_and_allowlisted_sites_name_the_data_folders()
    {
        var uses = ScanAll(RepoRoot());
        var allowed = Allowlist.ToDictionary(entry => (entry.File, entry.Folder), entry => entry.Uses);

        var counted = uses.GroupBy(use => (use.File, use.Folder)).ToList();
        var offending = counted
            .Where(group => !allowed.TryGetValue(group.Key, out var permitted) || group.Count() > permitted)
            .SelectMany(group => group)
            .ToList();
        Assert.True(offending.Count == 0,
            "Data folder named outside ComputeStorage (route it through ComputeStorage or "
            + "IHostPlatform.ComputeDataRoot, or allowlist it with a reason if it is not Leaf data):\n"
            + string.Join("\n", offending.Select(use => $"  {use.File}:{use.Line}  {use.Folder}")));
    }

    [Fact]
    public void The_allowlist_only_names_uses_that_still_exist()
    {
        var uses = ScanAll(RepoRoot());
        var stale = Allowlist
            .Where(entry => uses.Count(use => use.File == entry.File && use.Folder == entry.Folder) != entry.Uses)
            .Select(entry => $"  {entry.File} {entry.Folder} (listed {entry.Uses}, found "
                + uses.Count(use => use.File == entry.File && use.Folder == entry.Folder) + ")")
            .ToList();
        Assert.True(stale.Count == 0, "Allowlist entries out of date:\n" + string.Join("\n", stale));
    }

    /// <summary>The guard's own test: code is found however it is spelled, comments and strings are not.</summary>
    [Fact]
    public void The_scanner_sees_code_and_ignores_comments_and_strings()
    {
        const string source = """"
            using static System.Environment.SpecialFolder;
            // Environment.SpecialFolder.LocalApplicationData in a comment
            /// <summary>%LOCALAPPDATA% ApplicationData in a doc comment</summary>
            /* CommonApplicationData in a block */
            var a = "LocalApplicationData in a string";
            var b = @"ApplicationData in a verbatim ""string""";
            var c = $"{x} CommonApplicationData";
            var d = 'L';
            var e = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var f = GetFolderPath(CommonApplicationData);
            var g = Environment.GetEnvironmentVariable("APPDATA");
            var h = "http://example/ApplicationData"; var i = SpecialFolder.ApplicationData;
            """";

        var uses = Scan("fixture.cs", source);

        Assert.Equal(
            [(10, "LocalApplicationData"), (11, "CommonApplicationData"), (12, "APPDATA"), (13, "ApplicationData")],
            uses.Select(use => (use.Line, use.Folder)));
    }

    // ---- scanner ---------------------------------------------------------------------------------

    /// <summary>src/ (host, Core, plugin SDK) and plugins/, outside build output.</summary>
    internal static List<Use> ScanAll(string root)
    {
        var files = new[] { "src", "plugins" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj"))
            .Where(path => path != Resolver)
            .Order(StringComparer.Ordinal);
        return files.SelectMany(file => Scan(file, File.ReadAllText(Path.Combine(root, file)))).ToList();
    }

    internal static List<Use> Scan(string file, string source)
    {
        var code = CodeOnly(source);
        return Forbidden.Matches(code)
            .Select(match => new Use(file,
                1 + code.AsSpan(0, match.Index).Count('\n'),
                match.Groups["folder"].Success ? match.Groups["folder"].Value : match.Groups["variable"].Value))
            .ToList();
    }

    /// <summary>
    /// The source with comments blanked and string and character literal contents blanked, line
    /// breaks kept so line numbers hold. A variable name passed to <c>GetEnvironmentVariable</c> is
    /// the one literal kept, so the variable rule can match it.
    /// </summary>
    internal static string CodeOnly(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (ch == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') { output.Append(' '); i++; }
            }
            else if (ch == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Blank(source, i, end, output);
                i = end;
            }
            else if (ch == '"' || ((ch is '@' or '$') && (next == '"' || (next is '@' or '$' && Peek(source, i + 2) == '"'))))
            {
                var start = i;
                var verbatim = false;
                while (source[i] != '"') { verbatim |= source[i] == '@'; i++; }
                int end;
                if (source.AsSpan(i).StartsWith("\"\"\""))
                {
                    var quotes = 0;
                    while (i + quotes < source.Length && source[i + quotes] == '"') quotes++;
                    var close = source.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    end = close < 0 ? source.Length : close + quotes;
                }
                else
                {
                    end = i + 1;
                    while (end < source.Length)
                    {
                        if (verbatim && source[end] == '"' && Peek(source, end + 1) == '"') { end += 2; continue; }
                        if (!verbatim && source[end] == '\\') { end += 2; continue; }
                        if (source[end] == '"' || (!verbatim && source[end] == '\n')) { end++; break; }
                        end++;
                    }
                }
                end = Math.Min(end, source.Length);
                var literal = source[start..end];
                if (Regex.IsMatch(literal, @"^""(LOCALAPPDATA|APPDATA|ProgramData|PROGRAMDATA)""$"))
                    output.Append(literal);
                else
                    Blank(source, start, end, output);
                i = end;
            }
            else if (ch == '\'')
            {
                var end = i + 1;
                while (end < source.Length && source[end] != '\'' && source[end] != '\n')
                    end += source[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, source.Length);
                Blank(source, i, end, output);
                i = end;
            }
            else
            {
                output.Append(ch);
                i++;
            }
        }
        return output.ToString();
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    private static void Blank(string source, int start, int end, StringBuilder output)
    {
        for (var k = start; k < end; k++) output.Append(source[k] == '\n' ? '\n' : ' ');
    }

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("RedCompute.sln not found above " + file);
    }
}
