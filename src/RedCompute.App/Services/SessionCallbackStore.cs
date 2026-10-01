using System.IO;
using Microsoft.Data.Sqlite;

namespace RedCompute.App.Services;

internal sealed class SessionCallbackStore(string databasePath)
{
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS SessionPromptCallbacks (SessionId TEXT NOT NULL, CallbackId TEXT NOT NULL, EntryJson TEXT NOT NULL, PRIMARY KEY(SessionId, CallbackId))";
        command.ExecuteNonQuery();
        return db;
    }
    public IReadOnlyList<(string SessionId, string Json)> Load()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT SessionId, EntryJson FROM SessionPromptCallbacks";
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }
    public void Save(string sessionId, string callbackId, string json)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO SessionPromptCallbacks VALUES($session,$callback,$json) ON CONFLICT(SessionId,CallbackId) DO UPDATE SET EntryJson=excluded.EntryJson";
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$callback", callbackId); command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
    }
    public void Remove(string sessionId, string callbackId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM SessionPromptCallbacks WHERE SessionId=$session AND CallbackId=$callback";
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$callback", callbackId);
        command.ExecuteNonQuery();
    }
}
