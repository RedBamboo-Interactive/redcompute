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
        if (NeverTransient().IsMatch(error)) return false;
        return Transient().IsMatch(error);
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

    // Wording that means trying again cannot help, even when it shares a word with the above.
    [GeneratedRegex(
        @"usage limit|spending limit|spend limit|quota|rate limit|budget|authenticat|oauth|unauthori[sz]ed|forbidden"
        + @"|safeguards|refus|policy|invalid[_ ]request|prompt is too long|context (window|length)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeverTransient();
}
