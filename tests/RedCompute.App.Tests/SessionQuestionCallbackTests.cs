using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

// Incident eb0d256c663d: a delegated session parked on AskUserQuestion and nothing reached the
// delegating discussion until the 30-minute timeout denied it. Opted-in registrants now hear about
// each parked question exactly once, through the same outbox as completions, and the terminal
// completion still arrives afterwards exactly as before.
public sealed class SessionQuestionCallbackTests
{
    private const string CompleteUrl = "http://localhost/complete";
    private const string QuestionUrl = "http://localhost/question";

    [Fact]
    public async Task Parked_question_is_posted_once_to_the_opted_in_url_and_the_completion_still_fires()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle; f.Runtime.Info.StopReason = null; f.Runtime.Info.Title = "Leaf Commons relay";
        await f.Store.EnqueueAsync(f.Submission());
        var posts = new List<(string Url, JsonElement Body)>();
        using var http = Recording(posts);
        var pending = new List<PendingSessionQuestion>();
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http) { PendingQuestions = _ => [.. pending] };
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt", QuestionUrl);
        // A registrant that did not opt in never receives a question payload.
        await callbacks.RegisterAsync("session", "silent", "http://localhost/silent", "owner", "prompt");
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        f.Runtime.Info.Status = SessionStatus.Active;
        await callbacks.ObserveAsync("session"); await callbacks.DrainAsync();
        Assert.Empty(posts);

        pending.Add(Question("req-1"));
        await callbacks.QuestionsChangedAsync("session");
        await callbacks.QuestionsChangedAsync("session");
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt", QuestionUrl);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();

        var (url, question) = Assert.Single(posts);
        Assert.Equal(QuestionUrl, url);
        Assert.Equal("session.question", question.GetProperty("event").GetString());
        Assert.Equal("session", question.GetProperty("sessionId").GetString());
        Assert.Equal("prompt", question.GetProperty("callbackId").GetString());
        Assert.Equal("Leaf Commons relay", question.GetProperty("title").GetString());
        Assert.Equal("req-1", question.GetProperty("requestId").GetString());
        Assert.Equal("AskUserQuestion", question.GetProperty("toolName").GetString());
        Assert.Equal(["Postgres", "SQLite"], question.GetProperty("questions")[0].GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("label").GetString()));
        Assert.Equal(pending[0].AskedAt, question.GetProperty("askedAt").GetDateTimeOffset());
        Assert.Equal(pending[0].ExpiresAt, question.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("/ai-session/sessions/session/question", question.GetProperty("answer").GetProperty("path").GetString());

        // The question never consumed the subscription: completion arrives as it always did.
        pending.Clear(); await callbacks.QuestionResolvedAsync("session", "req-1");
        await f.Store.MarkDeliveredAsync(batch, "prompt");
        f.Runtime.Info.Status = SessionStatus.Idle;
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        var completions = posts.Skip(1).ToList();
        Assert.Equal([CompleteUrl, "http://localhost/silent"], completions.Select(p => p.Url).Order());
        Assert.All(completions, p =>
        {
            Assert.Equal("Idle", p.Body.GetProperty("status").GetString());
            Assert.Equal("prompt", p.Body.GetProperty("deliveredMessageUid").GetString());
            Assert.False(p.Body.TryGetProperty("requestId", out _));
        });
        Assert.Empty(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task Failed_notification_retries_the_identical_payload_on_the_outbox_backoff_without_blocking()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var bodies = new List<string>(); var calls = 0; var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler(async r => {
            bodies.Add(await r.Content!.ReadAsStringAsync()); return new(++calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK); }));
        var pending = new List<PendingSessionQuestion> { Question("req-1") };
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: () => now)
            { PendingQuestions = _ => [.. pending] };

        // Registering while the session is already parked (a continuation queued behind it).
        var register = callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt", QuestionUrl);
        Assert.True(await Task.WhenAny(register, Task.Delay(TimeSpan.FromSeconds(5))) == register);
        await callbacks.DrainAsync();
        Assert.Single(bodies);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Single(bodies);

        now = now.AddSeconds(31);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, bodies.Count); Assert.Equal(bodies[0], bodies[1]);
        now = now.AddMinutes(10);
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Equal(2, bodies.Count);
        // Only the completion subscription remains.
        Assert.Single(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task Question_resolved_before_delivery_is_dropped_even_after_reopen()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var bodies = new List<string>(); var now = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new Handler(async r => {
            bodies.Add(await r.Content!.ReadAsStringAsync()); return new(HttpStatusCode.ServiceUnavailable); }));
        var pending = new List<PendingSessionQuestion> { Question("req-1") };
        var callbacks = new SessionPromptCallbacks(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: () => now)
            { PendingQuestions = _ => [.. pending] };
        await callbacks.RegisterAsync("session", "prompt", CompleteUrl, "owner", "prompt", QuestionUrl);
        await callbacks.DrainAsync();
        Assert.Single(bodies);
        Assert.Equal(2, new SessionCallbackStore(f.Attachments.DatabasePath).Load().Count);

        // The question is answered (or the process restarted) before the retry is due.
        pending.Clear(); now = now.AddSeconds(31);
        callbacks = new(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info, http, now: () => now) { PendingQuestions = _ => [] };
        await callbacks.SweepAsync(default); await callbacks.DrainAsync();
        Assert.Single(bodies);
        Assert.Single(new SessionCallbackStore(f.Attachments.DatabasePath).Load());
    }

    [Fact]
    public async Task Registry_routes_parked_questions_through_the_durable_outbox_only_for_opted_in_registrations()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Active; f.Runtime.Info.StopReason = null;
        await f.Store.EnqueueAsync(f.Submission());
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        using var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync((r.RequestUri!.ToString(), JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())));
            return new(HttpStatusCode.OK); }));
        var pending = new List<PendingSessionQuestion>();
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachDurableStore(f.Attachments.DatabasePath, f.Store, _ => f.Runtime.Info);
        registry.AttachQuestionSource(_ => [.. pending], _ => f.Runtime.Info);
        registry.Register("session", CompleteUrl, "owner", "prompt", "prompt", QuestionUrl);

        pending.Add(Question("req-1"));
        registry.OnQuestionParked("session");
        registry.OnQuestionParked("session");

        var (url, body) = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QuestionUrl, url);
        Assert.Equal("req-1", body.GetProperty("requestId").GetString());
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task In_memory_registration_notifies_once_and_keeps_its_completion()
    {
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        using var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync((r.RequestUri!.ToString(), JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())));
            return new(HttpStatusCode.OK); }));
        var pending = new Dictionary<string, List<PendingSessionQuestion>> { ["session"] = [], ["quiet"] = [] };
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.AttachQuestionSource(id => pending.TryGetValue(id, out var q) ? [.. q] : [], id => Info(id, SessionStatus.Active));
        registry.Register("session", CompleteUrl, "owner", "operation", questionUrl: QuestionUrl);
        registry.Register("quiet", "http://localhost/quiet-complete", "owner", "quiet-operation");

        pending["session"].Add(Question("req-1"));
        pending["quiet"].Add(Question("req-quiet"));
        registry.OnQuestionParked("session");
        registry.OnQuestionParked("session");
        registry.OnQuestionParked("quiet");

        var (url, body) = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QuestionUrl, url);
        Assert.Equal("operation", body.GetProperty("callbackId").GetString());
        Assert.Equal("req-1", body.GetProperty("requestId").GetString());
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out _));

        registry.OnQuestionResolved("session", "req-1");
        registry.OnSessionEvent("session.updated", Info("session", SessionStatus.Idle));
        var completion = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompleteUrl, completion.Url);
        Assert.Equal("operation", completion.Body.GetProperty("callbackId").GetString());
        Assert.Equal("Idle", completion.Body.GetProperty("status").GetString());
    }

    internal static PendingSessionQuestion Question(string requestId)
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new[]
            {
                new
                {
                    question = "Which database?",
                    header = "Database",
                    multiSelect = false,
                    options = new[] { new { label = "Postgres", description = "Shared" }, new { label = "SQLite", description = "Local" } },
                },
            },
        });
        var askedAt = new DateTimeOffset(2026, 10, 24, 10, 28, 52, TimeSpan.Zero);
        return new PendingSessionQuestion
        {
            RequestId = requestId, ToolName = "AskUserQuestion", ToolUseId = "toolu_ask",
            Questions = input.GetProperty("questions"), Input = input,
            AskedAt = askedAt, ExpiresAt = askedAt.AddMinutes(30),
        };
    }

    private static UnifiedSessionInfo Info(string id, SessionStatus status) => new()
    {
        Id = id, Provider = "test", ProjectName = "test", ProjectPath = "test", Status = status, Title = "Relay",
    };

    private static HttpClient Recording(List<(string Url, JsonElement Body)> posts) => new(new Handler(async r =>
    {
        var body = JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync());
        lock (posts) posts.Add((r.RequestUri!.ToString(), body));
        return new(HttpStatusCode.OK);
    }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
