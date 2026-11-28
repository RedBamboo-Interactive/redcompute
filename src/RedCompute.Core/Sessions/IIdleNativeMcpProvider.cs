using RedCompute.Core.Jobs;

namespace RedCompute.Core.Sessions;

/// <summary>Refreshes the native MCP plan of a resident generation under the input queue's delivery lock.</summary>
public interface IIdleNativeMcpProvider
{
    bool HasResidentNativeMcp(string sessionId);
    Task<IdleNativeMcpCheck> CheckIdleNativeMcpAsync(string sessionId, JobProvenance source, string bearer, CancellationToken ct);
}

public enum IdleNativeMcpCheck
{
    Current,
    Parked,
    PlanUnavailable,
    ExitUnconfirmed,
}
