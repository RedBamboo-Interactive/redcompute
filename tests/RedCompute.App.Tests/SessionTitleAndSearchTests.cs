using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.SessionTitles;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Code-parity session actions (Brief A1/A2): the one title generator moved from Nova, the
/// explicit-title precedence, PUT/POST title routes and transcript search.
/// </summary>
[Collection("Unified session endpoint mapping")]
public sealed class SessionTitleAndSearchTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-11-01T00:00:00Z");

    // ── Generation: prompt and cleaning, unchanged from Nova ──────────────────────────────

    [Fact]
    public void Prompt_keeps_visible_conversation_and_labels_the_assistant()
    {
        var messages = new List<UnifiedMessageRecord>
        {
            Message(1, "user", "text", "<nova-context>hidden</nova-context>Fix the   login\nbug", uid: "u1"),
            Message(2, "assistant", "thinking", "secret reasoning"),
            Message(3, "assistant", "text", "Looking ", uid: "a1"),
            Message(4, "assistant", "text", "at it.", uid: "a1"),
            Message(5, "assistant", "text", "progress note", uid: "a2", phase: "commentary"),
            Message(6, "assistant", "tool_use", "{\"cmd\":\"ls\"}"),
            Message(7, "assistant", "text", "<nova-no-reply/>", uid: "a3"),
        };

        var labelled = SessionTitleGeneration.BuildPrompt("Old title", messages, "Nova");
        Assert.Equal("Current title: Old title\nConversation:\nUser: Fix the login bug\nNova: Looking at it.",
            labelled!.ReplaceLineEndings("\n"));

        var unlabelled = SessionTitleGeneration.BuildPrompt(null, messages);
        Assert.Equal("Conversation:\nUser: Fix the login bug\nAssistant: Looking at it.",
            unlabelled!.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("secret", unlabelled);
        Assert.DoesNotContain("progress note", unlabelled);

        Assert.Null(SessionTitleGeneration.BuildPrompt("t", [Message(1, "assistant", "thinking", "only thinking")]));
    }

    [Fact]
    public void Prompt_keeps_the_first_four_and_last_sixteen_messages_each_cut_to_600_characters()
    {
        var messages = Enumerable.Range(0, 30)
            .Select(i => Message(i, i % 2 == 0 ? "user" : "assistant", "text", $"m{i} " + new string('x', 700), uid: $"u{i}"))
            .ToList();
        var lines = SessionTitleGeneration.BuildPrompt(null, messages)!.ReplaceLineEndings("\n").Split('\n')[1..];

        Assert.Equal(20, lines.Length);
        Assert.StartsWith("User: m0 ", lines[0]);
        Assert.StartsWith("Assistant: m3 ", lines[3]);
        Assert.StartsWith("User: m14 ", lines[4]);
        Assert.All(lines, line => Assert.EndsWith("…", line));
        Assert.Equal(600, lines[0]["User: ".Length..].Length);
    }

    [Theory]
    [InlineData("Title: \"Fixing the login bug.\"", "Fixing the login bug")]
    [InlineData("Sure!\nConversation title: **Login fix**", "Login fix")]
    [InlineData("`Deploy plan;`", "Deploy plan")]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    public void Cleaning_takes_the_last_line_without_label_quotes_or_ending_punctuation(string raw, string? expected)
        => Assert.Equal(expected, SessionTitleGeneration.CleanGeneratedTitle(raw));

    [Fact]
    public void Cleaning_refuses_titles_over_seventy_characters()
        => Assert.Null(SessionTitleGeneration.CleanGeneratedTitle(new string('a', 71)));

    // ── Precedence ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Explicit_title_outranks_provider_titles_at_reads_and_mirror_and_survives_restart()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var path = Path.Combine(f.Root, "titles.db");
        var store = new SessionTitleStore(path);

        var untouched = store.Apply(Info("s1", "Provider title"));
        Assert.Equal(("Provider title", SessionTitleSources.Provider), (untouched.Title, untouched.TitleSource));
        Assert.Null(store.Apply(Info("s1", null)).TitleSource);
        Assert.Equal(("proj (test)", (string?)null), SessionTitleStore.ForMirror(null, "proj (test)", null));

        store.Set("s1", "test", "My rename", SessionTitleSources.Manual);
        // A later provider sync (Claude ai-title, Codex thread name) carries its own title again.
        var read = store.Apply(Info("s1", "Provider retitled"));
        Assert.Equal(("My rename", SessionTitleSources.Manual), (read.Title, read.TitleSource));
        Assert.Equal(("My rename", SessionTitleSources.Manual),
            SessionTitleStore.ForMirror("Provider retitled", "proj (test)", store.Get("s1")));

        // Explicit titles replace each other, latest first.
        store.Set("s1", "test", "Generated one", SessionTitleSources.Generated);
        Assert.Equal(SessionTitleSources.Generated, store.Apply(Info("s1", "x")).TitleSource);

        var restarted = new SessionTitleStore(path);
        Assert.Equal("Generated one", restarted.Apply(Info("s1", "Provider again")).Title);

        // A rebuilt local database adopts what the RedLeaf record already marks explicit.
        var empty = new SessionTitleStore(Path.Combine(f.Root, "fresh.db"));
        var mirrored = Info("s2", "Renamed elsewhere");
        mirrored.TitleSource = SessionTitleSources.Manual;
        empty.Apply(mirrored);
        Assert.Equal("Renamed elsewhere", empty.Apply(Info("s2", "Provider title")).Title);

        Assert.Throws<ArgumentException>(() => store.Set("s3", "test", "t", SessionTitleSources.Provider));
    }

    // ── PUT /ai-session/sessions/{id}/title ──────────────────────────────────────────────

    [Fact]
    public async Task Rename_saves_locally_and_on_the_record_and_later_provider_sync_cannot_undo_it()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner");

        var (status, body) = await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "own", body: new { title = "  My   rename " });
        Assert.Equal(200, status);
        Assert.Equal("own", body.GetProperty("id").GetString());
        Assert.Equal("My rename", body.GetProperty("title").GetString());
        Assert.Equal("manual", body.GetProperty("titleSource").GetString());
        Assert.Equal("My rename", h.Leaf.Sessions["own"].Name);
        Assert.Equal("manual", h.Leaf.Sessions["own"].Data["title_source"]!.GetValue<string>());
        Assert.Contains("PUT /api/entities/by-slug/ai-session-test-own", h.Leaf.Requests);
        Assert.Equal(["own"], h.Changed.Select(s => s.Id));

        // The provider's next mirror sync writes its own title back onto the record.
        h.Leaf.Sessions["own"].Name = "Provider retitled";
        h.Leaf.Sessions["own"].Data["title_source"] = "provider";
        var (_, detail) = await h.CallAsync("GET", "/ai-session/sessions/{id}", "own");
        Assert.Equal("My rename", detail.GetProperty("session").GetProperty("title").GetString());
        Assert.Equal("manual", detail.GetProperty("session").GetProperty("titleSource").GetString());
    }

    [Fact]
    public async Task Rename_refuses_invalid_titles_unknown_and_foreign_sessions()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner");
        h.Leaf.AddSession("foreign", "Theirs", owner: "other");
        h.Leaf.AddSession("secret", "Hidden", owner: "other", confidential: true);

        foreach (var title in new object?[] { "   ", new string('a', 201), null, 42 })
        {
            var (status, error) = await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "own", body: new { title });
            Assert.Equal(400, status);
            Assert.Equal("invalid_title", error.GetProperty("error").GetString());
        }
        Assert.Equal(200, (await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "own", body: new { title = new string('a', 200) })).Status);
        Assert.Equal(404, (await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "missing", body: new { title = "x" })).Status);
        Assert.Equal(403, (await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "foreign", body: new { title = "x" })).Status);
        Assert.Equal(404, (await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "secret", body: new { title = "x" })).Status);
        Assert.Equal("Theirs", h.Leaf.Sessions["foreign"].Name);
    }

    [Fact]
    public async Task Rename_that_RedLeaf_refuses_changes_nothing()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner");
        h.Leaf.FailWrites = true;

        var (status, error) = await h.CallAsync("PUT", "/ai-session/sessions/{id}/title", "own", body: new { title = "Lost" });
        Assert.Equal(503, status);
        Assert.Equal("redleaf_unavailable", error.GetProperty("error").GetString());
        Assert.Null(h.Titles.Get("own"));
        Assert.Empty(h.Changed);
    }

    // ── POST /ai-session/sessions/{id}/title/generate ────────────────────────────────────

    [Fact]
    public async Task Generate_runs_the_fast_tier_once_and_applies_a_generated_title()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner", records: Conversation());
        h.Provider.Text = "Title: \"Fixing the login bug.\"";

        var (status, body) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "own");
        Assert.Equal(200, status);
        Assert.Equal("Fixing the login bug", body.GetProperty("title").GetString());
        Assert.Equal("fast", body.GetProperty("qualityTier").GetString());
        Assert.Equal(h.Jobs.Created.Single().Id.ToString(), body.GetProperty("jobId").GetString());
        Assert.Equal("Fixing the login bug", body.GetProperty("session").GetProperty("title").GetString());
        Assert.Equal("generated", body.GetProperty("session").GetProperty("titleSource").GetString());
        Assert.Equal("generated", h.Titles.Get("own")!.Source);
        Assert.Equal("Fixing the login bug", h.Leaf.Sessions["own"].Name);

        var request = h.Provider.LastRequest!;
        Assert.Equal(48, request.MaxTokens);
        Assert.Equal("test-fast", request.Model);
        Assert.Equal(SessionTitleGeneration.SystemPrompt, request.System);
        Assert.Contains("records?entity_id=entity-own&order=desc&limit=500", string.Join("\n", h.Leaf.Requests));
        var prompt = JsonDocument.Parse(request.MessagesJson).RootElement[0].GetProperty("content").GetString()!;
        Assert.Equal("Current title: Provider title\nConversation:\nUser: Fix the login bug\nAssistant: Looking at it.",
            prompt.ReplaceLineEndings("\n"));

        var job = h.Jobs.Submissions.Single();
        Assert.Equal("Update session title", job.Name);
        Assert.False(job.Confidential);
        Assert.Equal(1, h.Jobs.Completed);
        Assert.Equal(["own"], h.Changed.Select(s => s.Id));
    }

    [Fact]
    public async Task Generate_without_apply_only_returns_and_carries_the_assistant_label()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner", records: Conversation());
        h.Provider.Text = "Login bug fix";

        var (status, body) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "own",
            query: "?apply=false&assistantLabel=%20Nova%20", jobName: "Update Nova discussion title");
        Assert.Equal(200, status);
        Assert.Equal("Login bug fix", body.GetProperty("title").GetString());
        Assert.Equal("Provider title", body.GetProperty("session").GetProperty("title").GetString());
        Assert.Equal("provider", body.GetProperty("session").GetProperty("titleSource").GetString());
        Assert.Null(h.Titles.Get("own"));
        Assert.Equal("Provider title", h.Leaf.Sessions["own"].Name);
        Assert.DoesNotContain(h.Leaf.Requests, r => r.StartsWith("PUT") || r.StartsWith("PATCH"));
        Assert.Empty(h.Changed);
        Assert.Equal("Update Nova discussion title", h.Jobs.Submissions.Single().Name);

        var prompt = JsonDocument.Parse(h.Provider.LastRequest!.MessagesJson).RootElement[0].GetProperty("content").GetString()!;
        Assert.Contains("\nNova: Looking at it.", prompt.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Assistant:", prompt);
    }

    [Fact]
    public async Task Generate_refuses_bad_parameters_empty_transcripts_and_inaccessible_sessions()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner", records: Conversation());
        h.Leaf.AddSession("quiet", "Quiet", owner: "owner", records: [Record(1, "assistant", "thinking", "hmm")]);
        h.Leaf.AddSession("foreign", "Theirs", owner: "other", records: Conversation());

        async Task Expect(int status, string? code, string id, string query = "")
        {
            var (actual, body) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", id, query: query);
            Assert.Equal(status, actual);
            if (code is not null) Assert.Equal(code, body.GetProperty("error").GetString());
        }

        await Expect(400, "invalid_apply", "own", "?apply=maybe");
        await Expect(400, "invalid_assistant_label", "own", "?assistantLabel=%20%20");
        await Expect(400, "invalid_assistant_label", "own", "?assistantLabel=" + new string('n', 41));
        await Expect(409, "transcript_empty", "quiet");
        await Expect(404, "not_found", "missing");
        await Expect(403, "forbidden", "foreign");
        Assert.Equal(0, h.Provider.GenerateCalls);
    }

    [Fact]
    public async Task Generate_failures_answer_title_generation_failed_with_the_job()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("own", "Provider title", owner: "owner", records: Conversation());

        h.Provider.Text = "\"\"";
        var (unusable, body) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "own");
        Assert.Equal(502, unusable);
        Assert.Equal("title_generation_failed", body.GetProperty("error").GetString());
        Assert.Equal("The fast model did not return a usable title.", body.GetProperty("message").GetString());
        Assert.Equal(h.Jobs.Created.Last().Id.ToString(), body.GetProperty("jobId").GetString());

        h.Provider.Fail = "provider exploded";
        var (failed, error) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "own");
        Assert.Equal(502, failed);
        Assert.Equal("title_generation_failed", error.GetProperty("error").GetString());
        Assert.Equal("provider exploded", error.GetProperty("message").GetString());
        Assert.Equal(h.Jobs.Created.Last().Id.ToString(), error.GetProperty("jobId").GetString());
        Assert.Equal(1, h.Jobs.Failed);
        Assert.Null(h.Titles.Get("own"));

        // A refusal before anything ran keeps its upstream status and has no job.
        await using var uncatalogued = await Host.CreateAsync(catalog: false);
        uncatalogued.Leaf.AddSession("own", "Provider title", owner: "owner", records: Conversation());
        var (unavailable, refusal) = await uncatalogued.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "own");
        Assert.Equal(503, unavailable);
        Assert.Equal("title_generation_failed", refusal.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("jobId").ValueKind);
        Assert.Empty(uncatalogued.Jobs.Created);
    }

    [Fact]
    public async Task Generate_for_a_confidential_session_runs_a_confidential_job()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.AddSession("secret", "Hidden", owner: "owner", confidential: true, records: Conversation());
        h.Provider.Text = "Private plan";

        var (status, _) = await h.CallAsync("POST", "/ai-session/sessions/{id}/title/generate", "secret",
            query: "?apply=false", principal: Host.TrustedInfrastructure());
        Assert.Equal(200, status);
        Assert.True(h.Jobs.Submissions.Single().Confidential);
    }

    // ── GET /ai-session/sessions/search ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_merges_transcript_and_title_matches_filtered_by_access_newest_first()
    {
        await using var h = await Host.CreateAsync();
        h.Leaf.ConversationHits =
        [
            new("older", "user", "text", "…the <mark>login</mark> bug…", "m-older", null, Age: 10),
            new("newer", "assistant", "text", "…fixed <mark>login</mark>…", "m-newer", null, Age: 30),
            new("archived", "user", "text", "…<mark>login</mark> archived…", "m-arch", null, Age: 40, Dismissed: true),
            new("foreign", "user", "text", "…their <mark>login</mark>…", "m-foreign", null, Age: 50, Owner: "other"),
            new("thinking-only", "assistant", "thinking", "…<mark>login</mark> pondering…", "m-think", null, Age: 60),
            new("subagent-only", "assistant", "text", "…<mark>login</mark> subagent…", "m-sub", "toolu_1", Age: 70),
        ];
        h.Leaf.TitleHits =
        [
            new("titled", "name", "Login page redesign", Age: 20),
            new("newer", "name", "Login fix", Age: 30),
            new("slug-only", "slug", "ai-session-test-login", Age: 80),
        ];

        var (status, body) = await h.CallAsync("GET", "/ai-session/sessions/search", null, query: "?q=%20login%20");
        Assert.Equal(200, status);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["archived", "newer", "titled", "older"], items.Select(i => i.GetProperty("session").GetProperty("id").GetString()));
        var newer = items[1];
        Assert.Equal("…fixed <mark>login</mark>…", newer.GetProperty("snippet").GetString());
        Assert.Equal("m-newer", newer.GetProperty("messageUid").GetString());
        Assert.False(newer.GetProperty("archived").GetBoolean());
        Assert.True(items[0].GetProperty("archived").GetBoolean());
        Assert.Equal("Login page redesign", items[2].GetProperty("snippet").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("messageUid").ValueKind);

        var conversations = h.Leaf.Requests.Single(r => r.Contains("/api/search/conversations"));
        Assert.Contains("q=login", conversations);
        Assert.Contains("streams=session-messages", conversations);
        Assert.Contains("event_type=text", conversations);
        Assert.Contains("limit=100", conversations);
        var titleQuery = JsonNode.Parse(h.Leaf.TitleQueryBody!)!;
        Assert.Equal("name", titleQuery["fieldPaths"]![0]!.GetValue<string>());
        Assert.Equal("ai-session", titleQuery["entityTypes"]![0]!.GetValue<string>());
        Assert.Equal(100, titleQuery["pageSize"]!.GetValue<int>());

        var (_, current) = await h.CallAsync("GET", "/ai-session/sessions/search", null, query: "?q=login&includeArchived=false&limit=2");
        Assert.Equal(["newer", "titled"], current.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("session").GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData("?q=a")]
    [InlineData("?q=%20%20b%20")]
    [InlineData("")]
    public async Task Search_refuses_queries_under_two_characters(string query)
    {
        await using var h = await Host.CreateAsync();
        var (status, body) = await h.CallAsync("GET", "/ai-session/sessions/search", null, query: query);
        Assert.Equal(400, status);
        Assert.Equal("invalid_query", body.GetProperty("error").GetString());
        Assert.DoesNotContain(h.Leaf.Requests, r => r.Contains("/api/search/"));
    }

    [Fact]
    public async Task Routes_are_described_for_discovery()
    {
        await using var h = await Host.CreateAsync();
        var described = h.Registry.GetEndpoints();
        var generate = described.Single(e => e.Method == "POST" && e.Path == "/ai-session/sessions/{id}/title/generate");
        Assert.Contains(generate.Parameters, p => p.Name == "assistantLabel");
        Assert.Contains(generate.Parameters, p => p.Name == "apply");
        Assert.Contains(described, e => e.Method == "PUT" && e.Path == "/ai-session/sessions/{id}/title");
        Assert.Contains(described, e => e.Method == "GET" && e.Path == "/ai-session/sessions/search");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────

    private static UnifiedSessionInfo Info(string id, string? title) => new()
    {
        Id = id, Provider = "test", ProjectName = "proj", ProjectPath = "/proj", Title = title,
    };

    private static UnifiedMessageRecord Message(long id, string role, string eventType, string content,
        string? uid = null, string? phase = null) => new()
    {
        Id = id, SessionId = "s", Role = role, EventType = eventType, Content = content, MessageUid = uid,
        Phase = phase, Timestamp = Epoch.AddSeconds(id),
    };

    private static object[] Conversation() =>
    [
        Record(1, "user", "text", "Fix the login bug", uid: "u1"),
        Record(2, "assistant", "thinking", "secret reasoning"),
        Record(3, "assistant", "text", "Looking at it.", uid: "a1"),
    ];

    private static object Record(long id, string role, string eventType, string content, string? uid = null) => new
    {
        id,
        data = JsonSerializer.Serialize(new
        {
            session_id = "ignored", role, event_type = eventType, content, message_uid = uid,
            timestamp = Epoch.AddSeconds(id).ToString("O"),
        }),
    };

    private sealed record ConversationHit(string SessionId, string Role, string EventType, string Snippet,
        string MessageUid, string? ParentToolUseId, int Age, bool Dismissed = false, string Owner = "owner");

    private sealed record TitleHit(string SessionId, string FieldPath, string Excerpt, int Age);

    private sealed class LeafSession
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required JsonObject Data { get; init; }
        public object[] Records { get; init; } = [];
    }

    private sealed class FakeLeaf : HttpMessageHandler
    {
        public Dictionary<string, LeafSession> Sessions { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public bool FailWrites { get; set; }
        public bool Catalog { get; init; } = true;
        public ConversationHit[] ConversationHits { get; set; } = [];
        public TitleHit[] TitleHits { get; set; } = [];
        public string? TitleQueryBody { get; private set; }

        public void AddSession(string id, string name, string owner, bool confidential = false, bool dismissed = false,
            object[]? records = null, int age = 0)
            => Sessions[id] = new LeafSession
            {
                Id = id, Name = name, Records = records ?? [],
                Data = SessionData(id, owner, confidential, dismissed, age),
            };

        private static JsonObject SessionData(string id, string owner, bool confidential, bool dismissed, int age) => new()
        {
            ["session_id"] = id, ["provider"] = "test", ["status"] = "Idle", ["project_name"] = "proj",
            ["started_at"] = Epoch.AddMinutes(age).ToString("O"), ["user_id"] = owner, ["owner_agent_id"] = "agent",
            ["confidential"] = confidential, ["dismissed"] = dismissed,
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            Requests.Add($"{request.Method} {path}{request.RequestUri.Query}");
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            if (request.Method == HttpMethod.Get && path == "/api/entities")
            {
                var type = query["type"].ToString();
                if (type == "ai-session")
                {
                    var id = query["data.session_id"].ToString();
                    var rows = Sessions.TryGetValue(id, out var row) ? new[] { Entity(row) } : [];
                    return Json(new { items = rows });
                }
                return Json(Catalog ? CatalogJson(type) : throw new HttpRequestException("RedLeaf unavailable"));
            }
            if (request.Method == HttpMethod.Get && path == "/api/streams/session-messages/records")
            {
                var row = Sessions.Values.Single(s => "entity-" + s.Id == query["entity_id"].ToString());
                return Json(new { items = row.Records.Reverse().ToArray() });
            }
            if (request.Method == HttpMethod.Patch && path.StartsWith("/api/entities/entity-") && path.EndsWith("/data"))
            {
                if (FailWrites) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var row = Sessions[path["/api/entities/entity-".Length..^"/data".Length]];
                foreach (var (key, value) in JsonNode.Parse(body!)!.AsObject())
                    row.Data[key] = value?.DeepClone();
                return Json(new { });
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/entities/by-slug/ai-session-test-"))
            {
                if (FailWrites) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                Sessions[path["/api/entities/by-slug/ai-session-test-".Length..]].Name =
                    JsonNode.Parse(body!)!["name"]!.GetValue<string>();
                return Json(new { });
            }
            if (request.Method == HttpMethod.Get && path == "/api/search/conversations")
            {
                return Json(new
                {
                    conversations = ConversationHits.Select(hit => new
                    {
                        entityId = "entity-" + hit.SessionId,
                        entityType = "ai-session",
                        entityName = hit.SessionId,
                        entityUpdatedAt = Epoch.AddMinutes(hit.Age),
                        entityData = SessionData(hit.SessionId, hit.Owner, false, hit.Dismissed, hit.Age).ToJsonString(),
                        hits = new[]
                        {
                            new
                            {
                                role = hit.Role, snippet = hit.Snippet,
                                fields = new Dictionary<string, string?>
                                {
                                    ["event_type"] = hit.EventType, ["message_uid"] = hit.MessageUid,
                                    ["parent_tool_use_id"] = hit.ParentToolUseId,
                                },
                            },
                        },
                    }),
                });
            }
            if (request.Method == HttpMethod.Post && path == "/api/search/query")
            {
                TitleQueryBody = body;
                return Json(new
                {
                    entities = new
                    {
                        items = TitleHits.Select(hit => new
                        {
                            entity = new
                            {
                                id = "entity-" + hit.SessionId, typeSlug = "ai-session", name = hit.Excerpt,
                                updatedAt = Epoch.AddMinutes(hit.Age),
                                data = SessionData(hit.SessionId, "owner", false, false, hit.Age),
                            },
                            matches = new[] { new { fieldPath = hit.FieldPath, excerpt = hit.Excerpt } },
                        }),
                    },
                });
            }
            throw new InvalidOperationException($"Unexpected RedLeaf request {request.Method} {request.RequestUri}");
        }

        private static object Entity(LeafSession row) => new
        {
            id = "entity-" + row.Id, slug = "ai-session-test-" + row.Id, name = row.Name, data = row.Data.ToJsonString(),
        };

        private static object CatalogJson(string type) => type switch
        {
            "quality-tier" => JsonNode.Parse("""{"items":[{"id":"tier-fast","slug":"fast","name":"Fast","data":{"label":"Fast","sort_order":1}}]}""")!,
            "quality-mode" => JsonNode.Parse("""{"items":[{"id":"mode-fast-test","slug":"fast-test","data":{"quality_tier":"tier-fast","provider":"test","model":"test-fast","effort":"low","is_default":true}}]}""")!,
            "provider" => JsonNode.Parse("""{"items":[{"id":"provider-test","slug":"test","name":"Test","data":{"backend":"test","status":"active"}}]}""")!,
            "suite-config" => JsonNode.Parse("""{"items":[{"id":"suite","slug":"suite-config","data":{"default_quality_tier":"tier-fast"}}]}""")!,
            _ => JsonNode.Parse("""{"items":[]}""")!,
        };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Jobs : IJobTracker
    {
        public List<JobSubmission> Submissions { get; } = [];
        public List<JobRecord> Created { get; } = [];
        public int Completed { get; private set; }
        public int Failed { get; private set; }

        public JobRecord CreateJob(JobSubmission submission)
        {
            Submissions.Add(submission);
            var job = new JobRecord { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
            Created.Add(job);
            return job;
        }
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) => Completed++;
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) => Failed++;
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    public interface ITitleProvider : ISessionProvider, IBackendProvider { }

    /// <summary>A session provider that generates <see cref="Text"/> and answers every other member with an empty default.</summary>
    public class TitleProvider : DispatchProxy
    {
        public string Text { get; set; } = "generated";
        public string? Fail { get; set; }
        public int GenerateCalls { get; private set; }
        public SessionGenerateRequest? LastRequest { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case "get_Name": return "Test";
                case "get_CapabilitySlug": return "ai-session";
                case "get_Capabilities": return SessionCapabilities.Generate;
                case "get_HealthCheckInterval": return TimeSpan.FromSeconds(10);
                case nameof(ISessionProvider.GenerateAsync):
                    GenerateCalls++;
                    LastRequest = (SessionGenerateRequest)args![0]!;
                    return Task.FromResult(Fail is null
                        ? new SessionGenerateResult(true, Text, "stream", "test-fast", 1, 1, null, null, null, null, false)
                        : new SessionGenerateResult(false, null, null, null, 0, 0, null, Fail, null, null, false));
            }
            var type = method.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                    .Invoke(null, [Default(inner)]);
            }
            return Default(type);
        }

        private static object? Default(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>)
                    || definition == typeof(IReadOnlyCollection<>) || definition == typeof(List<>)))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]));
            return null;
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly SessionRecoveryTests.Fixture _fixture;
        private readonly WebApplication _app;
        public FakeLeaf Leaf { get; }
        public Jobs Jobs { get; }
        public TitleProvider Provider { get; }
        public SessionTitleStore Titles { get; }
        public EndpointRegistry Registry { get; }
        public List<UnifiedSessionInfo> Changed { get; } = [];

        private Host(SessionRecoveryTests.Fixture fixture, WebApplication app, FakeLeaf leaf, TitleProvider provider,
            SessionTitleStore titles, EndpointRegistry registry, Jobs jobs)
        {
            _fixture = fixture; _app = app; Leaf = leaf; Provider = provider; Titles = titles; Registry = registry; Jobs = jobs;
        }

        public static async Task<Host> CreateAsync(bool catalog = true)
        {
            var f = new SessionRecoveryTests.Fixture("maintenance_restart");
            var leaf = new FakeLeaf { Catalog = catalog };
            var quality = new QualityModeService(f.Config, (_, _) => { }, new ProviderConfigService(f.Config, (_, _) => { }),
                new HttpClient(leaf) { BaseAddress = new Uri("http://127.0.0.1/") }, Path.Combine(f.Root, "quality.json"),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            if (catalog) Assert.True(await quality.InitialSyncAsync());
            leaf.Requests.Clear();
            var reader = new RedLeafSessionReader(new HttpClient(leaf) { BaseAddress = new Uri("http://127.0.0.1/") }, quality);

            var proxy = DispatchProxy.Create<ITitleProvider, TitleProvider>();
            var registry = new CapabilityRegistry();
            registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = proxy }, "test");

            var app = WebApplication.CreateSlimBuilder().Build();
            var endpoints = new EndpointRegistry(app);
            var titles = new SessionTitleStore(Path.Combine(f.Root, "titles.db"));
            Host? host = null;
            var jobs = new Jobs();
            UnifiedSessionEndpoints.Map(endpoints, registry, jobs, (_, _) => { }, f.Config,
                quality: quality, redLeafReader: reader, attachmentStore: f.Attachments, inputQueue: f.Service(),
                titles: titles, titleChanged: session => host!.Changed.Add(session));
            host = new Host(f, app, leaf, (TitleProvider)(object)proxy, titles, endpoints, jobs);
            return host;
        }

        public static ClaimsPrincipal Owner()
            => new(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("roles", "[\"operator\"]")], "signed"));

        public static ClaimsPrincipal TrustedInfrastructure()
            => new(new ClaimsIdentity([new Claim("sub", "service:redleaf"), new Claim("client_id", "redleaf"),
                new Claim("compute_provenance", "true")], "signed"));

        public async Task<(int Status, JsonElement Body)> CallAsync(string method, string pattern, string? id,
            string query = "", object? body = null, string? jobName = null, ClaimsPrincipal? principal = null)
        {
            var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == pattern
                    && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var ctx = new DefaultHttpContext { RequestServices = _app.Services, User = principal ?? Owner() };
            ctx.SetEndpoint(endpoint);
            ctx.Request.Method = method;
            ctx.Request.Path = id is null ? pattern : pattern.Replace("{id}", id);
            ctx.Request.QueryString = new(query);
            if (id is not null) ctx.Request.RouteValues["id"] = id;
            if (jobName is not null) ctx.Request.Headers["X-Job-Name"] = jobName;
            if (body is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                ctx.Request.ContentType = "application/json";
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Request.ContentLength = bytes.Length;
            }
            else if (method is "POST" or "PUT")
            {
                var bytes = "{}"u8.ToArray();
                ctx.Request.ContentType = "application/json";
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Request.ContentLength = bytes.Length;
            }
            ctx.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(ctx);
            ctx.Response.Body.Position = 0;
            var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
            return (ctx.Response.StatusCode, document.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            _fixture.Dispose();
        }
    }
}
