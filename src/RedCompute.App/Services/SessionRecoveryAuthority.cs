using System.Text.Json.Serialization;

namespace RedCompute.App.Services;

// Ephemeral internal result. Never put the token in queue storage, logs or public DTOs.
public sealed record SessionRecoveryAuthority([property: JsonIgnore] string? AccessToken, string? ErrorCode, bool Retryable)
{
    public override string ToString() => $"SessionRecoveryAuthority {{ Authorized = {AccessToken is not null}, ErrorCode = {ErrorCode}, Retryable = {Retryable} }}";
}
