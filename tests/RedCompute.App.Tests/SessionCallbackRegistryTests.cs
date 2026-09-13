using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionCallbackRegistryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostAcknowledgementRetriesSameCompletionAndLaterRegistrationIsDistinct(bool lostResponse)
    {
        var attempts = Channel.CreateUnbounded<JsonElement>();
        var calls = 0;
        using var http = new HttpClient(new Handler(async request => {
            var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            await attempts.Writer.WriteAsync(payload);
            if (Interlocked.Increment(ref calls) == 1)
            {
                if (lostResponse) throw new HttpRequestException("Response lost after receiver accepted");
                return new(HttpStatusCode.BadGateway);
            }
            return new(HttpStatusCode.OK);
        }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        registry.Register("session", "http://localhost/callback", callbackId: "prompt-one");
        registry.OnSessionEvent("session.updated", Idle());
        registry.OnSessionEvent("session.updated", Idle());
        var first = await attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var retry = await attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.GetRawText(), retry.GetRawText());
        Assert.Equal("prompt-one", retry.GetProperty("callbackId").GetString());
        registry.Register("session", "http://localhost/callback", callbackId: "prompt-two");
        registry.OnSessionEvent("session.ended", new { id = "session", reason = "completed" });
        var later = await attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("prompt-two", later.GetProperty("callbackId").GetString());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ImmediateCompletionAndLegacyRegistrationEachKeepTheirOwnDeliveryIdentity()
    {
        var attempts = Channel.CreateUnbounded<JsonElement>();
        using var http = new HttpClient(new Handler(async request => {
            await attempts.Writer.WriteAsync(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return new(HttpStatusCode.OK);
        }));
        var registry = new SessionCallbackRegistry(http, (_, _) => { });
        Assert.False(registry.RegisterIfStillActive("session", "http://localhost/callback", SessionStatus.Idle, callbackId: "already-finished"));
        Assert.Equal("already-finished", (await attempts.Reader.ReadAsync()).GetProperty("callbackId").GetString());
        var ids = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(registry.RegisterIfStillActive("session", "http://localhost/callback", SessionStatus.Idle, force: true));
            registry.OnSessionEvent("session.updated", Idle());
            Assert.True(ids.Add((await attempts.Reader.ReadAsync()).GetProperty("callbackId").GetString()!));
        }
    }

    private static UnifiedSessionInfo Idle() => new() {
        Id = "session", Provider = "test", ProjectName = "test", ProjectPath = "test", Status = SessionStatus.Idle,
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
