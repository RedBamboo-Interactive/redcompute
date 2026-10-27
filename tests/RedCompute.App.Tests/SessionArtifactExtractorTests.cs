using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RedCompute.App.Services.SessionArtifacts;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Drives the extractor and accumulator from tests/Fixtures/artifact-extraction-cases.json. The same
/// file is copied byte-for-byte into @redbamboo/chat, where the renderer's predicates are tested
/// against it, so a rule changing on either side fails a test on that side.
/// </summary>
public sealed class SessionArtifactExtractorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class CorpusRecord
    {
        public long Id { get; set; }
        public string Role { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? Content { get; set; }
        public string? MessageUid { get; set; }
        public long? Sequence { get; set; }
        public string? Phase { get; set; }
        public string? ToolName { get; set; }
        public string? ToolInput { get; set; }
        public TranscriptPayloadRef? PayloadRef { get; set; }
        public string? AttachmentsJson { get; set; }
    }

    private static readonly Lazy<JsonArray> Corpus = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "artifact-extraction-cases.json")))!.AsArray());

    public static IEnumerable<object[]> CaseNames()
        => Corpus.Value.Select(item => new object[] { item!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Corpus_case_extracts_exactly_the_expected_artifacts(string name)
    {
        var testCase = Corpus.Value.Single(item => item!["name"]!.GetValue<string>() == name)!;
        var records = testCase["records"]!.Deserialize<List<CorpusRecord>>(Web)!;

        // Session facts the extractor needs live on the case, not on records: the working folder.
        var projectPath = testCase["projectPath"]?.GetValue<string>();
        var accumulator = new SessionArtifactAccumulator();
        foreach (var record in records)
        {
            var message = ToMessage(record);
            accumulator.Add(message, SessionArtifactExtractor.Extract(message, projectPath));
        }

        var actual = JsonSerializer.SerializeToNode(accumulator.Query(null, null, null), Web)!.AsArray();
        foreach (var item in actual)
        {
            item!["first"]!.AsObject().Remove("timestamp");
            item["last"]!.AsObject().Remove("timestamp");
        }
        var expected = testCase["expected"]!;

        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"{name}\nexpected:\n{expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}\n" +
            $"actual:\n{actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
        // Expected order is part of the contract, which DeepEquals on arrays already enforces.
    }

    [Fact]
    public void Corpus_is_self_contained_synthetic_and_never_carries_inline_media()
    {
        var names = Corpus.Value.Select(item => item!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, name => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name));
        Assert.True(names.Count >= 35);

        foreach (var testCase in Corpus.Value)
        {
            foreach (var artifact in testCase!["expected"]!.AsArray())
            {
                var target = artifact!["target"]!.GetValue<string>();
                Assert.DoesNotContain("base64", target, StringComparison.OrdinalIgnoreCase);
                Assert.False(target.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
            }
        }

        var kinds = Corpus.Value.SelectMany(c => c!["expected"]!.AsArray())
            .Select(a => a!["kind"]!.GetValue<string>()).ToHashSet();
        Assert.Superset(new HashSet<string>(ArtifactKinds.All), kinds);
        var origins = Corpus.Value.SelectMany(c => c!["expected"]!.AsArray())
            .Select(a => a!["origin"]!.GetValue<string>()).ToHashSet();
        Assert.Superset(new HashSet<string>(ArtifactOrigins.All), origins);
    }

    [Fact]
    public void Extraction_never_reads_thinking_even_when_it_is_full_of_links()
    {
        var message = Message("thinking", "assistant", "[a](https://thinking.example.com) ![b](/api/assets/x.png)");
        Assert.Empty(SessionArtifactExtractor.Extract(message));
    }

    [Fact]
    public void Plain_text_without_links_is_cheap_and_empty()
    {
        Assert.Empty(SessionArtifactExtractor.Extract(Message("text", "assistant", "just words, no links")));
        Assert.Empty(SessionArtifactExtractor.Extract(Message("text", "assistant", null)));
        Assert.Empty(SessionArtifactExtractor.Extract(Message("text", "assistant", "   ")));
    }

    [Fact]
    public void Titles_are_bounded()
    {
        var long_ = new string('x', 500);
        var candidate = Assert.Single(SessionArtifactExtractor.Extract(
            Message("text", "assistant", $"[{long_}](https://long.example.com/)")));
        Assert.Equal(200, candidate.Title!.Length);
    }

    private static UnifiedMessageRecord Message(string eventType, string role, string? content) => new()
    {
        Id = 1, SessionId = "s1", Role = role, EventType = eventType, Content = content, Sequence = 1,
    };

    private static UnifiedMessageRecord ToMessage(CorpusRecord record) => new()
    {
        Id = record.Id,
        SessionId = "s1",
        Role = record.Role,
        EventType = record.EventType,
        Content = record.Content,
        MessageUid = record.MessageUid,
        Sequence = record.Sequence,
        Phase = record.Phase,
        ToolName = record.ToolName,
        ToolInput = record.ToolInput,
        PayloadRef = record.PayloadRef,
        AttachmentsJson = record.AttachmentsJson,
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(record.Id),
    };
}
