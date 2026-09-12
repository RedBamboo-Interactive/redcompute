using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptPagingTests
{
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

    private static RedLeafSessionReader CreateReader(HttpMessageHandler handler)
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
            quality);
    }

    private static TestRecord Record(int id, string uid) => new(id, uid, $"record-{id}");

    private sealed record TestRecord(long Id, string MessageUid, string Content);

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
                        role = "assistant",
                        event_type = "text",
                        content = record.Content,
                        message_uid = record.MessageUid,
                        epoch = "epoch-1",
                        sequence = record.Id,
                        timestamp = DateTimeOffset.UnixEpoch.AddSeconds(record.Id).ToString("O"),
                    }),
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
