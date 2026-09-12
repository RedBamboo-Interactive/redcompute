namespace RedCompute.PluginSdk;

/// <summary>
/// Request-scoped execution restrictions for a persistent provider session.
/// The selected profile is persisted by the provider so restart/resume cannot
/// silently regain capabilities that were absent when the session was created.
/// </summary>
public static class SessionExecutionProfile
{
    public const string Default = "default";
    public const string ConversationOnly = "conversation-only";

    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string Current => Normalize(CurrentValue.Value);

    public static bool TryNormalize(string? value, out string profile)
    {
        profile = Normalize(value);
        return string.IsNullOrWhiteSpace(value)
            || value.Equals(Default, StringComparison.OrdinalIgnoreCase)
            || value.Equals(ConversationOnly, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConversationOnly(string? value)
        => Normalize(value) == ConversationOnly;

    public static IDisposable Push(string profile)
    {
        if (!TryNormalize(profile, out var normalized))
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported session execution profile");

        var previous = CurrentValue.Value;
        CurrentValue.Value = normalized;
        return new Restore(() => CurrentValue.Value = previous);
    }

    private static string Normalize(string? value)
        => value?.Trim().Equals(ConversationOnly, StringComparison.OrdinalIgnoreCase) == true
            ? ConversationOnly
            : Default;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
