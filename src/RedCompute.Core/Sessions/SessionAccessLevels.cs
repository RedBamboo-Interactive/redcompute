namespace RedCompute.Core.Sessions;

/// <summary>
/// Provider-neutral access levels, lowest first: <c>plan</c> &lt; <c>ask</c> &lt; <c>auto</c> &lt; <c>full</c>.
/// A session's level says what its agent may do without a person approving it. Each provider maps
/// a level onto its own mechanism and reports the levels it can enforce; an unsupported level
/// resolves to <see cref="Ask"/>, never upward.
/// </summary>
public static class SessionAccessLevels
{
    /// <summary>Read and plan only; nothing is changed.</summary>
    public const string Plan = "plan";
    /// <summary>Edits inside the workspace run; anything else asks a person.</summary>
    public const string Ask = "ask";
    /// <summary>The provider's own classifier decides; what it blocks asks a person.</summary>
    public const string Auto = "auto";
    /// <summary>Everything runs without asking.</summary>
    public const string Full = "full";

    public static readonly IReadOnlyList<string> All = [Plan, Ask, Auto, Full];

    /// <summary>Position in the order, or -1 for anything that is not a level.</summary>
    public static int Rank(string? level) => level switch
    {
        Plan => 0,
        Ask => 1,
        Auto => 2,
        Full => 3,
        _ => -1,
    };

    public static bool IsKnown(string? level) => Rank(level) >= 0;

    /// <summary>Accepts a level name in any case and returns its canonical form.</summary>
    public static bool TryNormalize(string? value, out string level)
    {
        level = value?.Trim().ToLowerInvariant() ?? "";
        if (IsKnown(level)) return true;
        level = "";
        return false;
    }

    /// <summary>
    /// The level a legacy permission-mode name stands for: <c>bypassPermissions</c> is full;
    /// <c>acceptEdits</c>, <c>default</c>, <c>manual</c> and <c>dontAsk</c> are ask; <c>plan</c> and
    /// <c>auto</c> are themselves. Level names are accepted too.
    /// </summary>
    public static bool TryFromPermissionMode(string? mode, out string level)
    {
        level = mode?.Trim() switch
        {
            "bypassPermissions" => Full,
            "acceptEdits" or "default" or "manual" or "dontAsk" => Ask,
            _ => "",
        };
        return level.Length > 0 || TryNormalize(mode, out level);
    }

    public static string Lower(string a, string b) => Rank(a) <= Rank(b) ? a : b;

    public static bool IsAbove(string level, string than) => Rank(level) > Rank(than);
}

/// <summary>
/// The persisted access state of one provider session. A null <see cref="AccessLevel"/> marks a
/// session persisted before access levels: it resolves fresh from policy on its next launch, and a
/// stored provider mode such as <c>bypassPermissions</c> is never read as its level.
/// </summary>
/// <param name="AccessLevel">The level the session runs at.</param>
/// <param name="MaxAccessLevel">Hard cap set at start; nothing raises the session above it.</param>
/// <param name="Unattended">No person watches: asks are denied at once and never park.</param>
/// <param name="AccessLevelBeforePlan">The level to return to when the session leaves plan without naming one.</param>
public sealed record SessionAccessState(
    string? AccessLevel,
    string? MaxAccessLevel = null,
    bool Unattended = false,
    string? AccessLevelBeforePlan = null)
{
    public static readonly SessionAccessState Legacy = new((string?)null);
}
