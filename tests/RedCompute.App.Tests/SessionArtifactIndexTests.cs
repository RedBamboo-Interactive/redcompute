using RedCompute.App.Services;
using RedCompute.App.Services.SessionArtifacts;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionArtifactIndexTests
{
    private const string Epoch = "epoch-1";

    /// <summary>In-memory stand-in for the reader's ascending `after` page, with the reader's HasEarlier/HasLater semantics.</summary>
    private sealed class FakeTranscript(int pageSize = 500)
    {
        public List<UnifiedMessageRecord> Records { get; } = [];
        public List<long> AfterArguments { get; } = [];

        public void Add(string role, string content, string eventType = "text", string? epoch = Epoch, string? attachmentsJson = null)
        {
            var id = Records.Count == 0 ? 1 : Records[^1].Id + 1;
            Records.Add(new UnifiedMessageRecord
            {
                Id = id, SessionId = "s1", Role = role, EventType = eventType, Content = content,
                MessageUid = $"m{id}", Sequence = id, Epoch = epoch, AttachmentsJson = attachmentsJson,
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(id),
            });
        }

        public ArtifactRecordReader Reader => (after, _) =>
        {
            AfterArguments.Add(after);
            var page = Records.Where(record => record.Id > after).OrderBy(record => record.Id).Take(pageSize).ToList();
            var hasLater = page.Count > 0 && Records.Any(record => record.Id > page[^1].Id);
            var hasEarlier = page.Count > 0
                ? Records.Any(record => record.Id < page[0].Id)
                : Records.Any(record => record.Id <= after);
            return Task.FromResult(new TranscriptPageReadResult(
                page, page.Count == 0 ? null : page[0].Id, page.Count == 0 ? null : page[^1].Id, hasEarlier, hasLater));
        };
    }

    private static Task<SessionArtifactSnapshot> Query(
        SessionArtifactIndex index, FakeTranscript transcript, string session = "s1", string epoch = Epoch,
        SessionArtifactFilter? filter = null, string? projectPath = "C:/p")
        => index.QueryAsync(session, epoch, transcript.Reader, filter ?? new SessionArtifactFilter(), CancellationToken.None, projectPath);

    [Fact]
    public async Task Cold_scan_walks_every_page_and_a_warm_query_only_reads_the_new_tail()
    {
        var transcript = new FakeTranscript(pageSize: 2);
        for (var i = 1; i <= 5; i++) transcript.Add("assistant", $"[l{i}](https://e.example.com/{i})");
        var index = new SessionArtifactIndex();

        var cold = await Query(index, transcript);
        Assert.Equal(5, cold.Items.Count);
        Assert.Equal(5, cold.ThroughSequence);
        Assert.Equal([0L, 2L, 4L], transcript.AfterArguments);

        transcript.AfterArguments.Clear();
        transcript.Add("assistant", "[l6](https://e.example.com/6) and again [l1](https://e.example.com/1)");
        var warm = await Query(index, transcript);

        Assert.Equal([5L], transcript.AfterArguments);
        Assert.Equal(6, warm.Items.Count);
        Assert.Equal(6, warm.ThroughSequence);
        // l1 re-occurred in the newest record: it moves to the front with two occurrences.
        Assert.Equal(["url:https://e.example.com/1", "url:https://e.example.com/6"], warm.Items.Take(2).Select(i => i.Key).Order());
        var l1 = warm.Items.Single(i => i.Key == "url:https://e.example.com/1");
        Assert.Equal(2, l1.Occurrences);
        Assert.Equal(1, l1.First.Sequence);
        Assert.Equal(6, l1.Last.Sequence);

        // Nothing new: one cheap read, same answer.
        transcript.AfterArguments.Clear();
        var again = await Query(index, transcript);
        Assert.Equal([6L], transcript.AfterArguments);
        Assert.Equal(warm.Items.Select(i => i.Key), again.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task A_different_epoch_rebuilds_from_the_start_and_drops_old_artifacts()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[old](https://old.example.com/)");
        var index = new SessionArtifactIndex();
        Assert.Single((await Query(index, transcript)).Items);

        transcript.Records.Clear();
        transcript.Add("assistant", "[new](https://new.example.com/)");
        transcript.AfterArguments.Clear();
        var rebuilt = await Query(index, transcript, epoch: "epoch-2");

        Assert.Equal([0L], transcript.AfterArguments);
        Assert.Equal("url:https://new.example.com/", Assert.Single(rebuilt.Items).Key);
    }

    [Fact]
    public async Task A_cleared_transcript_empties_the_index_instead_of_serving_stale_artifacts()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[a](https://a.example.com/)");
        transcript.Add("assistant", "[b](https://b.example.com/)");
        var index = new SessionArtifactIndex();
        Assert.Equal(2, (await Query(index, transcript)).Items.Count);

        transcript.Records.Clear();
        var cleared = await Query(index, transcript);

        Assert.Empty(cleared.Items);
        Assert.Equal(0, cleared.ThroughSequence);
    }

    [Fact]
    public async Task A_sequence_rewind_under_the_watermark_rebuilds()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[a](https://a.example.com/)");
        transcript.Add("assistant", "[b](https://b.example.com/)");
        var index = new SessionArtifactIndex();
        await Query(index, transcript);

        transcript.Records.Clear();
        transcript.Records.Add(new UnifiedMessageRecord
        {
            Id = 3, SessionId = "s1", Role = "assistant", EventType = "text", Epoch = Epoch, Sequence = 1,
            Content = "[fresh](https://fresh.example.com/)", Timestamp = DateTimeOffset.UnixEpoch,
        });
        var rebuilt = await Query(index, transcript);

        Assert.Equal("url:https://fresh.example.com/", Assert.Single(rebuilt.Items).Key);
    }

    [Fact]
    public async Task SinceSequence_returns_only_artifacts_whose_last_occurrence_is_newer()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[a](https://a.example.com/)");
        transcript.Add("assistant", "[b](https://b.example.com/)");
        transcript.Add("assistant", "[c](https://c.example.com/)");
        transcript.Add("assistant", "[a](https://a.example.com/)");
        var index = new SessionArtifactIndex();

        var delta = await Query(index, transcript, filter: new SessionArtifactFilter(SinceSequence: 2));

        Assert.Equal(["url:https://a.example.com/", "url:https://c.example.com/"], delta.Items.Select(i => i.Key));
        Assert.Equal(4, delta.ThroughSequence);
        Assert.Empty((await Query(index, transcript, filter: new SessionArtifactFilter(SinceSequence: 4))).Items);
    }

    [Fact]
    public async Task Kind_and_origin_filters_answer_for_the_occurrences_they_asked_about()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "", eventType: "tool_use");
        transcript.Records[0].ToolName = "Write";
        transcript.Records[0].ToolInput = "{\"file_path\":\"C:\\\\p\\\\a.md\"}";
        transcript.Add("assistant", "see [a](C:/p/a.md) and ![i](/api/assets/i.png)");
        var index = new SessionArtifactIndex();

        var all = await Query(index, transcript);
        var file = all.Items.Single(i => i.Key == "file:c:/p/a.md");
        Assert.Equal((ArtifactOrigins.AssistantText, 2), (file.Origin, file.Occurrences));

        var written = (await Query(index, transcript, filter: new SessionArtifactFilter(
            Origins: new HashSet<string> { ArtifactOrigins.ToolWrite }))).Items;
        Assert.Equal(ArtifactOrigins.ToolWrite, Assert.Single(written).Origin);

        var linked = (await Query(index, transcript, filter: new SessionArtifactFilter(
            Origins: new HashSet<string> { ArtifactOrigins.AssistantText }))).Items;
        Assert.Equal(2, linked.Count);

        var images = (await Query(index, transcript, filter: new SessionArtifactFilter(
            Kinds: new HashSet<string> { ArtifactKinds.Image }))).Items;
        Assert.Equal("asset:i.png", Assert.Single(images).Key);

        Assert.Empty((await Query(index, transcript, filter: new SessionArtifactFilter(
            Origins: new HashSet<string> { ArtifactOrigins.Voice }))).Items);
    }

    [Fact]
    public async Task Written_files_count_only_inside_the_project_folder_and_a_changed_folder_rebuilds()
    {
        var transcript = new FakeTranscript();
        foreach (var path in new[] { "C:/p/in.md", "S:/scratch/out.py" })
        {
            transcript.Add("assistant", "", eventType: "tool_use");
            transcript.Records[^1].ToolName = "Write";
            transcript.Records[^1].ToolInput = System.Text.Json.JsonSerializer.Serialize(new { file_path = path });
        }
        var index = new SessionArtifactIndex();

        Assert.Equal("file:c:/p/in.md", Assert.Single((await Query(index, transcript)).Items).Key);
        Assert.Empty((await Query(index, transcript, projectPath: null)).Items);
        Assert.Empty((await Query(index, transcript, projectPath: "")).Items);
        Assert.Equal("file:s:/scratch/out.py",
            Assert.Single((await Query(index, transcript, projectPath: "s:/Scratch/")).Items).Key);
    }

    [Fact]
    public async Task Paging_walks_every_item_once_and_new_artifacts_do_not_disturb_an_open_cursor()
    {
        var transcript = new FakeTranscript();
        for (var i = 1; i <= 5; i++) transcript.Add("assistant", $"[l{i}](https://e.example.com/{i})");
        var index = new SessionArtifactIndex();
        var all = (await Query(index, transcript)).Items;

        var (first, cursor) = SessionArtifactCursor.Slice(all, null, 2, "s1", Epoch);
        Assert.Equal(2, first.Count);
        Assert.NotNull(cursor);

        // A new artifact lands while the client holds the cursor; it sorts ahead, so it never shifts the page.
        transcript.Add("assistant", "[l6](https://e.example.com/6)");
        var grown = (await Query(index, transcript)).Items;

        var seen = first.Select(i => i.Key).ToList();
        while (cursor is not null)
        {
            var (page, next) = SessionArtifactCursor.Slice(grown, SessionArtifactCursor.Decode(cursor, "s1", Epoch), 2, "s1", Epoch);
            seen.AddRange(page.Select(i => i.Key));
            cursor = next;
        }

        Assert.Equal(all.Select(i => i.Key), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Slice_without_a_cursor_returns_everything_when_the_limit_allows_and_no_next_cursor()
    {
        var (items, next) = SessionArtifactCursor.Slice([], null, 10, "s1", Epoch);
        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Cursors_are_bound_to_their_session_and_epoch_and_reject_garbage()
    {
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[a](https://a.example.com/)");
        var item = (await Query(new SessionArtifactIndex(), transcript)).Items[0];
        var cursor = SessionArtifactCursor.Encode("s1", Epoch, item);

        Assert.Equal("transcript_cursor_mismatch",
            Assert.Throws<TranscriptPageCursorException>(() => SessionArtifactCursor.Decode(cursor, "s2", Epoch)).Code);
        Assert.Equal("transcript_cursor_stale",
            Assert.Throws<TranscriptPageCursorException>(() => SessionArtifactCursor.Decode(cursor, "s1", "epoch-2")).Code);
        Assert.Equal("invalid_cursor",
            Assert.Throws<TranscriptPageCursorException>(() => SessionArtifactCursor.Decode("not*base64", "s1", Epoch)).Code);
        Assert.Equal("invalid_cursor",
            Assert.Throws<TranscriptPageCursorException>(() => SessionArtifactCursor.Decode("e30", "s1", Epoch)).Code);
    }

    [Fact]
    public async Task The_index_is_bounded_by_least_recently_used_sessions()
    {
        var index = new SessionArtifactIndex(capacity: 2);
        var transcript = new FakeTranscript();
        transcript.Add("assistant", "[a](https://a.example.com/)");

        await Query(index, transcript, "s1");
        await Query(index, transcript, "s2");
        await Query(index, transcript, "s1"); // s2 is now the least recently used
        await Query(index, transcript, "s3");

        Assert.Equal(2, index.SessionCount);
        Assert.True(index.Contains("s1"));
        Assert.False(index.Contains("s2"));
        Assert.True(index.Contains("s3"));

        // An evicted session simply rescans from the beginning.
        transcript.AfterArguments.Clear();
        await Query(index, transcript, "s2");
        Assert.Equal(0L, transcript.AfterArguments[0]);
        Assert.Equal(2, index.SessionCount);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_session_scan_it_once()
    {
        var transcript = new FakeTranscript(pageSize: 3);
        for (var i = 1; i <= 9; i++) transcript.Add("assistant", $"[l{i}](https://e.example.com/{i})");
        var index = new SessionArtifactIndex();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Query(index, transcript))));

        Assert.All(results, result => Assert.Equal(9, result.Items.Count));
        Assert.Equal(1, transcript.AfterArguments.Count(after => after == 0));
        Assert.Equal(1, transcript.AfterArguments.Count(after => after == 3));
    }

    [Fact]
    public async Task A_failed_read_keeps_the_pages_already_applied_and_resumes_from_the_watermark()
    {
        var transcript = new FakeTranscript(pageSize: 2);
        for (var i = 1; i <= 4; i++) transcript.Add("assistant", $"[l{i}](https://e.example.com/{i})");
        var index = new SessionArtifactIndex();

        var calls = 0;
        ArtifactRecordReader flaky = (after, ct) =>
            ++calls == 2 ? throw new IOException("redleaf down") : transcript.Reader(after, ct);
        await Assert.ThrowsAsync<IOException>(() => index.QueryAsync(
            "s1", Epoch, flaky, new SessionArtifactFilter(), CancellationToken.None, "C:/p"));

        transcript.AfterArguments.Clear();
        var recovered = await Query(index, transcript);
        Assert.Equal(4, recovered.Items.Count);
        Assert.Equal([2L], transcript.AfterArguments); // resumed after the first applied page
        Assert.All(recovered.Items, item => Assert.Equal(1, item.Occurrences)); // no page counted twice
    }
}
