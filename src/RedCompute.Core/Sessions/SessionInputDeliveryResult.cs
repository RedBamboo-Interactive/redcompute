namespace RedCompute.Core.Sessions;

public enum SessionInputDeliveryStatus
{
    Accepted,
    Busy,
    NotSteerable,
    Unsupported,
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
    public static SessionInputDeliveryResult NotSteerable(string code = "not_steerable", string message = "The active turn cannot accept steering") =>
        new(SessionInputDeliveryStatus.NotSteerable, code, message, false);
    public static SessionInputDeliveryResult Unsupported(string message = "The provider does not support steering") =>
        new(SessionInputDeliveryStatus.Unsupported, "steering_not_supported", message, false);
    public static SessionInputDeliveryResult Unavailable(string code, string message, bool retryable = false) =>
        new(SessionInputDeliveryStatus.Unavailable, code, message, retryable);
    public static SessionInputDeliveryResult Rejected(string code, string message) =>
        new(SessionInputDeliveryStatus.Rejected, code, message, false);
}
