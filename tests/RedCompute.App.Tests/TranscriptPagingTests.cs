using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptPagingTests
{
    [Fact]
    public async Task CanonicalPagedReaderRejectsAnIncompleteCheckpointBeforeReadingRecords()
    {
        var checkpoints = new TranscriptCheckpointCoordinator();
        var handler = new PagingHandler([Record(1, "turn")]);
        var reader = CreateReader(handler, checkpoints);
        Assert.Throws<IOException>(() => checkpoints.Write(SessionId, () => throw new IOException("write failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, null, null));
        Assert.Empty(handler.StreamRequestLimits);
        checkpoints.Write(SessionId, () => { });
        Assert.Single((await reader.GetTranscriptPageAsync(EntityId, SessionId, 500, null, null)).Messages);
    }

    [Fact]
    public async Task CanonicalRawReaderRejectsAnIncompleteCheckpointBeforeReadingRecords()
    {
        var checkpoints = new TranscriptCheckpointCoordinator();
        var reader = CreateReader(new EntityOnlyHandler(), checkpoints);
        Assert.Throws<IOException>(() => checkpoints.Write(SessionId, () => throw new IOException("write failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSessionAsync(SessionId));
    }

    private sealed class EntityOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/entities", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { items = new[] { new { id = EntityId, name = "fixture",
                data = JsonSerializer.Serialize(new { session_id = SessionId, provider = "opencode", status = "Active" }) } } }));
        }
    }

    [Fact]
    public async Task Reader_PagesEveryRecordBeyondRedLeafCapWithoutGapsOrDuplicates()
    {
        var handler = new PagingHandler(Enumerable.Range(1, 1085)
            .Select(id => Record(id, $"uid-{id}")));
        var reader = CreateReader(handler);

        var newest = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: null);
        var middle = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: newest.OldestRecordId, afterRecordId: null);
        var oldest = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: middle.OldestRecordId, afterRecordId: null);

        Assert.Equal((586L, 1085L), (newest.Messages[0].Id, newest.Messages[^1].Id));
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(586),
            newest.Messages[0].RecordCreatedAt);
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddSeconds(586),
            newest.Messages[0].Timestamp);
        Assert.True(newest.HasEarlier);
        Assert.False(newest.HasLater);
        Assert.Equal((86L, 585L), (middle.Messages[0].Id, middle.Messages[^1].Id));
        Assert.True(middle.HasEarlier);
        Assert.True(middle.HasLater);
        Assert.Equal((1L, 85L), (oldest.Messages[0].Id, oldest.Messages[^1].Id));
        Assert.False(oldest.HasEarlier);
        Assert.True(oldest.HasLater);

        var all = oldest.Messages.Concat(middle.Messages).Concat(newest.Messages)
            .Select(message => message.Id)
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 1085).Select(id => (long)id), all);
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(handler.StreamRequestLimits, limit => Assert.InRange(limit, 1, 1000));
    }

    [Fact]
    public async Task Reader_ExactLimitReportsNoEarlierRecord()
    {
        var reader = CreateReader(new PagingHandler(Enumerable.Range(1, 500)
            .Select(id => Record(id, $"uid-{id}"))));

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: null);

        Assert.Equal(500, page.Messages.Count);
        Assert.False(page.HasEarlier);
        Assert.False(page.HasLater);
    }

    [Fact]
    public async Task Reader_CompletesBoundaryUidAcrossMultipleRedLeafRequests()
    {
        var records = new List<TestRecord>();
        records.AddRange(Enumerable.Range(1, 201).Select(id => Record(id, $"older-{id}")));
        records.AddRange(Enumerable.Range(202, 1000).Select(id => Record(id, "large-boundary")));
        records.Add(Record(1202, "newest"));
        var handler = new PagingHandler(records);
        var reader = CreateReader(handler);

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 2, beforeRecordId: null, afterRecordId: null);

        Assert.Equal(1001, page.Messages.Count);
        Assert.Equal(202, page.Messages[0].Id);
        Assert.Equal(1202, page.Messages[^1].Id);
        Assert.Equal(1000, page.Messages.Count(message => message.MessageUid == "large-boundary"));
        Assert.True(page.HasEarlier);
        Assert.False(page.HasLater);
        Assert.Contains(1000, handler.StreamRequestLimits);
    }

    [Fact]
    public async Task Reader_AfterCursorCatchesUpInAscendingPages()
    {
        var reader = CreateReader(new PagingHandler(Enumerable.Range(1, 1085)
            .Select(id => Record(id, $"uid-{id}"))));

        var first = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: 500);
        var second = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: first.NewestRecordId);

        Assert.Equal((501L, 1000L), (first.Messages[0].Id, first.Messages[^1].Id));
        Assert.True(first.HasEarlier);
        Assert.True(first.HasLater);
        Assert.Equal((1001L, 1085L), (second.Messages[0].Id, second.Messages[^1].Id));
        Assert.True(second.HasEarlier);
        Assert.False(second.HasLater);
    }

    [Fact]
    public async Task Reader_AfterCursorCompletesNewestBoundaryUid()
    {
        var records = new List<TestRecord> { Record(1, "anchor") };
        records.AddRange(Enumerable.Range(2, 1000).Select(id => Record(id, "large-boundary")));
        records.Add(Record(1002, "later"));
        var handler = new PagingHandler(records);
        var reader = CreateReader(handler);

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 2, beforeRecordId: null, afterRecordId: 1);

        Assert.Equal(1000, page.Messages.Count);
        Assert.Equal(2, page.Messages[0].Id);
        Assert.Equal(1001, page.Messages[^1].Id);
        Assert.All(page.Messages, message => Assert.Equal("large-boundary", message.MessageUid));
        Assert.True(page.HasEarlier);
        Assert.True(page.HasLater);
        Assert.Contains(1000, handler.StreamRequestLimits);
    }

    [Fact]
    public async Task Reader_RejectsOversizedLogicalBoundaryWithoutPartialPage()
    {
        var reader = CreateReader(new PagingHandler(
        [
            new TestRecord(1, "oversized", new string('x', RedLeafSessionReader.MaxTranscriptBoundaryBytes)),
        ]));

        await Assert.ThrowsAsync<TranscriptPageTooLargeException>(() =>
            reader.GetTranscriptPageAsync(
                EntityId, SessionId, 1, beforeRecordId: null, afterRecordId: null));
    }

    [Fact]
    public async Task Reader_CoalescesRealShapeLegacyThinkingBeforeApplyingTheBoundaryRecordGuard()
    {
        const string uid = "5464ba3ed5be45e49b311b658134ade1";
        var records = new List<TestRecord>();
        var expectedThinking = new List<string>();
        long id = 1;
        var remainingToolPairs = 2_236;
        for (var run = 0; run < 300; run++)
        {
            var fragments = Enumerable.Range(0, 30)
                .Select(fragment => $"[{run:D3}.{fragment:D2}]")
                .ToArray();
            expectedThinking.Add(string.Concat(fragments));
            foreach (var fragment in fragments)
                records.Add(Thinking(id++, uid, fragment));

            var remainingRuns = 300 - run;
            var pairsThisRun = (int)Math.Ceiling((double)remainingToolPairs / remainingRuns);
            for (var pair = 0; pair < pairsThisRun; pair++)
            {
                var toolId = $"tool-{run}-{pair}";
                records.Add(new TestRecord(id++, uid, null,
                    EventType: "tool_use", MessageId: toolId, ToolName: "Read", ToolInput: "{}"));
                records.Add(new TestRecord(id++, uid, "ok",
                    EventType: "tool_result", MessageId: toolId, ToolResult: "ok"));
            }
            remainingToolPairs -= pairsThisRun;
        }

        Assert.Equal(13_472, records.Count);
        var reader = CreateReader(new PagingHandler(records));

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 50, beforeRecordId: null, afterRecordId: null);

        Assert.Equal(4_772, page.Messages.Count);
        Assert.Equal(expectedThinking,
            page.Messages.Where(message => message.EventType == "thinking").Select(message => message.Content));
        Assert.All(page.Messages, message => Assert.Equal(uid, message.MessageUid));
        Assert.False(page.HasEarlier);
        Assert.False(page.HasLater);
        Assert.Equal((1L, 13_472L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(page.Messages, "epoch-1"));
    }

    [Fact]
    public async Task Reader_CoalescesOneRunAcrossThousandRowBatchesInBothDirections()
    {
        const string uid = "legacy-run";
        var records = new List<TestRecord> { Record(1, "older") };
        records.AddRange(Enumerable.Range(2, 1_200)
            .Select(id => Thinking(id, uid, $"[{id}]")));
        records.Add(Record(1_202, "newer"));
        var expected = string.Concat(Enumerable.Range(2, 1_200).Select(id => $"[{id}]"));
        var handler = new PagingHandler(records);
        var reader = CreateReader(handler);

        var newest = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 1, beforeRecordId: null, afterRecordId: null);
        var descending = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 1, beforeRecordId: newest.OldestRecordId, afterRecordId: null);
        var ascending = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 1, beforeRecordId: null, afterRecordId: 1);

        foreach (var page in new[] { descending, ascending })
        {
            var thinking = Assert.Single(page.Messages);
            Assert.Equal((2L, 2L, "thinking", expected),
                (thinking.Id, thinking.Sequence, thinking.EventType, thinking.Content));
            Assert.Equal((2L, 1_201L), (page.OldestRecordId, page.NewestRecordId));
            // The advertised range describes the mapped message clients validate, not hidden fragments.
            Assert.Equal((2L, 2L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(page.Messages, "epoch-1"));
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), thinking.Timestamp);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(2), thinking.RecordCreatedAt);
        }
        Assert.Contains(1_000, handler.StreamRequestLimits);
    }

    [Fact]
    public async Task Reader_BeforeAndAfterWalksReconstructTheSameMappedTranscript()
    {
        var records = new List<TestRecord>();
        long id = 1;
        for (var unit = 0; unit < 35; unit++)
        {
            var uid = $"uid-{unit:D2}";
            if (unit % 3 == 0)
            {
                for (var fragment = 0; fragment < 5; fragment++)
                    records.Add(Thinking(id++, uid, $"{unit}.{fragment};"));
            }
            else
            {
                records.Add(new TestRecord(id++, uid, $"text-{unit}"));
            }
        }
        var reader = CreateReader(new PagingHandler(records));

        var newestPages = new List<TranscriptPageReadResult>();
        long? before = null;
        do
        {
            var page = await reader.GetTranscriptPageAsync(
                EntityId, SessionId, 7, beforeRecordId: before, afterRecordId: null);
            newestPages.Add(page);
            before = page.OldestRecordId;
        } while (newestPages[^1].HasEarlier);
        var beforeWalk = newestPages.AsEnumerable().Reverse().SelectMany(page => page.Messages).ToList();

        var afterWalk = new List<UnifiedMessageRecord>();
        long? after = 0;
        while (true)
        {
            var page = await reader.GetTranscriptPageAsync(
                EntityId, SessionId, 7, beforeRecordId: null, afterRecordId: after);
            afterWalk.AddRange(page.Messages);
            if (!page.HasLater) break;
            after = page.NewestRecordId;
        }

        static object Shape(UnifiedMessageRecord message) => new
        {
            message.Id,
            message.EventType,
            message.Content,
            message.MessageUid,
            message.Sequence,
        };
        Assert.Equal(35, beforeWalk.Count);
        Assert.Equal(beforeWalk.Select(Shape), afterWalk.Select(Shape));
        Assert.Equal(35, beforeWalk.Select(message => message.Id).Distinct().Count());
    }

    [Fact]
    public async Task Reader_MergesOnlyStrictlyEligibleContiguousLegacyThinking()
    {
        var records = new[]
        {
            Thinking(1, "merge", "a"),
            Thinking(2, "merge", "b"),
            Thinking(3, "merge", "message-id", messageId: "msg:0"),
            Thinking(4, "merge", "after-id"),
            Thinking(5, "uid-a", "uid-a"),
            Thinking(6, "uid-b", "uid-b"),
            Thinking(7, "payload", "payload", hasPayload: true),
            Thinking(8, "payload", "after-payload"),
            Thinking(9, "interrupted", "before-text"),
            new TestRecord(10, "interrupted", "text", EventType: "text"),
            Thinking(11, "interrupted", "after-text"),
            Thinking(12, "tool-field", "tool", toolName: "Read"),
            Thinking(13, "tool-field", "after-tool"),
            Thinking(14, "phase", "phase", phase: "commentary"),
            Thinking(15, "phase", "after-phase"),
            Thinking(16, "epoch", "epoch-1", epoch: "epoch-1"),
            Thinking(17, "epoch", "epoch-2", epoch: "epoch-2"),
        };
        var reader = CreateReader(new PagingHandler(records));

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: null);

        Assert.Equal(16, page.Messages.Count);
        Assert.Equal("ab", page.Messages[0].Content);
        Assert.Equal(1, page.Messages[0].Id);
        Assert.Equal(1, page.Messages[0].Sequence);
        Assert.Equal((1L, 16L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(page.Messages, "epoch-1"));
        Assert.Equal(Enumerable.Range(3, 15).Select(id => (long)id),
            page.Messages.Skip(1).Select(message => message.Id));
        Assert.NotNull(page.Messages.Single(message => message.Id == 7).PayloadRef);
    }

    [Fact]
    public async Task PageSequenceRange_UsesExtremesWhenStorageOrderIsInverted()
    {
        // Exact production after-page from session 1d600ab9e13b (2026-10-18): Claude persists a
        // completed text block at block stop, so its earlier sequence lands after later records.
        long[] sequences =
        [
            15692, 15671, 15689, 15691, 15711, 15713, 15714, 15746, 15748,
            15749, 15758, 15761, 15760, 15763, 15764, 15794, 15802, 15804,
        ];
        var records = sequences.Select((sequence, index) =>
            new TestRecord(index + 1, "5dfd96d9", $"record-{sequence}", Sequence: sequence));
        var reader = CreateReader(new PagingHandler(records));

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: null);

        Assert.Equal(15692, page.Messages[0].Sequence);
        Assert.Equal(sequences, page.Messages.Select(message => message.Sequence!.Value));
        Assert.Equal((15671L, 15804L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(page.Messages, "epoch-1"));
    }

    [Fact]
    public async Task PageSequenceRange_MatchesMappedMessagesWhenALegacyThinkingRunEndsThePage()
    {
        var records = new[]
        {
            new TestRecord(1, "turn", "text"),
            Thinking(2, "turn", "a"),
            Thinking(3, "turn", "b"),
            Thinking(4, "turn", "c"),
        };
        var reader = CreateReader(new PagingHandler(records));

        var page = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: null);

        Assert.Equal([1L, 2L], page.Messages.Select(message => message.Sequence!.Value));
        Assert.Equal((1L, 2L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(page.Messages, "epoch-1"));
        Assert.Equal(4L, page.NewestRecordId);
    }

    [Fact]
    public void PageSequenceRange_IgnoresOtherEpochsAndUnsequencedMessages()
    {
        UnifiedMessageRecord Message(long? sequence, string? epoch) =>
            new() { SessionId = SessionId, Role = "assistant", EventType = "text", Sequence = sequence, Epoch = epoch };

        Assert.Equal(((long?)null, (long?)null), UnifiedSessionEndpoints.TranscriptPageSequenceRange([], "epoch-1"));
        Assert.Equal((5L, 9L), UnifiedSessionEndpoints.TranscriptPageSequenceRange(
            [Message(9, "epoch-1"), Message(1, "epoch-0"), Message(null, "epoch-1"), Message(5, "epoch-1"), Message(99, null)],
            "epoch-1"));
    }

    [Fact]
    public async Task Reader_StillRejectsMoreThanTenThousandMappedUnitsInOneUid()
    {
        var records = Enumerable.Range(1, RedLeafSessionReader.MaxTranscriptBoundaryRecords + 1)
            .Select(id => Thinking(id, "still-too-large", "x", messageId: $"msg:{id}"));
        var reader = CreateReader(new PagingHandler(records));

        var error = await Assert.ThrowsAsync<TranscriptPageTooLargeException>(() =>
            reader.GetTranscriptPageAsync(
                EntityId, SessionId, 1, beforeRecordId: null, afterRecordId: null));

        Assert.Equal(RedLeafSessionReader.MaxTranscriptBoundaryRecords + 1, error.RecordCount);
    }

    [Fact]
    public void CursorCodec_RoundTripsAndRejectsMalformedMismatchAndStaleCursors()
    {
        var encoded = TranscriptPageCursorCodec.Encode(new TranscriptPageCursor(
            SessionId, "epoch-1", 42, TranscriptPageCursorEdge.Oldest));

        var decoded = TranscriptPageCursorCodec.Decode(
            encoded, SessionId, "epoch-1", TranscriptPageCursorEdge.Oldest);
        Assert.Equal(42, decoded.RecordId);

        Assert.Equal("invalid_cursor", Assert.Throws<TranscriptPageCursorException>(() =>
            TranscriptPageCursorCodec.Decode(
                "not-base64", SessionId, "epoch-1", TranscriptPageCursorEdge.Oldest)).Code);
        Assert.Equal("transcript_cursor_mismatch", Assert.Throws<TranscriptPageCursorException>(() =>
            TranscriptPageCursorCodec.Decode(
                encoded, "another-session", "epoch-1", TranscriptPageCursorEdge.Oldest)).Code);
        Assert.Equal("transcript_cursor_mismatch", Assert.Throws<TranscriptPageCursorException>(() =>
            TranscriptPageCursorCodec.Decode(
                encoded, SessionId, "epoch-1", TranscriptPageCursorEdge.Newest)).Code);
        Assert.Equal("transcript_cursor_stale", Assert.Throws<TranscriptPageCursorException>(() =>
            TranscriptPageCursorCodec.Decode(
                encoded, SessionId, "epoch-2", TranscriptPageCursorEdge.Oldest)).Code);
    }

    [Fact]
    public async Task Reader_OppositeSideFactsDoNotTrustForgedOrClearedAnchors()
    {
        var records = Enumerable.Range(1, 3).Select(id => Record(id, $"uid-{id}")).ToArray();
        var reader = CreateReader(new PagingHandler(records));

        var forgedBefore = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: 999, afterRecordId: null);
        Assert.Equal([1L, 2L, 3L], forgedBefore.Messages.Select(message => message.Id));
        Assert.False(forgedBefore.HasEarlier);
        Assert.False(forgedBefore.HasLater);

        var forgedAfter = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: 999);
        Assert.Empty(forgedAfter.Messages);
        Assert.True(forgedAfter.HasEarlier);
        Assert.False(forgedAfter.HasLater);

        var clearedReader = CreateReader(new PagingHandler([]));
        var clearedAfter = await clearedReader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: 999);
        Assert.Empty(clearedAfter.Messages);
        Assert.False(clearedAfter.HasEarlier);
        Assert.False(clearedAfter.HasLater);
    }

    [Fact]
    public async Task Reader_EmptyPageCountsAnExistingExclusiveAnchorOnTheOppositeSide()
    {
        var reader = CreateReader(new PagingHandler([Record(1, "uid-1")]));

        var before = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: 1, afterRecordId: null);
        Assert.Empty(before.Messages);
        Assert.False(before.HasEarlier);
        Assert.True(before.HasLater);

        var after = await reader.GetTranscriptPageAsync(
            EntityId, SessionId, 500, beforeRecordId: null, afterRecordId: 1);
        Assert.Empty(after.Messages);
        Assert.True(after.HasEarlier);
        Assert.False(after.HasLater);
    }

    private const string SessionId = "session-1";
    private const string EntityId = "1910ac53-d68a-4ccc-883d-0541c0091d9b";

    private static RedLeafSessionReader CreateReader(HttpMessageHandler handler, TranscriptCheckpointCoordinator? checkpoints = null)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var providers = new ProviderConfigService(config, log);
        var quality = new QualityModeService(
            config,
            log,
            providers,
            new HttpClient(new EmptyHandler()),
            Path.Combine(Path.GetTempPath(), $"redcompute-page-test-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2));
        return new RedLeafSessionReader(
            new HttpClient(handler) { BaseAddress = new Uri("http://redleaf/") },
            quality) { Checkpoints = checkpoints };
    }

    private static TestRecord Record(int id, string uid) => new(id, uid, $"record-{id}");

    private static TestRecord Thinking(
        long id,
        string uid,
        string content,
        string? messageId = null,
        bool hasPayload = false,
        string? toolName = null,
        string? phase = null,
        string? epoch = "epoch-1") =>
        new(id, uid, content, EventType: "thinking", MessageId: messageId,
            Epoch: epoch, HasPayload: hasPayload, ToolName: toolName, Phase: phase);

    private sealed record TestRecord(
        long Id,
        string? MessageUid,
        string? Content,
        string Role = "assistant",
        string EventType = "text",
        string? MessageId = null,
        string? Epoch = "epoch-1",
        long? Sequence = null,
        bool HasPayload = false,
        string? ToolName = null,
        string? ToolInput = null,
        string? ToolResult = null,
        string? Phase = null);

    private sealed class PagingHandler(IEnumerable<TestRecord> source) : HttpMessageHandler
    {
        private readonly IReadOnlyList<TestRecord> _records = source.OrderBy(record => record.Id).ToArray();
        public List<int> StreamRequestLimits { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = ParseQuery(request.RequestUri!);
            var limit = Math.Min(int.Parse(query["limit"]), 1000);
            StreamRequestLimits.Add(limit);
            IEnumerable<TestRecord> selected = _records;
            if (query.TryGetValue("before_id", out var before))
                selected = selected.Where(record => record.Id < long.Parse(before));
            if (query.TryGetValue("after_id", out var after))
                selected = selected.Where(record => record.Id > long.Parse(after));
            selected = query["order"] == "asc"
                ? selected.OrderBy(record => record.Id)
                : selected.OrderByDescending(record => record.Id);

            var body = new
            {
                items = selected.Take(limit).Select(record => new
                {
                    id = record.Id,
                    createdAt = DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(record.Id).ToString("O"),
                    data = JsonSerializer.Serialize(new
                    {
                        session_id = SessionId,
                        role = record.Role,
                        event_type = record.EventType,
                        content = record.Content,
                        tool_name = record.ToolName,
                        tool_input = record.ToolInput,
                        tool_result = record.ToolResult,
                        message_id = record.MessageId,
                        message_uid = record.MessageUid,
                        phase = record.Phase,
                        epoch = record.Epoch,
                        sequence = record.Sequence ?? record.Id,
                        timestamp = DateTimeOffset.UnixEpoch.AddSeconds(record.Id).ToString("O"),
                    }),
                    payload = record.HasPayload
                        ? (object)new
                        {
                            available = true,
                            length = 1,
                            contentType = "text/plain",
                            encoding = "utf-8",
                            sha256 = "fixture",
                        }
                        : null,
                }),
            };
            return Task.FromResult(Json(body));
        }
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Json(new { items = Array.Empty<object>() }));
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]));

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
