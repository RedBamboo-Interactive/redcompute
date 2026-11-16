using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Storage;

namespace RedCompute.App.Rebase;

/// <summary>What one owner did to its stored paths.</summary>
/// <param name="Examined">Values looked at.</param>
/// <param name="Changed">Values rewritten (rebased, made relative, or both).</param>
/// <param name="Skipped">Values that could not be rewritten and were left as they were (a UNIQUE clash).</param>
public sealed record RebaseOwnerResult(string Owner, int Examined, int Changed, int Skipped = 0);

/// <summary>
/// One store of paths and the rule that rewrites its legacy absolute values after a move. A rebaser is
/// idempotent (a second run, or a run after a crash halfway, changes nothing more), touches only values
/// under a <c>from</c> folder of the map, and runs before the owner's service opens the store.
/// </summary>
public interface IStoredPathRebaser
{
    string Owner { get; }
    RebaseOwnerResult Rebase(RebaseMap map);
}

/// <summary>The shared SQLite and JSON mechanics of the rebasers.</summary>
internal static class RebaseSupport
{
    /// <summary>
    /// Rewrites one text column row by row. <paramref name="transform"/> returns the new value, or null
    /// to leave the row alone. A missing database, table or column is not an error: there is nothing to rebase.
    /// </summary>
    public static (int Examined, int Changed, int Skipped) RewriteColumns(
        string databasePath, string table, string[] columns, Func<string, string?> transform)
    {
        if (!File.Exists(databasePath)) return (0, 0, 0);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 120,
        }.ToString());
        connection.Open();

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read()) present.Add(reader.GetString(1));
        }
        var live = columns.Where(present.Contains).ToArray();
        if (live.Length == 0) return (0, 0, 0);

        var pending = new List<(long RowId, string Column, string Value)>();
        var examined = 0;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT rowid, {string.Join(", ", live.Select(c => $"\"{c}\""))} FROM {table}";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var rowId = reader.GetInt64(0);
                for (var i = 0; i < live.Length; i++)
                {
                    if (reader.IsDBNull(i + 1)) continue;
                    var value = reader.GetString(i + 1);
                    if (value.Length == 0) continue;
                    examined++;
                    if (transform(value) is { } updated && !string.Equals(updated, value, StringComparison.Ordinal))
                        pending.Add((rowId, live[i], updated));
                }
            }
        }

        var changed = 0;
        var skipped = 0;
        for (var offset = 0; offset < pending.Count; offset += 500)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var (rowId, column, value) in pending.Skip(offset).Take(500))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET \"{column}\" = $value WHERE rowid = $rowid";
                update.Parameters.AddWithValue("$value", value);
                update.Parameters.AddWithValue("$rowid", rowId);
                try
                {
                    update.ExecuteNonQuery();
                    changed++;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // A UNIQUE clash: another row already holds the target value. Leave this one as it was.
                    skipped++;
                }
            }
            transaction.Commit();
        }
        return (examined, changed, skipped);
    }

    /// <summary>
    /// Walks a JSON tree and rewrites the string values <paramref name="transform"/> accepts, in place.
    /// Returns how many strings it examined and changed.
    /// </summary>
    public static (int Examined, int Changed) RewriteJson(
        JsonNode? node, Func<string, string, string?> transform, string? propertyName = null)
    {
        var examined = 0;
        var changed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                    {
                        if (string.IsNullOrEmpty(text)) continue;
                        var updated = transform(property.Key, text);
                        if (updated is null) continue;
                        examined++;
                        if (!string.Equals(updated, text, StringComparison.Ordinal))
                        {
                            obj[property.Key] = updated;
                            changed++;
                        }
                    }
                    else
                    {
                        var (e, c) = RewriteJson(property.Value, transform, property.Key);
                        examined += e;
                        changed += c;
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    var (e, c) = RewriteJson(item, transform, propertyName);
                    examined += e;
                    changed += c;
                }
                break;
        }
        return (examined, changed);
    }

    /// <summary>Writes the file through a sibling temp file so a crash never leaves it half written.</summary>
    public static void WriteJsonAtomically(string path, JsonNode node, JsonSerializerOptions options)
    {
        var temp = path + ".rebase.tmp";
        File.WriteAllText(temp, node.ToJsonString(options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// <c>Jobs.OutputLocation</c> in <c>redcompute.db</c>: an absolute value under a <c>from</c> folder becomes the
/// same absolute value under its <c>to</c> folder. Every other value is left exactly as stored.
/// </summary>
public sealed class JobOutputsRebaser(ComputeStorage storage) : IStoredPathRebaser
{
    public string Owner => "job-outputs";

    public RebaseOwnerResult Rebase(RebaseMap map)
    {
        var (examined, changed, skipped) = RebaseSupport.RewriteColumns(
            storage.Database, "Jobs", ["OutputLocation"], map.Rebase);
        return new RebaseOwnerResult(Owner, examined, changed, skipped);
    }
}

/// <summary>
/// <c>InputAttachments.StoredPath</c> (UNIQUE) in <c>attachments.db</c>: an absolute value under a <c>from</c>
/// folder is rebased, anything else left as stored. The full path already written into prompt text and Codex
/// <c>localImage</c> inputs is history and is not rewritten; delivery resolves the stored row by attachment id.
/// </summary>
public sealed class InputAttachmentsRebaser(ComputeStorage storage) : IStoredPathRebaser
{
    public string Owner => "input-attachments";

    public RebaseOwnerResult Rebase(RebaseMap map)
    {
        var (examined, changed, skipped) = RebaseSupport.RewriteColumns(
            storage.AttachmentsDatabase, "InputAttachments", ["StoredPath"], map.Rebase);
        return new RebaseOwnerResult(Owner, examined, changed, skipped);
    }
}

/// <summary>
/// A session plugin database (Claude, Codex, OpenCode): <c>Sessions.ProjectPath</c> and
/// <c>Sessions.ScratchDirectory</c> are real working directories and stay absolute, rebased when under a
/// <c>from</c> folder. The CLIs find their own history by session id, whatever the working directory.
/// </summary>
public sealed class SessionDatabaseRebaser(string owner, string databasePath) : IStoredPathRebaser
{
    public string Owner => owner;

    public RebaseOwnerResult Rebase(RebaseMap map)
    {
        var (examined, changed, skipped) = RebaseSupport.RewriteColumns(
            databasePath, "Sessions", ["ProjectPath", "ScratchDirectory"], map.Rebase);
        return new RebaseOwnerResult(Owner, examined, changed, skipped);
    }
}

/// <summary>
/// <c>long-lived-processes.json</c>: <c>logPath</c> and <c>workingDirectory</c> under a <c>from</c> folder are
/// rebased; every other value is left as stored.
/// </summary>
public sealed class LongLivedProcessesRebaser(ComputeStorage storage) : IStoredPathRebaser
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Owner => "long-lived-processes";

    public RebaseOwnerResult Rebase(RebaseMap map)
    {
        var path = Path.Combine(storage.LongLivedProcessesDirectory, "long-lived-processes.json");
        if (!File.Exists(path)) return new RebaseOwnerResult(Owner, 0, 0);

        var root = JsonNode.Parse(File.ReadAllText(path));
        var (examined, changed) = RebaseSupport.RewriteJson(root, (key, value) =>
            key.Equals("logPath", StringComparison.OrdinalIgnoreCase)
            || key.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase)
                ? map.Rebase(value)
                : null);
        if (changed > 0) RebaseSupport.WriteJsonAtomically(path, root!, WriteOptions);
        return new RebaseOwnerResult(Owner, examined, changed);
    }
}

/// <summary>
/// <c>config.json</c>: provider path settings (<c>ServerPath</c>, <c>VenvPath</c>, <c>VoicesBasePath</c>, the
/// ComfyUI <c>*Directory</c> and <c>WorkflowsDir</c> settings) under a <c>from</c> folder are rebased. A value
/// outside every <c>from</c> folder (<c>T:\Projects\ComfyUI</c>, <c>F:\AI\...</c>) is never touched.
/// </summary>
public sealed class ConfigRebaser(ComputeStorage storage) : IStoredPathRebaser
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Owner => "config";

    internal static bool IsPathSetting(string key)
        => key.Equals("ServerPath", StringComparison.OrdinalIgnoreCase)
           || key.Equals("VenvPath", StringComparison.OrdinalIgnoreCase)
           || key.Equals("VoicesBasePath", StringComparison.OrdinalIgnoreCase)
           || key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase)
           || key.EndsWith("Dir", StringComparison.OrdinalIgnoreCase);

    public RebaseOwnerResult Rebase(RebaseMap map)
    {
        var path = storage.ConfigFile;
        if (!File.Exists(path)) return new RebaseOwnerResult(Owner, 0, 0);

        var root = JsonNode.Parse(File.ReadAllText(path));
        var (examined, changed) = RebaseSupport.RewriteJson(root, (key, value) =>
            IsPathSetting(key) ? map.Rebase(value) : null);
        if (changed > 0) RebaseSupport.WriteJsonAtomically(path, root!, WriteOptions);
        return new RebaseOwnerResult(Owner, examined, changed);
    }
}

/// <summary>Every rebaser RedCompute owns, in run order.</summary>
public static class StoredPathRebasers
{
    public static IReadOnlyList<IStoredPathRebaser> For(ComputeStorage storage) =>
    [
        new JobOutputsRebaser(storage),
        new InputAttachmentsRebaser(storage),
        new SessionDatabaseRebaser("claude-sessions", Path.Combine(storage.PluginDirectory("claude-code"), "claude.db")),
        new SessionDatabaseRebaser("codex-sessions", Path.Combine(storage.PluginDirectory("codex"), "codex.db")),
        new SessionDatabaseRebaser("opencode-sessions", Path.Combine(storage.PluginDirectory("opencode"), "opencode.db")),
        new LongLivedProcessesRebaser(storage),
        new ConfigRebaser(storage),
    ];
}
