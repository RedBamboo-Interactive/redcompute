using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.Plugin.OpenCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OpenCodeTranscriptProjectionTests
{
    [Fact]
    public void NativeReaderReturnsCompletedBuildPartsAndSuppressesCompactionAndIncompleteMessages()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"opencode-native-{Guid.NewGuid():N}.db");
        try
        {
            CreateNativeDb(dbPath);
            var reader = new OpenCodeNativeSessionReader(dbPath);

            var parts = reader.GetCompletedAssistantParts("session-1");

            Assert.Collection(parts,
                reasoning =>
                {
                    Assert.Equal("reasoning-1", reasoning.Id);
                    Assert.Equal("reasoning", reasoning.Type);
                    Assert.Equal("Think once.", reasoning.Text);
                },
                text =>
                {
                    Assert.Equal("text-1", text.Id);
                    Assert.Equal("text", text.Type);
                    Assert.Equal("Answer once.", text.Text);
                },
                tool =>
                {
                    Assert.Equal("tool-1", tool.Id);
                    Assert.Equal("tool", tool.Type);
                    Assert.Equal("read", tool.ToolName);
                    Assert.Equal("call-1", tool.CallId);
                    Assert.Equal("{\"filePath\":\"README.md\"}", tool.ToolInput);
                    Assert.Equal("contents", tool.ToolResult);
                    Assert.False(tool.ToolFailed);
                });

            Assert.Equal(OpenCodeNativeMessageVisibility.Visible,
                reader.GetMessageVisibility("session-1", "build-message"));
            Assert.Equal(OpenCodeNativeMessageVisibility.Compaction,
                reader.GetMessageVisibility("session-1", "compaction-message"));
            Assert.Equal(OpenCodeNativeMessageVisibility.Missing,
                reader.GetMessageVisibility("session-1", "absent"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData("complete answer", "complete ", "answer")]
    [InlineData("complete answer", "complete answer", "")]
    [InlineData("complete answer", "wrong", null)]
    public void LateChunkReconciliationOnlyEmitsAnAuthoritativeSuffix(
        string authoritative, string delivered, string? expected) =>
        Assert.Equal(expected, OpenCodeSessionService.MissingLiveSuffix(authoritative, delivered));

    [Fact]
    public void ProjectFilesystemBoundaryAllowsContainedPathsAndRejectsEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"opencode-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            OpenCodeSessionService.EnsureProjectPathAllowed(root, Path.Combine(root, "nested", "file.txt"));

            Assert.Throws<UnauthorizedAccessException>(() =>
                OpenCodeSessionService.EnsureProjectPathAllowed(root,
                    Path.Combine(root, "..", "outside.txt")));
            Assert.Throws<UnauthorizedAccessException>(() =>
                OpenCodeSessionService.EnsureProjectPathAllowed(root, "relative.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateNativeDb(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER NOT NULL, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, message_id TEXT NOT NULL, time_created INTEGER NOT NULL, data TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();

        Insert(connection, "message", "build-message", "session-1", "build-message", 1,
            """{"role":"assistant","mode":"build","time":{"created":1,"completed":9}}""");
        Insert(connection, "message", "compaction-message", "session-1", "compaction-message", 2,
            """{"role":"assistant","mode":"compaction","summary":true,"time":{"created":2,"completed":9}}""");
        Insert(connection, "message", "incomplete-message", "session-1", "incomplete-message", 3,
            """{"role":"assistant","mode":"build","time":{"created":3}}""");

        Insert(connection, "part", "reasoning-1", "session-1", "build-message", 4,
            """{"type":"reasoning","text":"Think once.","time":{"start":4,"end":5}}""");
        Insert(connection, "part", "text-1", "session-1", "build-message", 5,
            """{"type":"text","text":"Answer once.","time":{"start":5,"end":6}}""");
        Insert(connection, "part", "tool-1", "session-1", "build-message", 6,
            """{"type":"tool","tool":"read","callID":"call-1","state":{"status":"completed","input":{"filePath":"README.md"},"output":"contents","title":"README.md","time":{"start":6,"end":7}}}""");
        Insert(connection, "part", "compaction-text", "session-1", "compaction-message", 7,
            """{"type":"text","text":"## Objective"}""");
        Insert(connection, "part", "incomplete-text", "session-1", "incomplete-message", 8,
            """{"type":"text","text":"not final"}""");
    }

    private static void Insert(SqliteConnection connection, string table, string id, string sessionId,
        string messageId, long created, string data)
    {
        using var command = connection.CreateCommand();
        command.CommandText = table == "message"
            ? "INSERT INTO message(id,session_id,time_created,data) VALUES($id,$session,$created,$data)"
            : "INSERT INTO part(id,session_id,message_id,time_created,data) VALUES($id,$session,$message,$created,$data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }
}
