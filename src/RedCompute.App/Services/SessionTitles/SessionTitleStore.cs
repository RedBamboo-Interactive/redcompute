using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services.SessionTitles;

/// <summary>An explicit session title: a person's rename or a requested fast-tier update.</summary>
public sealed record SessionTitleOverride(string SessionId, string Provider, string Title, string Source, DateTimeOffset SetAt);

public static class SessionTitleSources
{
    public const string Manual = "manual";
    public const string Generated = "generated";
    public const string Provider = "provider";

    public static bool IsExplicit(string? source) => source is Manual or Generated;
}

/// <summary>
/// The one precedence rule for session titles. Providers own their titles (Claude's ai-title and
/// first-turn titling, Codex thread names, OpenCode updates) and keep writing them to their own
/// stores; an explicit title recorded here wins over all of them wherever a session leaves
/// RedCompute: authenticated reads, live <c>session.updated</c> events and the RedLeaf
/// <c>ai-session</c> mirror (entity name plus <c>title_source</c>). Explicit titles replace each
/// other, latest first, matching Nova's discussion policy: a rename beats an earlier update and an
/// update beats an earlier rename; provider sync replaces neither.
///
/// Durable in RedCompute's own SQLite database, so a restart or a provider save cannot undo a
/// rename; the RedLeaf record carries the same title and source as its durable mirror.
/// </summary>
public sealed class SessionTitleStore
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, SessionTitleOverride> _titles = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SessionTitleStore(string? databasePath = null)
    {
        var usePooling = databasePath is null;
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "redcompute.db");
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = usePooling,
            DefaultTimeout = 5,
        }.ToString();
        EnsureSchema();
        Load();
    }

    public SessionTitleOverride? Get(string sessionId)
        => _titles.TryGetValue(sessionId, out var title) ? title : null;

    /// <summary>Records an explicit title and returns the one it replaced, for <see cref="Restore"/>.</summary>
    public SessionTitleOverride? Set(string sessionId, string provider, string title, string source)
    {
        if (!SessionTitleSources.IsExplicit(source))
            throw new ArgumentException("Only manual and generated titles are recorded", nameof(source));
        lock (_gate)
        {
            var previous = Get(sessionId);
            var next = new SessionTitleOverride(sessionId, provider, title, source, DateTimeOffset.UtcNow);
            Write(next);
            _titles[sessionId] = next;
            return previous;
        }
    }

    /// <summary>Puts back what <see cref="Set"/> replaced, when the change could not be mirrored.</summary>
    public void Restore(string sessionId, SessionTitleOverride? previous)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            if (previous is null)
            {
                command.CommandText = "DELETE FROM SessionTitleOverrides WHERE SessionId = $id";
                command.Parameters.AddWithValue("$id", sessionId);
                command.ExecuteNonQuery();
                _titles.TryRemove(sessionId, out _);
                return;
            }
            Write(previous);
            _titles[sessionId] = previous;
        }
    }

    /// <summary>
    /// Applies the precedence to a session leaving RedCompute: an explicit title wins; otherwise a
    /// title the RedLeaf record already marks explicit is adopted (a rebuilt local database
    /// recovers from its mirror), and any other title is the provider's.
    /// </summary>
    public UnifiedSessionInfo Apply(UnifiedSessionInfo session)
    {
        if (Get(session.Id) is { } explicitTitle)
        {
            session.Title = explicitTitle.Title;
            session.TitleSource = explicitTitle.Source;
            return session;
        }
        if (SessionTitleSources.IsExplicit(session.TitleSource) && !string.IsNullOrWhiteSpace(session.Title))
        {
            try { Set(session.Id, session.Provider, session.Title, session.TitleSource!); }
            catch (SqliteException) { /* the read still answers; the next read adopts it again */ }
            return session;
        }
        session.TitleSource = string.IsNullOrWhiteSpace(session.Title) ? null : SessionTitleSources.Provider;
        return session;
    }

    /// <summary>
    /// The name and <c>title_source</c> a provider snapshot mirrors to RedLeaf with: an explicit
    /// title wins over the provider's, which wins over the fallback (untitled, no source).
    /// </summary>
    public static (string Name, string? Source) ForMirror(string? providerTitle, string fallback,
        SessionTitleOverride? explicitTitle)
        => explicitTitle is not null
            ? (explicitTitle.Title, explicitTitle.Source)
            : providerTitle is null
                ? (fallback, null)
                : (providerTitle, SessionTitleSources.Provider);

    private void Write(SessionTitleOverride title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionTitleOverrides (SessionId, Provider, Title, Source, SetAt)
            VALUES ($id, $provider, $title, $source, $setAt)
            ON CONFLICT(SessionId) DO UPDATE SET
                Provider = excluded.Provider, Title = excluded.Title,
                Source = excluded.Source, SetAt = excluded.SetAt
            """;
        command.Parameters.AddWithValue("$id", title.SessionId);
        command.Parameters.AddWithValue("$provider", title.Provider);
        command.Parameters.AddWithValue("$title", title.Title);
        command.Parameters.AddWithValue("$source", title.Source);
        command.Parameters.AddWithValue("$setAt", title.SetAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, Provider, Title, Source, SetAt FROM SessionTitleOverrides";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = new SessionTitleOverride(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.TryParse(reader.GetString(4), out var at) ? at : DateTimeOffset.MinValue);
            _titles[title.SessionId] = title;
        }
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionTitleOverrides (
                SessionId TEXT PRIMARY KEY,
                Provider TEXT NOT NULL,
                Title TEXT NOT NULL,
                Source TEXT NOT NULL,
                SetAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
