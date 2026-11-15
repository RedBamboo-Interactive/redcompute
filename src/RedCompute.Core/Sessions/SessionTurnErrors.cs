using System.Text.RegularExpressions;

namespace RedCompute.Core.Sessions;

/// <summary>
/// The error a failed turn ended with, as the provider reported it, and whether it is transient:
/// the network or the model service was briefly unavailable, so the same turn would likely succeed
/// if tried again a little later.
/// </summary>
/// <remarks>
/// On 2026-11-14 a DNS outage of about an hour made every Claude CLI give up with
/// "API Error: Can't reach the API server — check your internet or DNS (ENOTFOUND)" after its own
/// ~4.5-minute retry. Those are the errors this recognizes. A usage limit, an authentication failure, a
/// safety refusal or an invalid request is never transient: trying again would not change the answer.
/// </remarks>
public static partial class SessionTurnErrors
{
    /// <summary>The longest error text RedCompute keeps on a turn.</summary>
    public const int MaxLength = 1000;

    public static string? Clip(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var trimmed = error.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..(MaxLength - 3)] + "...";
    }

    public static bool IsTransient(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        if (UsageLimit().IsMatch(error) || NeverTransient().IsMatch(error)) return false;
        return Transient().IsMatch(error);
    }

    /// <summary>The kinds of <see cref="Kind"/>.</summary>
    public const string UsageLimitKind = "usage_limit", AuthenticationKind = "authentication",
        TransientKind = "transient", OtherKind = "other";

    /// <summary>
    /// The provider account ran out of allowance: every session on that model fails the same way
    /// until it resets. 2026-11-15: "You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich).
    /// Switch to another model, ..." matched none of the older wordings, so it read as an ordinary
    /// failure and nothing said the account was out.
    /// </summary>
    public static bool IsUsageLimit(string? error) => !string.IsNullOrWhiteSpace(error) && UsageLimit().IsMatch(error);

    /// <summary>
    /// What a failed turn's error is: <c>usage_limit</c> or <c>authentication</c> (the provider
    /// account, not this turn: never retried, said once), <c>transient</c> (retried), or <c>other</c>.
    /// Null without an error.
    /// </summary>
    public static string? Kind(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        if (UsageLimit().IsMatch(error)) return UsageLimitKind;
        if (Authentication().IsMatch(error)) return AuthenticationKind;
        return IsTransient(error) ? TransientKind : OtherKind;
    }

    // Connectivity (Node/undici error codes and the CLI's own wording), provider overload and
    // gateway/server errors.
    [GeneratedRegex(
        @"\b(ENOTFOUND|EAI_AGAIN|ECONNREFUSED|ECONNRESET|ECONNABORTED|ETIMEDOUT|ENETUNREACH|ENETDOWN|EHOSTUNREACH|EPIPE|UND_ERR_[A-Z_]+)\b"
        + @"|can'?t reach the api server|connection error|network error|fetch failed|socket hang up|request timed out"
        + @"|overloaded|at capacity|temporarily unavailable|service unavailable|bad gateway|gateway time-?out|internal server error"
        + @"|\bapi error:?\s*5\d\d\b|\b(status|code|http)\s*[:=]?\s*(500|502|503|504|529)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Transient();

    // Account allowance, in the wordings Claude Code and the APIs have used. A per-minute "rate limit"
    // is deliberately not here: it is not the account running out.
    [GeneratedRegex(
        @"usage limit|spending limit|spend limit|\bquota\b|insufficient_quota|credit balance is too low|out of (extra )?usage"
        + @"|\bhit your\b[^.]{0,60}\blimit\b|(?<!rate )\blimit (reached|exceeded)\b|\bexceeded\b[^.]{0,40}(?<!rate )\blimit\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsageLimit();

    // The provider no longer accepts this account's credentials.
    [GeneratedRegex(
        @"authenticat|\boauth\b|unauthori[sz]ed|invalid (x-)?api[ -]key|please run /login|not logged in|\b(api error|status|http)\s*:?\s*401\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Authentication();

    // Wording that means trying again cannot help, even when it shares a word with the above.
    [GeneratedRegex(
        @"usage limit|spending limit|spend limit|quota|rate limit|budget|authenticat|oauth|unauthori[sz]ed|forbidden"
        + @"|safeguards|refus|policy|invalid[_ ]request|prompt is too long|context (window|length)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeverTransient();
}
