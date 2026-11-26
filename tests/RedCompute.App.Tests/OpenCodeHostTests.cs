using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, C13: OpenCode reads its native database, its inherited configuration and its project
/// files on the execution host, and the local host's SQLite query is the read the native reader and
/// the title lookup made themselves.
/// </summary>
public sealed class OpenCodeHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-opencode-host", Guid.NewGuid().ToString("N"));

    public OpenCodeHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---- the local host's SQLite read -----------------------------------------------------------------

    [Fact]
    public void A_query_returns_each_row_by_column_with_values_as_stored()
    {
        var db = Database("values.db", """
            CREATE TABLE t (id TEXT, n INTEGER, r REAL, b BLOB, z TEXT);
            INSERT INTO t VALUES ('a', 7, 1.5, x'6869', NULL);
            INSERT INTO t VALUES ('b', 8, 2.5, x'00', 'set');
            """);
        var host = new LocalExecutionHost("node_test");

        var rows = host.Queries.QuerySqliteReadOnly(host.Path(db), "SELECT id, n, r, b, z FROM t WHERE id = $id",
            new Dictionary<string, object?> { ["$id"] = "a" });

        var row = Assert.Single(rows);
        Assert.Equal(["id", "n", "r", "b", "z"], row.Keys);
        Assert.Equal("a", row["id"]);
        Assert.Equal(7L, row["n"]);
        Assert.Equal(1.5, row["r"]);
        Assert.Equal("hi"u8.ToArray(), (byte[])row["b"]!);
        Assert.Null(row["z"]);
        Assert.Empty(host.Queries.QuerySqliteReadOnly(host.Path(db), "SELECT id FROM t WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = "missing" }));
    }

    [Fact]
    public void A_query_is_read_only_and_runs_the_busy_timeout_it_is_given()
    {
        var db = Database("readonly.db", "CREATE TABLE t (id TEXT);");
        var host = new LocalExecutionHost("node_test");

        var write = Assert.Throws<SqliteException>(() =>
            host.Queries.QuerySqliteReadOnly(host.Path(db), "INSERT INTO t VALUES ('x')"));
        Assert.Equal(8, write.SqliteErrorCode); // SQLITE_READONLY

        Assert.Equal(2000L, host.Queries.QuerySqliteReadOnly(host.Path(db), "PRAGMA busy_timeout", options:
            OpenCodeNativeSessionReader.ReadOptions).Single().Values.Single());
        Assert.Equal(0L, host.Queries.QuerySqliteReadOnly(host.Path(db), "PRAGMA busy_timeout").Single().Values.Single());
    }

    [WindowsFact("an open SQLite handle blocks deleting the file only on Windows")]
    public void An_unpooled_query_leaves_no_handle_open_and_a_pooled_one_keeps_its_connection()
    {
        var host = new LocalExecutionHost("node_test");
        var unpooled = Database("unpooled.db", "CREATE TABLE t (id TEXT);");
        host.Queries.QuerySqliteReadOnly(host.Path(unpooled), "SELECT id FROM t", options: OpenCodeNativeSessionReader.ReadOptions);
        File.Delete(unpooled);
        Assert.False(File.Exists(unpooled));

        var pooled = Database("pooled.db", "CREATE TABLE t (id TEXT);");
        host.Queries.QuerySqliteReadOnly(host.Path(pooled), "SELECT id FROM t", options: SqliteReadOptions.Default);
        Assert.Throws<IOException>(() => File.Delete(pooled));
        SqliteConnection.ClearAllPools();
        File.Delete(pooled);
    }

    [Fact]
    public void A_missing_database_fails_as_opening_it_read_only_does()
    {
        var missing = Path.Combine(_root, "missing.db");
        var host = new LocalExecutionHost("node_test");
        var direct = Assert.Throws<SqliteException>(() =>
        {
            using var connection = new SqliteConnection($"Data Source={missing};Mode=ReadOnly;Pooling=False");
            connection.Open();
        });
        var hosted = Assert.Throws<SqliteException>(() =>
            host.Queries.QuerySqliteReadOnly(host.Path(missing), "SELECT 1", options: OpenCodeNativeSessionReader.ReadOptions));
        Assert.Equal(direct.Message, hosted.Message);
        Assert.Equal(direct.SqliteErrorCode, hosted.SqliteErrorCode);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Attributes_are_read_as_File_GetAttributes_reads_them()
    {
        var host = new LocalExecutionHost("node_test");
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "a");
        Assert.Equal(File.GetAttributes(file), host.Files.GetAttributes(host.Path(file)));
        Assert.Equal(File.GetAttributes(_root), host.Files.GetAttributes(host.Path(_root)));

        var missing = Path.Combine(_root, "missing.txt");
        var direct = Assert.Throws<FileNotFoundException>(() => File.GetAttributes(missing));
        var hosted = Assert.Throws<FileNotFoundException>(() => host.Files.GetAttributes(host.Path(missing)));
        Assert.Equal(direct.Message, hosted.Message);
    }

    // ---- the native reader over returned rows ---------------------------------------------------------

    /// <summary>
    /// A returned row reads as <c>SqliteDataReader</c> read the live row: text and integers alike, and
    /// on NULL the same exception with the same message.
    /// </summary>
    [Fact]
    public void Row_reads_match_the_data_reader_they_replace()
    {
        var db = Database("reads.db", """
            CREATE TABLE t (id TEXT, n INTEGER, z TEXT);
            INSERT INTO t VALUES ('a', 42, NULL);
            """);
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, n, z FROM t";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var host = new LocalExecutionHost("node_test");
        var row = host.Queries.QuerySqliteReadOnly(host.Path(db), "SELECT id, n, z FROM t").Single();

        Assert.Equal(reader.GetString(0), OpenCodeNativeSessionReader.GetString(row, "id", 0));
        Assert.Equal(reader.GetString(1), OpenCodeNativeSessionReader.GetString(row, "n", 1));
        Assert.Equal(reader.GetInt64(1), OpenCodeNativeSessionReader.GetInt64(row, "n", 1));
        var direct = Assert.ThrowsAny<Exception>(() => reader.GetString(2));
        var hosted = Assert.ThrowsAny<Exception>(() => OpenCodeNativeSessionReader.GetString(row, "z", 2));
        Assert.Equal(direct.GetType(), hosted.GetType());
        Assert.Equal(direct.Message, hosted.Message);
        var directInt = Assert.ThrowsAny<Exception>(() => reader.GetInt64(2));
        var hostedInt = Assert.ThrowsAny<Exception>(() => OpenCodeNativeSessionReader.GetInt64(row, "z", 2));
        Assert.Equal(directInt.GetType(), hostedInt.GetType());
        Assert.Equal(directInt.Message, hostedInt.Message);
    }

    /// <summary>The reader asks its own host, with the connection it always opened.</summary>
    [Fact]
    public void The_native_reader_queries_its_host_unpooled_with_a_busy_timeout()
    {
        var host = new RecordingHost();
        var dbPath = Path.Combine(_root, "native.db");
        File.WriteAllText(dbPath, "");
        var reader = new OpenCodeNativeSessionReader(dbPath, host);

        Assert.Equal(OpenCodeNativeMessageVisibility.Missing, reader.GetMessageVisibility("s1", "m1"));
        Assert.Empty(reader.GetCompletedAssistantParts("s1"));

        Assert.Collection(host.Queries.Calls,
            call =>
            {
                Assert.Equal(dbPath, call.Database.Value);
                Assert.Equal("SELECT data FROM message WHERE session_id=$session AND id=$message LIMIT 1", call.Sql);
                Assert.Equal(new Dictionary<string, object?> { ["$session"] = "s1", ["$message"] = "m1" }, call.Parameters);
                Assert.Same(OpenCodeNativeSessionReader.ReadOptions, call.Options);
            },
            call =>
            {
                Assert.Equal("SELECT id, time_created, data FROM message WHERE session_id=$session ORDER BY time_created, id", call.Sql);
                Assert.Equal(new Dictionary<string, object?> { ["$session"] = "s1" }, call.Parameters);
                Assert.Same(OpenCodeNativeSessionReader.ReadOptions, call.Options);
            });
        Assert.Equal(new SqliteReadOptions { Pooling = false, BusyTimeoutMilliseconds = 2000 }, OpenCodeNativeSessionReader.ReadOptions);
    }

    [Fact]
    public void The_database_path_is_resolved_on_the_host()
    {
        var host = new RecordingHost { Variables = { ["XDG_DATA_HOME"] = Path.Combine(_root, "xdg") } };
        Assert.Equal(Path.Combine(_root, "xdg", "opencode", "opencode.db"), OpenCodeNativeSessionReader.ResolveDbPath(host));
        Assert.Equal(Path.Combine(_root, "configured", "opencode", "opencode.db"),
            OpenCodeNativeSessionReader.ResolveDbPath(host, Path.Combine(_root, "configured")));
        Assert.Equal(Path.Combine(_root, "xdg", "opencode", "opencode.db"), new OpenCodeNativeSessionReader(host: host).DbPath);
    }

    // ---- the ACP environment ----------------------------------------------------------------------------

    /// <summary>
    /// The instruction file is named first in <c>OPENCODE_CONFIG_CONTENT</c>, merged over the value the
    /// launch would otherwise carry: the host's inherited one when the launch sets none, else its own.
    /// </summary>
    [Fact]
    public void Developer_instructions_merge_over_the_inherited_runtime_config()
    {
        var sessionId = $"test-{Guid.NewGuid():N}";
        var host = new RecordingHost { Variables = { ["OPENCODE_CONFIG_CONTENT"] = "{\"theme\":\"inherited\"}" } };
        var inherited = OpenCodeSessionService.NewEnvironment(host);
        var set = OpenCodeSessionService.NewEnvironment(host);
        set["OPENCODE_CONFIG_CONTENT"] = "{\"theme\":\"set\"}";
        string? instructionPath = null;
        try
        {
            OpenCodeSessionService.ApplyDeveloperInstructions(host, inherited, sessionId, "Be careful.");
            OpenCodeSessionService.ApplyDeveloperInstructions(host, set, sessionId, "Be careful.");

            using var fromHost = JsonDocument.Parse(inherited["OPENCODE_CONFIG_CONTENT"]);
            Assert.Equal("inherited", fromHost.RootElement.GetProperty("theme").GetString());
            instructionPath = fromHost.RootElement.GetProperty("instructions")[0].GetString();
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedCompute", "plugins", "opencode", "instructions", $"{sessionId}.md"), instructionPath);
            Assert.Equal("Be careful.", File.ReadAllText(instructionPath!));
            Assert.Equal("Be careful."u8.ToArray(), File.ReadAllBytes(instructionPath!)); // no BOM

            using var fromLaunch = JsonDocument.Parse(set["OPENCODE_CONFIG_CONTENT"]);
            Assert.Equal("set", fromLaunch.RootElement.GetProperty("theme").GetString());
            Assert.Single(inherited);
        }
        finally
        {
            if (instructionPath is not null && File.Exists(instructionPath)) File.Delete(instructionPath);
        }
    }

    /// <summary>The variables are keyed as <c>ProcessStartInfo.Environment</c> keys them on the host.</summary>
    [Fact]
    public void Launch_variables_are_keyed_as_the_start_info_environment()
    {
        var environment = OpenCodeSessionService.NewEnvironment(LocalExecutionHost.Shared);
        var start = new System.Diagnostics.ProcessStartInfo();
        environment["Golden_Key"] = "1";
        start.Environment["Golden_Key"] = "1";
        Assert.Equal(start.Environment.ContainsKey("GOLDEN_KEY"), environment.ContainsKey("GOLDEN_KEY"));
        Assert.Equal(OperatingSystem.IsWindows(), environment.ContainsKey("GOLDEN_KEY"));
    }

    // ---- project file requests --------------------------------------------------------------------------

    [Fact]
    public void Project_file_checks_ask_the_host()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(project, "nested"));
        var host = new RecordingHost();

        OpenCodeSessionService.EnsureProjectPathAllowed(host, project, Path.Combine(project, "nested", "new.txt"));

        Assert.Equal([project, Path.Combine(project, "nested")], host.Files.AttributeReads);
        Assert.Contains(Path.Combine(project, "nested", "new.txt"), host.Files.ExistenceChecks);
    }

    private string Database(string name, string sql)
    {
        var path = Path.Combine(_root, name);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return path;
    }

    /// <summary>
    /// The local host with a recorded query surface, recorded file checks and an environment of its
    /// own (falling back to this process's): an execution host whose state a test controls.
    /// </summary>
    private sealed class RecordingHost : IExecutionHost
    {
        private readonly LocalExecutionHost _local = new("node_recording");

        public RecordingHost()
        {
            Queries = new RecordingQueries();
            Files = new RecordingFiles(_local.Files);
            Platform = new VariablePlatform(_local.Platform, Variables);
        }

        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string NodeId => _local.NodeId;
        public HostPath Path(string value) => _local.Path(value);
        public IHostPathRules PathRules => _local.PathRules;
        public IHostProcesses Processes => _local.Processes;
        public IHostContainment Containment => _local.Containment;
        public RecordingFiles Files { get; }
        IHostFileSystem IExecutionHost.Files => Files;
        public IHostNetwork Network => _local.Network;
        public IHostPlatform Platform { get; }
        public IHostHardware Hardware => _local.Hardware;
        public RecordingQueries Queries { get; }
        IHostQueries IExecutionHost.Queries => Queries;
        public IHostRuntimes Runtimes => _local.Runtimes;
    }

    private sealed record QueryCall(HostPath Database, string Sql, IReadOnlyDictionary<string, object?>? Parameters,
        SqliteReadOptions? Options);

    private sealed class RecordingQueries : IHostQueries
    {
        public List<QueryCall> Calls { get; } = [];

        /// <summary>Recorded, then answered with no rows (the file the test hands is empty).</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QuerySqliteReadOnly(HostPath database, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, SqliteReadOptions? options = null)
        {
            Calls.Add(new QueryCall(database, sql, parameters, options));
            return [];
        }
    }

    private sealed class RecordingFiles(IHostFileSystem inner) : IHostFileSystem
    {
        public List<string> AttributeReads { get; } = [];
        public List<string> ExistenceChecks { get; } = [];

        public bool FileExists(HostPath path) { ExistenceChecks.Add(path.Value); return inner.FileExists(path); }
        public bool DirectoryExists(HostPath path) { ExistenceChecks.Add(path.Value); return inner.DirectoryExists(path); }
        public FileAttributes GetAttributes(HostPath path) { AttributeReads.Add(path.Value); return inner.GetAttributes(path); }
        public HostFileStat Stat(HostPath path) => inner.Stat(path);
        public string ReadAllText(HostPath path) => inner.ReadAllText(path);
        public Task<string> ReadAllTextAsync(HostPath path, CancellationToken ct = default) => inner.ReadAllTextAsync(path, ct);
        public IEnumerable<string> ReadLines(HostPath path) => inner.ReadLines(path);
        public byte[] ReadAllBytes(HostPath path) => inner.ReadAllBytes(path);
        public Stream OpenRead(HostPath path) => inner.OpenRead(path);
        public Stream OpenRead(HostPath path, FileShare share) => inner.OpenRead(path, share);
        public string? ResolveLinkTarget(HostPath path, bool returnFinalTarget) => inner.ResolveLinkTarget(path, returnFinalTarget);
        public void WriteAllText(HostPath path, string contents, System.Text.Encoding? encoding = null) => inner.WriteAllText(path, contents, encoding);
        public Task WriteAllTextAsync(HostPath path, string contents, System.Text.Encoding? encoding = null, CancellationToken ct = default)
            => inner.WriteAllTextAsync(path, contents, encoding, ct);
        public void WriteAllBytes(HostPath path, byte[] bytes) => inner.WriteAllBytes(path, bytes);
        public void ReplaceAtomically(HostPath path, string contents) => inner.ReplaceAtomically(path, contents);
        public HostPath? SetAside(HostPath path, DateTimeOffset now) => inner.SetAside(path, now);
        public void CreateDirectory(HostPath path) => inner.CreateDirectory(path);
        public IEnumerable<HostPath> EnumerateFiles(HostPath directory, string pattern, SearchOption option) => inner.EnumerateFiles(directory, pattern, option);
        public IEnumerable<HostPath> EnumerateDirectories(HostPath directory, string pattern, SearchOption option) => inner.EnumerateDirectories(directory, pattern, option);
        public void DeleteFile(HostPath path) => inner.DeleteFile(path);
        public void DeleteDirectory(HostPath path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public HostPath CreateTempDirectory(string prefix) => inner.CreateTempDirectory(prefix);
        public HostPath TempPath => inner.TempPath;
    }

    private sealed class VariablePlatform(IHostPlatform inner, Dictionary<string, string> variables) : IHostPlatform
    {
        public HostPlatformInfo Info => inner.Info;
        public bool IsWindows => inner.IsWindows;
        public bool IsLinux => inner.IsLinux;
        public bool CanLaunchLocalBackends => inner.CanLaunchLocalBackends;
        public string? GetEnvironmentVariable(string name)
            => variables.TryGetValue(name, out var value) ? value : inner.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder) => inner.GetFolderPath(folder);
        public string ComputeDataRoot => inner.ComputeDataRoot;
    }
}
