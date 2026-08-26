using System.Text.Json;

namespace RedCompute.Core.Sessions;

public sealed record PlannedRestartSession(string Provider, string SessionId, Guid? JobId);

public sealed record PlannedRestartData(
    string RunId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PlannedRestartSession> Sessions);

/// <summary>
/// Short-lived local handoff between the process that drained interactive sessions and the
/// replacement RedCompute process. This is lifecycle state, not an authentication token.
/// </summary>
public static class PlannedRestartCheckpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static PlannedRestartData? _current;

    public static PlannedRestartData? Current => Volatile.Read(ref _current);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "planned-restart.json");

    public static void Write(PlannedRestartData checkpoint, string? path = null)
    {
        var target = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.writing-{Environment.ProcessId}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoint, JsonOptions));
        File.Move(temporary, target, overwrite: true);
    }

    public static PlannedRestartData? LoadForStartup(
        string? path = null,
        DateTimeOffset? now = null,
        TimeSpan? maximumAge = null)
    {
        var target = Path.GetFullPath(path ?? DefaultPath);
        PlannedRestartData? checkpoint = null;
        try
        {
            if (File.Exists(target))
                checkpoint = JsonSerializer.Deserialize<PlannedRestartData>(File.ReadAllText(target), JsonOptions);
        }
        catch
        {
            checkpoint = null;
        }

        var ageLimit = maximumAge ?? TimeSpan.FromMinutes(15);
        if (checkpoint is null
            || string.IsNullOrWhiteSpace(checkpoint.RunId)
            || checkpoint.Sessions is null
            || (now ?? DateTimeOffset.UtcNow) - checkpoint.CreatedAt > ageLimit
            || checkpoint.CreatedAt - (now ?? DateTimeOffset.UtcNow) > TimeSpan.FromMinutes(1))
            checkpoint = null;

        Interlocked.Exchange(ref _current, checkpoint);
        return checkpoint;
    }

    public static bool ContainsSession(string provider, string sessionId)
        => Current?.Sessions.Any(session =>
            string.Equals(session.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) == true;

    public static HashSet<Guid> JobIds()
        => Current?.Sessions
            .Where(session => session.JobId.HasValue)
            .Select(session => session.JobId!.Value)
            .ToHashSet() ?? [];

    public static void Consume(string? path = null)
    {
        Interlocked.Exchange(ref _current, null);
        try { File.Delete(Path.GetFullPath(path ?? DefaultPath)); }
        catch { }
    }
}
