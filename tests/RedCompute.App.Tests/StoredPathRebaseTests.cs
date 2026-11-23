using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedCompute.App;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Rebase;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Core.Storage;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Data relocation phase 2B: writers keep the absolute form (a rolled-back build must read it), readers also accept a
/// relative value, and a rebaser rewrites an absolute value under a <c>from</c> folder of <c>REDCOMPUTE_REBASE_MAP</c>
/// to the same absolute value under its <c>to</c> folder. A value outside every <c>from</c> folder is
/// never touched, a sibling folder that merely shares a name prefix (<c>RedCompute2</c>) is not under it, and a
/// second run changes nothing.
/// </summary>
public sealed class StoredPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rc-stored-paths");

    [Fact]
    public void A_folder_boundary_is_a_separator_never_a_raw_prefix()
    {
        var data = Path.Combine(Root, "Data");
        Assert.True(StoredPaths.IsUnder(data, data));
        Assert.True(StoredPaths.IsUnder(data, Path.Combine(data, "a", "b.png")));
        Assert.True(StoredPaths.IsUnder(data + Path.DirectorySeparatorChar, Path.Combine(data, "a.png")));
        Assert.False(StoredPaths.IsUnder(data, Path.Combine(Root, "Data2", "a.png")));
        Assert.False(StoredPaths.IsUnder(data, Path.Combine(Root, "Dat")));
        Assert.False(StoredPaths.IsUnder(data, Path.Combine(Root, "Other", "Data", "a.png")));
    }

    [WindowsFact("Windows paths compare case-insensitively")]
    public void Windows_paths_match_without_regard_to_case_or_separator_style()
    {
        Assert.True(StoredPaths.IsUnder(@"C:\Users\A\AppData\Local\RedCompute", @"c:/users/a/appdata/local/redcompute/outputs/x.png"));
        Assert.False(StoredPaths.IsUnder(@"C:\Data", @"C:\Data2"));
        Assert.False(StoredPaths.IsUnder(@"C:\Data", @"D:\Data\x"));
    }

    [Fact]
    public void A_path_inside_the_root_is_stored_relative_and_resolves_back()
    {
        var outputs = Path.Combine(Root, "outputs");
        var inside = Path.Combine(outputs, "sub", "a.png");

        var stored = StoredPaths.ToStored(outputs, inside);

        Assert.Equal("sub/a.png", stored);
        Assert.Equal(inside, StoredPaths.Resolve(outputs, stored));
    }

    [Fact]
    public void A_path_outside_the_root_and_an_already_relative_value_are_left_alone()
    {
        var outputs = Path.Combine(Root, "outputs");
        var outside = Path.Combine(Root, "external", "ComfyUI", "out.png");
        var sibling = Path.Combine(Root, "outputs2", "a.png");

        Assert.Equal(outside, StoredPaths.ToStored(outputs, outside));
        Assert.Equal(sibling, StoredPaths.ToStored(outputs, sibling));
        Assert.Equal("a.png", StoredPaths.ToStored(outputs, "a.png"));
        Assert.Equal(outside, StoredPaths.Resolve(outputs, outside));
        Assert.Null(StoredPaths.ToStored(outputs, null));
        Assert.Equal("", StoredPaths.ToStored(outputs, ""));
    }

    [Fact]
    public void A_relative_value_that_climbs_out_of_the_root_resolves_to_nothing()
    {
        var outputs = Path.Combine(Root, "outputs");
        Assert.Equal(StoredPaths.Unresolvable, StoredPaths.Resolve(outputs, "../secret.txt"));
        Assert.Equal(StoredPaths.Unresolvable, StoredPaths.Resolve(outputs, "a/../../secret.txt"));
        Assert.False(File.Exists(StoredPaths.Resolve(outputs, "../secret.txt")));
    }
}

public sealed class RebaseMapTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "rc-rebase-map");
    private static string From => Path.Combine(Base, "old", "RedCompute");
    private static string To => Path.Combine(Base, "new", "RedCompute");

    private static string Json(string from, string to, long generation = 3)
        => JsonSerializer.Serialize(new { generation, map = new[] { new { from, to } } });

    [Fact]
    public void An_absent_variable_means_nothing_to_do()
        => Assert.Null(RebaseMap.FromEnvironment(_ => null));

    [Fact]
    public void A_valid_map_is_read_in_order_with_its_generation()
    {
        var other = Path.Combine(Base, "old", "RedLeaf");
        var json = JsonSerializer.Serialize(new
        {
            generation = 7,
            map = new[] { new { from = other, to = Path.Combine(Base, "new") }, new { from = From, to = To } },
        });

        var map = RebaseMap.FromEnvironment(name => name == RebaseMap.Variable ? json : null)!;

        Assert.Equal(7, map.Generation);
        Assert.Equal([new RebaseEntry(other, Path.Combine(Base, "new")), new RebaseEntry(From, To)], map.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"generation\":\"3\",\"map\":[]}")]
    [InlineData("{\"generation\":-1,\"map\":[]}")]
    [InlineData("{\"generation\":1}")]
    [InlineData("{\"generation\":1,\"map\":{}}")]
    [InlineData("{\"generation\":1,\"map\":[{\"from\":\"relative\\\\path\",\"to\":\"C:\\\\x\"}]}")]
    [InlineData("{\"generation\":1,\"map\":[{\"from\":\"/abs/ok\"}]}")]
    [InlineData("{\"generation\":1,\"map\":[7]}")]
    public void A_malformed_map_is_refused(string json)
        => Assert.Throws<RebaseMapException>(() => RebaseMap.Parse(json));

    [Fact]
    public void A_malformed_variable_stops_startup_like_a_bad_data_root()
    {
        Assert.False(StorageStartupCheck.Run(name => name == RebaseMap.Variable ? "{ nonsense" : null));
        Assert.True(StorageStartupCheck.Run(name => name == RebaseMap.Variable ? Json(From, To) : null));
        Assert.Equal(3, StorageStartupCheck.PendingRebase!.Generation);
        Assert.True(StorageStartupCheck.Run(_ => null));
        Assert.Null(StorageStartupCheck.PendingRebase);
    }

    [Fact]
    public void A_value_under_a_from_folder_moves_under_its_to_folder()
    {
        var map = RebaseMap.Parse(Json(From, To));
        var moved = map.Rebase(Path.Combine(From, "outputs", "a.png"));

        Assert.NotNull(moved);
        Assert.Equal(Path.GetFullPath(Path.Combine(To, "outputs", "a.png")), Path.GetFullPath(moved!));
        Assert.Equal(To, map.Rebase(From));
    }

    [Fact]
    public void A_value_outside_every_from_folder_is_not_touched()
    {
        var map = RebaseMap.Parse(Json(From, To));
        Assert.Null(map.Rebase(Path.Combine(Base, "external", "ComfyUI", "out.png")));
        Assert.Null(map.Rebase("relative/a.png"));
        Assert.Null(map.Rebase(null));
        Assert.Null(map.Rebase(""));
    }

    [Fact]
    public void A_sibling_folder_sharing_a_name_prefix_is_not_under_the_from_folder()
    {
        var map = RebaseMap.Parse(Json(From, To));
        Assert.Null(map.Rebase(From + "2" + Path.DirectorySeparatorChar + "outputs" + Path.DirectorySeparatorChar + "a.png"));
        Assert.Null(map.Rebase(From + "2"));
    }

    [WindowsFact("the literal paths are Windows paths")]
    public void The_boundary_holds_for_literal_windows_folders_and_case_does_not_matter()
    {
        var map = RebaseMap.Parse(Json(@"C:\Data", @"D:\Moved\Data"));
        Assert.Null(map.Rebase(@"C:\Data2\x.png"));
        Assert.Equal(@"D:\Moved\Data\x\y.png", map.Rebase(@"c:\DATA\x\y.png"));
        Assert.Equal(@"D:\Moved\Data", map.Rebase(@"C:\Data\"));
    }

    [Fact]
    public void Rebasing_twice_changes_nothing_more()
    {
        var map = RebaseMap.Parse(Json(From, To));
        var once = map.Rebase(Path.Combine(From, "outputs", "a.png"))!;

        Assert.Null(map.Rebase(once));
    }

    [Fact]
    public void A_to_folder_inside_its_from_folder_is_not_applied_twice()
    {
        var inner = Path.Combine(From, "moved");
        var map = RebaseMap.Parse(Json(From, inner));
        var once = map.Rebase(Path.Combine(From, "a.png"))!;

        Assert.Null(map.Rebase(once));
    }

    [Fact]
    public void The_most_specific_from_folder_wins_when_folders_nest()
    {
        var leaf = Path.Combine(Base, "old", "RedLeaf");
        var json = JsonSerializer.Serialize(new
        {
            generation = 1,
            map = new[]
            {
                new { from = leaf, to = Path.Combine(Base, "new") },
                new { from = Path.Combine(leaf, "RedCompute"), to = Path.Combine(Base, "new", "RedCompute") },
            },
        });
        var map = RebaseMap.Parse(json);

        var moved = map.Rebase(Path.Combine(leaf, "RedCompute", "outputs", "a.png"))!;

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "new", "RedCompute", "outputs", "a.png")), Path.GetFullPath(moved));
    }
}

/// <summary>Each rebaser against a small database or file, then the whole move end to end.</summary>
[Collection("Plugin database path")]
public sealed class StoredPathRebaseEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-rebase-e2e", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;
    private readonly string _openCodePath = OpenCodeDbContext.DbPath;

    // The move: RedLeaf's data root and RedCompute's own root, old and new.
    private string OldLeaf => Path.Combine(_root, "old", "RedLeaf");
    private string NewLeaf => Path.Combine(_root, "new", "RedLeaf");
    private string OldRoot => Path.Combine(_root, "old", "RedCompute");
    private string NewRoot => Path.Combine(_root, "new", "RedCompute");
    private string Sibling => Path.Combine(_root, "old", "RedCompute2");
    private string External => Path.Combine(_root, "external");

    private ComputeStorage OldStorage => new(OldRoot, Path.Combine(_root, "suite"), isRelocated: true);
    private ComputeStorage NewStorage => new(NewRoot, Path.Combine(_root, "suite"), isRelocated: true);

    private RebaseMap Map => new(5,
    [
        new RebaseEntry(OldLeaf, NewLeaf),
        new RebaseEntry(OldRoot, NewRoot),
    ]);

    public StoredPathRebaseEndToEndTests()
    {
        Directory.CreateDirectory(_root);
        JobOutputPaths.OverrideRoot = null;
    }

    public void Dispose()
    {
        JobOutputPaths.OverrideRoot = null;
        ClaudeDbContext.DbPath = _claudePath;
        CodexDbContext.DbPath = _codexPath;
        OpenCodeDbContext.DbPath = _openCodePath;
        SqliteConnection.ClearAllPools();
        try { DeleteTree(_root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Staged attachments are read-only files, which a plain recursive delete refuses.</summary>
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    // ------------------------------------------------------------------------------------ helpers

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static List<string?> Column(string database, string sql)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string?>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return values;
    }

    private static string Touch(string path, string text = "bytes")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static string Full(string? path) => Path.GetFullPath(path!);

    // ------------------------------------------------------------------------------ job outputs

    private Guid SeedJob(string database, string? legacyAbsolute)
    {
        using var db = new RedComputeDbContext(database);
        var job = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "comfyui", Status = JobStatus.Completed };
        db.Jobs.Add(job);
        db.SaveChanges();
        // The row as an older build wrote it: an absolute path.
        Execute(database, "UPDATE Jobs SET OutputLocation = $v WHERE Id = $id",
            ("$v", legacyAbsolute), ("$id", job.Id.ToString().ToUpperInvariant()));
        return job.Id;
    }

    private string CreateJobDatabase(ComputeStorage storage)
    {
        using var db = new RedComputeDbContext(storage.Database);
        db.Database.EnsureCreated();
        db.MigrateSchema();
        return storage.Database;
    }

    private static string? Stored(string database, Guid id)
        => Column(database, $"SELECT OutputLocation FROM Jobs WHERE upper(Id) = '{id.ToString().ToUpperInvariant()}'").Single();

    [Fact]
    public void Job_output_paths_are_written_absolute_and_a_relative_value_still_reads()
    {
        var storage = NewStorage;
        var database = CreateJobDatabase(storage);
        JobOutputPaths.OverrideRoot = storage.Outputs;
        var file = Touch(Path.Combine(storage.Outputs, "new.png"));
        var legacy = SeedJob(database, Touch(Path.Combine(External, "legacy.png")));

        Guid written;
        using (var db = new RedComputeDbContext(database))
        {
            var job = new JobRecord { CapabilitySlug = "image-gen", ProviderName = "comfyui", OutputLocation = file };
            db.Jobs.Add(job);
            db.SaveChanges();
            written = job.Id;
        }

        Assert.Equal(file, Stored(database, written));
        // A relative value (a later release may write one) resolves against the outputs folder.
        var relative = SeedJob(database, "new.png");
        using var read = new RedComputeDbContext(database);
        Assert.Equal(Full(file), Full(read.Jobs.Single(j => j.Id == relative).OutputLocation));
        Assert.Equal(Full(file), Full(read.Jobs.Single(j => j.Id == written).OutputLocation));
        Assert.Equal(Full(Path.Combine(External, "legacy.png")), Full(read.Jobs.Single(j => j.Id == legacy).OutputLocation));
    }

    [Fact]
    public void The_job_output_rebaser_moves_absolute_values_under_a_from_folder_and_leaves_every_other_value_as_stored()
    {
        var storage = NewStorage;
        var database = CreateJobDatabase(storage);
        var inOutputs = SeedJob(database, Path.Combine(OldRoot, "outputs", "a.png"));
        var nested = SeedJob(database, Path.Combine(OldRoot, "outputs", "sub", "b.png"));
        var otherUnderFrom = SeedJob(database, Path.Combine(OldRoot, "elsewhere", "c.png"));
        var outside = SeedJob(database, Path.Combine(External, "ComfyUI", "d.png"));
        var boundary = SeedJob(database, Path.Combine(Sibling, "outputs", "e.png"));
        var alreadyRelative = SeedJob(database, "f.png");
        var none = SeedJob(database, null);

        var result = new JobOutputsRebaser(storage).Rebase(Map);

        Assert.Equal(3, result.Changed);
        Assert.Equal(Full(Path.Combine(NewRoot, "outputs", "a.png")), Full(Stored(database, inOutputs)));
        Assert.Equal(Full(Path.Combine(NewRoot, "outputs", "sub", "b.png")), Full(Stored(database, nested)));
        Assert.Equal(Full(Path.Combine(NewRoot, "elsewhere", "c.png")), Full(Stored(database, otherUnderFrom)));
        Assert.Equal(Path.Combine(External, "ComfyUI", "d.png"), Stored(database, outside));
        Assert.Equal(Path.Combine(Sibling, "outputs", "e.png"), Stored(database, boundary));
        Assert.Equal("f.png", Stored(database, alreadyRelative));
        Assert.Null(Stored(database, none));
    }

    [Fact]
    public void The_job_output_rebaser_is_idempotent()
    {
        var storage = NewStorage;
        var database = CreateJobDatabase(storage);
        SeedJob(database, Path.Combine(OldRoot, "outputs", "a.png"));
        SeedJob(database, Path.Combine(OldRoot, "elsewhere", "c.png"));
        SeedJob(database, Path.Combine(External, "d.png"));

        var first = new JobOutputsRebaser(storage).Rebase(Map);
        var after = Column(database, "SELECT OutputLocation FROM Jobs ORDER BY rowid");
        var second = new JobOutputsRebaser(storage).Rebase(Map);

        Assert.Equal(2, first.Changed);
        Assert.Equal(0, second.Changed);
        Assert.Equal(after, Column(database, "SELECT OutputLocation FROM Jobs ORDER BY rowid"));
    }

    [Fact]
    public void An_old_job_output_downloads_from_the_new_location_through_the_resolver()
    {
        var storage = NewStorage;
        var database = CreateJobDatabase(storage);
        var id = SeedJob(database, Path.Combine(OldRoot, "outputs", "moved.png"));
        // The data was copied: the bytes exist only under the new root.
        Touch(Path.Combine(storage.Outputs, "moved.png"), "png bytes");
        Assert.False(File.Exists(Path.Combine(OldRoot, "outputs", "moved.png")));

        new JobOutputsRebaser(storage).Rebase(Map);

        JobOutputPaths.OverrideRoot = storage.Outputs;
        using var db = new RedComputeDbContext(database);
        var location = db.Jobs.Single(j => j.Id == id).OutputLocation;
        Assert.True(File.Exists(location));
        Assert.Equal("png bytes", File.ReadAllText(location!));
        Assert.StartsWith(Full(NewRoot), Full(location), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retention_cleanup_deletes_the_file_a_stored_output_names()
    {
        var storage = NewStorage;
        var database = CreateJobDatabase(storage);
        JobOutputPaths.OverrideRoot = storage.Outputs;
        var file = Touch(Path.Combine(storage.Outputs, "old.png"));
        using (var db = new RedComputeDbContext(database))
        {
            db.Jobs.Add(new JobRecord { CapabilitySlug = "image-gen", ProviderName = "comfyui", OutputLocation = file });
            db.SaveChanges();
        }
        Assert.Equal(file, Column(database, "SELECT OutputLocation FROM Jobs ORDER BY rowid").First());

        // A negative retention makes every job old.
        var removed = new JobTrackingService(() => new RedComputeDbContext(database)).CleanupOldJobs(-1);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(file));
    }

    // ------------------------------------------------------------------------------ attachments

    private static RedComputeConfig AttachmentConfig() => new()
    {
        InputAttachmentTtlMinutes = 60,
        InputAttachmentMaxFileSizeBytes = 4096,
        InputAttachmentMaxCount = 4,
        InputAttachmentMaxTurnSizeBytes = 8192,
    };

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task Attachments_are_stored_absolute_and_a_relative_row_still_resolves()
    {
        var storage = NewStorage;
        var store = new InputAttachmentStore(AttachmentConfig(), storage.InputAttachments);
        var uploaded = await store.UploadBytesAsync(Png(), "a.png", "image/png", "owner");

        var raw = Column(storage.AttachmentsDatabase, "SELECT StoredPath FROM InputAttachments").Single()!;
        Assert.Equal(uploaded.StoredPath, raw);
        Assert.Equal(Full(uploaded.StoredPath), Full((await store.GetAuthorizedAsync(uploaded.Id, "owner"))!.StoredPath));

        // A relative row (a later release may write one) resolves against the attachments folder.
        Execute(storage.AttachmentsDatabase, "UPDATE InputAttachments SET StoredPath = $p",
            ("$p", Path.GetRelativePath(storage.InputAttachments, uploaded.StoredPath)));
        Assert.Equal(Full(uploaded.StoredPath), Full((await store.GetAuthorizedAsync(uploaded.Id, "owner"))!.StoredPath));
    }

    [Fact]
    public async Task The_attachment_rebaser_rewrites_legacy_paths_and_a_re_sent_queued_input_resolves_the_row()
    {
        // Before the move: attachments under the old root, one queued input that carries it.
        var oldStore = new InputAttachmentStore(AttachmentConfig(), OldStorage.InputAttachments);
        var uploaded = await oldStore.UploadBytesAsync(Png(), "shot.png", "image/png", "owner");
        var oldPath = uploaded.StoredPath;
        var queue = new SessionInputQueueStore(AttachmentConfig(), oldStore);
        await queue.EnqueueAsync(new SessionInputQueueSubmission("session-1", "owner",
            [new QueuedSessionInputPart("attachment", uploaded.Id)], "look at this", null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null),
            SessionInputDeliveryPolicy.AfterCurrent, "uid-1", [uploaded.Id], null));
        // The row as the old build wrote it, absolute; then the folder is copied to the new root.
        Execute(OldStorage.AttachmentsDatabase, "UPDATE InputAttachments SET StoredPath = $p", ("$p", oldPath));
        Directory.CreateDirectory(NewStorage.InputAttachments);
        File.Copy(OldStorage.AttachmentsDatabase, NewStorage.AttachmentsDatabase);
        var newPath = Path.Combine(NewStorage.InputAttachments, Path.GetRelativePath(OldStorage.InputAttachments, oldPath));
        Touch(newPath, "same bytes");
        SqliteConnection.ClearAllPools();
        DeleteTree(OldRoot);

        var result = new InputAttachmentsRebaser(NewStorage).Rebase(Map);

        Assert.Equal(1, result.Changed);
        var raw = Column(NewStorage.AttachmentsDatabase, "SELECT StoredPath FROM InputAttachments").Single()!;
        Assert.Equal(Full(newPath), Full(raw));

        // The session resumes and re-sends the queued input: the attachment is resolved from the row.
        var newStore = new InputAttachmentStore(AttachmentConfig(), NewStorage.InputAttachments);
        var requeued = new SessionInputQueueStore(AttachmentConfig(), newStore);
        var item = await requeued.FindPromptAsync("session-1", "uid-1");
        Assert.NotNull(item);
        var attachment = await newStore.GetAuthorizedAsync(item!.AttachmentIds.Single(), "owner");
        Assert.Equal(Full(newPath), Full(attachment!.StoredPath));
        Assert.True(File.Exists(attachment.StoredPath));
        var text = SessionInputFormatting.FileReference(attachment.ToProviderAttachment());
        Assert.Contains(attachment.StoredPath, text);
        Assert.DoesNotContain(oldPath, text);

        // Idempotent.
        Assert.Equal(0, new InputAttachmentsRebaser(NewStorage).Rebase(Map).Changed);
    }

    [Fact]
    public async Task The_attachment_rebaser_leaves_a_value_outside_every_from_folder_and_a_boundary_sibling()
    {
        var storage = NewStorage;
        var store = new InputAttachmentStore(AttachmentConfig(), storage.InputAttachments);
        var one = await store.UploadBytesAsync(Png(), "a.png", "image/png", "owner");
        var two = await store.UploadBytesAsync(Png(), "b.png", "image/png", "owner");
        var outside = Path.Combine(External, "att", "x.png");
        var sibling = Path.Combine(Sibling, "input-attachments", "y.png");
        Execute(storage.AttachmentsDatabase, "UPDATE InputAttachments SET StoredPath = $p WHERE Id = $id", ("$p", outside), ("$id", one.Id));
        Execute(storage.AttachmentsDatabase, "UPDATE InputAttachments SET StoredPath = $p WHERE Id = $id", ("$p", sibling), ("$id", two.Id));

        var result = new InputAttachmentsRebaser(storage).Rebase(Map);

        Assert.Equal(0, result.Changed);
        Assert.Equivalent(new[] { outside, sibling }, Column(storage.AttachmentsDatabase, "SELECT StoredPath FROM InputAttachments"));
    }

    // ------------------------------------------------------------------------- session databases

    private void CreateSessionDatabases()
    {
        foreach (var plugin in new[] { "claude-code", "codex", "opencode" })
            Directory.CreateDirectory(NewStorage.PluginDirectory(plugin));
        ClaudeDbContext.DbPath = Path.Combine(NewStorage.PluginDirectory("claude-code"), "claude.db");
        CodexDbContext.DbPath = Path.Combine(NewStorage.PluginDirectory("codex"), "codex.db");
        OpenCodeDbContext.DbPath = Path.Combine(NewStorage.PluginDirectory("opencode"), "opencode.db");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new OpenCodeDbContext()) db.Initialize();
    }

    private static void InsertClaudeSession(string database, string id, string projectPath, string? scratch)
        => Execute(database, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, ScratchDirectory, Status, StartedAt, MessageCount, Dismissed,
                Unattended, DeliveredAsOneMessage, ClaudeSessionId)
            VALUES ($id, 'p', $path, $scratch, 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 1, 0, 0, 0, 'conv-1')
            """, ("$id", id), ("$path", projectPath), ("$scratch", scratch));

    [Fact]
    public void Session_databases_keep_absolute_working_directories_and_rebase_those_under_a_from_folder()
    {
        CreateSessionDatabases();
        var claude = ClaudeDbContext.DbPath;
        var codex = CodexDbContext.DbPath;
        var opencode = OpenCodeDbContext.DbPath;

        Execute(claude, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, ScratchDirectory, Status, StartedAt, MessageCount, Dismissed, Unattended, DeliveredAsOneMessage, ClaudeSessionId)
            VALUES ('c1','p',$a,$s,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0,0,'conv'),
                   ('c2','p',$b,NULL,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0,0,'conv'),
                   ('c3','p',$c,NULL,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0,0,'conv'),
                   ('c4','p',$d,NULL,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0,0,'conv')
            """,
            ("$a", Path.Combine(OldLeaf, "agents", "nova")), ("$s", Path.Combine(External, "scratch", "run-1")),
            ("$b", Path.Combine(OldRoot, "conversation-sessions", "s1")),
            ("$c", Path.Combine(External, "T-projects", "redcompute")),
            ("$d", Path.Combine(Sibling, "conversation-sessions", "s2")));
        Execute(codex, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, ScratchDirectory, Status, StartedAt, MessageCount, Dismissed, ExecutionProfile, Unattended)
            VALUES ('x1','p',$a,$s,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,'default',0)
            """, ("$a", Path.Combine(OldLeaf, "agents", "coder")), ("$s", Path.Combine(OldRoot, "scratch", "x")));
        Execute(opencode, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, ScratchDirectory, Status, StartedAt, MessageCount, Dismissed, Unattended)
            VALUES ('o1','p',$a,NULL,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0)
            """, ("$a", Path.Combine(OldLeaf, "agents", "oc")));

        var claudeResult = new SessionDatabaseRebaser("claude-sessions", claude).Rebase(Map);
        new SessionDatabaseRebaser("codex-sessions", codex).Rebase(Map);
        new SessionDatabaseRebaser("opencode-sessions", opencode).Rebase(Map);

        Assert.Equal(2, claudeResult.Changed);
        var paths = Column(claude, "SELECT ProjectPath FROM Sessions ORDER BY Id").Select(Full).ToList();
        Assert.Equal(Full(Path.Combine(NewLeaf, "agents", "nova")), paths[0]);
        Assert.Equal(Full(Path.Combine(NewRoot, "conversation-sessions", "s1")), paths[1]);
        Assert.Equal(Full(Path.Combine(External, "T-projects", "redcompute")), paths[2]);
        Assert.Equal(Full(Path.Combine(Sibling, "conversation-sessions", "s2")), paths[3]);
        Assert.Equal(Path.Combine(External, "scratch", "run-1"), Column(claude, "SELECT ScratchDirectory FROM Sessions WHERE Id='c1'").Single());
        Assert.Equal(Full(Path.Combine(NewLeaf, "agents", "coder")), Full(Column(codex, "SELECT ProjectPath FROM Sessions").Single()));
        Assert.Equal(Full(Path.Combine(NewRoot, "scratch", "x")), Full(Column(codex, "SELECT ScratchDirectory FROM Sessions").Single()));
        Assert.Equal(Full(Path.Combine(NewLeaf, "agents", "oc")), Full(Column(opencode, "SELECT ProjectPath FROM Sessions").Single()));

        // Idempotent.
        Assert.Equal(0, new SessionDatabaseRebaser("claude-sessions", claude).Rebase(Map).Changed);
        Assert.Equal(0, new SessionDatabaseRebaser("codex-sessions", codex).Rebase(Map).Changed);
        Assert.Equal(0, new SessionDatabaseRebaser("opencode-sessions", opencode).Rebase(Map).Changed);
    }

    [Fact]
    public void A_missing_session_database_is_nothing_to_rebase()
    {
        var result = new SessionDatabaseRebaser("claude-sessions", Path.Combine(_root, "absent", "claude.db")).Rebase(Map);
        Assert.Equal(new RebaseOwnerResult("claude-sessions", 0, 0), result);
        Assert.False(File.Exists(Path.Combine(_root, "absent", "claude.db")));
    }

    private sealed class Jobs : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class NoModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    [Fact]
    public async Task A_claude_session_whose_folder_moved_resumes_after_the_rebase_and_not_before()
    {
        CreateSessionDatabases();
        var movedFolder = Path.Combine(NewLeaf, "agents", "nova");
        Directory.CreateDirectory(movedFolder);
        var legacy = Path.Combine(OldLeaf, "agents", "nova");
        Assert.False(Directory.Exists(legacy));
        InsertClaudeSession(ClaudeDbContext.DbPath, "s1", legacy, null);
        var claudeExe = Touch(Path.Combine(_root, "cli", "claude.exe"), "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudeExe }, new Jobs(),
            new ClaudeSessionStore(), new NoModes(), () => null, (_, _) => { });

        // The legacy absolute folder is gone with the move: resume refuses it.
        Assert.Null(await service.ResumeSessionAsync("s1"));
        Assert.Equal(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);

        new SessionDatabaseRebaser("claude-sessions", ClaudeDbContext.DbPath).Rebase(Map);

        // The rebased record points at the moved folder: the project-path gate no longer stops it
        // (what happens after it needs a real CLI and is not this test's concern).
        Assert.Equal(Full(movedFolder), Full(new ClaudeSessionStore().FindSession("s1")!.ProjectPath));
        await service.ResumeSessionAsync("s1");
        Assert.NotEqual(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
    }

    // ---------------------------------------------------------------- long-lived processes, config

    [Fact]
    public void The_long_lived_registry_state_is_rebased_under_a_from_folder_and_stays_absolute()
    {
        Directory.CreateDirectory(NewRoot);
        var statePath = Path.Combine(NewRoot, "long-lived-processes.json");
        var records = new JsonArray(
            new JsonObject
            {
                ["id"] = "aaa", ["name"] = "preview", ["command"] = "pnpm dev", ["port"] = 4000,
                ["logPath"] = Path.Combine(OldRoot, "long-lived-processes", "aaa.log"),
                ["workingDirectory"] = Path.Combine(OldLeaf, "agents", "nova", "app"),
            },
            new JsonObject
            {
                ["id"] = "bbb", ["name"] = "docs", ["command"] = "x",
                ["logPath"] = Path.Combine(OldRoot, "long-lived-processes", "bbb.log"),
                ["workingDirectory"] = Path.Combine(External, "docs"),
            },
            new JsonObject
            {
                ["id"] = "ccc", ["name"] = "inside", ["command"] = "x",
                ["logPath"] = Path.Combine(OldRoot, "long-lived-processes", "ccc.log"),
                ["workingDirectory"] = Path.Combine(OldRoot, "conversation-sessions", "c"),
            });
        File.WriteAllText(statePath, records.ToJsonString());

        var result = new LongLivedProcessesRebaser(NewStorage).Rebase(Map);

        Assert.Equal(5, result.Changed);
        var rebased = JsonNode.Parse(File.ReadAllText(statePath))!.AsArray();
        Assert.Equal(Full(Path.Combine(NewRoot, "long-lived-processes", "aaa.log")), Full(rebased[0]!["logPath"]!.GetValue<string>()));
        Assert.Equal(Full(Path.Combine(NewLeaf, "agents", "nova", "app")), Full(rebased[0]!["workingDirectory"]!.GetValue<string>()));
        Assert.Equal(Path.Combine(External, "docs"), rebased[1]!["workingDirectory"]!.GetValue<string>());
        Assert.Equal(Full(Path.Combine(NewRoot, "conversation-sessions", "c")), Full(rebased[2]!["workingDirectory"]!.GetValue<string>()));
        Assert.Equal(4000, rebased[0]!["port"]!.GetValue<int>());
        Assert.Equal(0, new LongLivedProcessesRebaser(NewStorage).Rebase(Map).Changed);

        // The registry reads the paths and writes them back absolute.
        using var registry = new LongLivedProcessRegistry(NewRoot, _ => { }, monitor: false);
        var logPaths = registry.List().OrderBy(view => view.Id).Select(view => view.LogPath).ToArray();
        Assert.All(logPaths, log => Assert.True(Path.IsPathRooted(log)));
        Assert.Equal(Full(Path.Combine(NewRoot, "long-lived-processes", "aaa.log")), Full(logPaths[0]));
        var saved = JsonNode.Parse(File.ReadAllText(statePath))!.AsArray();
        Assert.Equal(Full(Path.Combine(NewRoot, "long-lived-processes", "aaa.log")), Full(saved[0]!["logPath"]!.GetValue<string>()));
        Assert.True(Path.IsPathRooted(saved[0]!["logPath"]!.GetValue<string>()));
    }

    private const string ConfigText = """
        {
          "ApiPort": 18800,
          "Capabilities": {
            "tts": { "ActiveProvider": null, "Providers": { "local": {
              "Type": "TtsLocal", "VenvPath": "~/tts-env", "ServerPath": "@OLD/servers/tts", "VoicesBasePath": "@OLD/voices",
              "ApiKey": "keep-me" } } },
            "image-gen": { "ActiveProvider": "comfyui", "Providers": { "comfyui": {
              "Type": "ComfyUI", "ServerPath": "@EXT/ComfyUI", "VenvPath": "@EXT/ComfyUI/.venv",
              "ModelsDirectory": "@EXT/Models", "OutputDirectory": "@OLD/comfy-out", "WorkflowsDir": "@SIB/workflows",
              "Host": "127.0.0.1" } } }
          }
        }
        """;

    [Fact]
    public void The_config_rebaser_rewrites_provider_paths_under_a_from_folder_and_nothing_else()
    {
        Directory.CreateDirectory(NewRoot);
        var text = ConfigText.Replace("@OLD", Slash(OldRoot)).Replace("@EXT", Slash(External)).Replace("@SIB", Slash(Sibling));
        File.WriteAllText(NewStorage.ConfigFile, text);

        var result = new ConfigRebaser(NewStorage).Rebase(Map);

        Assert.Equal(3, result.Changed);
        var providers = JsonNode.Parse(File.ReadAllText(NewStorage.ConfigFile))!["Capabilities"]!;
        var tts = providers["tts"]!["Providers"]!["local"]!;
        Assert.Equal(Full(Path.Combine(NewRoot, "servers", "tts")), Full(tts["ServerPath"]!.GetValue<string>()));
        Assert.Equal(Full(Path.Combine(NewRoot, "voices")), Full(tts["VoicesBasePath"]!.GetValue<string>()));
        Assert.Equal("~/tts-env", tts["VenvPath"]!.GetValue<string>());
        Assert.Equal("keep-me", tts["ApiKey"]!.GetValue<string>());
        var comfy = providers["image-gen"]!["Providers"]!["comfyui"]!;
        Assert.Equal(Slash(Path.Combine(External, "ComfyUI")), comfy["ServerPath"]!.GetValue<string>());
        Assert.Equal(Slash(Path.Combine(External, "Models")), comfy["ModelsDirectory"]!.GetValue<string>());
        Assert.Equal(Full(Path.Combine(NewRoot, "comfy-out")), Full(comfy["OutputDirectory"]!.GetValue<string>()));
        Assert.Equal(Slash(Path.Combine(Sibling, "workflows")), comfy["WorkflowsDir"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", comfy["Host"]!.GetValue<string>());
        Assert.Equal(0, new ConfigRebaser(NewStorage).Rebase(Map).Changed);
    }

    [Fact]
    public void RedComputes_own_install_directory_is_never_persisted_in_the_config()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "backends", "stt-local");
        var oldInstall = @"T:\Projects\redcompute\src\RedCompute.App\bin\Release\net9.0-windows\backends\diarization-local";
        Assert.True(ConfigManager.IsBundledBackendPath(bundled));
        Assert.True(ConfigManager.IsBundledBackendPath(oldInstall));
        Assert.True(ConfigManager.IsBundledBackendPath(oldInstall.Replace('\\', '/') + "/"));
        Assert.False(ConfigManager.IsBundledBackendPath(@"T:\Projects\Qwen3-TTS\qwen3-tts-server"));
        Assert.False(ConfigManager.IsBundledBackendPath(@"C:\x\backends\something-else"));
        Assert.False(ConfigManager.IsBundledBackendPath(null));

        var config = ConfigManager.CreateDefault();
        var stt = config.Capabilities["stt"].Providers["local-wsl"];
        Assert.Equal(bundled, stt.ServerPath);

        var written = JsonNode.Parse(ConfigManager.SerializeForPersistence(config, []))!;

        Assert.DoesNotContain("backends", written.ToJsonString());
        Assert.Null(written["Capabilities"]!["stt"]!["Providers"]!["local-wsl"]!["ServerPath"]);
        // The running config still has it.
        Assert.Equal(bundled, stt.ServerPath);

        // A provider the defaults do not fill in keeps its path as written: a previous build would not refill it.
        config.Capabilities["stt"].Providers["custom"] = new ProviderConfig { Type = "SttLocal", ServerPath = bundled };
        var custom = JsonNode.Parse(ConfigManager.SerializeForPersistence(config, []))!;
        Assert.NotNull(custom["Capabilities"]!["stt"]!["Providers"]!["custom"]!["ServerPath"]);
    }

    [Fact]
    public void A_bundled_path_left_in_the_file_by_an_older_build_is_pointed_at_this_install_and_dropped_on_save()
    {
        var config = ConfigManager.CreateDefault();
        var stt = config.Capabilities["stt"].Providers["local-wsl"];
        stt.ServerPath = @"C:\old-install\backends\stt-local";

        Assert.True(ConfigManager.AdoptBundledBackends(config));

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "backends", "stt-local"), stt.ServerPath);
        // A config that never carried one (the file had none) has nothing to adopt or rewrite.
        var clean = ConfigManager.CreateDefault();
        foreach (var provider in clean.Capabilities.Values.SelectMany(c => c.Providers.Values)) provider.ServerPath = null;
        Assert.False(ConfigManager.AdoptBundledBackends(clean));
    }

    [Theory]
    [InlineData("/home/rlupdate/.local/share/RedLeaf-Suite/components/redcompute/0.1.23/backends/stt-local")]
    [InlineData("/home/rlupdate/.local/share/RedLeaf-Suite/components/redcompute/0.1.23/backends/diarization-local/")]
    [InlineData(@"C:\old-install\backends\stt-local")]
    public void A_bundled_path_reads_the_same_whichever_separator_the_older_build_wrote(string persisted)
    {
        // Regression: 0.1.24 on Linux threw "Sequence contains no matching element" at startup on a config an
        // 0.1.23 install had written, because the slug was taken with Path.GetFileName on a backslash string.
        var config = ConfigManager.CreateDefault();
        config.Capabilities["stt"].Providers["local-wsl"].ServerPath = persisted;

        Assert.True(ConfigManager.IsBundledBackendPath(persisted));
        Assert.True(ConfigManager.AdoptBundledBackends(config));

        var adopted = config.Capabilities["stt"].Providers["local-wsl"].ServerPath!;
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "backends",
            ConfigManager.BundledBackendSlug(persisted)!), adopted);
    }

    [Fact]
    public void A_malformed_entry_is_skipped_with_a_warning_and_the_valid_one_is_still_adopted()
    {
        var config = ConfigManager.CreateDefault();
        config.Capabilities["stt"].Providers["local-wsl"].ServerPath = "/old/install/backends/stt-local";
        // A null provider (a JSON null in the file) cannot be read; it must not stop the others.
        config.Capabilities["stt"].Providers["broken"] = null!;
        var warnings = new List<string>();

        var dirty = ConfigManager.AdoptBundledBackends(config, warnings.Add);

        Assert.True(dirty);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "backends", "stt-local"),
            config.Capabilities["stt"].Providers["local-wsl"].ServerPath);
        var warning = Assert.Single(warnings);
        Assert.Contains("stt/broken", warning);
    }

    // ------------------------------------------------------------------------ the runner, the move

    private sealed class Throwing(string owner) : IStoredPathRebaser
    {
        public string Owner => owner;
        public RebaseOwnerResult Rebase(RebaseMap map) => throw new InvalidOperationException("disk said no");
    }

    private sealed class Counting(string owner) : IStoredPathRebaser
    {
        public int Runs;
        public string Owner => owner;
        public RebaseOwnerResult Rebase(RebaseMap map) { Runs++; return new RebaseOwnerResult(owner, 4, 2); }
    }

    [Fact]
    public void A_failed_owner_leaves_the_generation_incomplete_and_the_next_start_retries_it()
    {
        Directory.CreateDirectory(NewRoot);
        var good = new Counting("good");
        var failing = new StoredPathRebaseRunner(NewStorage, [good, new Throwing("bad")], _ => { });

        var first = failing.Run(Map);

        Assert.Equal(RebaseStates.Failed, first.State);
        Assert.Equal(5, first.Generation);
        Assert.Equal(RebaseStates.Completed, first.Owners.Single(o => o.Owner == "good").State);
        Assert.Contains("disk said no", first.Owners.Single(o => o.Owner == "bad").Error);

        var retry = new Counting("bad");
        var second = new StoredPathRebaseRunner(NewStorage, [good, retry], _ => { }).Run(Map);

        Assert.Equal(RebaseStates.Completed, second.State);
        Assert.Equal(1, retry.Runs);
        Assert.Equal(2, good.Runs);
    }

    [Fact]
    public void A_second_start_with_the_same_map_changes_nothing()
    {
        Directory.CreateDirectory(NewRoot);
        var owner = new Counting("only");
        var first = new StoredPathRebaseRunner(NewStorage, [owner], _ => { }).Run(Map);
        var second = new StoredPathRebaseRunner(NewStorage, [owner], _ => { }).Run(Map);

        Assert.Equal(RebaseStates.Completed, first.State);
        Assert.Equal(RebaseStates.Completed, second.State);
        Assert.Equal(1, owner.Runs);
        Assert.Equal(5, second.Generation);
        Assert.True(File.Exists(Path.Combine(NewRoot, StoredPathRebaseRunner.StateFileName)));
        // A map for a later generation does run.
        new StoredPathRebaseRunner(NewStorage, [owner], _ => { }).Run(new RebaseMap(6, Map.Entries));
        Assert.Equal(2, owner.Runs);
    }

    [Fact]
    public void Without_a_map_the_runner_does_nothing_and_reports_the_last_completed_generation()
    {
        Directory.CreateDirectory(NewRoot);
        var owner = new Counting("only");
        var runner = new StoredPathRebaseRunner(NewStorage, [owner], _ => { });
        Assert.Equal(new RebaseStatus(0, RebaseStates.Completed, []), runner.Run(null));
        runner.Run(Map);

        var restarted = new StoredPathRebaseRunner(NewStorage, [owner], _ => { });
        Assert.Equal(RebaseStates.Completed, restarted.Run(null).State);
        Assert.Equal(5, restarted.Status.Generation);
        Assert.Equal(1, owner.Runs);
    }

    [Fact]
    public async Task Moving_a_small_copy_of_every_store_leaves_no_value_under_the_old_roots()
    {
        // ---- the world before the move: schema-identical small stores under the old roots, legacy absolute values
        var oldStorage = OldStorage;
        var newStorage = NewStorage;
        Directory.CreateDirectory(OldRoot);
        var oldDatabase = CreateJobDatabase(oldStorage);
        var jobA = SeedJob(oldDatabase, Touch(Path.Combine(oldStorage.Outputs, "a.png"), "A"));
        var jobB = SeedJob(oldDatabase, Touch(Path.Combine(oldStorage.Outputs, "sub", "b.png"), "B"));
        var jobExternal = SeedJob(oldDatabase, Touch(Path.Combine(External, "ComfyUI", "c.png"), "C"));

        var oldAttachments = new InputAttachmentStore(AttachmentConfig(), oldStorage.InputAttachments);
        var attachment = await oldAttachments.UploadBytesAsync(Png(), "shot.png", "image/png", "owner");
        Execute(oldStorage.AttachmentsDatabase, "UPDATE InputAttachments SET StoredPath = $p", ("$p", attachment.StoredPath));

        foreach (var plugin in new[] { "claude-code", "codex", "opencode" })
            Directory.CreateDirectory(oldStorage.PluginDirectory(plugin));
        ClaudeDbContext.DbPath = Path.Combine(oldStorage.PluginDirectory("claude-code"), "claude.db");
        CodexDbContext.DbPath = Path.Combine(oldStorage.PluginDirectory("codex"), "codex.db");
        OpenCodeDbContext.DbPath = Path.Combine(oldStorage.PluginDirectory("opencode"), "opencode.db");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new OpenCodeDbContext()) db.Initialize();
        var agentFolder = Path.Combine(OldLeaf, "agents", "nova");
        InsertClaudeSession(ClaudeDbContext.DbPath, "s1", agentFolder, null);
        Execute(CodexDbContext.DbPath, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed, ExecutionProfile, Unattended)
            VALUES ('x1','p',$a,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,'default',0)
            """, ("$a", Path.Combine(oldStorage.ConversationSessions, "c1")));
        Execute(OpenCodeDbContext.DbPath, """
            INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed, Unattended)
            VALUES ('o1','p',$a,'Stopped','2026-11-01T00:00:00.0000000+00:00',1,0,0)
            """, ("$a", agentFolder));

        File.WriteAllText(Path.Combine(OldRoot, "long-lived-processes.json"), new JsonArray(new JsonObject
        {
            ["id"] = "aaa", ["name"] = "preview", ["command"] = "x",
            ["logPath"] = Path.Combine(OldRoot, "long-lived-processes", "aaa.log"),
            ["workingDirectory"] = agentFolder,
        }).ToJsonString());
        File.WriteAllText(oldStorage.ConfigFile,
            ConfigText.Replace("@OLD", Slash(OldRoot)).Replace("@EXT", Slash(External)).Replace("@SIB", Slash(Sibling)));

        // ---- the move: the kernel copies the folders (here, the files and databases), then starts RedCompute with the map
        SqliteConnection.ClearAllPools();
        CopyDirectory(OldRoot, NewRoot);
        CopyDirectory(OldLeaf, NewLeaf, create: true);
        Directory.CreateDirectory(agentFolder);
        Directory.CreateDirectory(Path.Combine(NewLeaf, "agents", "nova"));
        DeleteTree(OldRoot);
        DeleteTree(OldLeaf);
        ClaudeDbContext.DbPath = Path.Combine(newStorage.PluginDirectory("claude-code"), "claude.db");
        CodexDbContext.DbPath = Path.Combine(newStorage.PluginDirectory("codex"), "codex.db");
        OpenCodeDbContext.DbPath = Path.Combine(newStorage.PluginDirectory("opencode"), "opencode.db");

        var logs = new List<string>();
        var status = new StoredPathRebaseRunner(newStorage, StoredPathRebasers.For(newStorage), logs.Add).Run(Map);

        // ---- nothing is left under the old roots
        Assert.Equal(RebaseStates.Completed, status.State);
        Assert.Equal(5, status.Generation);
        Assert.Equal(7, status.Owners.Count);
        Assert.All(status.Owners, owner => Assert.Equal(RebaseStates.Completed, owner.State));
        var stores = new[]
        {
            ("jobs", Column(newStorage.Database, "SELECT OutputLocation FROM Jobs")),
            ("attachments", Column(newStorage.AttachmentsDatabase, "SELECT StoredPath FROM InputAttachments")),
            ("claude", Column(ClaudeDbContext.DbPath, "SELECT ProjectPath FROM Sessions")),
            ("codex", Column(CodexDbContext.DbPath, "SELECT ProjectPath FROM Sessions")),
            ("opencode", Column(OpenCodeDbContext.DbPath, "SELECT ProjectPath FROM Sessions")),
            ("long-lived", [File.ReadAllText(Path.Combine(NewRoot, "long-lived-processes.json"))]),
            ("config", [File.ReadAllText(newStorage.ConfigFile)]),
        };
        foreach (var (store, values) in stores)
            foreach (var value in values)
            {
                Assert.False(NamesFolder(value!, OldRoot) || NamesFolder(value!, OldLeaf),
                    $"{store} still names the old roots: {value}");
            }

        // ---- an old job output downloads from the new location
        JobOutputPaths.OverrideRoot = newStorage.Outputs;
        using (var db = new RedComputeDbContext(newStorage.Database))
        {
            var a = db.Jobs.Single(j => j.Id == jobA).OutputLocation;
            var b = db.Jobs.Single(j => j.Id == jobB).OutputLocation;
            Assert.Equal("A", File.ReadAllText(a!));
            Assert.Equal("B", File.ReadAllText(b!));
            Assert.StartsWith(Full(NewRoot), Full(a), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Full(Path.Combine(External, "ComfyUI", "c.png")), Full(db.Jobs.Single(j => j.Id == jobExternal).OutputLocation));
        }

        // ---- the attachment resolves from the new root
        var moved = new InputAttachmentStore(AttachmentConfig(), newStorage.InputAttachments);
        var resolved = await moved.GetAuthorizedAsync(attachment.Id, "owner");
        Assert.True(File.Exists(resolved!.StoredPath));
        Assert.StartsWith(Full(NewRoot), Full(resolved.StoredPath), StringComparison.OrdinalIgnoreCase);

        // ---- the Claude session resumes against the moved folder (the project-path gate passes)
        var claudeExe = Touch(Path.Combine(_root, "cli", "claude.exe"), "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudeExe }, new Jobs(),
            new ClaudeSessionStore(), new NoModes(), () => null, (_, _) => { });
        await service.ResumeSessionAsync("s1");
        Assert.NotEqual(SessionStopReasons.ProjectPathMissing, service.LastStartErrorCode);
        Assert.Equal(Full(Path.Combine(NewLeaf, "agents", "nova")), Full(new ClaudeSessionStore().FindSession("s1")!.ProjectPath));

        // ---- a second start with the same map changes nothing
        var before = Snapshot(newStorage);
        var again = new StoredPathRebaseRunner(newStorage, StoredPathRebasers.For(newStorage), logs.Add).Run(Map);
        Assert.Equal(RebaseStates.Completed, again.State);
        Assert.Equal(before, Snapshot(newStorage));
        Assert.Contains(logs, line => line.Contains("already completed"));

        // ---- the same rebasers, run directly again, rewrite nothing
        foreach (var rebaser in StoredPathRebasers.For(newStorage))
            Assert.Equal(0, rebaser.Rebase(Map).Changed);
    }

    /// <summary>True when the text names the folder (in any separator style or JSON escaping), on a folder boundary.</summary>
    private static bool NamesFolder(string text, string folder)
    {
        foreach (var form in new[] { folder, Slash(folder), folder.Replace("\\", "\\\\") }.Distinct())
        {
            for (var at = text.IndexOf(form, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(form, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var next = at + form.Length;
                if (next >= text.Length || !(char.IsLetterOrDigit(text[next]) || text[next] is '_' or '-' or '.'))
                    return true;
            }
        }
        return false;
    }

    private static string Snapshot(ComputeStorage storage)
    {
        var values = new List<string>();
        values.AddRange(Column(storage.Database, "SELECT OutputLocation FROM Jobs ORDER BY rowid").Select(v => v ?? ""));
        values.AddRange(Column(storage.AttachmentsDatabase, "SELECT StoredPath FROM InputAttachments ORDER BY rowid").Select(v => v ?? ""));
        values.Add(File.ReadAllText(storage.ConfigFile));
        values.Add(File.ReadAllText(Path.Combine(storage.LongLivedProcessesDirectory, "long-lived-processes.json")));
        return string.Join("\n", values);
    }

    private static void CopyDirectory(string from, string to, bool create = false)
    {
        if (!Directory.Exists(from))
        {
            if (create) Directory.CreateDirectory(to);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ------------------------------------------------------------------------------- the endpoint

    [Fact]
    public async Task The_status_endpoint_reports_the_rebase_and_is_declared_with_authentication()
    {
        var status = new RebaseStatus(5, RebaseStates.Completed,
            [new RebaseOwnerStatus("job-outputs", RebaseStates.Completed, 10, 7, 0, null)]);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        StorageEndpoints.Map(new EndpointRegistry(app), () => status);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == StorageEndpoints.RebaseStatusPath);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await route.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(5, body.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal("completed", body.RootElement.GetProperty("state").GetString());
        var owner = body.RootElement.GetProperty("owners")[0];
        Assert.Equal("job-outputs", owner.GetProperty("owner").GetString());
        Assert.Equal(7, owner.GetProperty("changed").GetInt32());
        Assert.DoesNotContain(_root, body.RootElement.GetRawText());
    }

    [Fact]
    public async Task The_job_database_snapshot_folder_stores_no_path_and_is_not_taken_for_the_live_database()
    {
        var storage = NewStorage;
        CreateJobDatabase(storage);
        var snapshots = new JobDatabaseSnapshotService(storage);
        var result = (await snapshots.TryCreateAsync())!;

        var name = Path.GetFileName(result.Path);
        Assert.NotEqual(Path.GetFileName(storage.Database), name);
        Assert.Matches(@"^redcompute-snapshot-\d{8}T\d{6}Z\.db$", name);
        Assert.Equal(storage.Backups, Path.GetDirectoryName(result.Path));
        // Nothing absolute about it is stored: it is derived from the data root at use, and no rebaser reads it.
        var bytes = File.ReadAllBytes(result.Path);
        new StoredPathRebaseRunner(storage, StoredPathRebasers.For(storage), _ => { }).Run(Map);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal(storage.Backups, snapshots.SnapshotDirectory);
    }
}
