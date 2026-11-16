using System.Text.Json.Serialization;

namespace RedCompute.NodeLink.Spool;

/// <summary>
/// What a spawned process does while the link is down (design 4.6), chosen per spawn by the provider:
/// <c>{"keepRunning": "PT15M", "spoolCapBytes": 268435456, "onLimit": {"action": "graceful-stop",
/// "closeStdin": true, "graceSeconds": 60}}</c>. Designed in Phase 3, used from Phase 5.
/// </summary>
public sealed record LinkLossPolicy(
    [property: JsonPropertyName("keepRunning")] string KeepRunning,
    [property: JsonPropertyName("spoolCapBytes")] long SpoolCapBytes,
    [property: JsonPropertyName("onLimit")] LinkLossLimitAction OnLimit)
{
    public static LinkLossPolicy Default { get; } = new("PT15M", 256L << 20, new LinkLossLimitAction("graceful-stop", true, 60));

    [JsonIgnore]
    public TimeSpan KeepRunningFor => System.Xml.XmlConvert.ToTimeSpan(KeepRunning);
}

public sealed record LinkLossLimitAction(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("closeStdin")] bool CloseStdin,
    [property: JsonPropertyName("graceSeconds")] int GraceSeconds);

public enum LinkLossVerdict
{
    /// <summary>The link is up, or down for less than the policy allows with room in the spool.</summary>
    KeepRunning,
    /// <summary>The time ran out or the spool reached 90%: run onLimit (close stdin, grace, tree kill).</summary>
    Park,
    /// <summary>The spool passed its cap anyway: kill at once (never drop data silently).</summary>
    Kill,
}

/// <summary>
/// The link-loss clock (design 4.6): it starts when the node detects the loss and runs on a monotonic
/// clock; a link that returns stops it.
/// </summary>
public sealed class LinkLossClock(TimeProvider time)
{
    private long _lostAt;

    public bool Running => _lostAt != 0;

    public TimeSpan Elapsed => _lostAt == 0 ? TimeSpan.Zero : time.GetElapsedTime(_lostAt);

    public void OnLinkLost()
    {
        if (_lostAt == 0) _lostAt = time.GetTimestamp();
    }

    public void OnLinkRestored() => _lostAt = 0;

    public LinkLossVerdict Evaluate(LinkLossPolicy policy, long spoolBytes)
    {
        var pressure = SpoolBudget.Evaluate(spoolBytes, policy.SpoolCapBytes);
        if (pressure == SpoolPressure.Overflow) return LinkLossVerdict.Kill;
        if (!Running) return LinkLossVerdict.KeepRunning;
        return pressure == SpoolPressure.Park || Elapsed >= policy.KeepRunningFor ? LinkLossVerdict.Park : LinkLossVerdict.KeepRunning;
    }
}
