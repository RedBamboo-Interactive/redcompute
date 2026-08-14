namespace RedCompute.Core.Sessions;

public enum SessionInputDeliveryStatus
{
    Accepted,
    Busy,
    Unavailable,
    Rejected,
}

public sealed record SessionInputDeliveryResult(
    SessionInputDeliveryStatus Status,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Retryable = false)
{
    public static SessionInputDeliveryResult Accepted() => new(SessionInputDeliveryStatus.Accepted);
    public static SessionInputDeliveryResult Busy() => new(SessionInputDeliveryStatus.Busy, "active_turn", "The session has an active turn", true);
    public static SessionInputDeliveryResult Unavailable(string code, string message, bool retryable = false) =>
        new(SessionInputDeliveryStatus.Unavailable, code, message, retryable);
    public static SessionInputDeliveryResult Rejected(string code, string message) =>
        new(SessionInputDeliveryStatus.Rejected, code, message, false);
}
