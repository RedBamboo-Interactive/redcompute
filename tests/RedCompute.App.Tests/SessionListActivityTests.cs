using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

[CollectionDefinition("Unified session endpoint mapping", DisableParallelization = true)]
public sealed class UnifiedSessionEndpointMappingCollection { }

[Collection("Unified session endpoint mapping")]
public sealed class SessionListActivityTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private sealed record Row(string Id, string Status, int Age, string Provider = "test", bool Dismissed = false,
        string Owner = "owner", bool Confidential = false, string? Source = null);

    [Fact]
    public async Task More_than_twenty_newer_idle_sessions_cannot_hide_old_active_or_starting_work()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var rows = Enumerable.Range(0, 25).Select(i => new Row($"idle-{i}", "Idle", i)).Concat([
            new Row("old-active", "Active", -100), new Row("old-starting", "Starting", -90),
            new Row("new-stopped", "Stopped", 30), new Row("new-error", "Error", 31)]).ToArray();
        var reader = Reader(f, rows);
        var list = await reader.GetSessionsAsync(null, 20, false);
        Assert.Equal(20, list.Count);
        Assert.Equal(["old-starting", "old-active", "new-error", "new-stopped", "idle-24"], list.Take(5).Select(s => s.Id));
        Assert.Equal(Epoch.AddMinutes(-100), list.Single(s => s.Id == "old-active").StartedAt);
        Assert.Equal(["old-starting"], (await reader.GetSessionsAsync(null, 1, false)).Select(s => s.Id));
        Assert.Empty(await reader.GetSessionsAsync(null, 0, false));
        Assert.Empty(await reader.GetSessionsAsync(null, -1, false));
    }

    [Fact]
    public async Task Ongoing_group_is_stable_within_started_at_order_and_preserves_provider_dismissed_filters()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var handler = new ListHandler([
            new("active-tie-first", "Active", 1), new("starting-tie-second", "Starting", 1),
            new("active-new", "Active", 2), new("idle-new", "Idle", 20),
            new("foreign-provider", "Active", 30, Provider: "other"), new("dismissed", "Active", 40, Dismissed: true)]);
        var reader = Reader(f, handler);
        Assert.Equal(["active-new", "active-tie-first", "starting-tie-second", "idle-new"],
            (await reader.GetSessionsAsync("test", 20, false)).Select(s => s.Id));
        Assert.Contains("data.provider=test", handler.Requests.Single());
        Assert.Contains("data.dismissed=false", handler.Requests.Single());
        Assert.Equal("dismissed", (await reader.GetSessionsAsync("test", 1, true)).Single().Id);
        Assert.DoesNotContain("data.dismissed", handler.Requests.Last());
    }

    [Fact]
    public async Task Mapped_default_list_keeps_own_old_activity_and_applies_current_confidential_policy_before_limit()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var rows = Enumerable.Range(0, 25).Select(i => new Row($"idle-{i}", "Idle", i)).Concat([
            new Row("excluded-source", "Active", 100, Source: "hidden"),
            new Row("foreign-confidential", "Active", 90, Owner: "other", Confidential: true),
            new Row("foreign-ordinary", "Starting", 80, Owner: "other"),
            new Row("own-active", "Active", -100, Confidential: true), new Row("own-starting", "Starting", -90)]).ToArray();
        var builder = WebApplication.CreateSlimBuilder(); await using var app = builder.Build();
        var registry = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(registry, f.Registry, null!, (_, _) => { }, f.Config,
            redLeafReader: Reader(f, rows), attachmentStore: f.Attachments, inputQueue: f.Service());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/ai-session/sessions" && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        async Task<JsonElement[]> Read(string query)
        {
            var ctx = new DefaultHttpContext { RequestServices = app.Services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim("roles", "[\"operator\"]")], "signed")) };
            ctx.SetEndpoint(endpoint); ctx.Request.Method = "GET"; ctx.Request.QueryString = new(query); ctx.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(ctx); Assert.Equal(200, ctx.Response.StatusCode);
            ctx.Response.Body.Position = 0;
            using var result = await JsonDocument.ParseAsync(ctx.Response.Body);
            return result.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        var list = await Read("?excludeSource=hidden");
        Assert.Equal(20, list.Length);
        Assert.Equal(["own-starting", "own-active"], list.Take(2).Select(s => s.GetProperty("id").GetString()));
        Assert.DoesNotContain(list, s => s.GetProperty("id").GetString() == "foreign-confidential");
        var bounded = await Read("?excludeSource=hidden&limit=2");
        Assert.Equal(2, bounded.Length); Assert.Contains(bounded, s => s.GetProperty("id").GetString() == "own-active");
    }

    private static RedLeafSessionReader Reader(SessionRecoveryTests.Fixture f, Row[] rows) => Reader(f, new ListHandler(rows));
    private static RedLeafSessionReader Reader(SessionRecoveryTests.Fixture f, ListHandler handler)
    {
        var quality = new QualityModeService(f.Config, (_, _) => { }, new ProviderConfigService(f.Config, (_, _) => { }),
            new HttpClient(handler), Path.Combine(f.Root, "quality.json"), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        return new RedLeafSessionReader(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, quality);
    }
    private sealed class ListHandler(Row[] rows) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var selected = rows.Where(r => !query.TryGetValue("data.provider", out var provider) || r.Provider == provider)
                .Where(r => !query.ContainsKey("data.dismissed") || !r.Dismissed);
            var items = selected.Select(r => new { id = r.Id, name = r.Id, data = JsonSerializer.Serialize(new {
                session_id = r.Id, provider = r.Provider, status = r.Status, started_at = Epoch.AddMinutes(r.Age),
                user_id = r.Owner, owner_agent_id = "agent", confidential = r.Confidential, source = r.Source, dismissed = r.Dismissed }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), Encoding.UTF8, "application/json") });
        }
    }
}
