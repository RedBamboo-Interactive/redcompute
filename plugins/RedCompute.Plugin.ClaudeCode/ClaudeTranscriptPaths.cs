namespace RedCompute.Plugin.ClaudeCode;

/// <summary>
/// Locates the Claude CLI's own transcript for a session under <c>~/.claude/projects</c>.
/// </summary>
internal static class ClaudeTranscriptPaths
{
    // The CLI truncates the slug and appends a hash past this length (observed folders are <= 86 chars,
    // so the exact hash is not reproduced; long paths are found by scanning instead).
    private const int MaxSlugLength = 200;

    /// <summary>
    /// The CLI replaces every non-alphanumeric character with '-' (S:/a b/c.d_e -> S--a-b-c-d-e).
    /// </summary>
    internal static string ProjectSlug(string projectPath)
    {
        var chars = new char[projectPath.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            var c = projectPath[i];
            chars[i] = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : '-';
        }
        return new string(chars);
    }

    internal static string DefaultProjectsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// Returns the transcript path, or null when none exists. Uses the computed slug folder first and falls
    /// back to scanning <c>projects/*/&lt;id&gt;.jsonl</c> (long, truncated-and-hashed slugs; CLI naming changes).
    /// </summary>
    internal static string? FindTranscript(string projectPath, string claudeSessionId, string? projectsRoot = null)
    {
        projectsRoot ??= DefaultProjectsRoot();
        var direct = Path.Combine(projectsRoot, ProjectSlug(projectPath), $"{claudeSessionId}.jsonl");
        if (File.Exists(direct)) return direct;
        if (!Directory.Exists(projectsRoot)) return null;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(projectsRoot))
            {
                var candidate = Path.Combine(folder, $"{claudeSessionId}.jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
