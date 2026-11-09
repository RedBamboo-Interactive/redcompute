namespace RedCompute.Core.Sessions;

/// <summary>
/// What the model is told when a permission is denied because nobody approved it, worded the same
/// across providers: why (the session's level, with nobody to approve) and where a person changes
/// it. Short on purpose: it is a tool result the model reads mid-turn.
/// </summary>
public static class SessionAccessNotices
{
    /// <summary>Where the fix lives, in the words RedLeaf's and the chat's own UI use.</summary>
    public const string WhereToChange =
        "The installation owner can change this in RedLeaf Settings > Agent permissions, or raise this session in its Info panel.";

    /// <summary>The level as the Settings tab and the Info panel name it.</summary>
    public static string Title(string? level) => level switch
    {
        SessionAccessLevels.Plan => "Plan",
        SessionAccessLevels.Ask => "Ask",
        SessionAccessLevels.Auto => "Auto",
        SessionAccessLevels.Full => "Full",
        _ => "Ask",
    };

    /// <summary>An unattended session's immediate denial of <paramref name="what"/> (a tool name or a short summary).</summary>
    public static string UnattendedDenial(string? level, string what)
        => $"Denied: this session runs at {Title(level)} with nobody to approve {what}. {WhereToChange} "
            + "Continue without it.";

    /// <summary>A parked approval that nobody answered before it expired.</summary>
    public static string ExpiredDenial(string? level, string what, TimeSpan waited)
        => $"Denied: this session runs at {Title(level)} and nobody approved {what} within {Minutes(waited)}. {WhereToChange} "
            + "Continue without it, or ask again when the user is back.";

    /// <summary>
    /// For harnesses whose denial carries no message (Codex app-server approvals, OpenCode's ACP
    /// <c>request_permission</c>, Claude one-shots in dontAsk): the same explanation, given up front
    /// as part of the session's instructions. <paramref name="unattended"/> says whether a person
    /// watches the session at launch.
    /// </summary>
    public static string LaunchNote(string? level, bool unattended, TimeSpan? waited = null)
        => $"Permissions: this session runs at {Title(level)}. "
            + (unattended
                ? "Nobody watches it, so anything that needs approval is denied at once."
                : $"Anything that needs approval waits for a person and is denied if nobody approves it within {Minutes(waited ?? TimeSpan.FromMinutes(30))}.")
            + $" A denied tool or command without a reason was denied for that. {WhereToChange} Continue without it.";

    /// <summary>Appends <see cref="LaunchNote"/> to existing instructions, keeping theirs first.</summary>
    public static string WithLaunchNote(string? instructions, string note)
        => string.IsNullOrWhiteSpace(instructions) ? note : $"{instructions.TrimEnd()}\n\n{note}";

    private static string Minutes(TimeSpan waited)
    {
        var minutes = Math.Max(1, (int)Math.Round(waited.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
